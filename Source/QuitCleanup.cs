using System;
using HarmonyLib;
using Verse;

namespace ReShadeForRimWorld;

// quitting waits a few frames: the effects are turned off first, so their GPU memory is freed the usual way while
// frames still run, then the game quits. Anything left is freed again when the engine disposes the mod (GpuGarbage)
[HarmonyPatch(typeof(Root), nameof(Root.Shutdown))]
public static class QuitCleanup
{
    private const int Frames = 10;
    private static int waited = -1;
    private static bool done;
    private static DateTime started;

    public static bool Quitting => waited >= 0;

    public static bool Prefix()
    {
        Runtime rt = ReShadeMod.Runtime;
        // nothing to free, or it's taking too long: quit right away
        if (done || rt?.Feature == null || (Quitting && DateTime.UtcNow - started > TimeSpan.FromSeconds(3)))
        {
            return true;
        }
        if (!Quitting)
        {
            waited = 0;
            started = DateTime.UtcNow;
            rt.Feature.RenderList = Array.Empty<RenderItem>();
            if (rt.Layers != null)
            {
                rt.Layers.ScreenList = Array.Empty<RenderItem>();
                rt.Layers.UiList = Array.Empty<RenderItem>();
            }
            UiCapture.TakeOver = false;
            UiCapture.Wanted = false;
        }
        return false;
    }

    // main thread, every frame
    public static void Tick()
    {
        if (!Quitting || done || ++waited < Frames)
        {
            return;
        }
        done = true;
        Root.Shutdown();
    }
}
