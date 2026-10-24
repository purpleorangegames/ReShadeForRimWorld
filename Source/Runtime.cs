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

    // keeps the user's order and toggles, adds new techniques where the preset says or at the end
    private void RefreshTechniques(Effect effect, Effect.CompileResult result)
    {
        var old = Techniques.Where(t => t.Effect == effect).ToList();
        if (!result.Success)
        {
            // keep the old entries so a typo in the editor doesn't lose the list
            return;
        }
        int insertAt = old.Count > 0 ? Techniques.IndexOf(old[0]) : -1;
        Techniques.RemoveAll(t => t.Effect == effect);
        var fresh = new List<TechniqueEntry>();
        foreach (FxTechnique t in result.Module.techniques)
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
            entry.Enabled = previous?.Enabled ?? Preset.Techniques.Contains(entry.Id);
            fresh.Add(entry);
        }
        if (insertAt >= 0)
        {
            Techniques.InsertRange(Math.Min(insertAt, Techniques.Count), fresh);
        }
        else
        {
            Techniques.AddRange(fresh);
            SortByPreset();
        }
        ApplyPresetValues(effect, result.Module);
    }

    private void SortByPreset()
    {
        List<string> order = Preset.TechniqueSorting;
        int Rank(TechniqueEntry t)
        {
            int i = order.IndexOf(t.Id);
            return i < 0 ? int.MaxValue : i;
        }
        var sorted = Techniques.Select((t, i) => (t, i)).OrderBy(x => Rank(x.t)).ThenBy(x => x.i).Select(x => x.t).ToList();
        Techniques.Clear();
        Techniques.AddRange(sorted);
    }

    // starts from the effect's own defaults, then the preset's values on top
    private void ApplyPresetValues(Effect effect, FxModule module)
    {
        var values = new Dictionary<string, float[]>();
        Preset.Values.TryGetValue(effect.Name, out var saved);
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
        Feature.RenderList = Techniques.Where(t => t.Enabled).Select(t => new RenderItem { Effect = t.Effect, Technique = t.Name }).ToArray();
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
        foreach (Effect e in Effects)
        {
            if (!Listed.TryGetValue(e, out var r) || r.Module == null)
            {
                continue;
            }
            // only values that differ from the effect's defaults go in the preset, like ReShade
            var section = new Dictionary<string, float[]>();
            foreach (FxUniform u in r.Module.uniforms)
            {
                if (e.Values.TryGetValue(u.name, out float[] v) && !v.SequenceEqual(Variables.Defaults(u)))
                {
                    section[u.name] = v;
                }
            }
            if (section.Count > 0)
            {
                Preset.Values[e.Name] = section;
            }
            else
            {
                Preset.Values.Remove(e.Name);
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
        SortByPreset();
        foreach (Effect e in Effects)
        {
            e.Values = new Dictionary<string, float[]>();
            if (Listed.TryGetValue(e, out var r) && r.Module != null)
            {
                ApplyPresetValues(e, r.Module);
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
            "SaveBeforeShot=" + (SaveBeforeAndAfter ? "1" : "0")
        });
    }
}
