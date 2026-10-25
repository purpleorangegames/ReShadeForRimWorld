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

// what an effect reads and writes besides its own textures, for one frame.
// The back buffer ping-pongs between A and B: a pass reads Current and draws into the other one.
// Current can also start out as an image that isn't ours (the scene), that one is only ever read.
public sealed class FrameTargets
{
    public FxTexture A, B;
    public FxTexture Current;
    public FxTexture StandIn;
    public FxTexture StandIn1D, StandIn3D;
    public VkImageView Depth;
    public bool HasDepth;
    public FxTexture Spare => Current == A ? B : A;
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
        public bool PerformanceMode;
        public List<string> Included = new();
        // some pass samples the depth buffer, so the scene's depth has to be made readable
        public bool UsesDepth;
        // some pass samples the back buffer through an sRGB view, which the game's scene image doesn't have
        public bool ReadsColorAsSrgb;
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
    private readonly Dictionary<string, PassGpu[]> techniquePasses = new();
    private FxTexture stencil;
    // uniforms ReShade fills in ("timer", "random", ...) and the ones set from the overlay, sorted out once per build
    private SourceUniform[] sourceUniforms = Array.Empty<SourceUniform>();
    private FxUniform[] valueUniforms = Array.Empty<FxUniform>();
    // performance mode: the variables' values as specialization constants, and the values they came from
    private uint[] specData = Array.Empty<uint>();
    private Dictionary<string, float[]> specFrom;

    private enum UniformSource : byte { Timer, FrameTime, FrameCount, Random, Date, DepthReady, OverlayOpen, Other }

    private struct SourceUniform
    {
        public FxUniform U;
        public UniformSource Kind;
        public int Min, Max;
    }

    private enum SlotKind : byte { BackBuffer, Depth, Own, StandIn }

    // one sampler binding of a pass, worked out at build time
    private struct SamplerSlot
    {
        public int Binding;
        public VkSampler Sampler;
        public SlotKind Kind;
        public bool Srgb;
        public FxTexture Own;
        public FxTextureInfo Info;
    }

    private sealed class PassGpu
    {
        public string Technique;
        public FxPass Pass;
        public VkRenderPass LoadPass, ClearPass, DontCarePass;
        public VkFramebuffer Framebuffer;
        // passes drawing on the back buffer get one framebuffer for each of the two ping-pong textures
        public VkFramebuffer FramebufferA, FramebufferB;
        public VkPipeline Pipeline;
        public VkShaderModule Vs, Ps;
        public FxTexture[] Targets;
        public bool ToBackBuffer;
        public bool UsesStencil, ClearsStencil;
        // some pixels are left as they were (blending, a write mask, discard, a smaller viewport...),
        // so the target has to hold the old image; otherwise it is drawn over completely
        public bool KeepsContent;
        public int Width, Height;
        public SamplerSlot[] Slots = Array.Empty<SamplerSlot>();
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

    // any thread: preprocesses and compiles the file with ReShade's compiler, or takes it from the cache
    public static CompileResult Compile(string filePath, int width, int height, IList<string> includePaths, IList<string> userDefines, string defineKey, bool performanceMode)
    {
        var result = new CompileResult { Width = width, Height = height, Defines = defineKey, PerformanceMode = performanceMode };
        var defines = new List<string>
        {
            "__RESHADE__=60500",
            "__RESHADE_PERMUTATION__=0",
            "__RESHADE_PERFORMANCE_MODE__=" + (performanceMode ? "1" : "0"),
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
        if (CompileCache.TryLoad(filePath, width, height, defineKey, performanceMode, out string cachedJson, out var cachedSpirv, out var cachedIncluded, out string cachedErrors))
        {
            result.Module = FxModule.Parse(cachedJson);
            result.Spirv = cachedSpirv;
            result.Included = cachedIncluded;
            result.Errors = cachedErrors;
            result.Success = true;
            result.UsesDepth = SamplesDepth(result.Module);
            result.ReadsColorAsSrgb = SamplesColorAsSrgb(result.Module);
            return result;
        }
        FxModule module = null;
        NativeFx.Result compiled = NativeFx.Compile(filePath, includePaths, defines, performanceMode, json =>
        {
            module = FxModule.Parse(json);
            return module.entry_points.Select(e => e.name);
        });
        result.Included = compiled.Included;
        if (!compiled.Success || module == null)
        {
            result.Errors = string.IsNullOrEmpty(compiled.Errors) ? "unknown compile error" : compiled.Errors;
            return result;
        }
        result.Module = module;
        result.Spirv = compiled.Spirv;
        result.Errors = compiled.Errors ?? "";
        result.Success = module.entry_points.All(e => result.Spirv.ContainsKey(e.name));
        if (!result.Success && result.Errors.Length == 0)
        {
            result.Errors = "missing SPIR-V for an entry point";
        }
        if (result.Success)
        {
            result.UsesDepth = SamplesDepth(module);
            result.ReadsColorAsSrgb = SamplesColorAsSrgb(module);
            CompileCache.Save(filePath, width, height, defineKey, performanceMode, compiled.ModuleJson, result.Spirv, result.Included, result.Errors);
        }
        return result;
    }

    // the compiler lists per pass the samplers its shaders really use
    private static bool SamplesDepth(FxModule m) => AnySampler(m, (s, t) => t != null && IsDepth(t));

    private static bool SamplesColorAsSrgb(FxModule m) => AnySampler(m, (s, t) => s.srgb && (t == null || IsBackBuffer(t)));

    private static bool AnySampler(FxModule m, Func<FxSampler, FxTextureInfo, bool> test)
    {
        foreach (FxPass pass in m.techniques.SelectMany(t => t.passes))
        {
            foreach (FxBinding b in pass.sampler_bindings)
            {
                FxSampler s = m.samplers[b.index];
                if (test(s, m.textures.FirstOrDefault(t => t.unique_name == s.texture_name)))
                {
                    return true;
                }
            }
        }
        return false;
    }

    private static bool IsBackBuffer(FxTextureInfo t) => t.semantic == "COLOR" || t.semantic == "SV_TARGET";
    private static bool IsDepth(FxTextureInfo t) => t.semantic == "DEPTH" || t.semantic == "SV_DEPTH";

    private FxTextureInfo TextureByName(string uniqueName) => Module.textures.FirstOrDefault(t => t.unique_name == uniqueName);

    private void Build(in FrameContext ctx, CompileResult result, FrameTargets io, IList<string> texturePaths, Action<string> log)
    {
        DestroyGpu();
        built = result;
        Module = result.Module;
        spirv = result.Spirv;
        builtFor = io.A;
        graphicsDevice = ctx.GraphicsDevice;
        device = ctx.Device;
        int framesInFlight = ctx.RenderContext.FramesInFlight;
        FxTexture backBuffer = io.A;

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
        specFrom = Values;
        specData = SpecData(specFrom);

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
                    gpu.Slots = BuildSlots(pass, gpu.Targets);
                    passes.Add(gpu);
                    continue;
                }
                var names = pass.render_targets.Where(n => !string.IsNullOrEmpty(n)).ToList();
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
                VkFormat[] formats = gpu.Targets.Select(t => t.TargetFormat(pass.srgb_write_enable)).ToArray();
                gpu.Width = gpu.Targets[0].Width;
                gpu.Height = gpu.Targets[0].Height;
                // ReShade's stencil buffer matches the back buffer, passes drawing at another size go without
                if (pass.stencil_enable && gpu.Width == backBuffer.Width && gpu.Height == backBuffer.Height)
                {
                    stencil ??= new FxTexture(graphicsDevice, backBuffer.Width, backBuffer.Height, 1, VkFormat.D32SFloatS8UInt, Name + ".Stencil", depthStencil: true);
                    gpu.UsesStencil = true;
                    gpu.ClearsStencil = !stencilClearedInTechnique;
                    stencilClearedInTechnique = true;
                }
                else if (pass.stencil_enable)
                {
                    log($"{Name}: pass {pass.name} uses stencil at a size other than the screen, stencil is skipped");
                }
                VkFormat stencilFormat = gpu.UsesStencil ? stencil.Format : VkFormat.Undefined;
                gpu.LoadPass = GpuUtil.CreateRenderPass(device, formats, clear: false, stencilFormat);
                gpu.ClearPass = GpuUtil.CreateRenderPass(device, formats, clear: true, stencilFormat);
                gpu.DontCarePass = GpuUtil.CreateRenderPass(device, formats, clear: false, stencilFormat, dontCare: true);
                VkImageView[] Views(FxTexture[] targets)
                {
                    var views = targets.Select(t => t.TargetView(pass.srgb_write_enable));
                    return (gpu.UsesStencil ? views.Append(stencil.View(false)) : views).ToArray();
                }
                if (gpu.ToBackBuffer)
                {
                    gpu.FramebufferA = GpuUtil.CreateFramebuffer(device, gpu.LoadPass, Views(new[] { io.A }), gpu.Width, gpu.Height);
                    gpu.FramebufferB = GpuUtil.CreateFramebuffer(device, gpu.LoadPass, Views(new[] { io.B }), gpu.Width, gpu.Height);
                }
                else
                {
                    gpu.Framebuffer = GpuUtil.CreateFramebuffer(device, gpu.LoadPass, Views(gpu.Targets), gpu.Width, gpu.Height);
                }
                gpu.Vs = RuntimeShaderCompiler.CreateShaderModule(device, spirv[pass.vs], Name + "." + pass.vs);
                gpu.Ps = RuntimeShaderCompiler.CreateShaderModule(device, spirv[pass.ps], Name + "." + pass.ps);
                gpu.Pipeline = CreatePipeline(gpu);
                gpu.KeepsContent = KeepsContent(gpu, spirv[pass.ps]);
                gpu.Slots = BuildSlots(pass, gpu.ToBackBuffer ? Array.Empty<FxTexture>() : gpu.Targets);
                passes.Add(gpu);
            }
        }
        foreach (var group in passes.GroupBy(p => p.Technique))
        {
            techniquePasses[group.Key] = group.ToArray();
        }

        // defaults from the effect source, sources like "timer" get written every frame
        uniformData = new byte[Math.Max(16, (Module.total_uniform_size + 15) & ~15)];
        var sources = new List<SourceUniform>();
        var values = new List<FxUniform>();
        foreach (FxUniform u in Module.uniforms)
        {
            if (u.has_initializer)
            {
                WriteConstant(u, u.initializer);
            }
            string source = u.annotations.GetString("source");
            if (source == null)
            {
                if (u.type.array_length == 0)
                {
                    values.Add(u);
                }
                continue;
            }
            sources.Add(new SourceUniform
            {
                U = u,
                Kind = source switch
                {
                    "timer" => UniformSource.Timer,
                    "frametime" => UniformSource.FrameTime,
                    "framecount" => UniformSource.FrameCount,
                    "random" => UniformSource.Random,
                    "date" => UniformSource.Date,
                    "bufready_depth" => UniformSource.DepthReady,
                    "overlay_open" => UniformSource.OverlayOpen,
                    _ => UniformSource.Other
                },
                Min = u.annotations.Find("min")?.value.i[0] ?? 0,
                Max = u.annotations.Find("max")?.value.i[0] ?? 32767
            });
        }
        sourceUniforms = sources.Where(x => x.Kind != UniformSource.Other).ToArray();
        valueUniforms = values.ToArray();
    }

    // what each sampler of the pass reads; a texture the pass draws to can't be read at the same time, ReShade unbinds it too
    private SamplerSlot[] BuildSlots(FxPass pass, FxTexture[] targets)
    {
        var slots = new SamplerSlot[pass.sampler_bindings.Count];
        for (int i = 0; i < slots.Length; i++)
        {
            FxBinding b = pass.sampler_bindings[i];
            FxSampler s = Module.samplers[b.index];
            FxTextureInfo tex = TextureByName(s.texture_name);
            var slot = new SamplerSlot { Binding = b.binding, Sampler = samplers[b.index], Srgb = s.srgb, Info = tex };
            if (tex == null || IsBackBuffer(tex))
            {
                slot.Kind = SlotKind.BackBuffer;
            }
            else if (IsDepth(tex))
            {
                slot.Kind = SlotKind.Depth;
            }
            else if (textures.TryGetValue(tex.unique_name, out FxTexture own) && Array.IndexOf(targets, own) < 0)
            {
                slot.Kind = SlotKind.Own;
                slot.Own = own;
            }
            else
            {
                slot.Kind = SlotKind.StandIn;
            }
            slots[i] = slot;
        }
        return slots;
    }

    // true when the pass may leave pixels of its targets untouched, so it can't start from an undefined image
    private static bool KeepsContent(PassGpu gpu, byte[] ps)
    {
        FxPass pass = gpu.Pass;
        if (pass.clear_render_targets)
        {
            return false;
        }
        // with several targets the shader may not write all of them
        if (gpu.UsesStencil || pass.stencil_enable || gpu.Targets.Length > 1)
        {
            return true;
        }
        if ((pass.viewport_width > 0 && pass.viewport_width != gpu.Width) || (pass.viewport_height > 0 && pass.viewport_height != gpu.Height))
        {
            return true;
        }
        // ReShade.fxh's full screen triangle; other vertex shaders may draw anywhere
        if (pass.vs != "E__PostProcessVS" || pass.num_vertices != 3 || pass.topology != 4)
        {
            return true;
        }
        for (int i = 0; i < gpu.Targets.Length; i++)
        {
            FxBlend b = pass.blend[i];
            if (b.enable || (b.write_mask & 0xF) != 0xF)
            {
                return true;
            }
        }
        return Discards(ps);
    }

    // OpKill, OpTerminateInvocation or OpDemoteToHelperInvocation anywhere in the pixel shader
    private static bool Discards(byte[] spv)
    {
        int words = spv.Length / 4;
        for (int i = 5; i < words;)
        {
            uint w = BitConverter.ToUInt32(spv, i * 4);
            uint op = w & 0xFFFF;
            if (op == 252 || op == 4416 || op == 5380)
            {
                return true;
            }
            i += Math.Max(1, (int)(w >> 16));
        }
        return false;
    }

    // one 32 bit value per specialization constant, from the overlay's values or the effect's default
    private uint[] SpecData(Dictionary<string, float[]> values)
    {
        List<FxUniform> constants = Module.spec_constants;
        if (constants == null || constants.Count == 0)
        {
            return Array.Empty<uint>();
        }
        var data = new uint[constants.Count];
        for (int i = 0; i < data.Length; i++)
        {
            FxUniform c = constants[i];
            int baseType = c.type.base_;
            if (c.type.array_length == 0 && values != null && values.TryGetValue(c.name, out float[] v) && c.offset < v.Length)
            {
                float x = v[c.offset];
                data[i] = baseType == FxType.Float ? BitConverter.SingleToUInt32Bits(x)
                    : baseType == FxType.Bool ? (x != 0 ? 1u : 0u)
                    : baseType == FxType.Int ? unchecked((uint)(int)x)
                    : (uint)x;
            }
            else
            {
                data[i] = baseType == FxType.Float ? BitConverter.SingleToUInt32Bits(c.initializer?.f?.FirstOrDefault() ?? 0f) : unchecked((uint)(c.initializer?.i?.FirstOrDefault() ?? 0));
            }
        }
        return data;
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
        int specCount = specData.Length;
        VkSpecializationMapEntry* specEntries = stackalloc VkSpecializationMapEntry[Math.Max(1, specCount)];
        VkSpecializationInfo spec = Specialization(specEntries);
        fixed (byte* vsNamePtr = vsName, psNamePtr = psName)
        fixed (uint* specValues = specData)
        {
            spec.Data = specValues;
            VkPipelineShaderStageCreateInfo* stages = stackalloc VkPipelineShaderStageCreateInfo[2];
            stages[0] = new VkPipelineShaderStageCreateInfo { Stage = VkShaderStageFlags.VertexBit, Module = gpu.Vs, Name = vsNamePtr, SpecializationInfo = specCount > 0 ? &spec : null };
            stages[1] = new VkPipelineShaderStageCreateInfo { Stage = VkShaderStageFlags.FragmentBit, Module = gpu.Ps, Name = psNamePtr, SpecializationInfo = specCount > 0 ? &spec : null };

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
        int specCount = specData.Length;
        VkSpecializationMapEntry* specEntries = stackalloc VkSpecializationMapEntry[Math.Max(1, specCount)];
        VkSpecializationInfo spec = Specialization(specEntries);
        fixed (byte* csNamePtr = csName)
        fixed (uint* specValues = specData)
        {
            spec.Data = specValues;
            var info = new VkComputePipelineCreateInfo();
            info.Stage = new VkPipelineShaderStageCreateInfo { Stage = VkShaderStageFlags.ComputeBit, Module = gpu.Cs, Name = csNamePtr, SpecializationInfo = specCount > 0 ? &spec : null };
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

    // SpecId i is the i-th specialization constant, one 32 bit value each (Data is set by the caller)
    private VkSpecializationInfo Specialization(VkSpecializationMapEntry* entries)
    {
        for (int i = 0; i < specData.Length; i++)
        {
            entries[i] = new VkSpecializationMapEntry { ConstantID = i, Offset = ByteSize.Of(i * 4), Size = (ByteSize64)4 };
        }
        return new VkSpecializationInfo { MapEntryCount = specData.Length, MapEntries = entries, DataSize = (ByteSize64)(specData.Length * 4) };
    }

    // performance mode: new values mean new pipelines, the old ones go away once the GPU is done with them
    private void RebuildPipelines()
    {
        DeviceEx d = device;
        VkPipeline[] old = passes.Select(p => p.Pipeline).ToArray();
        GpuGarbage.Add(() =>
        {
            foreach (VkPipeline p in old)
            {
                d.DestroyPipeline(p, d.VkAllocator);
            }
        });
        foreach (PassGpu gpu in passes)
        {
            gpu.Pipeline = gpu.IsCompute ? CreateComputePipeline(gpu) : CreatePipeline(gpu);
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

    private void WriteScalar(FxUniform u, int index, float value)
    {
        int at = u.offset + index * 4;
        if (index * 4 + 4 > u.size || at + 4 > uniformData.Length)
        {
            return;
        }
        Span<byte> dst = uniformData.AsSpan(at);
        if (u.type.base_ == FxType.Float)
        {
            MemoryMarshal.Write(dst, value);
        }
        else if (u.type.base_ == FxType.Bool)
        {
            MemoryMarshal.Write(dst, value != 0 ? 1 : 0);
        }
        else
        {
            MemoryMarshal.Write(dst, (int)value);
        }
    }

    // the "source" annotation values ReShade fills in for effects, then the overlay's values
    private void UpdateSourceUniforms(in FrameData frame, bool hasDepth)
    {
        Dictionary<string, float[]> values = Values;
        foreach (FxUniform u in valueUniforms)
        {
            if (values.TryGetValue(u.name, out float[] v))
            {
                for (int i = 0; i < v.Length; i++)
                {
                    WriteScalar(u, i, v[i]);
                }
            }
        }
        foreach (SourceUniform x in sourceUniforms)
        {
            FxUniform u = x.U;
            switch (x.Kind)
            {
                case UniformSource.Timer:
                    WriteScalar(u, 0, frame.TimerMs);
                    break;
                case UniformSource.FrameTime:
                    WriteScalar(u, 0, frame.FrameTimeMs);
                    break;
                case UniformSource.FrameCount:
                    WriteScalar(u, 0, frame.FrameCount);
                    break;
                case UniformSource.Random:
                    WriteScalar(u, 0, x.Min + (x.Max > x.Min ? frame.Random % (x.Max - x.Min + 1) : 0));
                    break;
                case UniformSource.Date:
                    WriteScalar(u, 0, frame.Year);
                    WriteScalar(u, 1, frame.Month);
                    WriteScalar(u, 2, frame.Day);
                    WriteScalar(u, 3, frame.Seconds);
                    break;
                case UniformSource.DepthReady:
                    WriteScalar(u, 0, hasDepth ? 1 : 0);
                    break;
                case UniformSource.OverlayOpen:
                    WriteScalar(u, 0, 0);
                    break;
            }
        }
    }

    // render thread, once per frame before any of this effect's techniques: GPU objects, uploads, uniforms
    public bool Prepare(in FrameContext ctx, in FrameData frame, FrameTargets io, IList<string> texturePaths, Action<string> log)
    {
        CompileResult result = Result;
        if (result == null || !result.Success)
        {
            return false;
        }
        if (built != result || builtFor != io.A)
        {
            Build(in ctx, result, io, texturePaths, log);
        }
        if (preparedFrame == frame.FrameCount)
        {
            return true;
        }
        preparedFrame = frame.FrameCount;
        CommandBuffer cmd = ctx.CommandBuffer;
        int frameIndex = ctx.SubmissionFrameIndex;

        // values are replaced as a whole when one changes, so a different dictionary means something changed
        Dictionary<string, float[]> current = Values;
        if (specData.Length > 0 && current != specFrom)
        {
            specFrom = current;
            uint[] data = SpecData(current);
            if (!data.AsSpan().SequenceEqual(specData))
            {
                specData = data;
                RebuildPipelines();
            }
        }

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

        UpdateSourceUniforms(in frame, io.HasDepth);
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
    private static VkImageView StandInView(FrameTargets io, FxTextureInfo t)
    {
        FxTexture standIn = t?.type == 1 ? io.StandIn1D : t?.type == 3 ? io.StandIn3D : io.StandIn;
        return standIn.View(false);
    }

    public bool HasTechnique(string name) => Module != null && Module.techniques.Any(t => t.name == name);

    // render thread, after Prepare
    public void RecordTechnique(in FrameContext ctx, FrameTargets io, string technique)
    {
        if (!techniquePasses.TryGetValue(technique, out PassGpu[] list))
        {
            return;
        }
        CommandBuffer cmd = ctx.CommandBuffer;
        VkDescriptorPool pool = pools[ctx.SubmissionFrameIndex];

        foreach (PassGpu gpu in list)
        {
            // a pass drawing on the back buffer reads the current image and draws into the other one
            FxTexture colorSource = io.Current;
            FxTexture target = gpu.ToBackBuffer ? io.Spare : null;
            if (gpu.ToBackBuffer && gpu.KeepsContent)
            {
                GpuUtil.Copy(cmd, colorSource.Image, target.Image, colorSource.Width, colorSource.Height);
            }

            VkDescriptorSet samplerSet = DescriptorSetWriter.AllocateDescriptorSet(device, pool, samplerLayout);
            SamplerSlot[] slots = gpu.Slots;
            int n = slots.Length;
            if (n > 0)
            {
                VkDescriptorImageInfo* images = stackalloc VkDescriptorImageInfo[n];
                VkWriteDescriptorSet* writes = stackalloc VkWriteDescriptorSet[n];
                for (int i = 0; i < n; i++)
                {
                    ref SamplerSlot slot = ref slots[i];
                    VkImageView view;
                    VkImageLayout layout = VkImageLayout.ShaderReadOnlyOptimal;
                    switch (slot.Kind)
                    {
                        case SlotKind.BackBuffer:
                            view = colorSource.View(slot.Srgb);
                            break;
                        case SlotKind.Depth when !io.Depth.IsNull():
                            view = io.Depth;
                            layout = VkImageLayout.DepthStencilReadOnlyOptimal;
                            break;
                        case SlotKind.Own:
                            view = slot.Own.View(slot.Srgb);
                            break;
                        default:
                            view = StandInView(io, slot.Info);
                            break;
                    }
                    images[i] = new VkDescriptorImageInfo { Sampler = slot.Sampler, ImageView = view, ImageLayout = layout };
                    writes[i] = new VkWriteDescriptorSet
                    {
                        DstSet = samplerSet,
                        DstBinding = slot.Binding,
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
                RenderPass = clear ? gpu.ClearPass : gpu.KeepsContent ? gpu.LoadPass : gpu.DontCarePass,
                Framebuffer = !gpu.ToBackBuffer ? gpu.Framebuffer : target == io.A ? gpu.FramebufferA : gpu.FramebufferB,
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

            if (gpu.ToBackBuffer)
            {
                io.Current = target;
            }
            else if (pass.generate_mipmaps)
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
        techniquePasses.Clear();
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
                foreach (VkFramebuffer fb in new[] { p.Framebuffer, p.FramebufferA, p.FramebufferB })
                {
                    if (!fb.IsNull())
                    {
                        d.DestroyFramebuffer(fb, d.VkAllocator);
                    }
                }
                d.DestroyRenderPass(p.LoadPass, d.VkAllocator);
                d.DestroyRenderPass(p.ClearPass, d.VkAllocator);
                d.DestroyRenderPass(p.DontCarePass, d.VkAllocator);
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
