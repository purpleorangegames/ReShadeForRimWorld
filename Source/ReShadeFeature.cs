using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Brutal.VulkanApi;
using HarmonyLib;
using Waylith.Graphics;
using Waylith.Rendering;
using Waylith.Rendering.PostFx;

namespace ReShadeForRimWorld;

// sits right before the game's PostFxFeature: runs the effects on the rendered scene,
// and the game's composite pass then draws our result instead of the raw scene (see CompositeInput)
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
    private FxTexture backBufferA, backBufferB, standIn, standIn1D, standIn3D;
    private FxTexture[] sceneViews = Array.Empty<FxTexture>();
    private readonly FrameTargets io = new();
    private readonly HashSet<Effect> used = new();
    private VkImageView[] output = Array.Empty<VkImageView>();

    // published by the runtime on the main thread, read on the render thread
    public volatile RenderItem[] RenderList = Array.Empty<RenderItem>();
    public volatile IList<string> IncludePaths = new List<string>();
    public volatile IList<string> TexturePaths = new List<string>();
    private readonly HashSet<Effect> builtEffects = new();
    public volatile bool Enabled = true;
    public volatile bool UseDepth = true;
    public volatile string ScreenshotRequest;

    public readonly ConcurrentQueue<string> Messages = new();
    public readonly GpuTimer Timer = new();

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

    // what the composite pass should draw for this frame, null when the scene is unchanged
    public VkImageView OutputFor(int frame)
    {
        VkImageView[] o = output;
        return (uint)frame < (uint)o.Length ? o[frame] : default;
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
            output = new VkImageView[framesInFlight];
            sceneViews = new FxTexture[framesInFlight];
        }
        output[frame] = default;
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
        Timer.Begin(in ctx);
        try
        {
            EnsureTargets(ctx.GraphicsDevice, source);
            ClearIfFresh(cmd, backBufferA);
            ClearIfFresh(cmd, backBufferB);
            ClearIfFresh(cmd, standIn);
            ClearIfFresh(cmd, standIn1D);
            ClearIfFresh(cmd, standIn3D);

            bool usesDepth = false, srgb = false;
            foreach (RenderItem item in list)
            {
                Effect.CompileResult r = item.Effect.Result;
                usesDepth |= r != null && r.UsesDepth;
                srgb |= r != null && r.ReadsColorAsSrgb;
            }
            io.A = backBufferA;
            io.B = backBufferB;
            io.StandIn = standIn;
            io.StandIn1D = standIn1D;
            io.StandIn3D = standIn3D;
            // effects read the scene as it is; only an sRGB read needs our own copy, the scene image has no sRGB view
            if (srgb && FxTexture.SrgbOf(source.ColorFormat) != VkFormat.Undefined)
            {
                GpuUtil.Copy(cmd, source.Color.Image, backBufferA.Image, source.Width, source.Height);
                io.Current = backBufferA;
            }
            else
            {
                io.Current = SceneView(frame, source);
            }
            FxTexture original = io.Current;

            string screenshot = Interlocked.Exchange(ref ScreenshotRequest, null);
            bool before = ReShadeMod.Runtime?.Settings.SaveBeforeAndAfter ?? true;
            if (screenshot != null && before)
            {
                Screenshot.Capture(cmd, ctx.GraphicsDevice, original.Image, original.Format, original.Width, original.Height, screenshot + " original.png", Say);
            }

            io.HasDepth = UseDepth && source.HasDepth;
            io.Depth = default;
            // the depth only changes layout when an effect samples it
            if (io.HasDepth && usesDepth)
            {
                io.Depth = source.DepthSampleView;
                GpuUtil.DepthToRead(cmd, source.Depth.Image, source.DepthFormat, true);
                depthReadable = true;
            }

            foreach (RenderItem item in list)
            {
                if (item.Effect.Prepare(in ctx, in data, io, TexturePaths, Say))
                {
                    builtEffects.Add(item.Effect);
                    item.Effect.RecordTechnique(in ctx, io, item.Technique);
                }
            }

            if (screenshot != null)
            {
                Screenshot.Capture(cmd, ctx.GraphicsDevice, io.Current.Image, io.Current.Format, io.Current.Width, io.Current.Height, screenshot + ".png", Say);
            }

            if (!io.Current.External)
            {
                output[frame] = io.Current.View(false);
            }
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
            Timer.End(in ctx);
        }
    }

    private static void ClearIfFresh(CommandBuffer cmd, FxTexture t)
    {
        if (t.Fresh)
        {
            GpuUtil.ClearFresh(cmd, t);
        }
    }

    // the scene's color image as a texture effects can read, one per frame in flight
    private FxTexture SceneView(int frame, RenderTexture source)
    {
        FxTexture view = sceneViews[frame];
        if (view == null || view.Image.VkHandle != source.Color.Image.VkHandle || view.Width != source.Width || view.Height != source.Height)
        {
            view = sceneViews[frame] = new FxTexture(source.Color.Image, source.Color.ImageView, source.Width, source.Height, source.ColorFormat, "Scene");
        }
        return view;
    }

    // effects with no enabled technique give their GPU memory back
    private void ReleaseUnused(RenderItem[] list)
    {
        if (builtEffects.Count == 0)
        {
            return;
        }
        used.Clear();
        foreach (RenderItem item in list)
        {
            used.Add(item.Effect);
        }
        if (builtEffects.IsSubsetOf(used))
        {
            return;
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
        if (backBufferA != null && backBufferA.Width == source.Width && backBufferA.Height == source.Height && backBufferA.Format == source.ColorFormat)
        {
            return;
        }
        backBufferA?.Dispose();
        backBufferB?.Dispose();
        backBufferA = new FxTexture(graphicsDevice, source.Width, source.Height, 1, source.ColorFormat, "BackBufferA");
        backBufferB = new FxTexture(graphicsDevice, source.Width, source.Height, 1, source.ColorFormat, "BackBufferB");
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
        backBufferA?.Dispose();
        backBufferB?.Dispose();
        standIn?.Dispose();
        standIn1D?.Dispose();
        standIn3D?.Dispose();
    }
}

// the game's composite draws the map onto the screen; after effects ran it samples our result instead of the scene
[HarmonyPatch(typeof(CompositePass), nameof(CompositePass.Record))]
public static class CompositeInput
{
    public static bool Prefix(CompositePass __instance, in FrameContext ctx, object featurePayload)
    {
        if (featurePayload == null || ReShadeMod.Feature == null)
        {
            return true;
        }
        VkImageView view = ReShadeMod.Feature.OutputFor(ctx.SubmissionFrameIndex);
        if (view.IsNull())
        {
            return true;
        }
        __instance.SetInput(0, view, __instance.Sampler);
        __instance.SetUniforms(__instance.Saturation);
        __instance.RecordInline(in ctx, ctx.RenderPass, ctx.SampleCount);
        return false;
    }
}
