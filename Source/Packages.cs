using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace ReShadeForRimWorld;

// one entry of ReShade's EffectPackages.ini
public sealed class EffectPackage
{
    public string Name = "";
    public string Description = "";
    public string InstallPath = "";
    public string TextureInstallPath = "";
    public string DownloadUrl = "";
    public string RepositoryUrl = "";
    public List<string> EffectFiles = new();
    public List<string> DenyEffectFiles = new();
    public bool Required;
    public bool EnabledByDefault;

    // ticked in the list = the user wants it installed
    public bool Selected;
    public HashSet<string> SkippedEffects = new(StringComparer.OrdinalIgnoreCase);
}

// the package list ReShade's installer offers, and installing them the same way it does
public sealed class Packages
{
    public const string ListUrl = "https://raw.githubusercontent.com/crosire/reshade-shaders/list/EffectPackages.ini";

    public List<EffectPackage> List = new();
    // name -> files we put on disk, so a package can be removed again
    public Dictionary<string, List<string>> Installed = new(StringComparer.OrdinalIgnoreCase);

    private readonly object gate = new();
    public volatile bool Busy;
    public volatile string Status = "";
    public volatile bool ReloadNeeded;
    public string Error = "";
    // what happened, newest last, shown under the buttons
    public readonly List<string> Log = new();

    private static readonly HttpClient http = new() { Timeout = TimeSpan.FromMinutes(5) };
    private static string CacheFile => Path.Combine(ReShadeMod.BaseDir, "EffectPackages.ini");
    private static string InstalledFile => Path.Combine(ReShadeMod.BaseDir, "InstalledPackages.ini");

    static Packages()
    {
        http.DefaultRequestHeaders.UserAgent.ParseAdd("ReShadeForRimWorld");
    }

    public Packages()
    {
        LoadInstalled();
        // the last list we got works offline
        if (File.Exists(CacheFile))
        {
            List = Parse(File.ReadAllText(CacheFile));
            foreach (EffectPackage p in List)
            {
                p.Selected = IsInstalled(p);
            }
        }
    }

    public static List<EffectPackage> Parse(string ini)
    {
        var list = new List<EffectPackage>();
        EffectPackage p = null;
        foreach (string raw in ini.Split('\n'))
        {
            string line = raw.Trim();
            if (line.StartsWith("[") && line.EndsWith("]"))
            {
                p = new EffectPackage();
                list.Add(p);
                continue;
            }
            int eq = line.IndexOf('=');
            if (p == null || eq < 0)
            {
                continue;
            }
            string key = line.Substring(0, eq).Trim(), value = line.Substring(eq + 1).Trim();
            switch (key)
            {
                case "PackageName": p.Name = value; break;
                case "PackageDescription": p.Description = value; break;
                case "InstallPath": p.InstallPath = value; break;
                case "TextureInstallPath": p.TextureInstallPath = value; break;
                case "DownloadUrl": p.DownloadUrl = value; break;
                case "RepositoryUrl": p.RepositoryUrl = value; break;
                case "EffectFiles": p.EffectFiles = SplitList(value); break;
                case "DenyEffectFiles": p.DenyEffectFiles = SplitList(value); break;
                case "Required": p.Required = value == "1"; break;
                case "Enabled": p.EnabledByDefault = value == "1"; break;
            }
        }
        list.RemoveAll(x => x.Name.Length == 0 || x.DownloadUrl.Length == 0);
        return list;
    }

    private static List<string> SplitList(string value) => value.Split(',').Select(v => v.Trim()).Where(v => v.Length > 0).ToList();

    public void Refresh()
    {
        if (Busy)
        {
            return;
        }
        Busy = true;
        Error = "";
        Status = "Downloading the package list...";
        Task.Run(async () =>
        {
            try
            {
                string ini = await http.GetStringAsync(ListUrl);
                List<EffectPackage> fresh = Parse(ini);
                Directory.CreateDirectory(ReShadeMod.BaseDir);
                File.WriteAllText(CacheFile, ini);
                // keep what the user already picked
                foreach (EffectPackage p in fresh)
                {
                    EffectPackage old = List.FirstOrDefault(o => o.Name == p.Name);
                    // nothing is ticked on its own, the mod works with an empty or hand-filled shader folder
                    p.Selected = old?.Selected ?? IsInstalled(p);
                    p.SkippedEffects = old?.SkippedEffects ?? p.SkippedEffects;
                }
                List = fresh;
                Status = $"{fresh.Count} packages available.";
            }
            catch (Exception e)
            {
                Error = "Could not get the package list: " + e.Message;
                Status = "";
            }
            finally
            {
                Busy = false;
            }
        });
    }

    public bool IsInstalled(EffectPackage p)
    {
        lock (gate)
        {
            return Installed.ContainsKey(p.Name);
        }
    }

    public List<EffectPackage> ToInstall => List.Where(p => p.Selected && !IsInstalled(p)).ToList();
    public List<EffectPackage> ToRemove => List.Where(p => !p.Selected && IsInstalled(p)).ToList();

    // installs what is ticked and not installed, removes what is installed and unticked
    public void Apply(bool reinstallInstalled = false)
    {
        if (Busy)
        {
            return;
        }
        List<EffectPackage> install = reinstallInstalled ? List.Where(IsInstalled).ToList() : ToInstall;
        List<EffectPackage> remove = reinstallInstalled ? new List<EffectPackage>() : ToRemove;
        if (install.Count == 0 && remove.Count == 0)
        {
            return;
        }
        Busy = true;
        Error = "";
        Task.Run(async () =>
        {
            try
            {
                foreach (EffectPackage p in remove)
                {
                    Status = $"Removing {p.Name}...";
                    int n = RemoveFiles(p);
                    AddLog($"Removed {p.Name} ({n} files).");
                }
                for (int i = 0; i < install.Count; i++)
                {
                    EffectPackage p = install[i];
                    Status = $"Installing {p.Name} ({i + 1}/{install.Count})...";
                    try
                    {
                        int n = await Install(p);
                        AddLog($"Installed {p.Name} ({n} files).");
                    }
                    catch (Exception e)
                    {
                        AddLog("Failed: " + e.Message);
                        Error = "Some packages failed, see the list below.";
                        p.Selected = IsInstalled(p);
                    }
                }
                Status = "Done. New effects are compiling, see the Home tab.";
            }
            finally
            {
                SaveInstalled();
                ReloadNeeded = true;
                Busy = false;
            }
        });
    }

    private void AddLog(string line)
    {
        lock (Log)
        {
            Log.Add(DateTime.Now.ToString("HH:mm:ss") + "  " + line);
            if (Log.Count > 50)
            {
                Log.RemoveAt(0);
            }
        }
    }

    // ".\reshade-shaders\Shaders\qUINT" from the list, relative to the ReShade folder
    private static string Target(string installPath, string fallback)
    {
        if (string.IsNullOrWhiteSpace(installPath))
        {
            return fallback;
        }
        string relative = installPath.Replace('\\', Path.DirectorySeparatorChar).TrimStart('.', Path.DirectorySeparatorChar);
        string full = Path.GetFullPath(Path.Combine(ReShadeMod.BaseDir, relative));
        // never write outside the ReShade folder, whatever the list says
        return full.StartsWith(Path.GetFullPath(ReShadeMod.BaseDir) + Path.DirectorySeparatorChar) ? full : fallback;
    }

    // same steps as ReShade's installer (setup/MainWindow.xaml.cs, InstallStep_InstallEffectPackage)
    private async Task<int> Install(EffectPackage p)
    {
        string temp = Path.Combine(Path.GetTempPath(), "ReShadeForRimWorld", Guid.NewGuid().ToString("N"));
        string zip = temp + ".zip";
        Directory.CreateDirectory(Path.GetDirectoryName(zip));
        try
        {
            using (HttpResponseMessage response = await http.GetAsync(p.DownloadUrl, HttpCompletionOption.ResponseHeadersRead))
            {
                response.EnsureSuccessStatusCode();
                long? total = response.Content.Headers.ContentLength;
                using Stream source = await response.Content.ReadAsStreamAsync();
                using FileStream file = File.Create(zip);
                var buffer = new byte[81920];
                long read = 0;
                int n;
                while ((n = await source.ReadAsync(buffer)) > 0)
                {
                    await file.WriteAsync(buffer.AsMemory(0, n));
                    read += n;
                    Status = total.HasValue
                        ? $"Downloading {p.Name}... {read * 100 / total.Value}%"
                        : $"Downloading {p.Name}... {read / 1024} KB";
                }
            }
            Status = $"Extracting {p.Name}...";
            ZipFile.ExtractToDirectory(zip, temp);

            string[] effects = Directory.GetFiles(temp, "*.fx", SearchOption.AllDirectories);
            string effectsDir = Directory.EnumerateDirectories(temp, "Shaders", SearchOption.AllDirectories).FirstOrDefault()
                ?? effects.Select(Path.GetDirectoryName).OrderBy(x => x.Length).FirstOrDefault();
            string texturesDir = Directory.EnumerateDirectories(temp, "Textures", SearchOption.AllDirectories).FirstOrDefault()
                ?? new[] { "*.png", "*.jpg", "*.jpeg" }.SelectMany(ext => Directory.EnumerateFiles(temp, ext, SearchOption.AllDirectories)).Select(Path.GetDirectoryName).OrderBy(x => x.Length).FirstOrDefault();

            foreach (string fx in effects)
            {
                string name = Path.GetFileName(fx);
                if (p.DenyEffectFiles.Contains(name, StringComparer.OrdinalIgnoreCase) || p.SkippedEffects.Contains(name))
                {
                    File.Delete(fx);
                }
            }

            var placed = new List<string>();
            if (effectsDir != null)
            {
                MoveFiles(effectsDir, Target(p.InstallPath, ReShadeMod.ShadersDir), placed);
            }
            if (texturesDir != null)
            {
                MoveFiles(texturesDir, Target(p.TextureInstallPath, ReShadeMod.TexturesDir), placed);
            }
            lock (gate)
            {
                if (Installed.TryGetValue(p.Name, out var before))
                {
                    placed = before.Union(placed).ToList();
                }
                Installed[p.Name] = placed;
            }
            return placed.Count;
        }
        catch (Exception e)
        {
            throw new Exception($"{p.Name}: {e.Message}", e);
        }
        finally
        {
            try
            {
                if (File.Exists(zip))
                {
                    File.Delete(zip);
                }
                if (Directory.Exists(temp))
                {
                    Directory.Delete(temp, true);
                }
            }
            catch
            {
            }
        }
    }

    private static void MoveFiles(string from, string to, List<string> placed)
    {
        Directory.CreateDirectory(to);
        foreach (string file in Directory.EnumerateFiles(from))
        {
            string target = Path.Combine(to, Path.GetFileName(file));
            File.Copy(file, target, true);
            placed.Add(target);
        }
        foreach (string dir in Directory.EnumerateDirectories(from))
        {
            MoveFiles(dir, Path.Combine(to, Path.GetFileName(dir)), placed);
        }
    }

    // deletes the files the package put on disk (files another installed package also placed stay)
    private int RemoveFiles(EffectPackage p)
    {
        List<string> files;
        HashSet<string> shared;
        lock (gate)
        {
            if (!Installed.TryGetValue(p.Name, out files))
            {
                return 0;
            }
            shared = new HashSet<string>(Installed.Where(kv => kv.Key != p.Name).SelectMany(kv => kv.Value));
            Installed.Remove(p.Name);
        }
        int removed = 0;
        foreach (string f in files)
        {
            try
            {
                if (!shared.Contains(f) && File.Exists(f))
                {
                    File.Delete(f);
                    removed++;
                }
            }
            catch
            {
            }
        }
        return removed;
    }

    private void LoadInstalled()
    {
        if (!File.Exists(InstalledFile))
        {
            return;
        }
        string section = null;
        foreach (string raw in File.ReadAllLines(InstalledFile))
        {
            string line = raw.Trim();
            if (line.StartsWith("[") && line.EndsWith("]"))
            {
                section = line.Substring(1, line.Length - 2);
                Installed[section] = new List<string>();
            }
            else if (section != null && line.Length > 0)
            {
                Installed[section].Add(line);
            }
        }
    }

    private void SaveInstalled()
    {
        var lines = new List<string>();
        lock (gate)
        {
            foreach (var kv in Installed)
            {
                lines.Add("[" + kv.Key + "]");
                lines.AddRange(kv.Value);
                lines.Add("");
            }
        }
        Directory.CreateDirectory(ReShadeMod.BaseDir);
        File.WriteAllLines(InstalledFile, lines);
    }
}
