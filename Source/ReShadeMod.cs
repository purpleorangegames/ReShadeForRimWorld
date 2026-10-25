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

    public override string SettingsCategory() => "ReShade for RimWorld";

    // the keys can also be set here, with the game's own UI, so a key another mod uses can't lock anyone out
    public override void DoSettingsWindowContents(UnityEngine.Rect inRect)
    {
        var list = new Listing_Standard();
        list.Begin(inRect);
        if (Runtime == null)
        {
            list.Label("The effects could not start, see the log.");
            list.End();
            return;
        }
        Settings s = Runtime.Settings;
        list.Label("Overlay key: " + Overlay.KeyName(s.OverlayKey));
        if (list.ButtonText(Overlay.WaitingForKey == 1 ? "Press a key... (Esc cancels)" : "Change overlay key"))
        {
            Overlay.WaitingForKey = 1;
        }
        list.Gap();
        list.Label("Screenshot key: " + Overlay.KeyName(s.ScreenshotKey));
        if (list.ButtonText(Overlay.WaitingForKey == 2 ? "Press a key... (Esc cancels)" : "Change screenshot key"))
        {
            Overlay.WaitingForKey = 2;
        }
        if (list.ButtonText("Clear screenshot key"))
        {
            s.ScreenshotKey = 0;
            s.Save();
        }
        list.Gap();
        if (list.ButtonText("Reset keys to defaults (F2, no screenshot key)"))
        {
            s.OverlayKey = 291;
            s.ScreenshotKey = 0;
            s.Save();
        }
        list.Gap();
        if (list.ButtonText("Open the overlay"))
        {
            Overlay.Open = true;
        }
        list.End();
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
            // UI layers run after the game's UI pass, before its screenshot feature so those include the effects
            int uiIndex = -1;
            for (int i = 0; i < renderer.GetFeatureCount(); i++)
            {
                if (renderer.GetFeatureAt(i).GetType().Name == "ImGuiRenderFeature")
                {
                    uiIndex = i;
                    break;
                }
            }
            if (uiIndex >= 0)
            {
                IRenderFeature postFx = renderer.GetFeatureAt(index + 1);
                var layers = new LayerFeature(sceneTargets.Current, () => postFx.IsActive) { TexturePaths = new List<string> { TexturesDir } };
                renderer.InsertFeature(uiIndex + 1, layers);
                Runtime.Layers = layers;
                Runtime.Publish();
            }
            else
            {
                Log.Warning("[ReShade] could not find the game's UI pass, the UI tab won't do anything");
            }
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
