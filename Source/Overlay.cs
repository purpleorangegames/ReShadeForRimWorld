using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Brutal.GlfwApi;
using Brutal.ImGuiApi;
using Brutal.Numerics;

namespace ReShadeForRimWorld;

// the ReShade style overlay, drawn with the game's own Dear ImGui context
public static class Overlay
{
    // RESHADE_OVERLAY_OPEN=1 opens it at start, handy when testing without a keyboard
    public static bool Open = Environment.GetEnvironmentVariable("RESHADE_OVERLAY_OPEN") == "1";
    // 1 = waiting for the overlay key, 2 = waiting for the screenshot key
    public static int WaitingForKey;

    private static readonly DateTime started = DateTime.UtcNow;
    private static bool testEditorOpened;
    private static readonly ImInputString filter = new(128);
    private static readonly ImInputString newPresetName = new(128);
    private static readonly ImInputString screenshotPath = new(1024);
    private static ImInputString globalDefinitions, presetDefinitions;
    private static readonly float4 red = new() { X = 1f, Y = 0.4f, Z = 0.4f, W = 1f };
    private static readonly float4 grey = new() { X = 0.6f, Y = 0.6f, Z = 0.6f, W = 1f };
    private static readonly float4 green = new() { X = 0.5f, Y = 0.9f, Z = 0.5f, W = 1f };
    private static readonly float4 yellow = new() { X = 1f, Y = 0.85f, Z = 0.4f, W = 1f };

    public static string KeyName(int key) => Enum.IsDefined(typeof(GlfwKey), key) ? ((GlfwKey)key).ToString() : "Key " + key;

    // main thread, inside the game's ImGui frame
    public static void Draw()
    {
        Runtime rt = ReShadeMod.Runtime;
        if (rt == null)
        {
            return;
        }
        string editFile = Environment.GetEnvironmentVariable("RESHADE_EDIT_FILE");
        if (editFile != null && !testEditorOpened)
        {
            testEditorOpened = true;
            Editor.Open(editFile);
        }
        if (!Open)
        {
            DrawBanner(rt);
        }
        else
        {
            DrawWindow(rt);
        }
        if (importOpen)
        {
            DrawImport(rt);
        }
        Editor.DrawAll(rt);
    }

    // like ReShade's startup message, shown for a few seconds or while effects compile
    private static void DrawBanner(Runtime rt)
    {
        int compiling = rt.Effects.Count(e => e.Compiling || e.Result == null);
        bool early = DateTime.UtcNow - started < TimeSpan.FromSeconds(12);
        if (!early && compiling == 0)
        {
            return;
        }
        ImGui.SetNextWindowPos(new float2 { X = 10, Y = 10 });
        ImGuiWindowFlags flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoInputs | ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoNav;
        if (ImGui.Begin("##ReShadeBanner", flags))
        {
            ImGui.Text("ReShade for RimWorld (unofficial)");
            ImGui.Text($"Press {KeyName(rt.Settings.OverlayKey)} to open the overlay.");
            if (compiling > 0)
            {
                ImGui.Text($"Compiling effects... {rt.Effects.Count - compiling}/{rt.Effects.Count}");
            }
            int failed = rt.Listed.Count(kv => !kv.Value.Success);
            if (failed > 0)
            {
                ImGui.TextColored(red, $"{failed} effect(s) failed to compile, see the overlay.");
            }
        }
        ImGui.End();
    }

    private static void DrawWindow(Runtime rt)
    {
        ImGui.SetNextWindowPos(new float2 { X = 20, Y = 20 }, ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(new float2 { X = 680, Y = 820 }, ImGuiCond.FirstUseEver);
        bool open = true;
        if (ImGui.Begin("ReShade for RimWorld##ReShadeMain", ref open))
        {
            DrawPresetBar(rt);
            if (ImGui.BeginTabBar("##tabs"))
            {
                if (ImGui.BeginTabItem("Game"))
                {
                    DrawTechniques(rt, rt.Techniques, game: true);
                    ImGui.EndTabItem();
                }
                if (rt.Settings.ShowUiTab && ImGui.BeginTabItem("UI", TabFlags("UI")))
                {
                    DrawTechniques(rt, rt.UiTechniques, game: false);
                    ImGui.EndTabItem();
                }
                if (ImGui.BeginTabItem("Shaders", TabFlags("Shaders")))
                {
                    DrawPackages(rt);
                    ImGui.EndTabItem();
                }
                if (ImGui.BeginTabItem("Settings"))
                {
                    DrawSettings(rt);
                    ImGui.EndTabItem();
                }
                if (ImGui.BeginTabItem("About"))
                {
                    DrawAbout();
                    ImGui.EndTabItem();
                }
                ImGui.EndTabBar();
            }
        }
        ImGui.End();
        if (!open)
        {
            Open = false;
        }
    }

    // RESHADE_OVERLAY_TAB=<name> picks a tab on the first frame, for testing without a mouse
    private static string startTab = Environment.GetEnvironmentVariable("RESHADE_OVERLAY_TAB");

    private static ImGuiTabItemFlags TabFlags(string name)
    {
        if (startTab != name)
        {
            return ImGuiTabItemFlags.None;
        }
        startTab = null;
        return ImGuiTabItemFlags.SetSelected;
    }

    private static void DrawPresetBar(Runtime rt)
    {
        // preset row
        string current = Path.GetFileName(rt.Preset.Path);
        ImGui.PushItemWidth(-220);
        if (ImGui.BeginCombo("##preset", current))
        {
            foreach (string file in rt.PresetFiles())
            {
                if (ImGui.Selectable(Path.GetFileName(file), file == rt.Preset.Path))
                {
                    rt.LoadPreset(file);
                }
            }
            ImGui.EndCombo();
        }
        ImGui.PopItemWidth();
        ImGui.SameLine();
        if (ImGui.Button("+##newpreset"))
        {
            newPresetName.Clear();
            ImGui.OpenPopup("New preset");
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("New preset");
        }
        ImGui.SameLine();
        if (ImGui.Button("Import"))
        {
            importOpen = true;
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Load a preset .ini and install the shaders it needs");
        }
        ImGui.SameLine();
        if (ImGui.Button("Reload"))
        {
            rt.ReloadAll();
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Find .fx files again and recompile everything");
        }
        if (ImGui.BeginPopup("New preset"))
        {
            ImGui.Text("Name:");
            ImGui.InputText("##name", newPresetName);
            if (ImGui.Button("Create") && newPresetName.Length > 0)
            {
                string name = newPresetName.ToString().Trim();
                if (!name.EndsWith(".ini", StringComparison.OrdinalIgnoreCase))
                {
                    name += ".ini";
                }
                string path = Path.Combine(ReShadeMod.BaseDir, string.Concat(name.Split(Path.GetInvalidFileNameChars())));
                rt.SavePreset();
                // a new preset starts as a copy of the current one, like in ReShade
                rt.Preset.Path = path;
                rt.SavePreset();
                rt.LoadPreset(path);
                ImGui.CloseCurrentPopup();
            }
            ImGui.EndPopup();
        }

        int failedCount = rt.Listed.Count(kv => !kv.Value.Success);
        int waiting = rt.Effects.Count(e => e.Compiling || e.Result == null);
        int techniques = rt.Techniques.Count(t => !t.Hidden);
        int gameOn = rt.Techniques.Count(t => t.Enabled);
        int uiOn = rt.UiTechniques.Count(t => t.Enabled);
        ImGui.Text(rt.Settings.ShowUiTab
            ? $"{rt.Effects.Count} effects, {techniques} techniques, {gameOn} on in Game, {uiOn} on in UI"
            : $"{rt.Effects.Count} effects, {techniques} techniques, {gameOn} enabled");
        if (failedCount > 0)
        {
            ImGui.SameLine();
            ImGui.TextColored(red, $"{failedCount} failed");
        }
        if (waiting > 0)
        {
            ImGui.SameLine();
            ImGui.TextColored(yellow, $"{waiting} compiling");
        }

        // the usual reason a whole folder fails: ReShade.fxh comes with the Standard effects package
        if (rt.Listed.Any(kv => !kv.Value.Success && kv.Value.Errors.IndexOf("ReShade.fxh", StringComparison.OrdinalIgnoreCase) >= 0))
        {
            ImGui.TextColored(yellow, "Some effects need ReShade.fxh. Install \"Standard effects\" in the Shaders tab, or copy it into the shaders folder.");
        }
    }

    // Game: effects on the map (or the whole screen with +UI). UI: effects on the UI layer only.
    private static void DrawTechniques(Runtime rt, List<TechniqueEntry> list, bool game)
    {
        if (game && rt.Layers != null)
        {
            bool whole = rt.Preset.GameChangesUI;
            if (ImGui.Checkbox("Game effects also change the UI", ref whole))
            {
                rt.Preset.GameChangesUI = whole;
                rt.Publish();
            }
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("Off: only the map changes. On: the whole list runs on the map and the UI together, like ReShade does.");
            }
        }
        if (!game)
        {
            ImGui.TextWrapped("Effects here only change the UI, the map stays as it is.");
            if (rt.Layers == null || UiCapture.Broken)
            {
                ImGui.TextColored(red, "UI layers are off, see the log for why.");
            }
        }
        ImGui.PushItemWidth(-110);
        ImGui.InputTextWithHint("##filter", "Search techniques", filter.Buffer);
        ImGui.PopItemWidth();
        ImGui.SameLine();
        if (ImGui.Button("Active to top"))
        {
            // enabled ones first, each group keeps its order, so the chain runs the same
            var sorted = list.Where(t => t.Enabled).Concat(list.Where(t => !t.Enabled)).ToList();
            list.Clear();
            list.AddRange(sorted);
            rt.Publish();
        }
        filter.EvaluateLength();
        string search = filter.ToString().Trim();

        // technique list
        float listHeight = Math.Max(160f, ImGui.GetContentRegionAvail().Y * 0.45f);
        if (ImGui.BeginChild("##techniques", new float2 { X = 0, Y = listHeight }, ImGuiChildFlags.Borders))
        {
            foreach (var failed in rt.Listed.Where(kv => game && !kv.Value.Success).OrderBy(kv => kv.Key.Name))
            {
                ImGui.TextColored(red, $"{failed.Key.Name} failed to compile");
                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip(failed.Value.Errors);
                }
                if (ImGui.IsItemClicked(ImGuiMouseButton.Right) || ImGui.IsItemClicked())
                {
                    Editor.Open(failed.Key.FilePath);
                }
            }
            // at most one move per frame, the rows are laid out again before the next one
            bool moved = false;
            for (int i = 0; i < list.Count; i++)
            {
                TechniqueEntry t = list[i];
                if (t.Hidden || (search.Length > 0 && t.Label.IndexOf(search, StringComparison.OrdinalIgnoreCase) < 0 && t.Effect.Name.IndexOf(search, StringComparison.OrdinalIgnoreCase) < 0))
                {
                    continue;
                }
                ImGui.PushID(t.Id);
                bool enabled = t.Enabled;
                if (ImGui.Checkbox("##on", ref enabled))
                {
                    t.Enabled = enabled;
                    rt.Publish();
                    if (enabled)
                    {
                        Select(t);
                    }
                }
                ImGui.SameLine();
                if (ImGui.Selectable($"{t.Label}  [{t.Effect.Name}]", selectedId == t.Id && selectedEffect == t.Effect))
                {
                    Select(t);
                }
                if (t.Tooltip != null && ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip(t.Tooltip);
                }
                // drag to reorder: move one visible row at a time, only once the mouse is past this row
                if (ImGui.IsItemActive() && search.Length == 0)
                {
                    float mouseY = ImGui.GetMousePos().Y;
                    int direction = mouseY < ImGui.GetItemRectMin().Y ? -1 : mouseY > ImGui.GetItemRectMax().Y ? 1 : 0;
                    int next = i + direction;
                    while (direction != 0 && next >= 0 && next < list.Count && list[next].Hidden)
                    {
                        next += direction;
                    }
                    if (direction != 0 && next >= 0 && next < list.Count && !moved)
                    {
                        list.RemoveAt(i);
                        list.Insert(next, t);
                        moved = true;
                        rt.Publish();
                    }
                }
                if (ImGui.BeginPopupContextItem("##ctx"))
                {
                    if (ImGui.Selectable("Edit source code"))
                    {
                        Editor.Open(t.Effect.FilePath);
                    }
                    if (ImGui.Selectable("Move to top"))
                    {
                        list.Remove(t);
                        list.Insert(0, t);
                        rt.Publish();
                    }
                    if (ImGui.Selectable("Move to bottom"))
                    {
                        list.Remove(t);
                        list.Add(t);
                        rt.Publish();
                    }
                    ImGui.EndPopup();
                }
                ImGui.PopID();
            }
            int pending = rt.Effects.Count(e => e.Compiling || e.Result == null);
            if (pending > 0)
            {
                ImGui.TextColored(grey, $"Compiling {pending} effect(s)...");
            }
            if (rt.Effects.Count == 0)
            {
                ImGui.TextWrapped("No effects yet. Install packages in the Shaders tab, or copy your own into " + ReShadeMod.ShadersDir);
            }
        }
        ImGui.EndChild();
        ImGui.TextColored(grey, "Drag to reorder, right click for more.");

        // variables of the enabled effects
        if (ImGui.BeginChild("##variables", new float2 { X = 0, Y = 0 }, ImGuiChildFlags.Borders))
        {
            var effects = list.Where(t => t.Enabled).Select(t => t.Effect).Distinct().ToList();
            // a technique that is off has no settings shown, nothing to jump to
            if (scrollToSelected && !effects.Contains(selectedEffect))
            {
                scrollToSelected = false;
            }
            foreach (Effect e in effects)
            {
                if (!rt.Listed.TryGetValue(e.Source ?? e, out var result) || result.Module == null)
                {
                    continue;
                }
                var uniforms = result.Module.uniforms.Where(Variables.Editable).ToList();
                if (uniforms.Count == 0)
                {
                    continue;
                }
                ImGui.PushID(e.FilePath);
                bool jump = scrollToSelected && e == selectedEffect;
                if (jump)
                {
                    ImGui.SetNextItemOpen(true);
                }
                bool open = ImGui.CollapsingHeader(e.Name, ImGuiTreeNodeFlags.DefaultOpen);
                if (jump)
                {
                    ImGui.SetScrollHereY(0f);
                    scrollToSelected = false;
                }
                if (open)
                {
                    string category = null;
                    foreach (FxUniform u in uniforms)
                    {
                        string c = u.annotations.GetString("ui_category") ?? "";
                        if (c != category)
                        {
                            category = c;
                            if (c.Length > 0)
                            {
                                ImGui.SeparatorText(c);
                            }
                        }
                        Dictionary<string, float[]> values = e.Values;
                        float[] v = values.TryGetValue(u.name, out float[] cur) ? cur : Variables.Defaults(u);
                        float[] changed = Variables.Draw(u, v);
                        if (changed != null)
                        {
                            rt.SetValue(e, u.name, changed);
                        }
                    }
                    if (ImGui.Button("Reset to defaults"))
                    {
                        foreach (FxUniform u in uniforms)
                        {
                            rt.SetValue(e, u.name, Variables.Defaults(u));
                        }
                    }
                }
                ImGui.PopID();
            }
        }
        ImGui.EndChild();
    }

    // the technique last clicked or turned on; its settings get scrolled into view once
    private static Effect selectedEffect;
    private static string selectedId;
    private static bool scrollToSelected;

    private static void Select(TechniqueEntry t)
    {
        selectedEffect = t.Effect;
        selectedId = t.Id;
        scrollToSelected = true;
    }

    private static bool packagesRequested;

    // Import: pick a preset .ini, see which packages it needs, install them and load it
    private static bool importOpen;
    private static string browseDir;
    private static string importPath;
    private static Packages.PresetPlan importPlan;
    private static int planListCount = -1;
    private static readonly ImInputString importPathInput = new(1024);

    private static void ChooseImport(string path)
    {
        importPath = path;
        importPlan = null;
        importPathInput.SetValue(path);
    }

    private static void DrawImport(Runtime rt)
    {
        Packages pk = rt.Packages;
        ImGui.SetNextWindowSize(new float2 { X = 620, Y = 640 }, ImGuiCond.FirstUseEver);
        bool open = true;
        if (ImGui.Begin("Import preset##ReShadeImport", ref open))
        {
            var fromMods = rt.ModPresets().GroupBy(x => Path.GetFullPath(x.path).ToLowerInvariant()).Select(g => g.First()).ToList();
            if (fromMods.Count > 0)
            {
                ImGui.SeparatorText("Presets from your mods");
                foreach (var (mod, path) in fromMods)
                {
                    if (ImGui.Selectable($"{mod}: {Path.GetFileName(path)}##{path}", importPath == path))
                    {
                        ChooseImport(path);
                    }
                }
            }

            ImGui.SeparatorText("Or a file");
            ImGui.PushItemWidth(-70);
            ImGui.InputText("##importpath", importPathInput);
            ImGui.PopItemWidth();
            ImGui.SameLine();
            if (ImGui.Button("Use") && File.Exists(importPathInput.ToString().Trim()))
            {
                ChooseImport(importPathInput.ToString().Trim());
            }
            browseDir ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            ImGui.TextDisabled(browseDir);
            if (ImGui.BeginChild("##browse", new float2 { X = 0, Y = 180 }, ImGuiChildFlags.Borders))
            {
                try
                {
                    DirectoryInfo parent = Directory.GetParent(browseDir);
                    if (parent != null && ImGui.Selectable("[..]"))
                    {
                        browseDir = parent.FullName;
                    }
                    foreach (string dir in Directory.EnumerateDirectories(browseDir).Where(d => !Path.GetFileName(d).StartsWith(".")).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
                    {
                        if (ImGui.Selectable("[" + Path.GetFileName(dir) + "]##" + dir))
                        {
                            browseDir = dir;
                        }
                    }
                    foreach (string ini in Directory.EnumerateFiles(browseDir, "*.ini").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                    {
                        if (ImGui.Selectable(Path.GetFileName(ini) + "##" + ini, importPath == ini))
                        {
                            ChooseImport(ini);
                        }
                    }
                }
                catch (Exception e)
                {
                    ImGui.TextColored(red, e.Message);
                }
            }
            ImGui.EndChild();

            if (importPath != null)
            {
                ImGui.SeparatorText(Path.GetFileName(importPath));
                if (pk.List.Count == 0)
                {
                    if (!pk.Busy)
                    {
                        pk.Refresh();
                    }
                    ImGui.Text("Getting ReShade's package list...");
                }
                else
                {
                    if (importPlan == null || importPlan.PresetPath != importPath || planListCount != pk.List.Count)
                    {
                        importPlan = pk.Plan(importPath);
                        planListCount = pk.List.Count;
                    }
                    Packages.PresetPlan plan = importPlan;
                    if (plan.Packages.Count == 0 && plan.NotFound.Count == 0)
                    {
                        ImGui.TextColored(green, plan.AlreadyHave.Count > 0 ? "You already have every effect it uses." : "This preset has no techniques turned on.");
                    }
                    foreach (EffectPackage p in plan.Packages)
                    {
                        List<string> fx = plan.Effects[p];
                        ImGui.BulletText(fx.Count == 0 ? $"{p.Name} (most effects need it)" : $"{p.Name}: {string.Join(", ", fx)}");
                    }
                    if (plan.AlreadyHave.Count > 0)
                    {
                        ImGui.TextColored(grey, $"Already installed: {string.Join(", ", plan.AlreadyHave)}");
                    }
                    if (plan.NotFound.Count > 0)
                    {
                        ImGui.TextColored(yellow, $"Not in any package on ReShade's list, copy these in yourself: {string.Join(", ", plan.NotFound)}");
                    }
                    ImGui.BeginDisabled(pk.Busy);
                    if (plan.Packages.Count > 0 && ImGui.Button($"Install {plan.Packages.Count} package(s) and load"))
                    {
                        pk.InstallFor(plan);
                        rt.ImportPreset(importPath, waitForPackages: true);
                    }
                    if (plan.Packages.Count > 0)
                    {
                        ImGui.SameLine();
                    }
                    if (ImGui.Button("Load only"))
                    {
                        rt.ImportPreset(importPath, waitForPackages: false);
                    }
                    ImGui.EndDisabled();
                }
            }
            if (pk.Busy)
            {
                ImGui.TextColored(yellow, pk.Status);
            }
            else if (rt.PendingPreset == null && pk.Status.Length > 0 && pk.Status.StartsWith("Done"))
            {
                ImGui.Text(pk.Status);
            }
            if (pk.Error.Length > 0)
            {
                ImGui.TextColored(red, pk.Error);
            }
        }
        ImGui.End();
        if (!open)
        {
            importOpen = false;
        }
    }

    // ReShade's installer list of effect packages, installed into reshade-shaders the same way.
    // A tick means "I want this installed": Apply installs new ticks and removes unticked installed packages.
    private static void DrawPackages(Runtime rt)
    {
        Packages pk = rt.Packages;
        if (!packagesRequested)
        {
            packagesRequested = true;
            pk.Refresh();
        }
        ImGui.TextWrapped("Effect packages from ReShade's own list. Tick what you want installed, untick to remove, then Apply. Each package has its own license, see its page.");
        List<EffectPackage> install = pk.ToInstall, remove = pk.ToRemove;
        ImGui.BeginDisabled(pk.Busy);
        if (ImGui.Button("Refresh list"))
        {
            pk.Refresh();
        }
        ImGui.SameLine();
        ImGui.BeginDisabled(install.Count == 0 && remove.Count == 0);
        if (ImGui.Button($"Apply changes ({install.Count} to install, {remove.Count} to remove)"))
        {
            if (remove.Count > 0)
            {
                ImGui.OpenPopup("Remove packages");
            }
            else
            {
                pk.Apply();
            }
        }
        ImGui.EndDisabled();
        ImGui.SameLine();
        if (ImGui.Button("Update installed"))
        {
            pk.Apply(reinstallInstalled: true);
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Download the installed packages again to get their newest versions");
        }
        ImGui.EndDisabled();

        if (ImGui.BeginPopupModal("Remove packages", ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.Text("These packages and their files will be deleted:");
            foreach (EffectPackage p in remove)
            {
                ImGui.BulletText(p.Name);
            }
            if (ImGui.Button("Remove and apply"))
            {
                pk.Apply();
                ImGui.CloseCurrentPopup();
            }
            ImGui.SameLine();
            if (ImGui.Button("Cancel"))
            {
                ImGui.CloseCurrentPopup();
            }
            ImGui.EndPopup();
        }

        if (pk.Busy)
        {
            ImGui.TextColored(yellow, pk.Status);
        }
        else if (pk.Status.Length > 0)
        {
            ImGui.Text(pk.Status);
        }
        if (pk.Error.Length > 0)
        {
            ImGui.TextColored(red, pk.Error);
        }
        lock (pk.Log)
        {
            if (pk.Log.Count > 0 && ImGui.CollapsingHeader($"Log ({pk.Log.Count})", ImGuiTreeNodeFlags.DefaultOpen))
            {
                foreach (string line in pk.Log.Skip(Math.Max(0, pk.Log.Count - 6)))
                {
                    ImGui.TextColored(line.Contains("Failed") ? red : grey, line);
                }
            }
        }

        if (ImGui.BeginChild("##packages", new float2 { X = 0, Y = 0 }, ImGuiChildFlags.Borders))
        {
            foreach (EffectPackage p in pk.List)
            {
                ImGui.PushID(p.Name);
                bool installed = pk.IsInstalled(p);
                bool sel = p.Selected;
                ImGui.BeginDisabled(pk.Busy);
                if (ImGui.Checkbox("##sel", ref sel))
                {
                    p.Selected = sel;
                }
                ImGui.EndDisabled();
                ImGui.SameLine();
                bool expanded = ImGui.TreeNode(p.Name);
                ImGui.SameLine();
                if (installed && p.Selected)
                {
                    ImGui.TextColored(green, "installed");
                }
                else if (installed)
                {
                    ImGui.TextColored(yellow, "will be removed");
                }
                else if (p.Selected)
                {
                    ImGui.TextColored(yellow, "will be installed");
                }
                else
                {
                    ImGui.NewLine();
                }
                if (!string.IsNullOrEmpty(p.Description))
                {
                    ImGui.TextColored(grey, p.Description + (p.Required ? "  Most effects need this package." : ""));
                }
                if (expanded)
                {
                    if (!string.IsNullOrEmpty(p.RepositoryUrl))
                    {
                        ImGui.TextWrapped(p.RepositoryUrl);
                        if (ImGui.Button("Open page"))
                        {
                            UnityEngine.Application.OpenURL(p.RepositoryUrl);
                        }
                    }
                    if (p.EffectFiles.Count > 0)
                    {
                        ImGui.TextColored(grey, "Effects to install:");
                        foreach (string fx in p.EffectFiles)
                        {
                            bool keep = !p.SkippedEffects.Contains(fx);
                            if (ImGui.Checkbox(fx, ref keep))
                            {
                                if (keep)
                                {
                                    p.SkippedEffects.Remove(fx);
                                }
                                else
                                {
                                    p.SkippedEffects.Add(fx);
                                }
                            }
                        }
                    }
                    ImGui.TreePop();
                }
                ImGui.Separator();
                ImGui.PopID();
            }
            if (pk.List.Count == 0 && !pk.Busy)
            {
                ImGui.Text("No package list yet.");
            }
        }
        ImGui.EndChild();
    }

    private static void DrawSettings(Runtime rt)
    {
        Settings s = rt.Settings;
        ImGui.SeparatorText("Keys");
        if (ImGui.Button((WaitingForKey == 1 ? "Press a key..." : KeyName(s.OverlayKey)) + "##overlaykey"))
        {
            WaitingForKey = 1;
        }
        ImGui.SameLine();
        ImGui.Text("Overlay key");
        if (ImGui.Button((WaitingForKey == 2 ? "Press a key..." : KeyName(s.ScreenshotKey)) + "##shotkey"))
        {
            WaitingForKey = 2;
        }
        ImGui.SameLine();
        ImGui.Text("Screenshot key");

        ImGui.SeparatorText("Overlay");
        bool showUi = s.ShowUiTab;
        if (ImGui.Checkbox("Show the UI tab (effects that only change the UI)", ref showUi))
        {
            s.ShowUiTab = showUi;
            s.Save();
            rt.Publish();
        }

        ImGui.SeparatorText("Screenshots");
        if (screenshotPath.Length == 0)
        {
            screenshotPath.SetValue(s.ScreenshotPath);
        }
        if (ImGui.InputText("Folder", screenshotPath))
        {
            s.ScreenshotPath = screenshotPath.ToString();
            s.Save();
        }
        bool before = s.SaveBeforeAndAfter;
        if (ImGui.Checkbox("Also save the image without effects", ref before))
        {
            s.SaveBeforeAndAfter = before;
            s.Save();
        }

        ImGui.SeparatorText("Preprocessor definitions");
        globalDefinitions ??= new ImInputString(8192, string.Join("\n", s.GlobalDefinitions));
        presetDefinitions ??= new ImInputString(8192, string.Join("\n", rt.Preset.Definitions));
        ImGui.Text("Global (NAME=VALUE, one per line):");
        ImGui.InputTextMultiline("##globaldefs", globalDefinitions, new float2 { X = -1, Y = 110 });
        ImGui.Text("This preset:");
        ImGui.InputTextMultiline("##presetdefs", presetDefinitions, new float2 { X = -1, Y = 110 });
        if (ImGui.Button("Apply and recompile"))
        {
            s.GlobalDefinitions = Lines(globalDefinitions);
            s.Save();
            rt.Preset.Definitions = Lines(presetDefinitions);
            rt.MarkPresetDirty();
        }
        ImGui.TextColored(grey, "Depth effects may need RESHADE_DEPTH_INPUT_IS_REVERSED and similar here.");

        ImGui.SeparatorText("Folders");
        ImGui.TextWrapped("Shaders: " + ReShadeMod.ShadersDir);
        ImGui.TextWrapped("Textures: " + ReShadeMod.TexturesDir);
        ImGui.TextWrapped("Presets: " + ReShadeMod.BaseDir);
        if (rt.StatusMessage.Length > 0)
        {
            ImGui.TextColored(red, rt.StatusMessage);
        }
    }

    private static List<string> Lines(ImInputString input)
    {
        return input.ToString().Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
    }

    private static void DrawAbout()
    {
        ImGui.TextWrapped("ReShade for RimWorld (unofficial) loads ReShade .fx effects in RimWorld 1.7.");
        ImGui.TextWrapped("Effects are compiled with ReShade's own effect compiler (reshadefx), Copyright (C) 2014 Patrick Mours, BSD 3-Clause license.");
        ImGui.TextWrapped("This mod is not made by or affiliated with crosire or the ReShade project.");
        ImGui.TextWrapped("Effect packages keep their own licenses.");
    }
}
