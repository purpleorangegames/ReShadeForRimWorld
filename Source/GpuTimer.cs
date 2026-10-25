using System;
using Brutal;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using Waylith.Rendering;

namespace ReShadeForRimWorld;

// GPU time of one piece of our work per frame, from timestamps read back a few frames later (never waits)
public sealed unsafe class GpuTimer : IDisposable
{
    private DeviceEx device;
    private VkQueryPool pool;
    private bool[] written = Array.Empty<bool>();
    private double nsPerTick;
    private double sumMs;
    private int samples;

    // average over the last second or so, in milliseconds
    public volatile float AverageMs;

    public void Begin(in FrameContext ctx)
    {
        int frames = ctx.RenderContext.FramesInFlight;
        if (device == null)
        {
            device = ctx.Device;
            nsPerTick = ctx.GraphicsDevice.PhysicalDevice.GetProperties().Limits.TimestampPeriod;
            var info = new VkQueryPoolCreateInfo { QueryType = VkQueryType.Timestamp, QueryCount = frames * 2 };
            pool = device.CreateQueryPool(in info, device.VkAllocator);
            written = new bool[frames];
        }
        int slot = ctx.SubmissionFrameIndex;
        if (slot >= written.Length)
        {
            return;
        }
        // this slot's previous frame has finished on the GPU by the time the slot comes around again
        if (written[slot])
        {
            Span<ulong> ticks = stackalloc ulong[2];
            VkResult r = device.GetQueryPoolResults(pool, slot * 2, 2, new ReadOnlySpan<byte>(System.Runtime.CompilerServices.Unsafe.AsPointer(ref ticks[0]), 16), (ByteSize64)8, VkQueryResultFlags._64Bit);
            if (r == VkResult.Success && ticks[1] >= ticks[0])
            {
                sumMs += (ticks[1] - ticks[0]) * nsPerTick / 1_000_000.0;
                if (++samples >= 60)
                {
                    AverageMs = (float)(sumMs / samples);
                    sumMs = 0;
                    samples = 0;
                }
            }
        }
        CommandBuffer cmd = ctx.CommandBuffer;
        cmd.ResetQueryPool(pool, slot * 2, 2);
        cmd.WriteTimestamp(VkPipelineStageFlags.TopOfPipeBit, pool, slot * 2);
        written[slot] = false;
    }

    public void End(in FrameContext ctx)
    {
        int slot = ctx.SubmissionFrameIndex;
        if (device == null || slot >= written.Length)
        {
            return;
        }
        ctx.CommandBuffer.WriteTimestamp(VkPipelineStageFlags.BottomOfPipeBit, pool, slot * 2 + 1);
        written[slot] = true;
    }

    public void Dispose()
    {
        if (device != null)
        {
            DeviceEx d = device;
            VkQueryPool p = pool;
            GpuGarbage.Add(() => d.DestroyQueryPool(p, d.VkAllocator));
            device = null;
        }
    }
}
