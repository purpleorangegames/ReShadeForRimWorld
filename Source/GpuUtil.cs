using System;
using Brutal;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using Brutal.VulkanApi.Vma;
using Waylith.Graphics;

namespace ReShadeForRimWorld;

public static class GpuUtil
{
    private const VkAccessFlags AnyRead = VkAccessFlags.ShaderReadBit | VkAccessFlags.ColorAttachmentReadBit;
    private const VkAccessFlags AnyWrite = VkAccessFlags.ColorAttachmentWriteBit | VkAccessFlags.TransferWriteBit | VkAccessFlags.ShaderWriteBit;
    // every stage effects read or write textures in, compute included
    private const VkPipelineStageFlags Graphics = VkPipelineStageFlags.FragmentShaderBit | VkPipelineStageFlags.ColorAttachmentOutputBit | VkPipelineStageFlags.ComputeShaderBit;
    private const VkPipelineStageFlags Tests = VkPipelineStageFlags.EarlyFragmentTestsBit | VkPipelineStageFlags.LateFragmentTestsBit;

    public static VkImageMemoryBarrier Barrier(VkImage image, VkImageLayout from, VkImageLayout to, VkAccessFlags src, VkAccessFlags dst, int baseLevel = 0, int levels = 1, VkImageAspectFlags aspect = VkImageAspectFlags.ColorBit)
    {
        var b = new VkImageMemoryBarrier();
        b.SrcAccessMask = src;
        b.DstAccessMask = dst;
        b.OldLayout = from;
        b.NewLayout = to;
        b.SrcQueueFamilyIndex = -1;
        b.DstQueueFamilyIndex = -1;
        b.Image = image;
        b.SubresourceRange = new VkImageSubresourceRange
        {
            AspectMask = aspect,
            BaseMipLevel = baseLevel,
            LevelCount = levels,
            BaseArrayLayer = 0,
            LayerCount = 1
        };
        return b;
    }

    private static void Barrier(CommandBuffer cmd, VkPipelineStageFlags src, VkPipelineStageFlags dst, in VkImageMemoryBarrier b)
    {
        cmd.PipelineBarrier(src, dst, VkDependencyFlags.None, default, default, new ReadOnlySpan<VkImageMemoryBarrier>(in b));
    }

    // copies mip 0 between two images that sit in ShaderReadOnlyOptimal (a fresh dst may start Undefined)
    public static void Copy(CommandBuffer cmd, VkImage src, VkImage dst, int width, int height, bool dstFresh = false)
    {
        Span<VkImageMemoryBarrier> pre = stackalloc VkImageMemoryBarrier[2];
        pre[0] = Barrier(src, VkImageLayout.ShaderReadOnlyOptimal, VkImageLayout.TransferSrcOptimal, AnyWrite, VkAccessFlags.TransferReadBit);
        pre[1] = Barrier(dst, dstFresh ? VkImageLayout.Undefined : VkImageLayout.ShaderReadOnlyOptimal, VkImageLayout.TransferDstOptimal, AnyRead | AnyWrite, VkAccessFlags.TransferWriteBit);
        cmd.PipelineBarrier(Graphics | VkPipelineStageFlags.TransferBit, VkPipelineStageFlags.TransferBit, VkDependencyFlags.None, default, default, pre);

        var layers = new VkImageSubresourceLayers();
        layers.AspectMask = VkImageAspectFlags.ColorBit;
        layers.MipLevel = 0;
        layers.BaseArrayLayer = 0;
        layers.LayerCount = 1;
        var region = new VkImageCopy();
        region.SrcSubresource = layers;
        region.DstSubresource = layers;
        region.Extent = new VkExtent3D(width, height, 1);
        cmd.CopyImage(src, VkImageLayout.TransferSrcOptimal, dst, VkImageLayout.TransferDstOptimal, new ReadOnlySpan<VkImageCopy>(in region));

        Span<VkImageMemoryBarrier> post = stackalloc VkImageMemoryBarrier[2];
        post[0] = Barrier(src, VkImageLayout.TransferSrcOptimal, VkImageLayout.ShaderReadOnlyOptimal, VkAccessFlags.TransferReadBit, AnyRead | AnyWrite);
        post[1] = Barrier(dst, VkImageLayout.TransferDstOptimal, VkImageLayout.ShaderReadOnlyOptimal, VkAccessFlags.TransferWriteBit, AnyRead | AnyWrite);
        cmd.PipelineBarrier(VkPipelineStageFlags.TransferBit, Graphics | VkPipelineStageFlags.TransferBit, VkDependencyFlags.None, default, default, post);
    }

    private static VkImageCopy FullCopy(int width, int height)
    {
        var layers = new VkImageSubresourceLayers();
        layers.AspectMask = VkImageAspectFlags.ColorBit;
        layers.MipLevel = 0;
        layers.BaseArrayLayer = 0;
        layers.LayerCount = 1;
        var region = new VkImageCopy();
        region.SrcSubresource = layers;
        region.DstSubresource = layers;
        region.Extent = new VkExtent3D(width, height, 1);
        return region;
    }

    // src (the screen) is already in TransferSrcOptimal, dst is one of our textures
    public static void CopyFromTransferSrc(CommandBuffer cmd, VkImage src, VkImage dst, int width, int height)
    {
        VkImageMemoryBarrier pre = Barrier(dst, VkImageLayout.ShaderReadOnlyOptimal, VkImageLayout.TransferDstOptimal, AnyRead | AnyWrite, VkAccessFlags.TransferWriteBit);
        cmd.PipelineBarrier(Graphics | VkPipelineStageFlags.TransferBit, VkPipelineStageFlags.TransferBit, VkDependencyFlags.None, default, default, new ReadOnlySpan<VkImageMemoryBarrier>(in pre));
        VkImageCopy region = FullCopy(width, height);
        cmd.CopyImage(src, VkImageLayout.TransferSrcOptimal, dst, VkImageLayout.TransferDstOptimal, new ReadOnlySpan<VkImageCopy>(in region));
        VkImageMemoryBarrier post = Barrier(dst, VkImageLayout.TransferDstOptimal, VkImageLayout.ShaderReadOnlyOptimal, VkAccessFlags.TransferWriteBit, AnyRead | AnyWrite);
        cmd.PipelineBarrier(VkPipelineStageFlags.TransferBit, Graphics | VkPipelineStageFlags.TransferBit, VkDependencyFlags.None, default, default, new ReadOnlySpan<VkImageMemoryBarrier>(in post));
    }

    // src is one of our textures, dst (the screen) is in dstLayout and is left in TransferDstOptimal
    public static void CopyToTransferDst(CommandBuffer cmd, VkImage src, VkImage dst, int width, int height, VkImageLayout dstLayout)
    {
        Span<VkImageMemoryBarrier> pre = stackalloc VkImageMemoryBarrier[2];
        pre[0] = Barrier(src, VkImageLayout.ShaderReadOnlyOptimal, VkImageLayout.TransferSrcOptimal, AnyWrite, VkAccessFlags.TransferReadBit);
        pre[1] = Barrier(dst, dstLayout, VkImageLayout.TransferDstOptimal, VkAccessFlags.TransferReadBit, VkAccessFlags.TransferWriteBit);
        cmd.PipelineBarrier(Graphics | VkPipelineStageFlags.TransferBit, VkPipelineStageFlags.TransferBit, VkDependencyFlags.None, default, default, pre);
        VkImageCopy region = FullCopy(width, height);
        cmd.CopyImage(src, VkImageLayout.TransferSrcOptimal, dst, VkImageLayout.TransferDstOptimal, new ReadOnlySpan<VkImageCopy>(in region));
        VkImageMemoryBarrier post = Barrier(src, VkImageLayout.TransferSrcOptimal, VkImageLayout.ShaderReadOnlyOptimal, VkAccessFlags.TransferReadBit, AnyRead | AnyWrite);
        cmd.PipelineBarrier(VkPipelineStageFlags.TransferBit, Graphics | VkPipelineStageFlags.TransferBit, VkDependencyFlags.None, default, default, new ReadOnlySpan<VkImageMemoryBarrier>(in post));
    }

    // a new texture goes to ShaderReadOnlyOptimal cleared to transparent black, like ReShade does on creation
    public static unsafe void ClearFresh(CommandBuffer cmd, FxTexture tex)
    {
        Barrier(cmd, VkPipelineStageFlags.TopOfPipeBit, VkPipelineStageFlags.TransferBit,
            Barrier(tex.Image, VkImageLayout.Undefined, VkImageLayout.TransferDstOptimal, VkAccessFlags.None, VkAccessFlags.TransferWriteBit, 0, tex.Levels));
        VkClearColorValue color = default;
        var range = new VkImageSubresourceRange
        {
            AspectMask = VkImageAspectFlags.ColorBit,
            BaseMipLevel = 0,
            LevelCount = tex.Levels,
            BaseArrayLayer = 0,
            LayerCount = 1
        };
        cmd.ClearColorImage(tex.Image, VkImageLayout.TransferDstOptimal, &color, new ReadOnlySpan<VkImageSubresourceRange>(in range));
        Barrier(cmd, VkPipelineStageFlags.TransferBit, Graphics | VkPipelineStageFlags.TransferBit,
            Barrier(tex.Image, VkImageLayout.TransferDstOptimal, VkImageLayout.ShaderReadOnlyOptimal, VkAccessFlags.TransferWriteBit, AnyRead | AnyWrite, 0, tex.Levels));
        tex.Fresh = false;
    }

    // fills mips 1..n from mip 0 with a blit chain
    public static void GenerateMips(CommandBuffer cmd, FxTexture tex)
    {
        if (tex.Levels <= 1)
        {
            return;
        }
        int w = tex.Width, h = tex.Height;
        for (int level = 1; level < tex.Levels; level++)
        {
            Span<VkImageMemoryBarrier> pre = stackalloc VkImageMemoryBarrier[2];
            pre[0] = Barrier(tex.Image, VkImageLayout.ShaderReadOnlyOptimal, VkImageLayout.TransferSrcOptimal, AnyWrite, VkAccessFlags.TransferReadBit, level - 1, 1);
            pre[1] = Barrier(tex.Image, VkImageLayout.ShaderReadOnlyOptimal, VkImageLayout.TransferDstOptimal, AnyRead, VkAccessFlags.TransferWriteBit, level, 1);
            cmd.PipelineBarrier(Graphics | VkPipelineStageFlags.TransferBit, VkPipelineStageFlags.TransferBit, VkDependencyFlags.None, default, default, pre);

            int nw = Math.Max(1, w / 2), nh = Math.Max(1, h / 2);
            var blit = new VkImageBlit();
            blit.SrcSubresource = new VkImageSubresourceLayers { AspectMask = VkImageAspectFlags.ColorBit, MipLevel = level - 1, BaseArrayLayer = 0, LayerCount = 1 };
            blit.DstSubresource = new VkImageSubresourceLayers { AspectMask = VkImageAspectFlags.ColorBit, MipLevel = level, BaseArrayLayer = 0, LayerCount = 1 };
            blit.SrcOffsets[1] = new VkOffset3D(w, h, 1);
            blit.DstOffsets[1] = new VkOffset3D(nw, nh, 1);
            cmd.BlitImage(tex.Image, VkImageLayout.TransferSrcOptimal, tex.Image, VkImageLayout.TransferDstOptimal, new ReadOnlySpan<VkImageBlit>(in blit), VkFilter.Linear);

            Span<VkImageMemoryBarrier> post = stackalloc VkImageMemoryBarrier[2];
            post[0] = Barrier(tex.Image, VkImageLayout.TransferSrcOptimal, VkImageLayout.ShaderReadOnlyOptimal, VkAccessFlags.TransferReadBit, AnyRead, level - 1, 1);
            post[1] = Barrier(tex.Image, VkImageLayout.TransferDstOptimal, VkImageLayout.ShaderReadOnlyOptimal, VkAccessFlags.TransferWriteBit, AnyRead | VkAccessFlags.TransferReadBit, level, 1);
            cmd.PipelineBarrier(VkPipelineStageFlags.TransferBit, Graphics | VkPipelineStageFlags.TransferBit, VkDependencyFlags.None, default, default, post);
            w = nw;
            h = nh;
        }
    }

    // uploads tightly packed mip 0 data, the staging buffer is freed a few frames later
    public static unsafe void Upload(CommandBuffer cmd, GraphicsDevice graphicsDevice, FxTexture tex, byte[] pixels)
    {
        VmaAllocator allocator = graphicsDevice.Allocator;
        var bufferInfo = new VkBufferCreateInfo
        {
            Size = ByteSize.Of(pixels.Length),
            Usage = VkBufferUsageFlags.TransferSrcBit,
            SharingMode = VkSharingMode.Exclusive
        };
        var allocInfo = new AllocationInfo.CreateInfo
        {
            Preference = MemoryPreference.PreferCpu,
            RequiredProperties = VkMemoryPropertyFlags.HostVisibleBit | VkMemoryPropertyFlags.HostCoherentBit,
            PreferredProperties = VkMemoryPropertyFlags.HostVisibleBit | VkMemoryPropertyFlags.HostCoherentBit,
            Priority = 0.5f
        };
        BufferAllocationInfo staging = allocator.AllocateBufferChecked("ReShade.Upload." + tex.Name, in bufferInfo, in allocInfo);
        var map = new AllocationMapInfo(staging.AllocationInfo)
        {
            Region = new MapRegion { Offset = ByteSize.Zero, Size = staging.BufferSize }
        };
        byte* mapped = (byte*)(void*)allocator.MapMemory(in map);
        pixels.AsSpan().CopyTo(new Span<byte>(mapped, pixels.Length));
        allocator.UnmapMemory(in map);

        Barrier(cmd, Graphics | VkPipelineStageFlags.TransferBit, VkPipelineStageFlags.TransferBit,
            Barrier(tex.Image, tex.Fresh ? VkImageLayout.Undefined : VkImageLayout.ShaderReadOnlyOptimal, VkImageLayout.TransferDstOptimal, AnyRead, VkAccessFlags.TransferWriteBit, 0, tex.Levels));
        var copy = new VkBufferImageCopy
        {
            BufferOffset = ByteSize.Zero,
            BufferRowLength = 0,
            BufferImageHeight = 0,
            ImageSubresource = new VkImageSubresourceLayers { AspectMask = VkImageAspectFlags.ColorBit, MipLevel = 0, BaseArrayLayer = 0, LayerCount = 1 },
            ImageOffset = new VkOffset3D(0, 0, 0),
            ImageExtent = new VkExtent3D(tex.Width, tex.Height, 1)
        };
        cmd.CopyBufferToImage(staging.VkBuffer, tex.Image, VkImageLayout.TransferDstOptimal, new ReadOnlySpan<VkBufferImageCopy>(in copy));
        Barrier(cmd, VkPipelineStageFlags.TransferBit, Graphics | VkPipelineStageFlags.TransferBit,
            Barrier(tex.Image, VkImageLayout.TransferDstOptimal, VkImageLayout.ShaderReadOnlyOptimal, VkAccessFlags.TransferWriteBit, AnyRead | VkAccessFlags.TransferReadBit, 0, tex.Levels));
        tex.Fresh = false;
        GenerateMips(cmd, tex);
        GpuGarbage.Add(() => allocator.FreeAllocation(in staging));
    }

    // around a compute pass: the textures it writes go to General and back, like ReShade's unordered_access state
    public static void ForCompute(CommandBuffer cmd, FxTexture tex, bool enter)
    {
        if (enter)
        {
            Barrier(cmd, Graphics | VkPipelineStageFlags.TransferBit, VkPipelineStageFlags.ComputeShaderBit,
                Barrier(tex.Image, VkImageLayout.ShaderReadOnlyOptimal, VkImageLayout.General, AnyRead | AnyWrite, VkAccessFlags.ShaderReadBit | VkAccessFlags.ShaderWriteBit, 0, tex.Levels));
        }
        else
        {
            Barrier(cmd, VkPipelineStageFlags.ComputeShaderBit, Graphics | VkPipelineStageFlags.TransferBit,
                Barrier(tex.Image, VkImageLayout.General, VkImageLayout.ShaderReadOnlyOptimal, VkAccessFlags.ShaderWriteBit, AnyRead | AnyWrite | VkAccessFlags.TransferReadBit, 0, tex.Levels));
        }
    }

    // a compute pass may read what the previous pass drew, and the next pass may read what it wrote
    public static void ComputeMemoryBarrier(CommandBuffer cmd)
    {
        var b = new VkMemoryBarrier
        {
            SrcAccessMask = AnyWrite,
            DstAccessMask = AnyRead | VkAccessFlags.ShaderWriteBit | VkAccessFlags.TransferReadBit
        };
        cmd.PipelineBarrier(Graphics | VkPipelineStageFlags.TransferBit, Graphics | VkPipelineStageFlags.TransferBit, VkDependencyFlags.None, new ReadOnlySpan<VkMemoryBarrier>(in b), default, default);
    }

    // ReShade clears the stencil once per technique, before its first pass that uses it
    public static unsafe void ClearStencil(CommandBuffer cmd, FxTexture stencil)
    {
        VkImageAspectFlags aspect = DepthAspect(stencil.Format);
        Barrier(cmd, Tests, VkPipelineStageFlags.TransferBit,
            Barrier(stencil.Image, stencil.Fresh ? VkImageLayout.Undefined : VkImageLayout.DepthStencilAttachmentOptimal, VkImageLayout.TransferDstOptimal,
                VkAccessFlags.DepthStencilAttachmentWriteBit, VkAccessFlags.TransferWriteBit, 0, 1, aspect));
        VkClearDepthStencilValue value = default;
        var range = new VkImageSubresourceRange { AspectMask = aspect, BaseMipLevel = 0, LevelCount = 1, BaseArrayLayer = 0, LayerCount = 1 };
        cmd.ClearDepthStencilImage(stencil.Image, VkImageLayout.TransferDstOptimal, &value, new ReadOnlySpan<VkImageSubresourceRange>(in range));
        Barrier(cmd, VkPipelineStageFlags.TransferBit, Tests,
            Barrier(stencil.Image, VkImageLayout.TransferDstOptimal, VkImageLayout.DepthStencilAttachmentOptimal,
                VkAccessFlags.TransferWriteBit, VkAccessFlags.DepthStencilAttachmentReadBit | VkAccessFlags.DepthStencilAttachmentWriteBit, 0, 1, aspect));
        stencil.Fresh = false;
    }

    public static VkImageAspectFlags DepthAspect(VkFormat format) => format switch
    {
        VkFormat.D24UNormS8UInt or VkFormat.D32SFloatS8UInt or VkFormat.D16UNormS8UInt => VkImageAspectFlags.DepthBit | VkImageAspectFlags.StencilBit,
        _ => VkImageAspectFlags.DepthBit
    };

    // the scene's depth leaves the game's render pass as an attachment; effects sample it read-only, then it goes back
    public static void DepthToRead(CommandBuffer cmd, VkImage depth, VkFormat format, bool toRead)
    {
        VkImageLayout attach = VkImageLayout.DepthStencilAttachmentOptimal, read = VkImageLayout.DepthStencilReadOnlyOptimal;
        VkPipelineStageFlags tests = VkPipelineStageFlags.EarlyFragmentTestsBit | VkPipelineStageFlags.LateFragmentTestsBit;
        if (toRead)
        {
            Barrier(cmd, tests, VkPipelineStageFlags.FragmentShaderBit,
                Barrier(depth, attach, read, VkAccessFlags.DepthStencilAttachmentWriteBit, VkAccessFlags.ShaderReadBit, 0, 1, DepthAspect(format)));
        }
        else
        {
            Barrier(cmd, VkPipelineStageFlags.FragmentShaderBit, tests,
                Barrier(depth, read, attach, VkAccessFlags.ShaderReadBit, VkAccessFlags.DepthStencilAttachmentReadBit | VkAccessFlags.DepthStencilAttachmentWriteBit, 0, 1, DepthAspect(format)));
        }
    }

    // render pass for 1..8 color targets plus an optional stencil buffer (always loaded, see ClearStencil);
    // color Load expects ShaderReadOnlyOptimal, colors end in ShaderReadOnlyOptimal.
    // dontCare: the pass draws over every pixel, so the old image isn't kept (saves reading it on tiled GPUs)
    public static unsafe VkRenderPass CreateRenderPass(DeviceEx device, ReadOnlySpan<VkFormat> formats, bool clear, VkFormat stencilFormat = VkFormat.Undefined, bool dontCare = false)
    {
        int n = formats.Length;
        bool stencil = stencilFormat != VkFormat.Undefined;
        VkAttachmentDescription* attachments = stackalloc VkAttachmentDescription[n + 1];
        VkAttachmentReference* refs = stackalloc VkAttachmentReference[n];
        for (int i = 0; i < n; i++)
        {
            attachments[i] = new VkAttachmentDescription
            {
                Format = formats[i],
                Samples = VkSampleCountFlags._1Bit,
                LoadOp = clear ? VkAttachmentLoadOp.Clear : dontCare ? VkAttachmentLoadOp.DontCare : VkAttachmentLoadOp.Load,
                StoreOp = VkAttachmentStoreOp.Store,
                StencilLoadOp = VkAttachmentLoadOp.DontCare,
                StencilStoreOp = VkAttachmentStoreOp.DontCare,
                InitialLayout = clear || dontCare ? VkImageLayout.Undefined : VkImageLayout.ShaderReadOnlyOptimal,
                FinalLayout = VkImageLayout.ShaderReadOnlyOptimal
            };
            refs[i] = new VkAttachmentReference { Attachment = i, Layout = VkImageLayout.ColorAttachmentOptimal };
        }
        var stencilRef = new VkAttachmentReference { Attachment = n, Layout = VkImageLayout.DepthStencilAttachmentOptimal };
        if (stencil)
        {
            attachments[n] = new VkAttachmentDescription
            {
                Format = stencilFormat,
                Samples = VkSampleCountFlags._1Bit,
                LoadOp = VkAttachmentLoadOp.DontCare,
                StoreOp = VkAttachmentStoreOp.DontCare,
                StencilLoadOp = VkAttachmentLoadOp.Load,
                StencilStoreOp = VkAttachmentStoreOp.Store,
                InitialLayout = VkImageLayout.DepthStencilAttachmentOptimal,
                FinalLayout = VkImageLayout.DepthStencilAttachmentOptimal
            };
        }
        var subpass = new VkSubpassDescription();
        subpass.PipelineBindPoint = VkPipelineBindPoint.Graphics;
        subpass.ColorAttachmentCount = n;
        subpass.ColorAttachments = refs;
        subpass.DepthStencilAttachment = stencil ? &stencilRef : null;
        VkSubpassDependency* deps = stackalloc VkSubpassDependency[2];
        deps[0] = new VkSubpassDependency
        {
            SrcSubpass = -1,
            DstSubpass = 0,
            SrcStageMask = Graphics | VkPipelineStageFlags.TransferBit | Tests,
            DstStageMask = VkPipelineStageFlags.ColorAttachmentOutputBit | Tests,
            SrcAccessMask = AnyRead | AnyWrite | VkAccessFlags.TransferReadBit | VkAccessFlags.DepthStencilAttachmentWriteBit,
            DstAccessMask = VkAccessFlags.ColorAttachmentReadBit | VkAccessFlags.ColorAttachmentWriteBit | VkAccessFlags.DepthStencilAttachmentReadBit | VkAccessFlags.DepthStencilAttachmentWriteBit
        };
        deps[1] = new VkSubpassDependency
        {
            SrcSubpass = 0,
            DstSubpass = -1,
            SrcStageMask = VkPipelineStageFlags.ColorAttachmentOutputBit,
            DstStageMask = Graphics | VkPipelineStageFlags.TransferBit,
            SrcAccessMask = VkAccessFlags.ColorAttachmentWriteBit,
            DstAccessMask = AnyRead | AnyWrite | VkAccessFlags.TransferReadBit
        };
        return device.CreateRenderPass(new VkRenderPassCreateInfo
        {
            AttachmentCount = stencil ? n + 1 : n,
            Attachments = attachments,
            SubpassCount = 1,
            Subpasses = &subpass,
            DependencyCount = 2,
            Dependencies = deps
        }, device.VkAllocator);
    }

    public static unsafe VkFramebuffer CreateFramebuffer(DeviceEx device, VkRenderPass renderPass, ReadOnlySpan<VkImageView> views, int width, int height)
    {
        fixed (VkImageView* p = views)
        {
            return device.CreateFramebuffer(new VkFramebufferCreateInfo
            {
                RenderPass = renderPass,
                AttachmentCount = views.Length,
                Attachments = p,
                Width = width,
                Height = height,
                Layers = 1
            }, device.VkAllocator);
        }
    }

    public static VkFormat ToVkFormat(int reshadeFormat)
    {
        return reshadeFormat switch
        {
            61 => VkFormat.R8UNorm,
            54 => VkFormat.R16SFloat,
            56 => VkFormat.R16UNorm,
            41 => VkFormat.R32SFloat,
            42 => VkFormat.R32UInt,
            43 => VkFormat.R32SInt,
            49 => VkFormat.R8G8UNorm,
            34 => VkFormat.R16G16SFloat,
            35 => VkFormat.R16G16UNorm,
            16 => VkFormat.R32G32SFloat,
            28 => VkFormat.R8G8B8A8UNorm,
            10 => VkFormat.R16G16B16A16SFloat,
            11 => VkFormat.R16G16B16A16UNorm,
            2 => VkFormat.R32G32B32A32SFloat,
            3 => VkFormat.R32G32B32A32UInt,
            4 => VkFormat.R32G32B32A32SInt,
            24 => VkFormat.A2B10G10R10UNormPack32,
            26 => VkFormat.B10G11R11UfloatPack32,
            _ => VkFormat.R8G8B8A8UNorm
        };
    }
}
