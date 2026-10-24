using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace ReShadeForRimWorld;

// one technique as listed on the Home tab
public sealed class TechniqueEntry
{
    public Effect Effect;
    public string Name;
    public string Label;
    public string Tooltip;
    public bool Enabled;
    public bool Hidden;
    public string Id => Name + "@" + Effect.Name;
}

// what the render thread runs, in order
public sealed class RenderItem
{
    public Effect Effect;
    public string Technique;
}

// main thread side of the mod: effects, techniques, preset, settings
public sealed class Runtime
{
    public readonly ReShadeFeature Feature;
    public readonly Settings Settings;
    public Preset Preset;
    public readonly List<Effect> Effects = new();
    public readonly List<TechniqueEntry> Techniques = new();
    // the UI tab: same effects, own copies with their own values, drawn on the UI layer
    public readonly List<TechniqueEntry> UiTechniques = new();
    private readonly Dictionary<Effect, Effect> uiCopies = new();
    private readonly Dictionary<Effect, Effect> screenCopies = new();
    public LayerFeature Layers;
    public readonly Dictionary<Effect, Effect.CompileResult> Listed = new();
    public string StatusMessage = "";
    public readonly Packages Packages = new();
    // a preset waiting for its packages to finish installing
    public string PendingPreset;

    private readonly Dictionary<Effect, int> compiledWidth = new();
    private int running;
    private DateTime presetDirtySince = DateTime.MaxValue;
    private int tick;

    public Runtime(ReShadeFeature feature)
    {
        Feature = feature;
        Settings = Settings.Load(ReShadeMod.SettingsFile);
        Directory.CreateDirectory(ReShadeMod.ShadersDir);
        Directory.CreateDirectory(ReShadeMod.TexturesDir);
        Preset = Preset.Load(Settings.PresetPath);
        ScanEffects();
        Feature.IncludePaths = IncludePaths();
        Feature.TexturePaths = new List<string> { ReShadeMod.TexturesDir };
    }

    public IList<string> Definitions => Settings.GlobalDefinitions.Concat(Preset.Definitions).ToList();

    // ReShade searches its effect paths recursively and loads every .fx it finds
    public void ScanEffects()
    {
        var found = Directory.Exists(ReShadeMod.ShadersDir)
            ? Directory.EnumerateFiles(ReShadeMod.ShadersDir, "*.fx", SearchOption.AllDirectories).OrderBy(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase).ToList()
            : new List<string>();
        foreach (Effect gone in Effects.Where(e => !found.Contains(e.FilePath)).ToList())
        {
            Effects.Remove(gone);
            Techniques.RemoveAll(t => t.Effect == gone);
            UiTechniques.RemoveAll(t => t.Effect.Source == gone);
            uiCopies.Remove(gone);
            screenCopies.Remove(gone);
            Listed.Remove(gone);
        }
        foreach (string path in found)
        {
            if (!Effects.Any(e => e.FilePath == path))
            {
                Effects.Add(new Effect(path));
            }
        }
        foreach (Effect e in Effects)
        {
            e.Stale = true;
        }
        Feature.IncludePaths = IncludePaths();
        Publish();
    }

    private List<string> IncludePaths()
    {
        var paths = new List<string> { ReShadeMod.ShadersDir };
        if (Directory.Exists(ReShadeMod.ShadersDir))
        {
            paths.AddRange(Directory.EnumerateDirectories(ReShadeMod.ShadersDir, "*", SearchOption.AllDirectories));
        }
        return paths;
    }

    // main thread, every frame
    public void Tick()
    {
        tick++;
        if (tick % 30 == 0)
        {
            foreach (Effect e in Effects)
            {
                e.CheckSource();
            }
            if (File.Exists(ReShadeMod.ScreenshotTriggerFile))
            {
                File.Delete(ReShadeMod.ScreenshotTriggerFile);
                ReShadeMod.TakeScreenshot();
            }
        }
        if (Packages.ReloadNeeded)
        {
            // newly installed or removed shaders get picked up right away
            Packages.ReloadNeeded = false;
            ScanEffects();
        }
        if (PendingPreset != null && !Packages.Busy && !Packages.ReloadNeeded)
        {
            string path = PendingPreset;
            PendingPreset = null;
            LoadPreset(path);
        }
        int width = Feature.SceneWidth, height = Feature.SceneHeight;
        if (width > 0)
        {
            ScheduleCompiles(width, height);
        }
        // pick up finished compiles
        bool changed = false;
        foreach (Effect e in Effects)
        {
            Effect.CompileResult r = e.Result;
            if (r != null && (!Listed.TryGetValue(e, out var seen) || seen != r))
            {
                Listed[e] = r;
                RefreshTechniques(e, r);
                changed = true;
                if (!r.Success)
                {
                    Feature.Say($"[ReShade] {e.Name} failed to compile:\n{r.Errors}");
                }
            }
        }
        if (changed)
        {
            Publish();
        }
        if (DateTime.UtcNow - presetDirtySince > TimeSpan.FromSeconds(1))
        {
            SavePreset();
        }
    }

    private void ScheduleCompiles(int width, int height)
    {
        IList<string> defines = Definitions;
        string defineKey = string.Join("\n", defines);
        IList<string> include = Feature.IncludePaths;
        foreach (Effect e in Effects)
        {
            if (running >= 2)
            {
                return;
            }
            Effect.CompileResult r = e.Result;
            bool outdated = r == null || r.Width != width || r.Height != height || r.Defines != defineKey;
            if (e.Compiling || !(e.Stale || outdated))
            {
                continue;
            }
            e.Stale = false;
            e.Compiling = true;
            System.Threading.Interlocked.Increment(ref running);
            string path = e.FilePath;
            Task.Run(() =>
            {
                try
                {
                    e.Result = Effect.Compile(path, width, height, include, defines);
                }
                catch (Exception ex)
                {
                    e.Result = new Effect.CompileResult { Errors = ex.Message, Width = width, Height = height, Defines = defineKey };
                }
                finally
                {
                    e.Compiling = false;
                    System.Threading.Interlocked.Decrement(ref running);
                }
            });
        }
    }

    public Effect UiCopy(Effect e)
    {
        if (!uiCopies.TryGetValue(e, out Effect copy))
        {
            uiCopies[e] = copy = new Effect(e, shareValues: false);
        }
        return copy;
    }

    // the Game list run on the whole screen shares the Game tab's values, it's the same list
    private Effect ScreenCopy(Effect e)
    {
        if (!screenCopies.TryGetValue(e, out Effect copy))
        {
            screenCopies[e] = copy = new Effect(e, shareValues: true);
        }
        return copy;
    }

    private void RefreshTechniques(Effect effect, Effect.CompileResult result)
    {
        if (!result.Success)
        {
            // keep the old entries so a typo in the editor doesn't lose the list
            return;
        }
        RefreshList(Techniques, effect, result.Module, Preset.Techniques, Preset.TechniqueSorting);
        Effect ui = UiCopy(effect);
        RefreshList(UiTechniques, ui, result.Module, Preset.TechniquesUI, Preset.TechniqueSortingUI);
        ApplyPresetValues(effect, result.Module, effect.Name);
        ApplyPresetValues(ui, result.Module, UiSection(effect));
    }

    private static string UiSection(Effect e) => "UI:" + e.Name;

    // keeps the user's order and toggles, adds new techniques where the preset says or at the end
    private static void RefreshList(List<TechniqueEntry> list, Effect effect, FxModule module, List<string> enabled, List<string> sorting)
    {
        var old = list.Where(t => t.Effect == effect).ToList();
        int insertAt = old.Count > 0 ? list.IndexOf(old[0]) : -1;
        list.RemoveAll(t => t.Effect == effect);
        var fresh = new List<TechniqueEntry>();
        foreach (FxTechnique t in module.techniques)
        {
            var entry = new TechniqueEntry
            {
                Effect = effect,
                Name = t.name,
                Label = t.annotations.GetString("ui_label") ?? t.name,
                Tooltip = t.annotations.GetString("ui_tooltip"),
                Hidden = (t.annotations.Find("hidden")?.value.i[0] ?? 0) != 0
            };
            TechniqueEntry previous = old.FirstOrDefault(o => o.Name == t.name);
            entry.Enabled = previous?.Enabled ?? enabled.Contains(entry.Id);
            fresh.Add(entry);
        }
        if (insertAt >= 0)
        {
            list.InsertRange(Math.Min(insertAt, list.Count), fresh);
        }
        else
        {
            list.AddRange(fresh);
            SortBy(list, sorting);
        }
    }

    private static void SortBy(List<TechniqueEntry> list, List<string> order)
    {
        int Rank(TechniqueEntry t)
        {
            int i = order.IndexOf(t.Id);
            return i < 0 ? int.MaxValue : i;
        }
        var sorted = list.Select((t, i) => (t, i)).OrderBy(x => Rank(x.t)).ThenBy(x => x.i).Select(x => x.t).ToList();
        list.Clear();
        list.AddRange(sorted);
    }

    // starts from the effect's own defaults, then the preset's values on top
    private void ApplyPresetValues(Effect effect, FxModule module, string section)
    {
        var values = new Dictionary<string, float[]>();
        Preset.Values.TryGetValue(section, out var saved);
        foreach (FxUniform u in module.uniforms)
        {
            if (u.annotations.GetString("source") != null || u.type.array_length > 0)
            {
                continue;
            }
            if (effect.Values.TryGetValue(u.name, out float[] current))
            {
                values[u.name] = current;
            }
            else if (saved != null && saved.TryGetValue(u.name, out float[] fromPreset))
            {
                values[u.name] = fromPreset;
            }
            else
            {
                values[u.name] = Variables.Defaults(u);
            }
        }
        effect.Values = values;
    }

    public void SetValue(Effect effect, string name, float[] value)
    {
        var copy = new Dictionary<string, float[]>(effect.Values) { [name] = value };
        effect.Values = copy;
        MarkPresetDirty();
    }

    public void MarkPresetDirty()
    {
        if (presetDirtySince == DateTime.MaxValue)
        {
            presetDirtySince = DateTime.UtcNow;
        }
    }

    // publishes the enabled techniques to the render thread and remembers them in the preset
    public void Publish()
    {
        // the Game list runs on the map, or with "also change the UI" on the whole screen after the UI is drawn, like ReShade
        bool whole = Preset.GameChangesUI && Layers != null;
        RenderItem[] game = Techniques.Where(t => t.Enabled).Select(t => new RenderItem { Effect = whole ? ScreenCopy(t.Effect) : t.Effect, Technique = t.Name }).ToArray();
        Feature.RenderList = whole ? Array.Empty<RenderItem>() : game;
        if (Layers != null)
        {
            Layers.ScreenList = whole ? game : Array.Empty<RenderItem>();
            // the UI tab's effects only run while the tab is turned on in the settings
            Layers.UiList = Settings.ShowUiTab ? UiTechniques.Where(t => t.Enabled).Select(t => new RenderItem { Effect = t.Effect, Technique = t.Name }).ToArray() : Array.Empty<RenderItem>();
            // the UI is only taken over from the game while something needs it
            UiCapture.Wanted = Layers.ScreenList.Length > 0 || Layers.UiList.Length > 0;
        }
        MarkPresetDirty();
    }

    public void SavePreset()
    {
        presetDirtySince = DateTime.MaxValue;
        // effects still compiling keep what the preset had for them
        var listedNames = new HashSet<string>(Listed.Where(kv => kv.Value.Success).Select(kv => kv.Key.Name), StringComparer.OrdinalIgnoreCase);
        bool Pending(string id) => !listedNames.Contains(id.Substring(id.IndexOf('@') + 1));
        Preset.Techniques = Techniques.Where(t => t.Enabled).Select(t => t.Id).Concat(Preset.Techniques.Where(Pending)).Distinct().ToList();
        Preset.TechniqueSorting = Techniques.Select(t => t.Id).Concat(Preset.TechniqueSorting.Where(Pending)).Distinct().ToList();
        Preset.TechniquesUI = UiTechniques.Where(t => t.Enabled).Select(t => t.Id).Concat(Preset.TechniquesUI.Where(Pending)).Distinct().ToList();
        // the UI order only matters once something there was turned on
        Preset.TechniqueSortingUI = Preset.TechniquesUI.Count == 0 && Preset.TechniqueSortingUI.Count == 0
            ? new List<string>()
            : UiTechniques.Select(t => t.Id).Concat(Preset.TechniqueSortingUI.Where(Pending)).Distinct().ToList();
        foreach (Effect e in Effects)
        {
            if (!Listed.TryGetValue(e, out var r) || r.Module == null)
            {
                continue;
            }
            SaveValues(e, r.Module, e.Name);
            if (uiCopies.TryGetValue(e, out Effect ui))
            {
                SaveValues(ui, r.Module, UiSection(e));
            }
        }
        try
        {
            Preset.Save();
        }
        catch (Exception ex)
        {
            StatusMessage = "Could not save preset: " + ex.Message;
        }
    }

    // only values that differ from the effect's defaults go in the preset, like ReShade
    private void SaveValues(Effect e, FxModule module, string name)
    {
        var section = new Dictionary<string, float[]>();
        foreach (FxUniform u in module.uniforms)
        {
            if (e.Values.TryGetValue(u.name, out float[] v) && !v.SequenceEqual(Variables.Defaults(u)))
            {
                section[u.name] = v;
            }
        }
        if (section.Count > 0)
        {
            Preset.Values[name] = section;
        }
        else
        {
            Preset.Values.Remove(name);
        }
    }

    public void LoadPreset(string path)
    {
        SavePreset();
        Preset = Preset.Load(path);
        Settings.PresetPath = path;
        Settings.Save();
        foreach (TechniqueEntry t in Techniques)
        {
            t.Enabled = Preset.Techniques.Contains(t.Id);
        }
        foreach (TechniqueEntry t in UiTechniques)
        {
            t.Enabled = Preset.TechniquesUI.Contains(t.Id);
        }
        SortBy(Techniques, Preset.TechniqueSorting);
        SortBy(UiTechniques, Preset.TechniqueSortingUI);
        foreach (Effect e in Effects)
        {
            e.Values = new Dictionary<string, float[]>();
            Effect ui = UiCopy(e);
            ui.Values = new Dictionary<string, float[]>();
            if (Listed.TryGetValue(e, out var r) && r.Module != null)
            {
                ApplyPresetValues(e, r.Module, e.Name);
                ApplyPresetValues(ui, r.Module, UiSection(e));
            }
        }
        Publish();
    }

    // copies a preset from elsewhere into the ReShade folder (keeping both if the name is taken) and loads it,
    // after the packages it needs are installed when there are any
    public string ImportPreset(string source, bool waitForPackages)
    {
        string name = Path.GetFileName(source);
        string target = Path.Combine(ReShadeMod.BaseDir, name);
        if (Path.GetFullPath(source) != Path.GetFullPath(target))
        {
            for (int i = 2; File.Exists(target) && File.ReadAllText(target) != File.ReadAllText(source); i++)
            {
                target = Path.Combine(ReShadeMod.BaseDir, Path.GetFileNameWithoutExtension(name) + " " + i + Path.GetExtension(name));
            }
            Directory.CreateDirectory(ReShadeMod.BaseDir);
            File.Copy(source, target, true);
        }
        if (waitForPackages)
        {
            PendingPreset = target;
        }
        else
        {
            LoadPreset(target);
        }
        return target;
    }

    // presets other mods ship in a "presets" folder, like the old TiagoReshadePresets
    public IEnumerable<(string mod, string path)> ModPresets()
    {
        foreach (Verse.ModContentPack mod in Verse.LoadedModManager.RunningMods)
        {
            foreach (string dir in new[] { "presets", "Presets", "ReShade" })
            {
                string folder = Path.Combine(mod.RootDir, dir);
                if (!Directory.Exists(folder))
                {
                    continue;
                }
                foreach (string ini in Directory.EnumerateFiles(folder, "*.ini", SearchOption.AllDirectories))
                {
                    yield return (mod.Name, ini);
                }
            }
        }
    }

    public IEnumerable<string> PresetFiles()
    {
        if (!Directory.Exists(ReShadeMod.BaseDir))
        {
            return Enumerable.Empty<string>();
        }
        return Directory.EnumerateFiles(ReShadeMod.BaseDir, "*.ini")
            .Where(p => !string.Equals(Path.GetFileName(p), Path.GetFileName(ReShadeMod.SettingsFile), StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase);
    }

    public void ReloadAll()
    {
        ScanEffects();
    }
}

// ReShade.ini: overlay and screenshot keys, the current preset, global definitions
public sealed class Settings
{
    public string Path;
    public int OverlayKey = 268;      // GLFW Home
    public int ScreenshotKey = 283;   // GLFW PrintScreen
    public bool SaveBeforeAndAfter = true;
    // the UI tab is for people who want it, off by default
    public bool ShowUiTab;
    public string ScreenshotPath;
    public string PresetPath;
    public List<string> GlobalDefinitions = new();

    public static Settings Load(string path)
    {
        var s = new Settings
        {
            Path = path,
            ScreenshotPath = ReShadeMod.ScreenshotsDir,
            PresetPath = System.IO.Path.Combine(ReShadeMod.BaseDir, "ReShadePreset.ini")
        };
        if (!File.Exists(path))
        {
            return s;
        }
        foreach (string raw in File.ReadAllLines(path))
        {
            int eq = raw.IndexOf('=');
            if (eq < 0)
            {
                continue;
            }
            string key = raw.Substring(0, eq).Trim(), value = raw.Substring(eq + 1).Trim();
            switch (key)
            {
                case "KeyOverlay": int.TryParse(value, out s.OverlayKey); break;
                case "KeyScreenshot": int.TryParse(value, out s.ScreenshotKey); break;
                case "SaveBeforeShot": s.SaveBeforeAndAfter = value == "1"; break;
                case "ShowUiTab": s.ShowUiTab = value == "1"; break;
                case "SavePath": s.ScreenshotPath = ReShadeMod.FromStored(value); break;
                case "PresetPath": s.PresetPath = ReShadeMod.FromStored(value); break;
                case "PreprocessorDefinitions": s.GlobalDefinitions = value.Split(',').Select(v => v.Trim()).Where(v => v.Length > 0).ToList(); break;
            }
        }
        return s;
    }

    public void Save()
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path));
        File.WriteAllLines(Path, new[]
        {
            "[GENERAL]",
            "PresetPath=" + ReShadeMod.ToStored(PresetPath),
            "PreprocessorDefinitions=" + string.Join(",", GlobalDefinitions),
            "",
            "[INPUT]",
            "KeyOverlay=" + OverlayKey.ToString(CultureInfo.InvariantCulture),
            "KeyScreenshot=" + ScreenshotKey.ToString(CultureInfo.InvariantCulture),
            "",
            "[SCREENSHOT]",
            "SavePath=" + ReShadeMod.ToStored(ScreenshotPath),
            "SaveBeforeShot=" + (SaveBeforeAndAfter ? "1" : "0"),
            "",
            "[OVERLAY]",
            "ShowUiTab=" + (ShowUiTab ? "1" : "0")
        });
    }
}
