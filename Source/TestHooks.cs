using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RimWorld;
using Verse;

namespace ReShadeForRimWorld;

// switches for testing without a keyboard, all off unless the environment variable is set:
// RESHADE_TEST_SAVE=<name>  saves the game once a map is up (use with -quicktest)
// RESHADE_TEST_LOAD=<name>  loads that save from the main menu and pauses it, so every run shows the same frame
// RESHADE_PERF_LOG=1        logs the GPU time of the mod's work every few seconds
// RESHADE_TEST_CYCLE=<dir>  after loading, goes through every preset .ini in dir and saves a screenshot of each to dir/shots
// RESHADE_TEST_RUN=1        after loading, lets the time run instead of pausing
// RESHADE_TEST_WORLD=1      after loading, goes to the world map (with the cycle, to see the effects there)
public static class TestHooks
{
    private static readonly string saveName = Environment.GetEnvironmentVariable("RESHADE_TEST_SAVE");
    private static readonly string loadName = Environment.GetEnvironmentVariable("RESHADE_TEST_LOAD");
    public static readonly bool PerfLog = Environment.GetEnvironmentVariable("RESHADE_PERF_LOG") == "1";
    private static bool saved, loadStarted, paused;
    private static int playingFrames;
    private static DateTime lastPerfLog = DateTime.UtcNow;
    private static readonly string cycleDir = Environment.GetEnvironmentVariable("RESHADE_TEST_CYCLE");
    private static readonly bool world = Environment.GetEnvironmentVariable("RESHADE_TEST_WORLD") == "1";
    private static bool worldShown;
    private static string[] cycle;
    private static int cycleStep = -1;
    private static DateTime cycleNext;

    // main thread, every frame
    public static void Tick(Runtime rt)
    {
        if (saveName == null && loadName == null && !PerfLog)
        {
            return;
        }
        bool playing = Current.ProgramState == ProgramState.Playing && Find.CurrentMap != null;
        playingFrames = playing ? playingFrames + 1 : 0;
        if (saveName != null && !saved && playingFrames > 300)
        {
            saved = true;
            GameDataSaveLoader.SaveGame(saveName);
            Log.Message("[ReShade] test save written: " + saveName);
        }
        if (loadName != null && !loadStarted && Current.ProgramState == ProgramState.Entry)
        {
            loadStarted = true;
            GameDataSaveLoader.LoadGame(loadName);
        }
        if (loadName != null && !paused && playingFrames > 5)
        {
            paused = true;
            Find.TickManager.CurTimeSpeed = Environment.GetEnvironmentVariable("RESHADE_TEST_RUN") == "1" ? TimeSpeed.Superfast : TimeSpeed.Paused;
            Log.Message("[ReShade] test save loaded and paused");
        }
        if (Environment.GetEnvironmentVariable("RESHADE_TEST_NODEPTH") == "1")
        {
            rt.Feature.UseDepth = false;
        }
        if (world && paused && playingFrames == 60)
        {
            // midday, the camera on a power conduit, so there's a bright line on the map before going to the world
            Map map = Find.CurrentMap;
            int hour = GenLocalDate.HourOfDay(map);
            Find.TickManager.DebugSetTicksGame(Find.TickManager.TicksGame + (12 - hour + 24) % 24 * GenDate.TicksPerHour);
            // a horizontal stretch: conduits on both sides
            var conduits = map.listerThings.ThingsOfDef(ThingDefOf.PowerConduit);
            var cells = new HashSet<IntVec3>(conduits.Select(c => c.Position));
            Thing conduit = conduits.FirstOrDefault(c => cells.Contains(c.Position + IntVec3.East) && cells.Contains(c.Position + IntVec3.West)
                && cells.Contains(c.Position + IntVec3.East * 2) && cells.Contains(c.Position + IntVec3.West * 2));
            Log.Message("[ReShade] test conduit " + (conduit?.Position.ToString() ?? "none"));
            if (conduit != null)
            {
                Find.CameraDriver.SetRootPosAndSize(conduit.DrawPos, 15f);
            }
        }
        if (world && paused && playingFrames == 200 && cycleDir != null)
        {
            Directory.CreateDirectory(Path.Combine(cycleDir, "shots"));
            rt.Feature.ScreenshotRequest = Path.Combine(cycleDir, "shots", "0-map");
        }
        if (world && paused && !worldShown && playingFrames > 260)
        {
            worldShown = true;
            CameraJumper.TryShowWorld();
            Log.Message("[ReShade] test world map shown");
        }
        if (cycleDir != null && paused)
        {
            Cycle(rt);
        }
        if (PerfLog && DateTime.UtcNow - lastPerfLog > TimeSpan.FromSeconds(5))
        {
            lastPerfLog = DateTime.UtcNow;
            Log.Message("[ReShade] order: " + string.Join(",", rt.Feature.RenderList.Select(i => i.Technique)));
            Log.Message($"[ReShade] perf: map {rt.Feature.Timer.AverageMs:0.000} ms, layers {(rt.Layers?.Timer.AverageMs ?? 0):0.000} ms, techniques {rt.Feature.RenderList.Length}+{rt.Layers?.ScreenList.Length ?? 0}+{rt.Layers?.UiList.Length ?? 0}");
        }
    }

    // even steps load preset step/2, odd steps take its screenshot
    private static void Cycle(Runtime rt)
    {
        if (cycle == null)
        {
            cycle = Directory.GetFiles(cycleDir, "*.ini").OrderBy(p => p, StringComparer.Ordinal).ToArray();
            cycleNext = DateTime.UtcNow.AddSeconds(60);
            Directory.CreateDirectory(Path.Combine(cycleDir, "shots"));
        }
        if (DateTime.UtcNow < cycleNext || cycleStep >= cycle.Length * 2)
        {
            return;
        }
        cycleStep++;
        int i = cycleStep / 2;
        if (i >= cycle.Length)
        {
            Log.Message("[ReShade] test cycle done");
            return;
        }
        if (cycleStep % 2 == 0)
        {
            rt.LoadPreset(cycle[i]);
            cycleNext = DateTime.UtcNow.AddSeconds(12);
        }
        else
        {
            rt.Feature.ScreenshotRequest = Path.Combine(cycleDir, "shots", Path.GetFileNameWithoutExtension(cycle[i]));
            cycleNext = DateTime.UtcNow.AddSeconds(3);
        }
    }
}
