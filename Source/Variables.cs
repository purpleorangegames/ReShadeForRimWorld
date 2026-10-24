using System;
using System.Collections.Generic;
using System.Linq;
using Brutal.ImGuiApi;
using Brutal.Numerics;

namespace ReShadeForRimWorld;

// effect variables: defaults from the source and the widgets ReShade picks from ui_* annotations
public static class Variables
{
    public static int Components(FxUniform u) => Math.Max(1, u.type.rows) * Math.Max(1, u.type.cols);

    public static float[] Defaults(FxUniform u)
    {
        int n = Components(u);
        var v = new float[n];
        if (!u.has_initializer)
        {
            return v;
        }
        for (int i = 0; i < n && i < 16; i++)
        {
            v[i] = u.type.base_ == FxType.Float ? u.initializer.f[i] : u.initializer.i[i];
        }
        return v;
    }

    public static bool Editable(FxUniform u)
    {
        return u.annotations.GetString("source") == null
            && u.type.array_length == 0
            && Number(u, "hidden", 0) == 0
            && Number(u, "noedit", 0) == 0;
    }

    private static float Number(FxUniform u, string name, float fallback)
    {
        FxAnnotation a = u.annotations.Find(name);
        if (a == null)
        {
            return fallback;
        }
        return a.type.base_ == FxType.Float ? a.value.f[0] : a.value.i[0];
    }

    // "Off\0On\0" style item lists, as ReShade writes them in ui_items
    private static string[] Items(FxUniform u)
    {
        string items = u.annotations.GetString("ui_items");
        return items == null ? null : items.Split('\0').Where(s => s.Length > 0).ToArray();
    }

    // draws the widget for one variable, returns the new value when the user changed it
    public static float[] Draw(FxUniform u, float[] value)
    {
        string label = (u.annotations.GetString("ui_label") ?? u.name) + "##" + u.name;
        string type = u.annotations.GetString("ui_type") ?? "";
        string tooltip = u.annotations.GetString("ui_tooltip");
        float min = Number(u, "ui_min", 0), max = Number(u, "ui_max", 0);
        float step = Number(u, "ui_step", 0);
        int n = Components(u);
        float[] v = (float[])value.Clone();
        bool changed = false;
        bool isFloat = u.type.base_ == FxType.Float;
        string[] items = Items(u);

        if (u.type.base_ == FxType.Bool && n == 1 && items == null)
        {
            bool b = v[0] != 0;
            if (ImGui.Checkbox(label, ref b))
            {
                v[0] = b ? 1 : 0;
                changed = true;
            }
        }
        else if (!isFloat && n == 1 && items != null && (type == "combo" || type == "list" || type == "radio" || u.type.base_ == FxType.Bool))
        {
            int current = (int)v[0];
            if (type == "radio")
            {
                ImGui.Text(u.annotations.GetString("ui_label") ?? u.name);
                for (int i = 0; i < items.Length; i++)
                {
                    if (ImGui.RadioButton(items[i] + "##" + u.name + i, current == i))
                    {
                        current = i;
                        changed = true;
                    }
                }
            }
            else if (ImGui.Combo(label, ref current, string.Join("\0", items) + "\0\0"))
            {
                changed = true;
            }
            v[0] = current;
        }
        else if (isFloat && type == "color" && (n == 3 || n == 4))
        {
            if (n == 3)
            {
                var c = new float3 { X = v[0], Y = v[1], Z = v[2] };
                if (ImGui.ColorEdit3(label, ref c))
                {
                    v[0] = c.X; v[1] = c.Y; v[2] = c.Z;
                    changed = true;
                }
            }
            else
            {
                var c = new float4 { X = v[0], Y = v[1], Z = v[2], W = v[3] };
                if (ImGui.ColorEdit4(label, ref c))
                {
                    v[0] = c.X; v[1] = c.Y; v[2] = c.Z; v[3] = c.W;
                    changed = true;
                }
            }
        }
        else if (isFloat)
        {
            bool slider = type == "slider" && max > min;
            float speed = step > 0 ? step : 0.001f * Math.Max(1f, max - min);
            bool edited = false;
            switch (n)
            {
                case 2:
                {
                    var f = new float2 { X = v[0], Y = v[1] };
                    edited = slider ? ImGui.SliderFloat2(label, ref f, min, max) : ImGui.DragFloat2(label, ref f, speed, min, max);
                    v[0] = f.X; v[1] = f.Y;
                    break;
                }
                case 3:
                {
                    var f = new float3 { X = v[0], Y = v[1], Z = v[2] };
                    edited = slider ? ImGui.SliderFloat3(label, ref f, min, max) : ImGui.DragFloat3(label, ref f, speed, min, max);
                    v[0] = f.X; v[1] = f.Y; v[2] = f.Z;
                    break;
                }
                case 4:
                {
                    var f = new float4 { X = v[0], Y = v[1], Z = v[2], W = v[3] };
                    edited = slider ? ImGui.SliderFloat4(label, ref f, min, max) : ImGui.DragFloat4(label, ref f, speed, min, max);
                    v[0] = f.X; v[1] = f.Y; v[2] = f.Z; v[3] = f.W;
                    break;
                }
                default:
                    for (int i = 0; i < n; i++)
                    {
                        string l = n == 1 ? label : $"{label}[{i}]##{u.name}{i}";
                        float f = v[i];
                        if (slider ? ImGui.SliderFloat(l, ref f, min, max) : ImGui.DragFloat(l, ref f, speed, min, max))
                        {
                            v[i] = f;
                            edited = true;
                        }
                    }
                    break;
            }
            changed = edited;
        }
        else
        {
            bool slider = type == "slider" && max > min;
            for (int i = 0; i < n; i++)
            {
                string l = n == 1 ? label : $"{label}[{i}]##{u.name}{i}";
                int k = (int)v[i];
                bool edited = slider
                    ? ImGui.SliderInt(l, ref k, (int)min, (int)max)
                    : ImGui.DragInt(l, ref k, step > 0 ? step : 0.2f, (int)min, (int)max);
                if (edited)
                {
                    v[i] = k;
                    changed = true;
                }
            }
        }
        if (tooltip != null && ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(tooltip);
        }
        return changed ? v : null;
    }
}
