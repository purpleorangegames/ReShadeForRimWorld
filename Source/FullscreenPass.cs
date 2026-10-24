using System;
using Brutal;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using Waylith.Graphics;
using Waylith.Rendering;
using Waylith.Rendering.PostFx;

namespace ReShadeForRimWorld;

// a full screen triangle with a small GLSL fragment shader, used to put the UI layer together.
// Inputs are sampled at binding 0..n-1, one int push constant is available as "mode".
public sealed unsafe class FullscreenPass : IDisposable
{
    private readonly string name;
    private readonly string fragmentSource;
    private readonly int inputs;
    private DeviceEx device;
    private VkShaderModule vs, fs;
    private VkDescriptorSetLayout setLayout;
    private VkPipelineLayout pipelineLayout;
    private VkRenderPass renderPass;
    private VkPipeline pipeline;
    private VkSampler sampler;
    private VkDescriptorPool[] pools = Array.Empty<VkDescriptorPool>();
    private VkFormat format;

    public FullscreenPass(string name, int inputs, string fragmentSource)
    {
        this.name = name;
        this.inputs = inputs;
        this.fragmentSource = fragmentSource;
    }

    private void Build(in FrameContext ctx, VkFormat targetFormat)
    {
        Destroy();
        device = ctx.Device;
        format = targetFormat;
        vs = PostFxPassHelpers.CompileFullscreenVertex(device);
        fs = PostFxPassHelpers.Compile(device, fragmentSource, RuntimeShaderStage.Fragment, "ReShade." + name + ".frag");
        var bindings = new VkDescriptorSetLayoutBinding[inputs];
        for (int i = 0; i < inputs; i++)
        {
            bindings[i] = new VkDescriptorSetLayoutBinding { Binding = i, DescriptorType = VkDescriptorType.CombinedImageSampler, DescriptorCount = 1, StageFlags = VkShaderStageFlags.FragmentBit };
        }
        fixed (VkDescriptorSetLayoutBinding* p = bindings)
        {
            setLayout = device.CreateDescriptorSetLayout(new DescriptorSetLayoutEx.CreateInfo { Bindings = new Span<VkDescriptorSetLayoutBinding>(p, inputs) }, device.VkAllocator);
        }
        var push = new VkPushConstantRange();
        push.StageFlags = VkShaderStageFlags.FragmentBit;
        push.Offset = ByteSize.Zero;
        push.Size = ByteSize.Of(4);
        VkDescriptorSetLayout layout = setLayout;
        pipelineLayout = device.CreatePipelineLayout(new ReadOnlySpan<VkDescriptorSetLayout>(in layout), new ReadOnlySpan<VkPushConstantRange>(in push), device.VkAllocator);
        renderPass = GpuUtil.CreateRenderPass(device, new[] { targetFormat }, clear: false);
        pipeline = PostFxPassHelpers.CreateFullscreenPipeline(device, renderPass, VkSampleCountFlags._1Bit, vs, fs, pipelineLayout, BlendState.Opaque, "ReShade." + name);
        var info = new VkSamplerCreateInfo();
        info.MinFilter = VkFilter.Nearest;
        info.MagFilter = VkFilter.Nearest;
        info.MipmapMode = VkSamplerMipmapMode.Nearest;
        info.AddressModeU = VkSamplerAddressMode.ClampToEdge;
        info.AddressModeV = VkSamplerAddressMode.ClampToEdge;
        info.AddressModeW = VkSamplerAddressMode.ClampToEdge;
        info.MaxLod = 0f;
        sampler = device.CreateSampler(in info, device.VkAllocator);
        pools = new VkDescriptorPool[ctx.RenderContext.FramesInFlight];
        var size = new VkDescriptorPoolSize { Type = VkDescriptorType.CombinedImageSampler, DescriptorCount = Math.Max(1, inputs) };
        for (int i = 0; i < pools.Length; i++)
        {
            pools[i] = device.CreateDescriptorPool(new VkDescriptorPoolCreateInfo { MaxSets = 1, PoolSizeCount = 1, PoolSizes = &size }, device.VkAllocator);
        }
    }

    // draws into target (kept in ShaderReadOnlyOptimal), sampling the inputs
    public void Record(in FrameContext ctx, FxTexture target, ReadOnlySpan<FxTexture> sources, int mode)
    {
        if (device == null || format != target.Format)
        {
            Build(in ctx, target.Format);
        }
        CommandBuffer cmd = ctx.CommandBuffer;
        VkDescriptorPool pool = pools[ctx.SubmissionFrameIndex];
        device.ResetDescriptorPool(pool, 0);
        VkDescriptorSet set = DescriptorSetWriter.AllocateDescriptorSet(device, pool, setLayout);
        VkDescriptorImageInfo* images = stackalloc VkDescriptorImageInfo[inputs];
        VkWriteDescriptorSet* writes = stackalloc VkWriteDescriptorSet[inputs];
        for (int i = 0; i < inputs; i++)
        {
            images[i] = new VkDescriptorImageInfo { Sampler = sampler, ImageView = sources[i].View(false), ImageLayout = VkImageLayout.ShaderReadOnlyOptimal };
            writes[i] = new VkWriteDescriptorSet { DstSet = set, DstBinding = i, DescriptorCount = 1, DescriptorType = VkDescriptorType.CombinedImageSampler, ImageInfo = images + i };
        }
        device.UpdateDescriptorSets(new ReadOnlySpan<VkWriteDescriptorSet>(writes, inputs), default(ReadOnlySpan<VkCopyDescriptorSet>));

        VkFramebuffer framebuffer = target.Framebuffer(renderPass);
        var begin = new VkRenderPassBeginInfo { RenderPass = renderPass, Framebuffer = framebuffer, RenderArea = new VkRect2D(target.Extent) };
        cmd.BeginRenderPass(in begin, VkSubpassContents.Inline);
        cmd.BindPipeline(VkPipelineBindPoint.Graphics, pipeline);
        var vp = new VkViewport { X = 0, Y = 0, Width = target.Width, Height = target.Height, MinDepth = 0, MaxDepth = 1 };
        cmd.SetViewport(0, new ReadOnlySpan<VkViewport>(in vp));
        var scissor = new VkRect2D(target.Extent);
        cmd.SetScissor(0, new ReadOnlySpan<VkRect2D>(in scissor));
        cmd.BindDescriptorSet(VkPipelineBindPoint.Graphics, pipelineLayout, 0, set, default(Span<ByteSize32>));
        cmd.PushConstants(pipelineLayout, VkShaderStageFlags.FragmentBit, ByteSize.Zero, new ReadOnlySpan<byte>(&mode, 4));
        cmd.Draw(3, 1, 0, 0);
        cmd.EndRenderPass();
    }

    private void Destroy()
    {
        if (device == null)
        {
            return;
        }
        DeviceEx d = device;
        VkPipeline p = pipeline;
        VkPipelineLayout pl = pipelineLayout;
        VkDescriptorSetLayout sl = setLayout;
        VkRenderPass rp = renderPass;
        VkShaderModule v = vs, f = fs;
        VkSampler s = sampler;
        VkDescriptorPool[] old = pools;
        GpuGarbage.Add(() =>
        {
            d.DestroyPipeline(p, d.VkAllocator);
            d.DestroyPipelineLayout(pl, d.VkAllocator);
            d.DestroyDescriptorSetLayout(sl, d.VkAllocator);
            d.DestroyRenderPass(rp, d.VkAllocator);
            d.DestroyShaderModule(v, d.VkAllocator);
            d.DestroyShaderModule(f, d.VkAllocator);
            d.DestroySampler(s, d.VkAllocator);
            foreach (VkDescriptorPool pool in old)
            {
                d.DestroyDescriptorPool(pool, d.VkAllocator);
            }
        });
        device = null;
    }

    public void Dispose() => Destroy();
}
