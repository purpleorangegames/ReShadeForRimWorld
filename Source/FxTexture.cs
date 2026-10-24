using System;
using System.Linq;
using System.Threading;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using Brutal.VulkanApi.Vma;
using Waylith.Graphics;
using Waylith.Rendering;

namespace ReShadeForRimWorld;

// a 2D texture with a full mip chain and, for 8 bit RGBA/BGRA, sRGB views next to the linear ones.
// Kept in ShaderReadOnlyOptimal (all mips) between operations.
public sealed class FxTexture : IDisposable
{
    private static long nextHandle = 0x5E5E0000;

    public readonly int Width, Height, Depth, Levels;
    // 1, 2 or 3 dimensions, like ReShade's texture1D/2D/3D
    public readonly int Dimensions;
    public readonly VkFormat Format, SrgbFormat;
    public readonly string Name;
    public bool Fresh = true;

    private readonly DeviceEx device;
    private GpuTextureAllocation allocation;
    private VkImageView srgbView, linearMip0, srgbMip0;

    public VkImage Image => allocation.Image;
    public VkImageView View(bool srgb) => srgb && !srgbView.IsNull() ? srgbView : allocation.ImageView;
    public VkImageView TargetView(bool srgb) => srgb && !srgbMip0.IsNull() ? srgbMip0 : linearMip0;
    public VkFormat TargetFormat(bool srgb) => srgb && SrgbFormat != VkFormat.Undefined ? SrgbFormat : Format;
    public VkExtent2D Extent => new VkExtent2D(Width, Height);

    public static VkFormat SrgbOf(VkFormat f) => f switch
    {
        VkFormat.R8G8B8A8UNorm => VkFormat.R8G8B8A8SRGB,
        VkFormat.B8G8R8A8UNorm => VkFormat.B8G8R8A8SRGB,
        _ => VkFormat.Undefined
    };

    public readonly bool IsDepthStencil;
    public readonly bool Storage;
    private VkImageView[] storageViews = Array.Empty<VkImageView>();

    public FxTexture(GraphicsDevice graphicsDevice, int width, int height, int levels, VkFormat format, string name, bool depthStencil = false, bool storage = false, int dimensions = 2, int depth = 1)
    {
        Dimensions = Math.Clamp(dimensions, 1, 3);
        IsDepthStencil = depthStencil;
        Storage = storage;
        device = graphicsDevice.Device;
        Width = Math.Max(1, width);
        Height = Dimensions == 1 ? 1 : Math.Max(1, height);
        Depth = Dimensions == 3 ? Math.Max(1, depth) : 1;
        int maxLevels = 1 + (int)Math.Floor(Math.Log2(Math.Max(Math.Max(Width, Height), Depth)));
        Levels = Math.Clamp(levels == 0 ? maxLevels : levels, 1, maxLevels);
        Format = format;
        // sRGB formats can't be storage images, ReShade drops the sRGB views for these too
        SrgbFormat = storage ? VkFormat.Undefined : SrgbOf(format);
        Name = name;

        var info = new VkImageCreateInfo
        {
            Flags = SrgbFormat != VkFormat.Undefined ? VkImageCreateFlags.MutableFormatBit : VkImageCreateFlags.None,
            ImageType = Dimensions == 1 ? VkImageType._1D : Dimensions == 3 ? VkImageType._3D : VkImageType._2D,
            Format = format,
            Extent = new VkExtent3D(Width, Height, Depth),
            MipLevels = Levels,
            ArrayLayers = 1,
            Samples = VkSampleCountFlags._1Bit,
            Tiling = VkImageTiling.Optimal,
            Usage = depthStencil
                ? VkImageUsageFlags.TransferDstBit | VkImageUsageFlags.DepthStencilAttachmentBit
                : VkImageUsageFlags.TransferSrcBit | VkImageUsageFlags.TransferDstBit | VkImageUsageFlags.SampledBit | VkImageUsageFlags.ColorAttachmentBit | (storage ? VkImageUsageFlags.StorageBit : 0),
            SharingMode = VkSharingMode.Exclusive,
            InitialLayout = VkImageLayout.Undefined
        };
        ImageAllocationInfo image = GpuTextureService.AllocateTextureImage(graphicsDevice.Allocator, "ReShade." + name, GpuMemoryCategory.RenderTarget, in info);
        VkImageView view = CreateView(image.Image, format, 0, Levels);
        allocation = new GpuTextureAllocation(device, graphicsDevice.Allocator, image, view, new IntPtr(Interlocked.Increment(ref nextHandle)), Levels, format, "ReShade." + name, GpuMemoryCategory.RenderTarget);
        linearMip0 = Levels == 1 ? view : CreateView(image.Image, format, 0, 1);
        if (SrgbFormat != VkFormat.Undefined)
        {
            srgbView = CreateView(image.Image, SrgbFormat, 0, Levels);
            srgbMip0 = Levels == 1 ? srgbView : CreateView(image.Image, SrgbFormat, 0, 1);
        }
    }

    // compute shaders write one mip level through a linear view (sRGB formats can't be storage images)
    public VkImageView StorageView(int level)
    {
        level = Math.Clamp(level, 0, Levels - 1);
        if (storageViews.Length != Levels)
        {
            storageViews = new VkImageView[Levels];
        }
        if (storageViews[level].IsNull())
        {
            storageViews[level] = CreateView(allocation.Image, Format, level, 1);
        }
        return storageViews[level];
    }

    private VkImageView CreateView(VkImage image, VkFormat format, int baseLevel, int levelCount)
    {
        var info = new VkImageViewCreateInfo
        {
            Image = image,
            ViewType = Dimensions == 1 ? VkImageViewType._1D : Dimensions == 3 ? VkImageViewType._3D : VkImageViewType._2D,
            Format = format,
            SubresourceRange = new VkImageSubresourceRange
            {
                AspectMask = IsDepthStencil ? GpuUtil.DepthAspect(format) : VkImageAspectFlags.ColorBit,
                BaseMipLevel = baseLevel,
                LevelCount = levelCount,
                BaseArrayLayer = 0,
                LayerCount = 1
            }
        };
        return device.CreateImageView(in info, device.VkAllocator);
    }

    public void Dispose()
    {
        if (allocation == null)
        {
            return;
        }
        GpuTextureAllocation a = allocation;
        VkImageView[] views = new[] { srgbView, Levels > 1 ? linearMip0 : default, Levels > 1 ? srgbMip0 : default }.Concat(storageViews).ToArray();
        DeviceEx d = device;
        allocation = null;
        GpuGarbage.Add(() =>
        {
            foreach (VkImageView v in views)
            {
                if (!v.IsNull())
                {
                    d.DestroyImageView(v, d.VkAllocator);
                }
            }
            // the engine defers the image itself again, which is fine
            a.Dispose();
        });
    }
}
