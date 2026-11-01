using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using HarmonyLib;
using Verse;

namespace ReShadeForRimWorld;

// the diagnostic build (built with -p:Diagnostic=true) keeps a log of everything the mod does from the start of the
// game, in memory only. It goes to a file when the player clicks save. In the normal build the calls here are left out
public static class Diag
{
    // about 8 MB, far more than a long session writes
    private const int MaxChars = 4_000_000;
    private static readonly StringBuilder buffer = new();
    private static bool full;
    private static readonly object gate = new();
    private static readonly Stopwatch clock = Stopwatch.StartNew();
    private static readonly ConcurrentDictionary<string, int> counts = new();
    private static double nextStatus = 10;

    public static string LogDir => Path.Combine(ReShadeMod.BaseDir, "Logs");

    [Conditional("DIAGNOSTIC")]
    public static void Log(string message)
    {
        lock (gate)
        {
            if (full)
            {
                return;
            }
            buffer.Append($"{DateTime.Now:HH:mm:ss.fff} {clock.Elapsed.TotalSeconds,8:0.000}s [{Environment.CurrentManagedThreadId}] {message}\n");
            if (buffer.Length > MaxChars)
            {
                full = true;
                buffer.Append("log full, nothing more is kept\n");
            }
        }
    }

    // for things that happen every frame: only the first few get written
    [Conditional("DIAGNOSTIC")]
    public static void First(string key, int max, string message)
    {
        int n = counts.AddOrUpdate(key, 1, static (_, c) => c + 1);
        if (n < max)
        {
            Log(message);
        }
        else if (n == max)
        {
            Log(message + " (not written again)");
        }
    }

    // counted, and shown in the status lines
    [Conditional("DIAGNOSTIC")]
    public static void Count(string key)
    {
        counts.AddOrUpdate(key, 1, static (_, c) => c + 1);
    }

    [Conditional("DIAGNOSTIC")]
    public static void Start(ModContentPack content)
    {
        Verse.Log.Message("[ReShade] diagnostic build, the log is saved from the mod options");
        Try("system", () =>
        {
            Log("ReShade for RimWorld diagnostic build " + content.ModMetaData.ModVersion);
            Log("game " + RimWorld.VersionControl.CurrentVersionStringWithRev);
            Log("os " + RuntimeInformation.OSDescription + ", " + RuntimeInformation.OSArchitecture + ", process " + RuntimeInformation.ProcessArchitecture);
            Log("runtime " + RuntimeInformation.FrameworkDescription);
            Log("wine " + WineVersion());
            Log("command line " + Environment.CommandLine);
            foreach (string v in new[] { "XDG_SESSION_TYPE", "XDG_CURRENT_DESKTOP", "WAYLAND_DISPLAY", "DISPLAY", "SteamDeck", "STEAM_COMPAT_DATA_PATH", "PRESSURE_VESSEL_RUNTIME", "container", "FLATPAK_ID", "VK_ICD_FILENAMES", "VK_DRIVER_FILES", "VK_INSTANCE_LAYERS", "LANG" })
            {
                Log($"env {v}={Environment.GetEnvironmentVariable(v) ?? "(not set)"}");
            }
        });
        Try("mod folder", () =>
        {
            Log("mod folder " + content.RootDir);
            foreach (string f in Directory.EnumerateFiles(content.RootDir, "*", SearchOption.AllDirectories).Where(f => f.Contains(Path.DirectorySeparatorChar + "1.7" + Path.DirectorySeparatorChar)))
            {
                var info = new FileInfo(f);
                Log($"  {Path.GetRelativePath(content.RootDir, f)} {info.Length} bytes {info.LastWriteTime:yyyy-MM-dd HH:mm}");
            }
        });
        Try("mods", () =>
        {
            foreach (ModContentPack m in LoadedModManager.RunningModsListForReading)
            {
                Log($"mod {m.PackageIdPlayerFacing} \"{m.Name}\" {m.RootDir}");
            }
        });
        Try("ReShade folder", () =>
        {
            Log("ReShade folder " + ReShadeMod.BaseDir + (Directory.Exists(ReShadeMod.BaseDir) ? "" : " (doesn't exist yet)"));
            if (Directory.Exists(ReShadeMod.ShadersDir))
            {
                Log(".fx files " + Directory.EnumerateFiles(ReShadeMod.ShadersDir, "*.fx", SearchOption.AllDirectories).Count());
            }
            if (File.Exists(ReShadeMod.SettingsFile))
            {
                Log("ReShade.ini:\n" + File.ReadAllText(ReShadeMod.SettingsFile).TrimEnd());
            }
        });
    }

    // a tiny effect, to see the compiler works apart from the player's shaders
    [Conditional("DIAGNOSTIC")]
    public static void TestCompiler()
    {
        Try("test compile", () =>
        {
            string file = Path.Combine(Path.GetTempPath(), "ReShadeDiagnosticTest.fx");
            File.WriteAllText(file, """
                void VS(uint id : SV_VertexID, out float4 pos : SV_Position, out float2 uv : TEXCOORD)
                {
                    uv = float2((id << 1) & 2, id & 2);
                    pos = float4(uv * float2(2, -2) + float2(-1, 1), 0, 1);
                }
                float4 PS(float4 pos : SV_Position, float2 uv : TEXCOORD) : SV_Target
                {
                    return float4(uv, 0, 1);
                }
                technique DiagnosticTest { pass { VertexShader = VS; PixelShader = PS; } }
                """);
            var watch = Stopwatch.StartNew();
            // straight to the compiler, the cache would skip it
            NativeFx.Result r = NativeFx.Compile(file, new List<string>(), new List<string>(), false, json => FxModule.Parse(json).entry_points.Select(e => e.name));
            Log($"test compile: success {r.Success}, {r.Spirv?.Count ?? 0} shaders, {watch.ElapsedMilliseconds} ms" + (r.Success ? "" : "\n" + r.Errors));
            File.Delete(file);
        });
    }

    // what got patched, and who else patches the same methods
    [Conditional("DIAGNOSTIC")]
    public static void Patch(Harmony harmony)
    {
        foreach (Type t in typeof(Diag).Assembly.GetTypes().Where(t => t.GetCustomAttributes(typeof(HarmonyPatch), false).Length > 0))
        {
            try
            {
                List<MethodInfo> done = harmony.CreateClassProcessor(t).Patch();
                Log($"patched {t.Name}: {string.Join(", ", done?.Select(m => m.DeclaringType?.Name + "." + m.Name) ?? Array.Empty<string>())}");
            }
            catch (Exception e)
            {
                Log($"patch {t.Name} FAILED: {e}");
            }
        }
        foreach (MethodBase m in harmony.GetPatchedMethods())
        {
            Patches info = Harmony.GetPatchInfo(m);
            var others = info.Owners.Where(o => o != harmony.Id).ToList();
            if (others.Count > 0)
            {
                Log($"{m.DeclaringType?.Name}.{m.Name} is also patched by {string.Join(", ", others)}");
            }
        }
    }

    // main thread, every frame
    [Conditional("DIAGNOSTIC")]
    public static void Tick(Runtime rt)
    {
        double now = clock.Elapsed.TotalSeconds;
        if (now < nextStatus)
        {
            return;
        }
        // often at first, then once a minute
        nextStatus = now + (now < 120 ? 10 : 60);
        Status(rt);
    }

    [Conditional("DIAGNOSTIC")]
    public static void Status(Runtime rt)
    {
        Try("status", () =>
        {
            ReShadeFeature f = rt.Feature;
            int ok = rt.Effects.Count(e => e.Result?.Success == true), failed = rt.Effects.Count(e => e.Result != null && !e.Result.Success);
            Log($"status: {Current.ProgramState}, effects on {f.Enabled}, world map {f.OnWorld}, scene {f.SceneWidth}x{f.SceneHeight}, " +
                $"effects {rt.Effects.Count} ({ok} compiled, {failed} failed, {rt.Effects.Count(e => e.Compiling)} compiling), " +
                $"map list {f.RenderList.Length}, screen list {rt.Layers?.ScreenList.Length ?? -1}, UI list {rt.Layers?.UiList.Length ?? -1}, " +
                $"overlay {(Overlay.Open ? "open" : "closed")}, GPU {f.Timer.AverageMs:0.00} ms");
            Log("  counts: " + string.Join(", ", counts.OrderBy(c => c.Key).Select(c => $"{c.Key} {c.Value}")));
        });
    }

    // the diagnostic log and the game's own log in one file, the one to send
    public static string Save()
    {
        if (ReShadeMod.Runtime != null)
        {
            Status(ReShadeMod.Runtime);
        }
        Log("saving the log");
        string file = Path.Combine(LogDir, "Send this " + DateTime.Now.ToString("yyyy-MM-dd HH-mm-ss") + ".txt");
        Directory.CreateDirectory(LogDir);
        using var w = new StreamWriter(file, false, new UTF8Encoding(false));
        w.WriteLine("=== ReShade diagnostic log ===");
        lock (gate)
        {
            w.Write(buffer.ToString());
        }
        w.WriteLine();
        w.WriteLine("=== game log ===");
        try
        {
            // the game's log can be read while it's being written, the same way its crash report does
            Type fileLog = typeof(Waylith.EngineApplication).Assembly.GetType("Waylith.Logging.FileLog");
            fileLog.GetMethod("CopyLogTo", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, new object[] { w });
        }
        catch (Exception e)
        {
            w.WriteLine("could not read the game's log: " + e);
        }
        return file;
    }

    private static string WineVersion()
    {
        if (!OperatingSystem.IsWindows())
        {
            return "no (native)";
        }
        if (NativeLibrary.TryLoad("ntdll.dll", out IntPtr ntdll) && NativeLibrary.TryGetExport(ntdll, "wine_get_version", out IntPtr get))
        {
            unsafe
            {
                return Marshal.PtrToStringAnsi(((delegate* unmanaged<IntPtr>)get)()) ?? "yes";
            }
        }
        return "no (real Windows)";
    }

    private static void Try(string what, Action action)
    {
        try
        {
            action();
        }
        catch (Exception e)
        {
            Log(what + " failed: " + e);
        }
    }
}
