using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Brutal.VulkanApi;
using Waylith.Graphics;
using Waylith.Rendering;

namespace ReShadeForRimWorld;

// sits right before the game's PostFxFeature: takes the rendered scene, runs the effects on it,
// and the game's composite pass then draws our result instead of the raw scene
public sealed class ReShadeFeature : IRenderFeature, IRenderPass
{
    private readonly IRenderFeature postFx;
    private readonly Func<int, RenderTexture> scene;
    private readonly IRenderPass[] passes;
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly Random random = new();
    private double lastFrameMs;
    private int frameCount;

    // touched only on the render thread
    private FxTexture backBuffer, backBufferCopy, standIn, standIn1D, standIn3D;
    private RenderTexture[] output = Array.Empty<RenderTexture>();

    // published by the runtime on the main thread, read on the render thread
    public volatile RenderItem[] RenderList = Array.Empty<RenderItem>();
    public volatile IList<string> IncludePaths = new List<string>();
    public volatile IList<string> TexturePaths = new List<string>();
    private readonly HashSet<Effect> builtEffects = new();
    public volatile bool Enabled = true;
    public volatile bool UseDepth = true;
    public volatile string ScreenshotRequest;

    public readonly ConcurrentQueue<string> Messages = new();

    public byte PassId { get; } = RenderPassRegistry.Register("ReShade");
    public PassAttachment Attachment => null;
    public bool PreBackbuffer => true;
    public bool IsActive => postFx.IsActive;
    public IReadOnlyList<IRenderPass> Passes => passes;

    public ReShadeFeature(IRenderFeature postFx, Func<int, RenderTexture> scene)
    {
        this.postFx = postFx;
        this.scene = scene;
        passes = new IRenderPass[] { this };
    }

    // what the composite pass should draw for this frame
    public RenderTexture OutputFor(int frame)
    {
        RenderTexture[] o = output;
        return (uint)frame < (uint)o.Length && o[frame] != null ? o[frame] : scene(frame);
    }

    // size effects get compiled for; the scene target matches the window
    public int SceneWidth => SceneSize().w;
    public int SceneHeight => SceneSize().h;

    private (int w, int h) SceneSize()
    {
        try
        {
            RenderTexture rt = scene(0);
            return rt == null ? (0, 0) : (rt.Width, rt.Height);
        }
        catch
        {
            return (0, 0);
        }
    }

    // nothing here runs every frame, so every message is worth showing
    public void Say(string message)
    {
        Messages.Enqueue(message);
    }

    // main thread
    public object CaptureFrame(in FrameTiming timing)
    {
        try
        {
            ReShadeMod.Runtime?.Tick();
        }
        catch (Exception e)
        {
            Say("[ReShade] runtime error: " + e);
        }
        ReShadeMod.FlushMessages();
        if (!Enabled || !postFx.IsActive)
        {
            frameCount++;
            return null;
        }
        double now = clock.Elapsed.TotalMilliseconds;
        DateTime date = DateTime.Now;
        var data = new FrameData
        {
            TimerMs = (float)now,
            FrameTimeMs = (float)(now - lastFrameMs),
            FrameCount = frameCount++,
            Random = random.Next(),
            Year = date.Year,
            Month = date.Month,
            Day = date.Day,
            Seconds = (float)date.TimeOfDay.TotalSeconds
        };
        lastFrameMs = now;
        return data;
    }

    // render thread
    public void Record(in FrameContext ctx, object featurePayload)
    {
        int framesInFlight = ctx.RenderContext.FramesInFlight;
        GpuGarbage.Tick(framesInFlight);
        int frame = ctx.SubmissionFrameIndex;
        if (output.Length != framesInFlight)
        {
            output = new RenderTexture[framesInFlight];
        }
        output[frame] = null;
        RenderItem[] list = RenderList;
        ReleaseUnused(list);
        string screenshotRequest = ScreenshotRequest;
        if (featurePayload is not FrameData data || (list.Length == 0 && screenshotRequest == null))
        {
            return;
        }
        RenderTexture source = scene(frame);
        if (source == null)
        {
            return;
        }
        CommandBuffer cmd = ctx.CommandBuffer;
        bool depthReadable = false;
        try
        {
            EnsureTargets(ctx.GraphicsDevice, source);
            if (backBufferCopy.Fresh)
            {
                GpuUtil.ClearFresh(cmd, backBufferCopy);
            }
            foreach (FxTexture t in new[] { standIn, standIn1D, standIn3D })
            {
                if (t.Fresh)
                {
                    GpuUtil.ClearFresh(cmd, t);
                }
            }
            GpuUtil.Copy(cmd, source.Color.Image, backBuffer.Image, source.Width, source.Height, backBuffer.Fresh);
            backBuffer.Fresh = false;
            string screenshot = Interlocked.Exchange(ref ScreenshotRequest, null);
            bool before = ReShadeMod.Runtime?.Settings.SaveBeforeAndAfter ?? true;
            if (screenshot != null && before)
            {
                Screenshot.Capture(cmd, ctx.GraphicsDevice, backBuffer.Image, backBuffer.Format, backBuffer.Width, backBuffer.Height, screenshot + " original.png", Say);
            }

            var io = new FrameTargets { BackBuffer = backBuffer, BackBufferCopy = backBufferCopy, StandIn = standIn, StandIn1D = standIn1D, StandIn3D = standIn3D };
            if (UseDepth && source.HasDepth)
            {
                io.Depth = source.DepthSampleView;
                GpuUtil.DepthToRead(cmd, source.Depth.Image, source.DepthFormat, true);
                depthReadable = true;
            }

            foreach (RenderItem item in list)
            {
                if (item.Effect.Prepare(in ctx, in data, in io, TexturePaths, Say))
                {
                    builtEffects.Add(item.Effect);
                    item.Effect.RecordTechnique(in ctx, in io, item.Technique);
                }
            }

            if (screenshot != null)
            {
                Screenshot.Capture(cmd, ctx.GraphicsDevice, backBuffer.Image, backBuffer.Format, backBuffer.Width, backBuffer.Height, screenshot + ".png", Say);
            }

            // hand the result to the composite through the scene's own render texture, so it keeps its format and size
            GpuUtil.Copy(cmd, backBuffer.Image, source.Color.Image, source.Width, source.Height);
            output[frame] = source;
        }
        catch (Exception e)
        {
            Say("[ReShade] effects turned off after an error: " + e);
            Enabled = false;
        }
        finally
        {
            if (depthReadable)
            {
                GpuUtil.DepthToRead(cmd, source.Depth.Image, source.DepthFormat, false);
            }
        }
    }

    // effects with no enabled technique give their GPU memory back
    private void ReleaseUnused(RenderItem[] list)
    {
        if (builtEffects.Count == 0)
        {
            return;
        }
        var used = new HashSet<Effect>();
        foreach (RenderItem item in list)
        {
            used.Add(item.Effect);
        }
        builtEffects.RemoveWhere(e =>
        {
            if (used.Contains(e))
            {
                return false;
            }
            e.ReleaseGpu();
            return true;
        });
    }

    private void EnsureTargets(GraphicsDevice graphicsDevice, RenderTexture source)
    {
        standIn ??= new FxTexture(graphicsDevice, 1, 1, 1, VkFormat.R8G8B8A8UNorm, "StandIn");
        standIn1D ??= new FxTexture(graphicsDevice, 1, 1, 1, VkFormat.R8G8B8A8UNorm, "StandIn1D", dimensions: 1);
        standIn3D ??= new FxTexture(graphicsDevice, 1, 1, 1, VkFormat.R8G8B8A8UNorm, "StandIn3D", dimensions: 3);
        if (backBuffer != null && backBuffer.Width == source.Width && backBuffer.Height == source.Height && backBuffer.Format == source.ColorFormat)
        {
            return;
        }
        backBuffer?.Dispose();
        backBufferCopy?.Dispose();
        backBuffer = new FxTexture(graphicsDevice, source.Width, source.Height, 1, source.ColorFormat, "BackBuffer");
        backBufferCopy = new FxTexture(graphicsDevice, source.Width, source.Height, 1, source.ColorFormat, "BackBufferCopy");
        Say($"[ReShade] back buffer {source.Width}x{source.Height} {source.ColorFormat}");
    }

    public void OnRenderTargetRebuilt(IRenderTarget renderTarget)
    {
    }

    public void Dispose()
    {
        foreach (Effect e in builtEffects)
        {
            e.Dispose();
        }
        backBuffer?.Dispose();
        backBufferCopy?.Dispose();
        standIn?.Dispose();
        standIn1D?.Dispose();
        standIn3D?.Dispose();
    }
}
