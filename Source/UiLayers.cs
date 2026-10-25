using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using Brutal.ImGuiApi;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using HarmonyLib;
using Waylith.Graphics;
using Waylith.Rendering;

namespace ReShadeForRimWorld;

// The game draws all its UI into ImGui's background draw list; every ImGui window belongs to our overlay.
// While whole screen or UI techniques are on, the game's snapshot copy of the frame's draw lists is
// filtered: the game keeps its own lists and our overlay gets a snapshot of its own, drawn on top after
// the effects. With UI techniques the game's UI pass is also skipped and LayerFeature draws the game's
// snapshot itself, on a layer of its own.
[HarmonyPatch]
public static class UiCapture
{
    // set on the main thread by the runtime: anything on the whole screen or the UI layer, and UI layer effects
    public static volatile bool Wanted;
    public static volatile bool TakeOver;
    public static volatile bool Broken;

    // the frame most recently copied, read by LayerFeature.CaptureFrame right after
    public static object LastGame, LastOverlay;
    public static int LastFrame = -1;
    private static readonly ConcurrentDictionary<int, bool> takenOver = new();

    private delegate void CopyFromFn(object snapshot, ImDrawDataPtr drawData, object materialDraws);
    private static Type snapshotType;
    private static CopyFromFn copyFrom;
    private static Func<object, int> getFrame;
    private static Action<object, int> setFrame;
    private static object[] overlaySnapshots = Array.Empty<object>();

    [ThreadStatic] private static bool inEndFrame, inOwnCopy;
    private static readonly List<ImDrawListPtr> all = new(), game = new(), ours = new();
    private static int totalVtx, totalIdx;
    private static bool split;

    [HarmonyPatch("RimWorldWaylith.Imgui.ImGuiHost", "EndFrame"), HarmonyPrefix]
    public static void BeforeEndFrame() => inEndFrame = true;

    [HarmonyPatch("RimWorldWaylith.Imgui.ImGuiHost", "EndFrame"), HarmonyPostfix]
    public static void AfterEndFrame() => inEndFrame = false;

    private static bool Init(object snapshot)
    {
        if (copyFrom != null)
        {
            return true;
        }
        snapshotType = snapshot.GetType();
        MethodInfo method = snapshotType.GetMethods().First(m => m.Name == "CopyFrom" && m.GetParameters().Length == 2 && m.GetParameters()[0].ParameterType == typeof(ImDrawDataPtr));
        FieldInfo frameNumber = snapshotType.GetField("FrameNumber");
        if (frameNumber == null)
        {
            return false;
        }
        copyFrom = Fast.Call<CopyFromFn>(method);
        getFrame = Fast.Getter<int>(frameNumber);
        setFrame = Fast.Setter<int>(frameNumber);
        return true;
    }

    // ImGuiDrawSnapshot.CopyFrom(ImDrawDataPtr, IReadOnlyList<ImGuiMaterialDraw>), called from ImGuiHost.EndFrame
    public static MethodBase CopyFromTarget()
    {
        Type type = AccessTools.TypeByName("RimWorldWaylith.Imgui.ImGuiDrawSnapshot");
        return type?.GetMethods().FirstOrDefault(m => m.Name == "CopyFrom" && m.GetParameters().Length == 2 && m.GetParameters()[0].ParameterType == typeof(ImDrawDataPtr));
    }

    private static unsafe bool StartsWith(byte* p, ReadOnlySpan<byte> prefix)
    {
        for (int i = 0; i < prefix.Length; i++)
        {
            if (p[i] != prefix[i])
            {
                return false;
            }
        }
        return true;
    }

    private static unsafe bool IsGameList(ImDrawListPtr l)
    {
        byte* owner = (byte*)(nint)l._OwnerName.Pointer;
        return owner == null || *owner == 0 || StartsWith(owner, "##Background"u8) || StartsWith(owner, "##Foreground"u8);
    }

    // points ImGui's draw data at a subset of its lists (resize first, the span only covers the current size)
    private static void UseLists(ref ImDrawDataPtr drawData, List<ImDrawListPtr> subset, int vtx = -1, int idx = -1)
    {
        ref ImVector<ImDrawListPtr> lists = ref drawData.CmdLists;
        lists.Count = subset.Count;
        int v = 0, x = 0;
        for (int i = 0; i < subset.Count; i++)
        {
            lists.Span[i] = subset[i];
            v += subset[i].VtxBuffer.Count;
            x += subset[i].IdxBuffer.Count;
        }
        drawData.CmdListsCount = subset.Count;
        drawData.TotalVtxCount = vtx >= 0 ? vtx : v;
        drawData.TotalIdxCount = idx >= 0 ? idx : x;
    }

    public static void BeforeCopy(object __instance, ImDrawDataPtr drawData)
    {
        split = false;
        if (!inEndFrame || inOwnCopy || !Wanted || Broken)
        {
            return;
        }
        try
        {
            if (!Init(__instance))
            {
                Broken = true;
                ReShadeMod.Runtime?.Feature.Say("[ReShade] UI layers are off, this game version draws its UI differently than expected");
                return;
            }
            all.Clear();
            game.Clear();
            ours.Clear();
            foreach (ImDrawListPtr l in drawData.CmdLists.Span)
            {
                all.Add(l);
                (IsGameList(l) ? game : ours).Add(l);
            }
            // with the overlay closed there is nothing of ours to take out
            if (ours.Count == 0)
            {
                return;
            }
            split = true;
            totalVtx = drawData.TotalVtxCount;
            totalIdx = drawData.TotalIdxCount;
            UseLists(ref drawData, game);
        }
        catch (Exception e)
        {
            Broken = true;
            ReShadeMod.Runtime?.Feature.Say("[ReShade] UI layers turned off, could not split the UI: " + e);
        }
    }

    public static void AfterCopy(object __instance, ImDrawDataPtr drawData, object materialDraws)
    {
        if (!inEndFrame || inOwnCopy || !Wanted || Broken || copyFrom == null)
        {
            return;
        }
        try
        {
            int frame = getFrame(__instance);
            object overlay = null;
            if (split)
            {
                split = false;
                if (overlaySnapshots.Length == 0)
                {
                    overlaySnapshots = new object[8];
                    for (int i = 0; i < overlaySnapshots.Length; i++)
                    {
                        overlaySnapshots[i] = Activator.CreateInstance(snapshotType, nonPublic: true);
                    }
                }
                overlay = overlaySnapshots[(frame % overlaySnapshots.Length + overlaySnapshots.Length) % overlaySnapshots.Length];
                UseLists(ref drawData, ours);
                inOwnCopy = true;
                try
                {
                    copyFrom(overlay, drawData, materialDraws);
                }
                finally
                {
                    inOwnCopy = false;
                }
                setFrame(overlay, frame);
                // put ImGui's draw data back the way it was
                UseLists(ref drawData, all, totalVtx, totalIdx);
            }
            bool take = TakeOver;
            LastGame = take ? __instance : null;
            LastOverlay = overlay;
            LastFrame = frame;
            if (take)
            {
                takenOver[frame] = true;
            }
        }
        catch (Exception e)
        {
            Broken = true;
            ReShadeMod.Runtime?.Feature.Say("[ReShade] UI layers turned off, could not split the UI: " + e);
        }
    }

    // the game's own UI pass, skipped for frames LayerFeature draws
    [HarmonyPatch("RimWorldWaylith.Imgui.ImGuiRenderFeature", "Record"), HarmonyPrefix]
    public static bool SkipGameUi(object featurePayload)
    {
        if (featurePayload == null || getFrame == null || takenOver.IsEmpty || featurePayload.GetType() != snapshotType)
        {
            return true;
        }
        return !takenOver.TryRemove(getFrame(featurePayload), out _);
    }

    // a frame we took but could not draw (LayerFeature inactive) must not lose its UI next time
    public static void Forget(int frame) => takenOver.TryRemove(frame, out _);
}

[HarmonyPatch]
public static class UiCaptureCopy
{
    public static MethodBase TargetMethod() => UiCapture.CopyFromTarget();
    public static void Prefix(object __instance, ImDrawDataPtr drawData) => UiCapture.BeforeCopy(__instance, drawData);
    public static void Postfix(object __instance, ImDrawDataPtr drawData, object materialDraws) => UiCapture.AfterCopy(__instance, drawData, materialDraws);
}

// typed delegates for the game's internal ImGui methods, so the frame's calls skip reflection
public static class Fast
{
    public static T Call<T>(MethodInfo method) where T : Delegate
    {
        MethodInfo invoke = typeof(T).GetMethod("Invoke");
        Type[] args = invoke.GetParameters().Select(p => p.ParameterType).ToArray();
        var dm = new DynamicMethod("ReShade." + method.Name, invoke.ReturnType, args, typeof(Fast).Module, true);
        ILGenerator il = dm.GetILGenerator();
        ParameterInfo[] target = method.GetParameters();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, method.DeclaringType);
        for (int i = 0; i < target.Length; i++)
        {
            il.Emit(OpCodes.Ldarg_S, (byte)(i + 1));
            Type want = target[i].ParameterType;
            if (args[i + 1] == typeof(object) && want != typeof(object))
            {
                il.Emit(want.IsValueType ? OpCodes.Unbox_Any : OpCodes.Castclass, want);
            }
        }
        il.Emit(OpCodes.Callvirt, method);
        il.Emit(OpCodes.Ret);
        return (T)dm.CreateDelegate(typeof(T));
    }

    public static Func<object, T> Getter<T>(FieldInfo field)
    {
        var dm = new DynamicMethod("ReShade.get_" + field.Name, typeof(T), new[] { typeof(object) }, typeof(Fast).Module, true);
        ILGenerator il = dm.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, field.DeclaringType);
        il.Emit(OpCodes.Ldfld, field);
        il.Emit(OpCodes.Ret);
        return (Func<object, T>)dm.CreateDelegate(typeof(Func<object, T>));
    }

    public static Action<object, T> Setter<T>(FieldInfo field)
    {
        var dm = new DynamicMethod("ReShade.set_" + field.Name, null, new[] { typeof(object), typeof(T) }, typeof(Fast).Module, true);
        ILGenerator il = dm.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, field.DeclaringType);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Stfld, field);
        il.Emit(OpCodes.Ret);
        return (Action<object, T>)dm.CreateDelegate(typeof(Action<object, T>));
    }
}

public sealed class LayerFrame
{
    public FrameData Data;
    public object Game, Overlay;
    public int Frame;
}

// runs after the game's backbuffer pass. With UI techniques: the game's UI on its own layer, its effects, blended
// over the map. Then whole screen effects, and our overlay on top.
public sealed class LayerFeature : IRenderFeature, IPostBackbufferPass
{
    private readonly IRenderPass[] passes;
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly Random random = new();
    private double lastFrameMs;
    private int frameCount;

    public volatile RenderItem[] UiList = Array.Empty<RenderItem>();
    public readonly GpuTimer Timer = new();
    public volatile RenderItem[] ScreenList = Array.Empty<RenderItem>();
    private readonly HashSet<Effect> builtEffects = new();
    private readonly HashSet<Effect> used = new();

    // render thread
    private FxTexture screen, uiRaw, uiFx, uiFxCopy, final, finalCopy, standIn, standIn1D, standIn3D;
    private readonly FrameTargets uiIo = new(), screenIo = new();
    private readonly FxTexture[] one = new FxTexture[1], three = new FxTexture[3];
    private VkRenderPass uiClearPass, finalLoadPass;
    private object uiRenderer, overlayRenderer;
    private delegate void RenderFn(object renderer, object snapshot, ref FrameContext ctx);
    private RenderFn render;
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
        object game = UiCapture.LastGame, overlay = UiCapture.LastOverlay;
        UiCapture.LastGame = null;
        UiCapture.LastOverlay = null;
        if (UiCapture.Broken)
        {
            if (game != null)
            {
                UiCapture.Forget(UiCapture.LastFrame);
            }
            return null;
        }
        // a UI taken from the game or an overlay split off has to be drawn even if the lists just changed
        if (game == null && overlay == null && !(UiCapture.Wanted && ScreenList.Length > 0))
        {
            return null;
        }
        double now = clock.Elapsed.TotalMilliseconds;
        DateTime date = DateTime.Now;
        var frame = new LayerFrame
        {
            Game = game,
            Overlay = overlay,
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
        render ??= Fast.Call<RenderFn>(type.GetMethod("Render"));
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
        render(renderer, snapshot, ref uiCtx);
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
        Timer.Begin(in ctx);
        try
        {
            EnsureTargets(in ctx);
            ClearIfFresh(cmd, screen);
            ClearIfFresh(cmd, uiRaw);
            ClearIfFresh(cmd, uiFx);
            ClearIfFresh(cmd, uiFxCopy);
            ClearIfFresh(cmd, final);
            ClearIfFresh(cmd, finalCopy);
            ClearIfFresh(cmd, standIn);
            ClearIfFresh(cmd, standIn1D);
            ClearIfFresh(cmd, standIn3D);

            ScreenBarrier(cmd, swapchain, presentLayout, VkImageLayout.TransferSrcOptimal);
            if (frame.Game != null)
            {
                // the screen as the game left it: the map with game effects, no UI
                GpuUtil.CopyFromTransferSrc(cmd, swapchain, screen.Image, screen.Width, screen.Height);

                // the game's UI on its own transparent layer
                uiRenderer ??= CreateRenderer(in ctx, uiClearPass);
                DrawUi(uiRenderer, frame.Game, in ctx, uiClearPass, uiRaw, clear: true);

                FxTexture uiResult = uiRaw;
                if (uiList.Length > 0)
                {
                    one[0] = uiRaw;
                    unpremultiply.Record(in ctx, uiFx, one, 0);
                    Targets(uiIo, uiFx, uiFxCopy);
                    RunList(in ctx, uiList, uiIo, in frame.Data);
                    uiResult = uiIo.Current;
                }
                three[0] = screen;
                three[1] = uiRaw;
                three[2] = uiResult;
                composite.Record(in ctx, final, three, uiList.Length > 0 ? 1 : 0);
            }
            else
            {
                // the game drew its UI itself, the whole screen effects start from what is on the screen
                GpuUtil.CopyFromTransferSrc(cmd, swapchain, final.Image, final.Width, final.Height);
            }

            // whole screen techniques see the map and the UI together
            Targets(screenIo, final, finalCopy);
            RenderTexture depthSource = null;
            bool usesDepth = false;
            foreach (RenderItem item in screenList)
            {
                usesDepth |= item.Effect.Result?.UsesDepth ?? false;
            }
            if (screenList.Length > 0 && sceneActive())
            {
                RenderTexture sceneRt = scene(ctx.SubmissionFrameIndex);
                if (sceneRt != null && sceneRt.HasDepth && sceneRt.Width == final.Width && sceneRt.Height == final.Height)
                {
                    screenIo.HasDepth = true;
                    if (usesDepth)
                    {
                        depthSource = sceneRt;
                        screenIo.Depth = sceneRt.DepthSampleView;
                        GpuUtil.DepthToRead(cmd, sceneRt.Depth.Image, sceneRt.DepthFormat, true);
                    }
                }
            }
            try
            {
                RunList(in ctx, screenList, screenIo, in frame.Data);
            }
            finally
            {
                if (depthSource != null)
                {
                    GpuUtil.DepthToRead(cmd, depthSource.Depth.Image, depthSource.DepthFormat, false);
                }
            }

            // our overlay goes on top untouched, like ReShade's own menu
            FxTexture result = screenIo.Current;
            if (frame.Overlay != null)
            {
                overlayRenderer ??= CreateRenderer(in ctx, finalLoadPass);
                DrawUi(overlayRenderer, frame.Overlay, in ctx, finalLoadPass, result, clear: false);
            }

            GpuUtil.CopyToTransferDst(cmd, result.Image, swapchain, result.Width, result.Height, VkImageLayout.TransferSrcOptimal);
            ScreenBarrier(cmd, swapchain, VkImageLayout.TransferDstOptimal, presentLayout);
        }
        catch (Exception e)
        {
            UiCapture.Broken = true;
            Say("[ReShade] UI layers turned off after an error: " + e);
            ScreenBarrier(cmd, swapchain, VkImageLayout.Undefined, presentLayout);
        }
        finally
        {
            Timer.End(in ctx);
        }
    }

    private void Targets(FrameTargets io, FxTexture a, FxTexture b)
    {
        io.A = a;
        io.B = b;
        io.Current = a;
        io.StandIn = standIn;
        io.StandIn1D = standIn1D;
        io.StandIn3D = standIn3D;
        io.Depth = default;
        io.HasDepth = false;
    }

    private void RunList(in FrameContext ctx, RenderItem[] list, FrameTargets io, in FrameData data)
    {
        foreach (RenderItem item in list)
        {
            if (item.Effect.Prepare(in ctx, in data, io, TexturePaths, Say))
            {
                builtEffects.Add(item.Effect);
                item.Effect.RecordTechnique(in ctx, io, item.Technique);
            }
        }
    }

    private static void ClearIfFresh(CommandBuffer cmd, FxTexture t)
    {
        if (t.Fresh)
        {
            GpuUtil.ClearFresh(cmd, t);
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
        used.Clear();
        foreach (RenderItem item in a)
        {
            used.Add(item.Effect);
        }
        foreach (RenderItem item in b)
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
