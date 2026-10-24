using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorldWaylith.Rendering;
using Verse;
using Waylith;
using Waylith.Rendering;
using Waylith.Rendering.PostFx;

namespace ReShadeForRimWorld;

public class ReShadeMod : Mod
{
    public static ReShadeFeature Feature;

    // same layout ReShade uses next to a game's exe, kept in RimWorld's save data folder
    public static string BaseDir => Path.Combine(GenFilePaths.SaveDataFolderPath, "ReShade");
    public static string ShadersDir => Path.Combine(BaseDir, "reshade-shaders", "Shaders");
    public static string TexturesDir => Path.Combine(BaseDir, "reshade-shaders", "Textures");
    public static string SettingsFile => Path.Combine(BaseDir, "ReShade.ini");
    public static string ScreenshotsDir => Path.Combine(BaseDir, "Screenshots");
    public static string ScreenshotTriggerFile => Path.Combine(BaseDir, "TakeScreenshot");
    public static Runtime Runtime;

    // config files keep paths inside the ReShade folder relative to it, so they still work if the save folder moves
    public static string ToStored(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return "";
        }
        string full = Path.GetFullPath(path);
        string root = Path.GetFullPath(BaseDir) + Path.DirectorySeparatorChar;
        return full.StartsWith(root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) ? full.Substring(root.Length).Replace('\\', '/') : full;
    }

    public static string FromStored(string stored)
    {
        if (string.IsNullOrWhiteSpace(stored))
        {
            return "";
        }
        string text = stored.Trim();
        // ReShade writes .\ReShadePreset.ini style paths
        if (text.StartsWith(".\\") || text.StartsWith("./"))
        {
            text = text.Substring(2);
        }
        if (Path.IsPathRooted(text))
        {
            return text;
        }
        return Path.GetFullPath(Path.Combine(BaseDir, text.Replace('\\', Path.DirectorySeparatorChar)));
    }

    public ReShadeMod(ModContentPack content) : base(content)
    {
        NativeFx.Init(content.RootDir);
        LongEventHandler.ExecuteWhenFinished(Install);
    }

    private static void Install()
    {
        try
        {
            EngineApplication app = EngineApplication.Instance;
            if (app == null)
            {
                Log.Warning("[ReShade] no engine application, effects off");
                return;
            }
            Type bootstrap = typeof(PostFxFeature).Assembly.GetType("RimWorldWaylith.Bootstrap");
            var sceneTargets = (SceneColorTargets)bootstrap.GetField("_sceneTargets", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
            var composite = (CompositePass)bootstrap.GetField("_composite", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
            FrameRenderer renderer = app.FrameRenderer;
            int index = -1;
            for (int i = 0; i < renderer.GetFeatureCount(); i++)
            {
                if (renderer.GetFeatureAt(i) is PostFxFeature)
                {
                    index = i;
                    break;
                }
            }
            if (sceneTargets == null || composite == null || index < 0)
            {
                Log.Warning("[ReShade] could not find the game's post processing, effects off");
                return;
            }

            Feature = new ReShadeFeature(renderer.GetFeatureAt(index), sceneTargets.Current);
            Runtime = new Runtime(Feature);
            renderer.InsertFeature(index, Feature);
            composite.Source = Feature.OutputFor;
            new Harmony("tiagocc0.reshadeforrimworld").PatchAll();
            Log.Message($"[ReShade] ready, {Runtime.Effects.Count} effects in {ShadersDir}, preset {Runtime.Preset.Path}");
        }
        catch (Exception e)
        {
            Log.Error("[ReShade] failed to hook into rendering: " + e);
        }
    }

    // main thread: the screenshot key, the trigger file, or the overlay
    public static void TakeScreenshot()
    {
        if (Feature == null)
        {
            return;
        }
        string folder = Runtime?.Settings.ScreenshotPath;
        if (string.IsNullOrWhiteSpace(folder))
        {
            folder = ScreenshotsDir;
        }
        Feature.ScreenshotRequest = Path.Combine(folder, "RimWorld " + DateTime.Now.ToString("yyyy-MM-dd HH-mm-ss"));
    }

    public static void FlushMessages()
    {
        while (Feature != null && Feature.Messages.TryDequeue(out string m))
        {
            if (m.Contains("failed") || m.Contains("error"))
            {
                Log.Warning(m);
            }
            else
            {
                Log.Message(m);
            }
        }
    }
}
