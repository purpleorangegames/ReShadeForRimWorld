using System;
using HarmonyLib;
using Verse;

namespace ReShadeForRimWorld;

// the engine closes inside a try that hides any error, and skips freeing everything after it. These write any error
// the engine would hide to the log
[HarmonyPatch]
public static class QuitTrace
{

    [HarmonyPatch(typeof(Waylith.Rendering.RenderThread), nameof(Waylith.Rendering.RenderThread.Dispose)), HarmonyFinalizer]
    public static Exception RenderThreadDone(Exception __exception)
    {
        if (__exception != null)
        {
            Log.Warning("[ReShade] closing: the render thread failed to stop: " + __exception);
        }
        return __exception;
    }

    [HarmonyPatch(typeof(Waylith.Rendering.FrameRenderer), nameof(Waylith.Rendering.FrameRenderer.Dispose)), HarmonyFinalizer]
    public static Exception RendererDone(Exception __exception)
    {
        if (__exception != null)
        {
            Log.Warning("[ReShade] closing: the renderer failed to free: " + __exception);
        }
        GpuLedger.Report("closing");
        return __exception;
    }

    // one thing failing to free doesn't stop the rest
    public static void Try(string what, Action free)
    {
        try
        {
            free();
        }
        catch (Exception e)
        {
            Log.Warning($"[ReShade] closing: couldn't free {what}: {e}");
        }
    }
}
