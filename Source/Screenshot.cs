using System;
using System.IO;
using System.Threading.Tasks;
using Brutal;
using Brutal.StbApi;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using Brutal.VulkanApi.Vma;
using Waylith.Graphics;

namespace ReShadeForRimWorld;

// copies an 8 bit image into a host buffer, and writes it as PNG once the GPU is surely done with it
public static class Screenshot
{
    public static unsafe void Capture(CommandBuffer cmd, GraphicsDevice graphicsDevice, VkImage image, VkFormat format, int width, int height, string path, Action<string> log)
    {
        bool bgra = format == VkFormat.B8G8R8A8UNorm || format == VkFormat.B8G8R8A8SRGB;
        if (!bgra && format != VkFormat.R8G8B8A8UNorm && format != VkFormat.R8G8B8A8SRGB)
        {
            log($"[ReShade] screenshots of {format} are not supported");
            return;
        }
        int size = width * height * 4;
        VmaAllocator allocator = graphicsDevice.Allocator;
        var bufferInfo = new VkBufferCreateInfo
        {
            Size = ByteSize.Of(size),
            Usage = VkBufferUsageFlags.TransferDstBit,
            SharingMode = VkSharingMode.Exclusive
        };
        var allocInfo = new AllocationInfo.CreateInfo
        {
            Preference = MemoryPreference.PreferCpu,
            RequiredProperties = VkMemoryPropertyFlags.HostVisibleBit | VkMemoryPropertyFlags.HostCoherentBit,
            PreferredProperties = VkMemoryPropertyFlags.HostVisibleBit | VkMemoryPropertyFlags.HostCoherentBit | VkMemoryPropertyFlags.HostCachedBit,
            Priority = 0.5f
        };
        BufferAllocationInfo buffer = allocator.AllocateBufferChecked("ReShade.Screenshot", in bufferInfo, in allocInfo);
        long id = GpuLedger.Take("Screenshot");

        VkImageMemoryBarrier pre = GpuUtil.Barrier(image, VkImageLayout.ShaderReadOnlyOptimal, VkImageLayout.TransferSrcOptimal,
            VkAccessFlags.ColorAttachmentWriteBit | VkAccessFlags.TransferWriteBit, VkAccessFlags.TransferReadBit);
        cmd.PipelineBarrier(VkPipelineStageFlags.ColorAttachmentOutputBit | VkPipelineStageFlags.TransferBit, VkPipelineStageFlags.TransferBit, VkDependencyFlags.None, default, default, new ReadOnlySpan<VkImageMemoryBarrier>(in pre));
        var copy = new VkBufferImageCopy
        {
            BufferOffset = ByteSize.Zero,
            BufferRowLength = 0,
            BufferImageHeight = 0,
            ImageSubresource = new VkImageSubresourceLayers { AspectMask = VkImageAspectFlags.ColorBit, MipLevel = 0, BaseArrayLayer = 0, LayerCount = 1 },
            ImageOffset = new VkOffset3D(0, 0, 0),
            ImageExtent = new VkExtent3D(width, height, 1)
        };
        cmd.CopyImageToBuffer(image, VkImageLayout.TransferSrcOptimal, buffer.VkBuffer, new ReadOnlySpan<VkBufferImageCopy>(in copy));
        VkImageMemoryBarrier post = GpuUtil.Barrier(image, VkImageLayout.TransferSrcOptimal, VkImageLayout.ShaderReadOnlyOptimal,
            VkAccessFlags.TransferReadBit, VkAccessFlags.ShaderReadBit | VkAccessFlags.TransferWriteBit | VkAccessFlags.ColorAttachmentWriteBit);
        cmd.PipelineBarrier(VkPipelineStageFlags.TransferBit, VkPipelineStageFlags.FragmentShaderBit | VkPipelineStageFlags.TransferBit | VkPipelineStageFlags.ColorAttachmentOutputBit, VkDependencyFlags.None, default, default, new ReadOnlySpan<VkImageMemoryBarrier>(in post));

        GpuGarbage.Add(() =>
        {
            var map = new AllocationMapInfo(buffer.AllocationInfo)
            {
                Region = new MapRegion { Offset = ByteSize.Zero, Size = buffer.BufferSize }
            };
            byte* mapped = (byte*)(void*)allocator.MapMemory(in map);
            var pixels = new byte[size];
            new ReadOnlySpan<byte>(mapped, size).CopyTo(pixels);
            allocator.UnmapMemory(in map);
            allocator.FreeAllocation(in buffer);
            GpuLedger.Free(id);
            Task.Run(() =>
            {
                for (int i = 0; i < size; i += 4)
                {
                    if (bgra)
                    {
                        (pixels[i], pixels[i + 2]) = (pixels[i + 2], pixels[i]);
                    }
                    pixels[i + 3] = 255;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                bool ok = Stb.WritePng(path, width, height, 4, pixels);
                log(ok ? "[ReShade] saved screenshot " + path : "[ReShade] could not write " + path);
            });
        });
    }
}
