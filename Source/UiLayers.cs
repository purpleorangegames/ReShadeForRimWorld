using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using Brutal.ImGuiApi;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using HarmonyLib;
using Waylith.Graphics;
using Waylith.Rendering;

namespace ReShadeForRimWorld;

// The game draws all its UI into ImGui's background draw list; every ImGui window belongs to our overlay.
// When UI or whole screen techniques are on, the frame's draw lists get split in two snapshots and the
// game's own UI pass is skipped, LayerFeature then draws them itself.
[HarmonyPatch]
public static class UiCapture
{
    private static Type snapshotType;
    private static MethodInfo copyFrom;
    private static FieldInfo frameNumberField;
    private static FieldInfo guiBackendField;
    private static PropertyInfo materialDrawsProperty;
    private static object[] overlaySnapshots = Array.Empty<object>();

    // set on the main thread by the runtime when any UI or whole screen technique is on
    public static volatile bool Wanted;
    public static volatile bool Broken;

    // the frame most recently split, read by LayerFeature.CaptureFrame right after
    public static object LastGame, LastOverlay;
    public static int LastFrame = -1;
    private static readonly ConcurrentDictionary<int, bool> takenOver = new();

    private static bool Init(object snapshot, object host)
    {
        if (copyFrom != null)
        {
            return true;
        }
        snapshotType = snapshot.GetType();
        copyFrom = snapshotType.GetMethods().First(m => m.Name == "CopyFrom" && m.GetParameters().Length == 2);
        frameNumberField = snapshotType.GetField("FrameNumber");
        guiBackendField = host.GetType().GetField("guiBackend", BindingFlags.Instance | BindingFlags.NonPublic);
        object backend = guiBackendField?.GetValue(host);
        materialDrawsProperty = backend?.GetType().GetProperty("MaterialDraws", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        return copyFrom != null && frameNumberField != null && materialDrawsProperty != null;
    }

    [HarmonyPatch("RimWorldWaylith.Imgui.ImGuiHost", "EndFrame"), HarmonyPostfix]
    public static unsafe void EndFrame(object __instance, object snapshot)
    {
        if (!Wanted || Broken)
        {
            return;
        }
        try
        {
            if (!Init(snapshot, __instance))
            {
                Broken = true;
                ReShadeMod.Runtime?.Feature.Say("[ReShade] UI layers are off, this game version draws its UI differently than expected");
                return;
            }
            object materialDraws = materialDrawsProperty.GetValue(guiBackendField.GetValue(__instance));
            ImDrawDataPtr drawData = ImGui.GetDrawData();
            ref ImVector<ImDrawListPtr> lists = ref drawData.CmdLists;
            ImDrawListPtr[] all = lists.Span.ToArray();
            var game = new List<ImDrawListPtr>();
            var ours = new List<ImDrawListPtr>();
            foreach (ImDrawListPtr l in all)
            {
                string owner = Marshal.PtrToStringUTF8((nint)l._OwnerName.Pointer) ?? "";
                if (owner.Length == 0 || owner.StartsWith("##Background") || owner.StartsWith("##Foreground"))
                {
                    game.Add(l);
                }
                else
                {
                    ours.Add(l);
                }
            }
            int totalVtx = drawData.TotalVtxCount, totalIdx = drawData.TotalIdxCount;

            int ring = Math.Max(4, overlaySnapshots.Length);
            if (overlaySnapshots.Length == 0)
            {
                overlaySnapshots = new object[8];
                for (int i = 0; i < overlaySnapshots.Length; i++)
                {
                    overlaySnapshots[i] = Activator.CreateInstance(snapshotType, nonPublic: true);
                }
            }
            int frame = (int)frameNumberField.GetValue(snapshot);
            object overlay = overlaySnapshots[(frame % overlaySnapshots.Length + overlaySnapshots.Length) % overlaySnapshots.Length];

            // the game snapshot only gets the game's lists, the overlay one only ours
            CopyLists(ref drawData, game, snapshot, materialDraws);
            CopyLists(ref drawData, ours, overlay, materialDraws);
            frameNumberField.SetValue(overlay, frame);

            // put ImGui's draw data back the way it was (growing the vector clears the new slots)
            lists.Count = all.Length;
            all.AsSpan().CopyTo(lists.Span);
            drawData.CmdListsCount = all.Length;
            drawData.TotalVtxCount = totalVtx;
            drawData.TotalIdxCount = totalIdx;

            LastGame = snapshot;
            LastOverlay = overlay;
            LastFrame = frame;
            takenOver[frame] = true;
        }
        catch (Exception e)
        {
            Broken = true;
            ReShadeMod.Runtime?.Feature.Say("[ReShade] UI layers turned off, could not split the UI: " + e);
        }
    }

    private static void CopyLists(ref ImDrawDataPtr drawData, List<ImDrawListPtr> subset, object target, object materialDraws)
    {
        ref ImVector<ImDrawListPtr> lists = ref drawData.CmdLists;
        // resize first, the span only covers the current size
        lists.Count = subset.Count;
        int vtx = 0, idx = 0;
        for (int i = 0; i < subset.Count; i++)
        {
            lists.Span[i] = subset[i];
            vtx += subset[i].VtxBuffer.Count;
            idx += subset[i].IdxBuffer.Count;
        }
        drawData.CmdListsCount = subset.Count;
        drawData.TotalVtxCount = vtx;
        drawData.TotalIdxCount = idx;
        copyFrom.Invoke(target, new[] { (object)drawData, materialDraws });
    }

    // the game's own UI pass, skipped for frames LayerFeature draws
    [HarmonyPatch("RimWorldWaylith.Imgui.ImGuiRenderFeature", "Record"), HarmonyPrefix]
    public static bool SkipGameUi(object featurePayload)
    {
        if (featurePayload == null || frameNumberField == null || featurePayload.GetType() != snapshotType)
        {
            return true;
        }
        int frame = (int)frameNumberField.GetValue(featurePayload);
        return !takenOver.TryRemove(frame, out _);
    }

    // a frame we took but could not draw (LayerFeature inactive) must not lose its UI next time
    public static void Forget(int frame) => takenOver.TryRemove(frame, out _);
}

public sealed class LayerFrame
{
    public FrameData Data;
    public object Game, Overlay;
    public int Frame;
}

// runs after the game's backbuffer pass: UI layer effects, blend over the map, whole screen effects, overlay on top
public sealed class LayerFeature : IRenderFeature, IPostBackbufferPass
{
    private readonly IRenderPass[] passes;
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly Random random = new();
    private double lastFrameMs;
    private int frameCount;

    public volatile RenderItem[] UiList = Array.Empty<RenderItem>();
    public volatile RenderItem[] ScreenList = Array.Empty<RenderItem>();
    private readonly HashSet<Effect> builtEffects = new();

    // render thread
    private FxTexture screen, uiRaw, uiFx, uiFxCopy, final, finalCopy, standIn, standIn1D, standIn3D;
    private VkRenderPass uiClearPass, finalLoadPass;
    private object uiRenderer, overlayRenderer;
    private MethodInfo renderMethod;
    private DeviceEx device;
    private bool warnedUsage;
    private readonly FullscreenPass unpremultiply = new("UiUnpremultiply", 1, @"#version 450
layout(location = 0) in vec2 vUv;
layout(location = 0) out vec4 outColor;
layout(set = 0, binding = 0) uniform sampler2D ui;
void main()
{
    vec4 c = textureLod(ui, vUv, 0.0);
    // the UI layer is drawn with premultiplied alpha, effects expect plain colors
    outColor = vec4(c.a > 0.0001 ? c.rgb / c.a : vec3(0.0), 1.0);
}");
    private readonly FullscreenPass composite = new("UiComposite", 3, @"#version 450
layout(location = 0) in vec2 vUv;
layout(location = 0) out vec4 outColor;
layout(set = 0, binding = 0) uniform sampler2D scene;
layout(set = 0, binding = 1) uniform sampler2D uiRaw;
layout(set = 0, binding = 2) uniform sampler2D uiFx;
layout(push_constant) uniform Mode { int withEffects; } mode;
void main()
{
    vec3 s = textureLod(scene, vUv, 0.0).rgb;
    vec4 r = textureLod(uiRaw, vUv, 0.0);
    // with UI effects their colors are used, but only where the UI was, with the UI's own coverage
    vec3 c = mode.withEffects != 0 ? mix(s, textureLod(uiFx, vUv, 0.0).rgb, r.a) : r.rgb + s * (1.0 - r.a);
    outColor = vec4(c, 1.0);
}");

    public byte PassId { get; } = RenderPassRegistry.Register("ReShade.Layers");
    public PassAttachment Attachment => null;
    public bool PreBackbuffer => false;
    public bool IsActive => true;
    public IReadOnlyList<IRenderPass> Passes => passes;
    public IList<string> TexturePaths = new List<string>();

    // the map's render target, for its depth: whole screen effects get the same depth the map effects get
    private readonly Func<int, RenderTexture> scene;
    private readonly Func<bool> sceneActive;

    public LayerFeature(Func<int, RenderTexture> scene, Func<bool> sceneActive)
    {
        this.scene = scene;
        this.sceneActive = sceneActive;
        passes = new IRenderPass[] { this };
    }

    // main thread, after the game's ImGui feature captured (and we split) this frame's UI
    public object CaptureFrame(in FrameTiming timing)
    {
        if (!UiCapture.Wanted || UiCapture.Broken || UiCapture.LastGame == null)
        {
            if (UiCapture.LastFrame >= 0)
            {
                UiCapture.Forget(UiCapture.LastFrame);
            }
            return null;
        }
        double now = clock.Elapsed.TotalMilliseconds;
        DateTime date = DateTime.Now;
        var frame = new LayerFrame
        {
            Game = UiCapture.LastGame,
            Overlay = UiCapture.LastOverlay,
            Frame = UiCapture.LastFrame,
            Data = new FrameData
            {
                TimerMs = (float)now,
                FrameTimeMs = (float)(now - lastFrameMs),
                FrameCount = frameCount++,
                Random = random.Next(),
                Year = date.Year,
                Month = date.Month,
                Day = date.Day,
                Seconds = (float)date.TimeOfDay.TotalSeconds
            }
        };
        lastFrameMs = now;
        UiCapture.LastGame = null;
        return frame;
    }

    private static VkFormat Unorm(VkFormat f) => f switch
    {
        VkFormat.B8G8R8A8SRGB => VkFormat.B8G8R8A8UNorm,
        VkFormat.R8G8B8A8SRGB => VkFormat.R8G8B8A8UNorm,
        _ => f
    };

    private object CreateRenderer(in FrameContext ctx, VkRenderPass renderPass)
    {
        Type type = typeof(Waylith.Rendering.PostFx.CompositePass).Assembly.GetType("RimWorldWaylith.Imgui.ImGuiVulkanRenderer")
            ?? AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("RimWorldWaylith.Imgui.ImGuiVulkanRenderer")).First(t => t != null);
        renderMethod ??= type.GetMethod("Render");
        int frames = ctx.RenderContext.FramesInFlight;
        return Activator.CreateInstance(type, ctx.GraphicsDevice, renderPass, 0, VkSampleCountFlags._1Bit, frames + 1, frames, 1024,
            ctx.GraphicsDevice.ShaderLayoutCache, new ShaderModuleCache(ctx.Device));
    }

    private void DrawUi(object renderer, object snapshot, in FrameContext ctx, VkRenderPass renderPass, FxTexture target, bool clear)
    {
        CommandBuffer cmd = ctx.CommandBuffer;
        VkClearValue clearValue = new VkClearColorValue();
        var begin = new VkRenderPassBeginInfo
        {
            RenderPass = renderPass,
            Framebuffer = target.Framebuffer(renderPass),
            RenderArea = new VkRect2D(target.Extent),
            ClearValueCount = clear ? 1 : 0
        };
        unsafe
        {
            begin.ClearValues = clear ? &clearValue : null;
            cmd.BeginRenderPass(in begin, VkSubpassContents.Inline);
        }
        var uiCtx = new FrameContext(ctx.RenderContext, ctx.Resources, cmd, renderPass, target.Framebuffer(renderPass), target.Extent, VkSampleCountFlags._1Bit, target.Format, VkFormat.Undefined);
        renderMethod.Invoke(renderer, new object[] { snapshot, uiCtx });
        cmd.EndRenderPass();
    }

    // render thread, after the game's backbuffer pass ended
    public void Record(in FrameContext ctx, object featurePayload)
    {
        RenderItem[] uiList = UiList, screenList = ScreenList;
        ReleaseUnused(uiList, screenList);
        if (featurePayload is not LayerFrame frame)
        {
            return;
        }
        CommandBuffer cmd = ctx.CommandBuffer;
        VkImage swapchain = ctx.Resources.ColorImage;
        VkImageLayout presentLayout = ctx.RenderContext is RenderContext rc ? rc.FinalLayout : VkImageLayout.PresentSrcKHR;
        if (ctx.RenderContext is WindowRenderContext w && (w.SwapchainImageUsage & (VkImageUsageFlags.TransferSrcBit | VkImageUsageFlags.TransferDstBit)) != (VkImageUsageFlags.TransferSrcBit | VkImageUsageFlags.TransferDstBit))
        {
            if (!warnedUsage)
            {
                warnedUsage = true;
                ReShadeMod.Runtime?.Feature.Say("[ReShade] this GPU can't copy to and from the screen, UI layers are off");
            }
            UiCapture.Broken = true;
            return;
        }
        try
        {
            EnsureTargets(in ctx);
            foreach (FxTexture t in new[] { screen, uiRaw, uiFx, uiFxCopy, final, finalCopy, standIn, standIn1D, standIn3D })
            {
                if (t.Fresh)
                {
                    GpuUtil.ClearFresh(cmd, t);
                }
            }

            // the screen as the game left it: the map with game effects, no UI
            ScreenBarrier(cmd, swapchain, presentLayout, VkImageLayout.TransferSrcOptimal);
            GpuUtil.CopyFromTransferSrc(cmd, swapchain, screen.Image, screen.Width, screen.Height);

            // the game's UI on its own transparent layer
            uiRenderer ??= CreateRenderer(in ctx, uiClearPass);
            DrawUi(uiRenderer, frame.Game, in ctx, uiClearPass, uiRaw, clear: true);

            bool uiEffects = uiList.Length > 0;
            if (uiEffects)
            {
                unpremultiply.Record(in ctx, uiFx, new[] { uiRaw }, 0);
                var io = new FrameTargets { BackBuffer = uiFx, BackBufferCopy = uiFxCopy, StandIn = standIn, StandIn1D = standIn1D, StandIn3D = standIn3D };
                foreach (RenderItem item in uiList)
                {
                    if (item.Effect.Prepare(in ctx, in frame.Data, in io, TexturePaths, Say))
                    {
                        builtEffects.Add(item.Effect);
                        item.Effect.RecordTechnique(in ctx, in io, item.Technique);
                    }
                }
            }
            composite.Record(in ctx, final, new[] { screen, uiRaw, uiEffects ? uiFx : uiRaw }, uiEffects ? 1 : 0);

            // whole screen techniques see the map and the UI together
            var screenIo = new FrameTargets { BackBuffer = final, BackBufferCopy = finalCopy, StandIn = standIn, StandIn1D = standIn1D, StandIn3D = standIn3D };
            RenderTexture depthSource = null;
            if (screenList.Length > 0 && sceneActive())
            {
                RenderTexture sceneRt = scene(ctx.SubmissionFrameIndex);
                if (sceneRt != null && sceneRt.HasDepth && sceneRt.Width == final.Width && sceneRt.Height == final.Height)
                {
                    depthSource = sceneRt;
                    screenIo.Depth = sceneRt.DepthSampleView;
                    GpuUtil.DepthToRead(cmd, sceneRt.Depth.Image, sceneRt.DepthFormat, true);
                }
            }
            try
            {
                foreach (RenderItem item in screenList)
                {
                    if (item.Effect.Prepare(in ctx, in frame.Data, in screenIo, TexturePaths, Say))
                    {
                        builtEffects.Add(item.Effect);
                        item.Effect.RecordTechnique(in ctx, in screenIo, item.Technique);
                    }
                }
            }
            finally
            {
                if (depthSource != null)
                {
                    GpuUtil.DepthToRead(cmd, depthSource.Depth.Image, depthSource.DepthFormat, false);
                }
            }

            // our overlay goes on top untouched, like ReShade's own menu
            overlayRenderer ??= CreateRenderer(in ctx, finalLoadPass);
            DrawUi(overlayRenderer, frame.Overlay, in ctx, finalLoadPass, final, clear: false);

            GpuUtil.CopyToTransferDst(cmd, final.Image, swapchain, final.Width, final.Height, VkImageLayout.TransferSrcOptimal);
            ScreenBarrier(cmd, swapchain, VkImageLayout.TransferDstOptimal, presentLayout);
        }
        catch (Exception e)
        {
            UiCapture.Broken = true;
            Say("[ReShade] UI layers turned off after an error: " + e);
            ScreenBarrier(cmd, swapchain, VkImageLayout.Undefined, presentLayout);
        }
    }

    // access masks follow the layouts, so each one fits the stages the barrier names
    private static void ScreenBarrier(CommandBuffer cmd, VkImage image, VkImageLayout from, VkImageLayout to)
    {
        static VkAccessFlags Access(VkImageLayout layout, bool source) => layout switch
        {
            VkImageLayout.TransferSrcOptimal => VkAccessFlags.TransferReadBit,
            VkImageLayout.TransferDstOptimal => VkAccessFlags.TransferWriteBit,
            _ => VkAccessFlags.None
        };
        VkImageMemoryBarrier b = GpuUtil.Barrier(image, from, to, Access(from, true), Access(to, false));
        // the game's color writes to the screen are made visible with a memory barrier, the present layout itself takes no access flags
        var m = new VkMemoryBarrier { SrcAccessMask = VkAccessFlags.ColorAttachmentWriteBit, DstAccessMask = VkAccessFlags.TransferReadBit | VkAccessFlags.TransferWriteBit };
        cmd.PipelineBarrier(VkPipelineStageFlags.ColorAttachmentOutputBit | VkPipelineStageFlags.TransferBit, VkPipelineStageFlags.TransferBit | VkPipelineStageFlags.BottomOfPipeBit, VkDependencyFlags.None, new ReadOnlySpan<VkMemoryBarrier>(in m), default, new ReadOnlySpan<VkImageMemoryBarrier>(in b));
    }

    private void Say(string message) => ReShadeMod.Runtime?.Feature.Say(message);

    private void EnsureTargets(in FrameContext ctx)
    {
        GraphicsDevice gd = ctx.GraphicsDevice;
        VkExtent2D extent = ctx.Extent;
        VkFormat format = Unorm(ctx.ColorFormat);
        if (device == null)
        {
            device = ctx.Device;
            standIn = new FxTexture(gd, 1, 1, 1, VkFormat.R8G8B8A8UNorm, "Layers.StandIn");
            standIn1D = new FxTexture(gd, 1, 1, 1, VkFormat.R8G8B8A8UNorm, "Layers.StandIn1D", dimensions: 1);
            standIn3D = new FxTexture(gd, 1, 1, 1, VkFormat.R8G8B8A8UNorm, "Layers.StandIn3D", dimensions: 3);
        }
        if (screen != null && screen.Width == extent.Width && screen.Height == extent.Height && screen.Format == format)
        {
            return;
        }
        foreach (FxTexture t in new[] { screen, uiRaw, uiFx, uiFxCopy, final, finalCopy })
        {
            t?.Dispose();
        }
        screen = new FxTexture(gd, extent.Width, extent.Height, 1, format, "Layers.Screen");
        uiRaw = new FxTexture(gd, extent.Width, extent.Height, 1, format, "Layers.UiRaw");
        uiFx = new FxTexture(gd, extent.Width, extent.Height, 1, format, "Layers.UiFx");
        uiFxCopy = new FxTexture(gd, extent.Width, extent.Height, 1, format, "Layers.UiFxCopy");
        final = new FxTexture(gd, extent.Width, extent.Height, 1, format, "Layers.Final");
        finalCopy = new FxTexture(gd, extent.Width, extent.Height, 1, format, "Layers.FinalCopy");
        if (uiClearPass.IsNull())
        {
            uiClearPass = GpuUtil.CreateRenderPass(device, new[] { format }, clear: true);
            finalLoadPass = GpuUtil.CreateRenderPass(device, new[] { format }, clear: false);
        }
        Say($"[ReShade] UI layers {extent.Width}x{extent.Height} {ctx.ColorFormat}");
    }

    private void ReleaseUnused(RenderItem[] a, RenderItem[] b)
    {
        if (builtEffects.Count == 0)
        {
            return;
        }
        var used = new HashSet<Effect>(a.Concat(b).Select(i => i.Effect));
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

    public void OnRenderTargetRebuilt(IRenderTarget renderTarget)
    {
    }

    public void Dispose()
    {
        foreach (FxTexture t in new[] { screen, uiRaw, uiFx, uiFxCopy, final, finalCopy, standIn, standIn1D, standIn3D })
        {
            t?.Dispose();
        }
        (uiRenderer as IDisposable)?.Dispose();
        (overlayRenderer as IDisposable)?.Dispose();
        unpremultiply.Dispose();
        composite.Dispose();
    }
}
