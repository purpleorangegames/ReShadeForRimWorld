using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Brutal;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using Brutal.VulkanApi.Vma;
using Waylith.Graphics;
using Waylith.Rendering;

namespace ReShadeForRimWorld;

// values that change every frame, captured on the main thread
public struct FrameData
{
    public float TimerMs;
    public float FrameTimeMs;
    public int FrameCount;
    public int Random;
    public float Year, Month, Day, Seconds;
}

// what an effect reads and writes besides its own textures, for one frame
public struct FrameTargets
{
    public FxTexture BackBuffer;
    public FxTexture BackBufferCopy;
    public FxTexture StandIn;
    public FxTexture StandIn1D, StandIn3D;
    public VkImageView Depth;
}

// one .fx file: compiled module plus all the GPU objects needed to run its techniques
public sealed unsafe class Effect : IDisposable
{
    public readonly string FilePath;
    public string Name => Path.GetFileName(FilePath);

    // compiling happens on a worker thread, the newest result is swapped in here
    public sealed class CompileResult
    {
        public bool Success;
        public string Errors = "";
        public FxModule Module;
        public Dictionary<string, byte[]> Spirv;
        public int Width, Height;
        public string Defines;
    }

    // a layer copy (UI layer, whole screen) shares its source's compile and may share its values,
    // but has its own GPU objects since it draws onto a different image
    public readonly Effect Source;
    private readonly bool shareValues;
    private volatile CompileResult result;
    public CompileResult Result
    {
        get => Source != null ? Source.Result : result;
        set => result = value;
    }
    public volatile bool Compiling;
    // main thread: set when the source, screen size or definitions changed
    public bool Stale = true;
    // variable values set from the overlay, by uniform name; replaced as a whole, never edited in place
    private volatile Dictionary<string, float[]> values = new();
    public Dictionary<string, float[]> Values
    {
        get => shareValues ? Source.Values : values;
        set
        {
            if (shareValues)
            {
                Source.Values = value;
            }
            else
            {
                values = value;
            }
        }
    }

    // render thread state
    private CompileResult built;
    private FxModule Module;
    private Dictionary<string, byte[]> spirv;
    private long preparedFrame = -1;
    private VkDescriptorSet uniformSet;

    private DeviceEx device;
    private GraphicsDevice graphicsDevice;
    private FxTexture builtFor;
    private readonly Dictionary<string, FxTexture> textures = new();
    private readonly List<(FxTexture tex, byte[] pixels)> uploads = new();
    private VkSampler[] samplers = Array.Empty<VkSampler>();
    private VkDescriptorSetLayout uniformLayout, samplerLayout, storageLayout;
    private VkPipelineLayout pipelineLayout;
    private VkDescriptorPool[] pools = Array.Empty<VkDescriptorPool>();
    private BufferAllocationInfo uniformBuffer;
    private AllocationMapInfo uniformMap;
    private byte* uniformMapped;
    private int uniformStride;
    private byte[] uniformData = Array.Empty<byte>();
    private readonly List<PassGpu> passes = new();
    private FxTexture stencil;

    private sealed class PassGpu
    {
        public string Technique;
        public FxPass Pass;
        public VkRenderPass LoadPass, ClearPass;
        public VkFramebuffer Framebuffer;
        public VkPipeline Pipeline;
        public VkShaderModule Vs, Ps;
        public FxTexture[] Targets;
        public bool ToBackBuffer;
        public bool UsesStencil, ClearsStencil;
        public int Width, Height;
        // compute passes
        public bool IsCompute;
        public VkShaderModule Cs;
        public FxTexture[] StorageTargets = Array.Empty<FxTexture>();
    }

    private DateTime sourceStamp;

    public Effect(string filePath)
    {
        FilePath = filePath;
        sourceStamp = File.GetLastWriteTimeUtc(filePath);
    }

    public Effect(Effect source, bool shareValues)
    {
        Source = source;
        FilePath = source.FilePath;
        this.shareValues = shareValues;
    }

    // main thread
    public void CheckSource()
    {
        DateTime stamp = File.Exists(FilePath) ? File.GetLastWriteTimeUtc(FilePath) : DateTime.MinValue;
        if (stamp != sourceStamp)
        {
            sourceStamp = stamp;
            Stale = true;
        }
    }

    // any thread: preprocesses and compiles the file with ReShade's compiler
    public static CompileResult Compile(string filePath, int width, int height, IList<string> includePaths, IList<string> userDefines)
    {
        var result = new CompileResult { Width = width, Height = height, Defines = string.Join("\n", userDefines) };
        var defines = new List<string>
        {
            "__RESHADE__=60500",
            "__RESHADE_PERMUTATION__=0",
            "__RESHADE_PERFORMANCE_MODE__=0",
            "__VENDOR__=0",
            "__DEVICE__=0",
            "__RENDERER__=0x20000",
            "__APPLICATION__=0",
            "BUFFER_WIDTH=" + width,
            "BUFFER_HEIGHT=" + height,
            "BUFFER_RCP_WIDTH=(1.0 / BUFFER_WIDTH)",
            "BUFFER_RCP_HEIGHT=(1.0 / BUFFER_HEIGHT)",
            "BUFFER_COLOR_SPACE=1",
            "BUFFER_COLOR_FORMAT=0",
            "BUFFER_COLOR_BIT_DEPTH=8"
        };
        defines.AddRange(userDefines);
        var first = NativeFx.Compile(filePath, includePaths, defines);
        if (!first.Success)
        {
            result.Errors = string.IsNullOrEmpty(first.Errors) ? "unknown compile error" : first.Errors;
            return result;
        }
        result.Module = FxModule.Parse(first.ModuleJson);
        var second = NativeFx.Compile(filePath, includePaths, defines, result.Module.entry_points.Select(e => e.name));
        result.Spirv = second.Spirv;
        result.Errors = second.Errors ?? "";
        result.Success = result.Module.entry_points.All(e => result.Spirv.ContainsKey(e.name));
        if (!result.Success && result.Errors.Length == 0)
        {
            result.Errors = "missing SPIR-V for an entry point";
        }
        return result;
    }

    private static bool IsBackBuffer(FxTextureInfo t) => t.semantic == "COLOR" || t.semantic == "SV_TARGET";
    private static bool IsDepth(FxTextureInfo t) => t.semantic == "DEPTH" || t.semantic == "SV_DEPTH";

    private FxTextureInfo TextureByName(string uniqueName) => Module.textures.FirstOrDefault(t => t.unique_name == uniqueName);

    private void Build(in FrameContext ctx, CompileResult result, FxTexture backBuffer, IList<string> texturePaths, Action<string> log)
    {
        DestroyGpu();
        built = result;
        Module = result.Module;
        spirv = result.Spirv;
        builtFor = backBuffer;
        graphicsDevice = ctx.GraphicsDevice;
        device = ctx.Device;
        int framesInFlight = ctx.RenderContext.FramesInFlight;

        foreach (FxTextureInfo t in Module.textures)
        {
            if (IsBackBuffer(t) || IsDepth(t))
            {
                continue;
            }
            if (!string.IsNullOrEmpty(t.semantic))
            {
                log($"{Name}: texture semantic {t.semantic} is not known, it stays black");
            }
            var tex = new FxTexture(graphicsDevice, t.width, t.height, t.levels, GpuUtil.ToVkFormat(t.format), Name + "." + t.unique_name, storage: t.storage_access, dimensions: t.type, depth: t.depth);
            textures[t.unique_name] = tex;
            string source = t.annotations.GetString("source");
            if (source != null)
            {
                string path = ImageLoader.Find(texturePaths, source);
                if (path == null)
                {
                    log($"{Name}: image '{source}' for texture {t.name} not found in reshade-shaders/Textures");
                    continue;
                }
                byte[] pixels = ImageLoader.Load(path, tex.Width, tex.Height, tex.Format, out string error);
                if (pixels == null)
                {
                    log($"{Name}: image '{source}': {error}");
                    continue;
                }
                uploads.Add((tex, pixels));
            }
        }

        samplers = new VkSampler[Module.samplers.Count];
        for (int i = 0; i < samplers.Length; i++)
        {
            samplers[i] = CreateSampler(Module.samplers[i]);
        }

        uniformLayout = CreateLayout(VkDescriptorType.UniformBuffer, 1);
        // the compiler numbers sampler bindings per pass, from what that pass's shaders use
        int samplerSlots = Module.techniques.SelectMany(t => t.passes).SelectMany(p => p.sampler_bindings).Select(b => b.binding + 1).DefaultIfEmpty(0).Max();
        samplerLayout = CreateLayout(VkDescriptorType.CombinedImageSampler, samplerSlots);
        int storageSlots = Module.techniques.SelectMany(t => t.passes).SelectMany(p => p.storage_bindings).Select(b => b.binding + 1).DefaultIfEmpty(0).Max();
        storageLayout = CreateLayout(VkDescriptorType.StorageImage, storageSlots);
        VkDescriptorSetLayout* setLayouts = stackalloc VkDescriptorSetLayout[3] { uniformLayout, samplerLayout, storageLayout };
        pipelineLayout = device.CreatePipelineLayout(new ReadOnlySpan<VkDescriptorSetLayout>(setLayouts, 3), default(ReadOnlySpan<VkPushConstantRange>), device.VkAllocator);

        int passCount = Module.techniques.Sum(t => t.passes.Count);
        pools = new VkDescriptorPool[framesInFlight];
        VkDescriptorPoolSize* sizes = stackalloc VkDescriptorPoolSize[3];
        sizes[0] = new VkDescriptorPoolSize { Type = VkDescriptorType.UniformBuffer, DescriptorCount = 1 };
        sizes[1] = new VkDescriptorPoolSize { Type = VkDescriptorType.CombinedImageSampler, DescriptorCount = Math.Max(1, passCount * Math.Max(1, samplerSlots)) };
        sizes[2] = new VkDescriptorPoolSize { Type = VkDescriptorType.StorageImage, DescriptorCount = Math.Max(1, passCount * Math.Max(1, storageSlots)) };
        for (int i = 0; i < framesInFlight; i++)
        {
            pools[i] = device.CreateDescriptorPool(new VkDescriptorPoolCreateInfo
            {
                // per frame: one uniform set, then a sampler set and a storage set for each pass
                MaxSets = passCount * 2 + 1,
                PoolSizeCount = 3,
                PoolSizes = sizes
            }, device.VkAllocator);
        }

        CreateUniformBuffer(framesInFlight);

        foreach (FxTechnique technique in Module.techniques)
        {
            bool stencilClearedInTechnique = false;
            foreach (FxPass pass in technique.passes)
            {
                var gpu = new PassGpu { Technique = technique.name, Pass = pass };
                if (!string.IsNullOrEmpty(pass.cs))
                {
                    gpu.IsCompute = true;
                    gpu.Cs = RuntimeShaderCompiler.CreateShaderModule(device, spirv[pass.cs], Name + "." + pass.cs);
                    gpu.Pipeline = CreateComputePipeline(gpu);
                    gpu.StorageTargets = pass.storage_bindings
                        .Select(b => textures.TryGetValue(Module.storages[b.index].texture_name, out FxTexture st) ? st : null)
                        .Where(st => st != null).Distinct().ToArray();
                    gpu.Targets = gpu.StorageTargets;
                    passes.Add(gpu);
                    continue;
                }
                var names = pass.render_targets.Where(n => !string.IsNullOrEmpty(n)).ToList();
                VkFormat[] formats;
                VkImageView[] views;
                if (names.Count == 0)
                {
                    gpu.ToBackBuffer = true;
                    gpu.Targets = new[] { backBuffer };
                }
                else
                {
                    var targets = new List<FxTexture>();
                    foreach (string n in names)
                    {
                        if (textures.TryGetValue(n, out FxTexture t))
                        {
                            targets.Add(t);
                        }
                        else
                        {
                            log($"{Name}: pass {pass.name} renders to unknown texture {n}");
                        }
                    }
                    if (targets.Count == 0)
                    {
                        continue;
                    }
                    gpu.Targets = targets.ToArray();
                }
                formats = gpu.Targets.Select(t => t.TargetFormat(pass.srgb_write_enable)).ToArray();
                views = gpu.Targets.Select(t => t.TargetView(pass.srgb_write_enable)).ToArray();
                gpu.Width = gpu.Targets[0].Width;
                gpu.Height = gpu.Targets[0].Height;
                // ReShade's stencil buffer matches the back buffer, passes drawing at another size go without
                if (pass.stencil_enable && gpu.Width == backBuffer.Width && gpu.Height == backBuffer.Height)
                {
                    stencil ??= new FxTexture(graphicsDevice, backBuffer.Width, backBuffer.Height, 1, VkFormat.D32SFloatS8UInt, Name + ".Stencil", depthStencil: true);
                    gpu.UsesStencil = true;
                    gpu.ClearsStencil = !stencilClearedInTechnique;
                    stencilClearedInTechnique = true;
                    views = views.Append(stencil.View(false)).ToArray();
                }
                else if (pass.stencil_enable)
                {
                    log($"{Name}: pass {pass.name} uses stencil at a size other than the screen, stencil is skipped");
                }
                VkFormat stencilFormat = gpu.UsesStencil ? stencil.Format : VkFormat.Undefined;
                gpu.LoadPass = GpuUtil.CreateRenderPass(device, formats, clear: false, stencilFormat);
                gpu.ClearPass = GpuUtil.CreateRenderPass(device, formats, clear: true, stencilFormat);
                gpu.Framebuffer = GpuUtil.CreateFramebuffer(device, gpu.LoadPass, views, gpu.Width, gpu.Height);
                gpu.Vs = RuntimeShaderCompiler.CreateShaderModule(device, spirv[pass.vs], Name + "." + pass.vs);
                gpu.Ps = RuntimeShaderCompiler.CreateShaderModule(device, spirv[pass.ps], Name + "." + pass.ps);
                gpu.Pipeline = CreatePipeline(gpu);
                passes.Add(gpu);
            }
        }

        // defaults from the effect source, sources like "timer" get written every frame
        uniformData = new byte[Math.Max(16, (Module.total_uniform_size + 15) & ~15)];
        foreach (FxUniform u in Module.uniforms)
        {
            if (u.has_initializer)
            {
                WriteConstant(u, u.initializer);
            }
        }
    }

    private VkDescriptorSetLayout CreateLayout(VkDescriptorType type, int count)
    {
        var bindings = new VkDescriptorSetLayoutBinding[count];
        for (int i = 0; i < count; i++)
        {
            bindings[i] = new VkDescriptorSetLayoutBinding
            {
                Binding = i,
                DescriptorType = type,
                DescriptorCount = 1,
                StageFlags = VkShaderStageFlags.VertexBit | VkShaderStageFlags.FragmentBit | VkShaderStageFlags.ComputeBit
            };
        }
        fixed (VkDescriptorSetLayoutBinding* p = bindings)
        {
            return device.CreateDescriptorSetLayout(new DescriptorSetLayoutEx.CreateInfo
            {
                Bindings = new Span<VkDescriptorSetLayoutBinding>(p, count)
            }, device.VkAllocator);
        }
    }

    private VkSampler CreateSampler(FxSampler s)
    {
        static VkSamplerAddressMode Address(int a) => a switch
        {
            1 => VkSamplerAddressMode.Repeat,
            2 => VkSamplerAddressMode.MirroredRepeat,
            4 => VkSamplerAddressMode.ClampToBorder,
            _ => VkSamplerAddressMode.ClampToEdge
        };
        bool aniso = s.filter == 0x55;
        var info = new VkSamplerCreateInfo();
        info.MinFilter = aniso || (s.filter & 0x10) != 0 ? VkFilter.Linear : VkFilter.Nearest;
        info.MagFilter = aniso || (s.filter & 0x04) != 0 ? VkFilter.Linear : VkFilter.Nearest;
        info.MipmapMode = aniso || (s.filter & 0x01) != 0 ? VkSamplerMipmapMode.Linear : VkSamplerMipmapMode.Nearest;
        info.AddressModeU = Address(s.address_u);
        info.AddressModeV = Address(s.address_v);
        info.AddressModeW = Address(s.address_w);
        info.MipLodBias = s.lod_bias;
        info.MinLod = Math.Max(s.min_lod, -1000f);
        info.MaxLod = Math.Min(s.max_lod, 1000f);
        info.BorderColor = VkBorderColor.FloatTransparentBlack;
        return device.CreateSampler(in info, device.VkAllocator);
    }

    private void CreateUniformBuffer(int framesInFlight)
    {
        uniformStride = Math.Max(256, (Module.total_uniform_size + 255) & ~255);
        var bufferInfo = new VkBufferCreateInfo
        {
            Size = ByteSize.Of(uniformStride * framesInFlight),
            Usage = VkBufferUsageFlags.UniformBufferBit,
            SharingMode = VkSharingMode.Exclusive
        };
        var allocInfo = new AllocationInfo.CreateInfo
        {
            Preference = MemoryPreference.PreferCpu,
            RequiredProperties = VkMemoryPropertyFlags.HostVisibleBit | VkMemoryPropertyFlags.HostCoherentBit,
            PreferredProperties = VkMemoryPropertyFlags.HostVisibleBit | VkMemoryPropertyFlags.HostCoherentBit,
            Priority = 1f
        };
        uniformBuffer = graphicsDevice.Allocator.AllocateBufferChecked("ReShade." + Name + ".Uniforms", in bufferInfo, in allocInfo);
        uniformMap = new AllocationMapInfo(uniformBuffer.AllocationInfo)
        {
            Region = new MapRegion { Offset = ByteSize.Zero, Size = uniformBuffer.BufferSize }
        };
        uniformMapped = (byte*)(void*)graphicsDevice.Allocator.MapMemory(in uniformMap);
    }

    private VkPipeline CreatePipeline(PassGpu gpu)
    {
        FxPass pass = gpu.Pass;
        byte[] vsName = System.Text.Encoding.UTF8.GetBytes(pass.vs + "\0");
        byte[] psName = System.Text.Encoding.UTF8.GetBytes(pass.ps + "\0");
        fixed (byte* vsNamePtr = vsName, psNamePtr = psName)
        {
            VkPipelineShaderStageCreateInfo* stages = stackalloc VkPipelineShaderStageCreateInfo[2];
            stages[0] = new VkPipelineShaderStageCreateInfo { Stage = VkShaderStageFlags.VertexBit, Module = gpu.Vs, Name = vsNamePtr };
            stages[1] = new VkPipelineShaderStageCreateInfo { Stage = VkShaderStageFlags.FragmentBit, Module = gpu.Ps, Name = psNamePtr };

            var vertexInput = new VkPipelineVertexInputStateCreateInfo();
            var inputAssembly = new VkPipelineInputAssemblyStateCreateInfo();
            inputAssembly.Topology = (VkPrimitiveTopology)Math.Max(0, pass.topology - 1);
            var viewport = new VkPipelineViewportStateCreateInfo();
            viewport.ViewportCount = 1;
            viewport.ScissorCount = 1;
            var raster = new VkPipelineRasterizationStateCreateInfo();
            raster.PolygonMode = VkPolygonMode.Fill;
            raster.CullMode = VkCullModeFlags.None;
            raster.FrontFace = VkFrontFace.CounterClockwise;
            raster.LineWidth = 1f;
            var multisample = new VkPipelineMultisampleStateCreateInfo();
            multisample.RasterizationSamples = VkSampleCountFlags._1Bit;
            var depthStencil = new VkPipelineDepthStencilStateCreateInfo();
            depthStencil.DepthTestEnable = false;
            depthStencil.DepthWriteEnable = false;
            depthStencil.DepthCompareOp = VkCompareOp.Always;
            depthStencil.StencilTestEnable = gpu.UsesStencil;
            if (gpu.UsesStencil)
            {
                // reshadefx: zero, keep, replace, ... ; Vulkan: keep, zero, replace, ...
                static VkStencilOp Op(int op) => op switch { 0 => VkStencilOp.Zero, 1 => VkStencilOp.Keep, _ => (VkStencilOp)op };
                var face = new VkStencilOpState
                {
                    FailOp = Op(pass.stencil_fail_op),
                    PassOp = Op(pass.stencil_pass_op),
                    DepthFailOp = Op(pass.stencil_depth_fail_op),
                    CompareOp = (VkCompareOp)pass.stencil_func,
                    CompareMask = pass.stencil_read_mask,
                    WriteMask = pass.stencil_write_mask,
                    Reference = pass.stencil_ref
                };
                depthStencil.Front = face;
                depthStencil.Back = face;
            }

            int count = gpu.Targets.Length;
            VkPipelineColorBlendAttachmentState* attachments = stackalloc VkPipelineColorBlendAttachmentState[count];
            for (int i = 0; i < count; i++)
            {
                FxBlend b = pass.blend[i];
                var a = new VkPipelineColorBlendAttachmentState();
                a.BlendEnable = b.enable;
                a.SrcColorBlendFactor = (VkBlendFactor)b.src_color;
                a.DstColorBlendFactor = (VkBlendFactor)b.dst_color;
                a.ColorBlendOp = (VkBlendOp)Math.Max(0, b.color_op - 1);
                a.SrcAlphaBlendFactor = (VkBlendFactor)b.src_alpha;
                a.DstAlphaBlendFactor = (VkBlendFactor)b.dst_alpha;
                a.AlphaBlendOp = (VkBlendOp)Math.Max(0, b.alpha_op - 1);
                a.ColorWriteMask = (VkColorComponentFlags)(b.write_mask & 0xF);
                attachments[i] = a;
            }
            var blend = new VkPipelineColorBlendStateCreateInfo();
            blend.AttachmentCount = count;
            blend.Attachments = attachments;

            VkDynamicState* dynamicStates = stackalloc VkDynamicState[2] { VkDynamicState.Viewport, VkDynamicState.Scissor };
            var dynamic = new VkPipelineDynamicStateCreateInfo();
            dynamic.DynamicStateCount = 2;
            dynamic.DynamicStates = dynamicStates;

            var info = new VkGraphicsPipelineCreateInfo();
            info.StageCount = 2;
            info.Stages = stages;
            info.VertexInputState = &vertexInput;
            info.InputAssemblyState = &inputAssembly;
            info.ViewportState = &viewport;
            info.RasterizationState = &raster;
            info.MultisampleState = &multisample;
            info.DepthStencilState = &depthStencil;
            info.ColorBlendState = &blend;
            info.DynamicState = &dynamic;
            info.Layout = pipelineLayout;
            info.RenderPass = gpu.LoadPass;
            info.Subpass = 0;

            Span<VkPipeline> result = stackalloc VkPipeline[1];
            VkResult r = device.CreateGraphicsPipelines(default(VkPipelineCache), new ReadOnlySpan<VkGraphicsPipelineCreateInfo>(in info), device.VkAllocator, result);
            if (r != VkResult.Success)
            {
                throw new InvalidOperationException($"{Name}: pipeline for pass {pass.name} failed: {r}");
            }
            return result[0];
        }
    }

    private VkPipeline CreateComputePipeline(PassGpu gpu)
    {
        byte[] csName = System.Text.Encoding.UTF8.GetBytes(gpu.Pass.cs + "\0");
        fixed (byte* csNamePtr = csName)
        {
            var info = new VkComputePipelineCreateInfo();
            info.Stage = new VkPipelineShaderStageCreateInfo { Stage = VkShaderStageFlags.ComputeBit, Module = gpu.Cs, Name = csNamePtr };
            info.Layout = pipelineLayout;
            Span<VkPipeline> result = stackalloc VkPipeline[1];
            VkResult r = device.CreateComputePipelines(default(VkPipelineCache), new ReadOnlySpan<VkComputePipelineCreateInfo>(in info), device.VkAllocator, result);
            if (r != VkResult.Success)
            {
                throw new InvalidOperationException($"{Name}: compute pipeline for pass {gpu.Pass.name} failed: {r}");
            }
            return result[0];
        }
    }

    private void WriteConstant(FxUniform u, FxConstant c)
    {
        int components = Math.Max(1, u.type.rows) * Math.Max(1, u.type.cols);
        if (u.type.array_length > 0 && c.array != null && c.array.Count > 0)
        {
            int elementSize = u.size / (int)u.type.array_length;
            for (int e = 0; e < c.array.Count && e < u.type.array_length; e++)
            {
                WriteComponents(u.offset + e * elementSize, u.type.base_, components, c.array[e]);
            }
            return;
        }
        WriteComponents(u.offset, u.type.base_, components, c);
    }

    private void WriteComponents(int offset, int baseType, int components, FxConstant c)
    {
        for (int i = 0; i < components && offset + i * 4 + 4 <= uniformData.Length; i++)
        {
            if (baseType == FxType.Float)
            {
                MemoryMarshal.Write(uniformData.AsSpan(offset + i * 4), c.f[i]);
            }
            else
            {
                MemoryMarshal.Write(uniformData.AsSpan(offset + i * 4), c.i[i]);
            }
        }
    }

    private void WriteScalars(FxUniform u, params float[] values)
    {
        for (int i = 0; i < values.Length && i * 4 + 4 <= u.size; i++)
        {
            if (u.type.base_ == FxType.Float)
            {
                MemoryMarshal.Write(uniformData.AsSpan(u.offset + i * 4), values[i]);
            }
            else if (u.type.base_ == FxType.Bool)
            {
                MemoryMarshal.Write(uniformData.AsSpan(u.offset + i * 4), values[i] != 0 ? 1 : 0);
            }
            else
            {
                MemoryMarshal.Write(uniformData.AsSpan(u.offset + i * 4), (int)values[i]);
            }
        }
    }

    // the "source" annotation values ReShade fills in for effects
    private void UpdateSourceUniforms(in FrameData frame, bool hasDepth)
    {
        Dictionary<string, float[]> values = Values;
        foreach (FxUniform u in Module.uniforms)
        {
            string source = u.annotations.GetString("source");
            if (source == null)
            {
                if (u.type.array_length == 0 && values.TryGetValue(u.name, out float[] v))
                {
                    WriteScalars(u, v);
                }
                continue;
            }
            switch (source)
            {
                case "timer":
                    WriteScalars(u, frame.TimerMs);
                    break;
                case "frametime":
                    WriteScalars(u, frame.FrameTimeMs);
                    break;
                case "framecount":
                    WriteScalars(u, frame.FrameCount);
                    break;
                case "random":
                {
                    int min = u.annotations.Find("min")?.value.i[0] ?? 0;
                    int max = u.annotations.Find("max")?.value.i[0] ?? 32767;
                    WriteScalars(u, min + (max > min ? frame.Random % (max - min + 1) : 0));
                    break;
                }
                case "date":
                    WriteScalars(u, frame.Year, frame.Month, frame.Day, frame.Seconds);
                    break;
                case "bufready_depth":
                    WriteScalars(u, hasDepth ? 1 : 0);
                    break;
                case "overlay_open":
                    WriteScalars(u, 0);
                    break;
            }
        }
    }

    // render thread, once per frame before any of this effect's techniques: GPU objects, uploads, uniforms
    public bool Prepare(in FrameContext ctx, in FrameData frame, in FrameTargets io, IList<string> texturePaths, Action<string> log)
    {
        CompileResult result = Result;
        if (result == null || !result.Success)
        {
            return false;
        }
        if (built != result || builtFor != io.BackBuffer)
        {
            Build(in ctx, result, io.BackBuffer, texturePaths, log);
        }
        if (preparedFrame == frame.FrameCount)
        {
            return true;
        }
        preparedFrame = frame.FrameCount;
        CommandBuffer cmd = ctx.CommandBuffer;
        int frameIndex = ctx.SubmissionFrameIndex;

        foreach (FxTexture tex in textures.Values)
        {
            if (tex.Fresh)
            {
                GpuUtil.ClearFresh(cmd, tex);
            }
        }
        foreach ((FxTexture tex, byte[] pixels) in uploads)
        {
            GpuUtil.Upload(cmd, graphicsDevice, tex, pixels);
        }
        uploads.Clear();

        bool hasDepth = !io.Depth.IsNull();
        UpdateSourceUniforms(in frame, hasDepth);
        new ReadOnlySpan<byte>(uniformData).CopyTo(new Span<byte>(uniformMapped + frameIndex * uniformStride, uniformStride));

        VkDescriptorPool pool = pools[frameIndex];
        device.ResetDescriptorPool(pool, 0);
        uniformSet = DescriptorSetWriter.AllocateDescriptorSet(device, pool, uniformLayout);
        var bufferInfo = new VkDescriptorBufferInfo();
        bufferInfo.Buffer = uniformBuffer.VkBuffer;
        bufferInfo.Offset = ByteSize.Of(frameIndex * uniformStride);
        bufferInfo.Range = ByteSize.Of(uniformStride);
        var uniformWrite = new VkWriteDescriptorSet
        {
            DstSet = uniformSet,
            DstBinding = 0,
            DescriptorCount = 1,
            DescriptorType = VkDescriptorType.UniformBuffer,
            BufferInfo = &bufferInfo
        };
        device.UpdateDescriptorSets(new ReadOnlySpan<VkWriteDescriptorSet>(in uniformWrite), default(ReadOnlySpan<VkCopyDescriptorSet>));
        return true;
    }

    // an empty texture of the same kind the shader declared, so 1D and 3D samplers stay valid
    private static VkImageView StandInView(in FrameTargets io, FxTextureInfo t)
    {
        FxTexture standIn = t?.type == 1 ? io.StandIn1D : t?.type == 3 ? io.StandIn3D : io.StandIn;
        return standIn.View(false);
    }

    public bool HasTechnique(string name) => Module != null && Module.techniques.Any(t => t.name == name);

    // render thread, after Prepare
    public void RecordTechnique(in FrameContext ctx, in FrameTargets io, string technique)
    {
        CommandBuffer cmd = ctx.CommandBuffer;
        int frameIndex = ctx.SubmissionFrameIndex;
        VkDescriptorPool pool = pools[frameIndex];
        bool hasDepth = !io.Depth.IsNull();

        foreach (PassGpu gpu in passes)
        {
            if (gpu.Technique != technique)
            {
                continue;
            }
            FxTexture colorSource = io.BackBuffer;
            if (gpu.ToBackBuffer)
            {
                // the pass draws onto the back buffer, so it reads a copy of it
                GpuUtil.Copy(cmd, io.BackBuffer.Image, io.BackBufferCopy.Image, io.BackBuffer.Width, io.BackBuffer.Height);
                colorSource = io.BackBufferCopy;
            }

            VkDescriptorSet samplerSet = DescriptorSetWriter.AllocateDescriptorSet(device, pool, samplerLayout);
            List<FxBinding> bindings = gpu.Pass.sampler_bindings;
            int n = bindings.Count;
            if (n > 0)
            {
                VkDescriptorImageInfo* images = stackalloc VkDescriptorImageInfo[n];
                VkWriteDescriptorSet* writes = stackalloc VkWriteDescriptorSet[n];
                for (int i = 0; i < n; i++)
                {
                    int samplerIndex = bindings[i].index;
                    FxSampler s = Module.samplers[samplerIndex];
                    FxTextureInfo tex = TextureByName(s.texture_name);
                    VkImageView view;
                    VkImageLayout layout = VkImageLayout.ShaderReadOnlyOptimal;
                    if (tex == null || IsBackBuffer(tex))
                    {
                        view = colorSource.View(s.srgb);
                    }
                    else if (IsDepth(tex))
                    {
                        if (hasDepth)
                        {
                            view = io.Depth;
                            layout = VkImageLayout.DepthStencilReadOnlyOptimal;
                        }
                        else
                        {
                            view = StandInView(in io, tex);
                        }
                    }
                    else if (textures.TryGetValue(tex.unique_name, out FxTexture own) && Array.IndexOf(gpu.Targets, own) < 0)
                    {
                        view = own.View(s.srgb);
                    }
                    else
                    {
                        // a texture can't be read while it is being drawn to, ReShade unbinds it too
                        view = StandInView(in io, tex);
                    }
                    images[i] = new VkDescriptorImageInfo { Sampler = samplers[samplerIndex], ImageView = view, ImageLayout = layout };
                    writes[i] = new VkWriteDescriptorSet
                    {
                        DstSet = samplerSet,
                        DstBinding = bindings[i].binding,
                        DescriptorCount = 1,
                        DescriptorType = VkDescriptorType.CombinedImageSampler,
                        ImageInfo = images + i
                    };
                }
                device.UpdateDescriptorSets(new ReadOnlySpan<VkWriteDescriptorSet>(writes, n), default(ReadOnlySpan<VkCopyDescriptorSet>));
            }
            VkDescriptorSet storageSet = DescriptorSetWriter.AllocateDescriptorSet(device, pool, storageLayout);
            if (gpu.IsCompute)
            {
                RecordCompute(cmd, gpu, uniformSet, samplerSet, storageSet);
                continue;
            }

            FxPass pass = gpu.Pass;
            if (gpu.ClearsStencil)
            {
                GpuUtil.ClearStencil(cmd, stencil);
            }
            bool clear = pass.clear_render_targets;
            int targetCount = gpu.Targets.Length;
            VkClearValue* clearValues = stackalloc VkClearValue[targetCount];
            for (int i = 0; i < targetCount; i++)
            {
                clearValues[i] = new VkClearColorValue();
            }
            var begin = new VkRenderPassBeginInfo
            {
                RenderPass = clear ? gpu.ClearPass : gpu.LoadPass,
                Framebuffer = gpu.Framebuffer,
                RenderArea = new VkRect2D(new VkExtent2D(gpu.Width, gpu.Height)),
                ClearValueCount = clear ? targetCount : 0,
                ClearValues = clear ? clearValues : null
            };
            cmd.BeginRenderPass(in begin, VkSubpassContents.Inline);
            cmd.BindPipeline(VkPipelineBindPoint.Graphics, gpu.Pipeline);
            float w = pass.viewport_width > 0 ? pass.viewport_width : gpu.Width;
            float h = pass.viewport_height > 0 ? pass.viewport_height : gpu.Height;
            // ReShade flips the viewport on Vulkan so effects written for D3D come out the right way up
            var vp = new VkViewport { X = 0f, Y = h, Width = w, Height = -h, MinDepth = 0f, MaxDepth = 1f };
            cmd.SetViewport(0, new ReadOnlySpan<VkViewport>(in vp));
            var scissor = new VkRect2D(new VkExtent2D(gpu.Width, gpu.Height));
            cmd.SetScissor(0, new ReadOnlySpan<VkRect2D>(in scissor));
            cmd.BindDescriptorSet(VkPipelineBindPoint.Graphics, pipelineLayout, 0, uniformSet, default(Span<ByteSize32>));
            cmd.BindDescriptorSet(VkPipelineBindPoint.Graphics, pipelineLayout, 1, samplerSet, default(Span<ByteSize32>));
            cmd.BindDescriptorSet(VkPipelineBindPoint.Graphics, pipelineLayout, 2, storageSet, default(Span<ByteSize32>));
            cmd.Draw(Math.Max(1, pass.num_vertices), 1, 0, 0);
            cmd.EndRenderPass();

            if (pass.generate_mipmaps)
            {
                foreach (FxTexture t in gpu.Targets)
                {
                    GpuUtil.GenerateMips(cmd, t);
                }
            }
        }
    }

    // GPU objects are destroyed a few frames later, the frames in flight may still use them
    // like ReShade: the written textures go to General, dispatch(DispatchSizeX, Y, Z), back to readable, then mipmaps
    private void RecordCompute(CommandBuffer cmd, PassGpu gpu, VkDescriptorSet uniforms, VkDescriptorSet samplerSet, VkDescriptorSet storageSet)
    {
        FxPass pass = gpu.Pass;
        List<FxBinding> bindings = pass.storage_bindings;
        if (bindings.Count > 0)
        {
            VkDescriptorImageInfo* images = stackalloc VkDescriptorImageInfo[bindings.Count];
            VkWriteDescriptorSet* writes = stackalloc VkWriteDescriptorSet[bindings.Count];
            int n = 0;
            foreach (FxBinding b in bindings)
            {
                FxStorage st = Module.storages[b.index];
                if (!textures.TryGetValue(st.texture_name, out FxTexture tex))
                {
                    continue;
                }
                images[n] = new VkDescriptorImageInfo { ImageView = tex.StorageView(st.level), ImageLayout = VkImageLayout.General };
                writes[n] = new VkWriteDescriptorSet
                {
                    DstSet = storageSet,
                    DstBinding = b.binding,
                    DescriptorCount = 1,
                    DescriptorType = VkDescriptorType.StorageImage,
                    ImageInfo = images + n
                };
                n++;
            }
            device.UpdateDescriptorSets(new ReadOnlySpan<VkWriteDescriptorSet>(writes, n), default(ReadOnlySpan<VkCopyDescriptorSet>));
        }

        GpuUtil.ComputeMemoryBarrier(cmd);
        foreach (FxTexture t in gpu.StorageTargets)
        {
            GpuUtil.ForCompute(cmd, t, enter: true);
        }
        cmd.BindPipeline(VkPipelineBindPoint.Compute, gpu.Pipeline);
        cmd.BindDescriptorSet(VkPipelineBindPoint.Compute, pipelineLayout, 0, uniforms, default(Span<ByteSize32>));
        cmd.BindDescriptorSet(VkPipelineBindPoint.Compute, pipelineLayout, 1, samplerSet, default(Span<ByteSize32>));
        cmd.BindDescriptorSet(VkPipelineBindPoint.Compute, pipelineLayout, 2, storageSet, default(Span<ByteSize32>));
        cmd.Dispatch(Math.Max(1, pass.viewport_width), Math.Max(1, pass.viewport_height), Math.Max(1, pass.viewport_dispatch_z));
        foreach (FxTexture t in gpu.StorageTargets)
        {
            GpuUtil.ForCompute(cmd, t, enter: false);
        }
        if (pass.generate_mipmaps)
        {
            foreach (FxTexture t in gpu.StorageTargets)
            {
                GpuUtil.GenerateMips(cmd, t);
            }
        }
    }

    // render thread: frees the GPU side, e.g. when no technique of this effect is enabled anymore
    public void ReleaseGpu() => DestroyGpu();

    private void DestroyGpu()
    {
        builtFor = null;
        built = null;
        preparedFrame = -1;
        if (device == null)
        {
            return;
        }
        DeviceEx d = device;
        foreach (FxTexture t in textures.Values)
        {
            t.Dispose();
        }
        textures.Clear();
        uploads.Clear();
        stencil?.Dispose();
        stencil = null;
        PassGpu[] oldPasses = passes.ToArray();
        passes.Clear();
        VkSampler[] oldSamplers = samplers;
        samplers = Array.Empty<VkSampler>();
        VkDescriptorPool[] oldPools = pools;
        pools = Array.Empty<VkDescriptorPool>();
        VkPipelineLayout oldLayout = pipelineLayout;
        VkDescriptorSetLayout l0 = uniformLayout, l1 = samplerLayout, l2 = storageLayout;
        pipelineLayout = default;
        bool hadUniforms = uniformMapped != null;
        BufferAllocationInfo oldBuffer = uniformBuffer;
        AllocationMapInfo oldMap = uniformMap;
        VmaAllocator allocator = graphicsDevice.Allocator;
        uniformMapped = null;

        GpuGarbage.Add(() =>
        {
            foreach (PassGpu p in oldPasses)
            {
                d.DestroyPipeline(p.Pipeline, d.VkAllocator);
                if (p.IsCompute)
                {
                    d.DestroyShaderModule(p.Cs, d.VkAllocator);
                    continue;
                }
                d.DestroyShaderModule(p.Vs, d.VkAllocator);
                d.DestroyShaderModule(p.Ps, d.VkAllocator);
                d.DestroyFramebuffer(p.Framebuffer, d.VkAllocator);
                d.DestroyRenderPass(p.LoadPass, d.VkAllocator);
                d.DestroyRenderPass(p.ClearPass, d.VkAllocator);
            }
            foreach (VkSampler s in oldSamplers)
            {
                d.DestroySampler(s, d.VkAllocator);
            }
            foreach (VkDescriptorPool pool in oldPools)
            {
                d.DestroyDescriptorPool(pool, d.VkAllocator);
            }
            if (hadUniforms)
            {
                allocator.UnmapMemory(in oldMap);
                allocator.FreeAllocation(in oldBuffer);
            }
            if (!oldLayout.IsNull())
            {
                d.DestroyPipelineLayout(oldLayout, d.VkAllocator);
                d.DestroyDescriptorSetLayout(l0, d.VkAllocator);
                d.DestroyDescriptorSetLayout(l1, d.VkAllocator);
                d.DestroyDescriptorSetLayout(l2, d.VkAllocator);
            }
        });
    }

    public void Dispose()
    {
        DestroyGpu();
    }
}
