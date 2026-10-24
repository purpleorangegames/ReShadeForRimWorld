using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace ReShadeForRimWorld;

// a ReShade preset .ini: global keys (Techniques, TechniqueSorting, PreprocessorDefinitions)
// and one section per effect file holding its variable values
public sealed class Preset
{
    public string Path;
    public List<string> Techniques = new();        // "Name@File.fx", enabled ones
    public List<string> TechniqueSorting = new();  // "Name@File.fx", full order
    public List<string> Definitions = new();       // "NAME=VALUE"
    // our own keys, ReShade ignores them: the UI tab's list and the Game techniques that run on the whole screen
    public List<string> TechniquesUI = new();
    public List<string> TechniqueSortingUI = new();
    public List<string> TechniquesScreen = new();
    public Dictionary<string, Dictionary<string, float[]>> Values = new(StringComparer.OrdinalIgnoreCase);

    public static Preset Load(string path)
    {
        var preset = new Preset { Path = path };
        if (!File.Exists(path))
        {
            return preset;
        }
        string section = "";
        foreach (string raw in File.ReadAllLines(path))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith(";") || line.StartsWith("#"))
            {
                continue;
            }
            if (line.StartsWith("[") && line.EndsWith("]"))
            {
                section = line.Substring(1, line.Length - 2);
                continue;
            }
            int eq = line.IndexOf('=');
            if (eq < 0)
            {
                continue;
            }
            string key = line.Substring(0, eq).Trim();
            string value = line.Substring(eq + 1).Trim();
            if (section.Length == 0)
            {
                switch (key)
                {
                    case "Techniques":
                        preset.Techniques = SplitList(value);
                        break;
                    case "TechniqueSorting":
                        preset.TechniqueSorting = SplitList(value);
                        break;
                    case "PreprocessorDefinitions":
                        preset.Definitions = SplitList(value);
                        break;
                    case "TechniquesUI":
                        preset.TechniquesUI = SplitList(value);
                        break;
                    case "TechniqueSortingUI":
                        preset.TechniqueSortingUI = SplitList(value);
                        break;
                    case "TechniquesScreen":
                        preset.TechniquesScreen = SplitList(value);
                        break;
                }
                continue;
            }
            if (!preset.Values.TryGetValue(section, out var values))
            {
                preset.Values[section] = values = new Dictionary<string, float[]>();
            }
            values[key] = value.Split(',').Select(v => float.TryParse(v.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float f) ? f : 0f).ToArray();
        }
        return preset;
    }

    private static List<string> SplitList(string value)
    {
        return value.Split(',').Select(v => v.Trim()).Where(v => v.Length > 0).ToList();
    }

    public void Save()
    {
        var sb = new StringBuilder();
        sb.Append("PreprocessorDefinitions=").Append(string.Join(",", Definitions)).Append('\n');
        sb.Append("Techniques=").Append(string.Join(",", Techniques)).Append('\n');
        sb.Append("TechniqueSorting=").Append(string.Join(",", TechniqueSorting)).Append('\n');
        if (TechniquesUI.Count > 0 || TechniquesScreen.Count > 0 || TechniqueSortingUI.Count > 0)
        {
            sb.Append("TechniquesUI=").Append(string.Join(",", TechniquesUI)).Append('\n');
            sb.Append("TechniqueSortingUI=").Append(string.Join(",", TechniqueSortingUI)).Append('\n');
            sb.Append("TechniquesScreen=").Append(string.Join(",", TechniquesScreen)).Append('\n');
        }
        foreach (var section in Values.OrderBy(s => s.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (section.Value.Count == 0)
            {
                continue;
            }
            sb.Append('\n').Append('[').Append(section.Key).Append("]\n");
            foreach (var kv in section.Value.OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                sb.Append(kv.Key).Append('=').Append(string.Join(",", kv.Value.Select(f => f.ToString("0.000000", CultureInfo.InvariantCulture)))).Append('\n');
            }
        }
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path));
        File.WriteAllText(Path, sb.ToString());
    }
}
