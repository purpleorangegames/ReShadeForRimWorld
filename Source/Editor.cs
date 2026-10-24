using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Brutal.ImGuiApi;
using Brutal.Numerics;

namespace ReShadeForRimWorld;

// ReShade's "Edit source code": edit the .fx in game, saving recompiles it and shows the compiler's messages
public sealed class Editor
{
    private static readonly List<Editor> open = new();
    private static readonly float4 red = new() { X = 1f, Y = 0.4f, Z = 0.4f, W = 1f };
    private static readonly float4 yellow = new() { X = 1f, Y = 0.85f, Z = 0.4f, W = 1f };
    private static readonly float4 green = new() { X = 0.5f, Y = 0.9f, Z = 0.5f, W = 1f };

    private readonly string path;
    private ImInputString buffer;
    private string savedText;
    private bool visible = true;
    private string status = "";

    private Editor(string path)
    {
        this.path = path;
        Load();
    }

    public static void Open(string path)
    {
        Editor existing = open.FirstOrDefault(e => e.path == path);
        if (existing != null)
        {
            existing.visible = true;
            ImGui.SetWindowFocus(existing.Title);
            return;
        }
        open.Add(new Editor(path));
    }

    private string Title => Path.GetFileName(path) + "##editor" + path;

    private void Load()
    {
        savedText = File.Exists(path) ? File.ReadAllText(path).Replace("\r\n", "\n") : "";
        int bytes = Encoding.UTF8.GetByteCount(savedText);
        // fixed size buffer with plenty of room to grow while editing
        buffer = new ImInputString(Math.Max(1 << 20, bytes * 2 + 65536), savedText);
        status = "";
    }

    private bool Dirty => buffer.ToString() != savedText;

    private void Save(Runtime rt)
    {
        string text = buffer.ToString();
        try
        {
            File.WriteAllText(path, text);
            savedText = text;
            Effect e = rt.Effects.FirstOrDefault(x => x.FilePath == path);
            if (e != null)
            {
                // recompile right away instead of waiting for the file check
                e.CheckSource();
                e.Stale = true;
            }
            status = "Saved, compiling...";
        }
        catch (Exception ex)
        {
            status = "Could not save: " + ex.Message;
        }
    }

    public static void DrawAll(Runtime rt)
    {
        for (int i = open.Count - 1; i >= 0; i--)
        {
            if (!open[i].Draw(rt))
            {
                open.RemoveAt(i);
            }
        }
    }

    // returns false once the window was closed
    private bool Draw(Runtime rt)
    {
        if (!visible)
        {
            return false;
        }
        ImGui.SetNextWindowSize(new float2 { X = 900, Y = 760 }, ImGuiCond.FirstUseEver);
        bool keep = true;
        string dirtyMark = Dirty ? " *" : "";
        if (ImGui.Begin(Path.GetFileName(path) + dirtyMark + "###editor" + path, ref keep, ImGuiWindowFlags.None))
        {
            if (ImGui.Button("Save") || (ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows) && ImGui.GetIO().KeyCtrl && ImGui.IsKeyPressed(ImGuiKey.S, false)))
            {
                Save(rt);
            }
            ImGui.SameLine();
            if (ImGui.Button("Revert"))
            {
                Load();
            }
            ImGui.SameLine();
            ImGui.TextDisabled(path);

            Effect effect = rt.Effects.FirstOrDefault(x => x.FilePath == path);
            Effect.CompileResult result = effect?.Result;
            string[] messages = result == null ? Array.Empty<string>() : result.Errors.Split('\n').Where(l => l.Trim().Length > 0).ToArray();
            float messagesHeight = Math.Min(160f, 26f + messages.Length * 18f);

            float2 avail = ImGui.GetContentRegionAvail();
            ImGui.InputTextMultiline("##source", buffer, new float2 { X = -1, Y = Math.Max(100f, avail.Y - messagesHeight - 8f) }, ImGuiInputTextFlags.AllowTabInput);

            if (ImGui.BeginChild("##messages", new float2 { X = 0, Y = 0 }, ImGuiChildFlags.Borders))
            {
                if (effect?.Compiling == true)
                {
                    ImGui.Text("Compiling...");
                }
                else if (result != null)
                {
                    if (result.Success && messages.Length == 0)
                    {
                        ImGui.TextColored(green, "Compiled without errors.");
                    }
                    foreach (string m in messages)
                    {
                        ImGui.TextColored(m.Contains("error") ? red : yellow, m);
                    }
                }
                if (status.Length > 0 && effect?.Compiling != true && result == null)
                {
                    ImGui.Text(status);
                }
            }
            ImGui.EndChild();
        }
        ImGui.End();
        return keep;
    }
}
