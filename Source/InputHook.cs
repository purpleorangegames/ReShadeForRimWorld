using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Brutal.GlfwApi;
using Brutal.ImGuiApi;
using Brutal.Numerics;
using HarmonyLib;

namespace ReShadeForRimWorld;

// The game uses Dear ImGui only to draw, it never feeds it input. These patches on the GLFW input bridge
// give ImGui the mouse and keyboard, and keep the game from seeing what the overlay used.
[HarmonyPatch]
public static class InputHook
{
    private const string Bridge = "RimWorldWaylith.GlfwInputBridge";

    private static bool OverlayWantsMouse => Overlay.Open && ImGui.GetIO().WantCaptureMouse;
    private static bool OverlayWantsKeyboard => Overlay.Open && (ImGui.GetIO().WantCaptureKeyboard || ImGui.GetIO().WantTextInput);

    [HarmonyPatch(Bridge, "OnCursorPos"), HarmonyPostfix]
    public static void CursorPos(GlfwWindow window, double2 pos)
    {
        // ImGui works in framebuffer pixels, GLFW reports window coordinates
        int2 fb = window.FramebufferSize, size = window.Size;
        float sx = size.X > 0 ? (float)fb.X / size.X : 1f;
        float sy = size.Y > 0 ? (float)fb.Y / size.Y : 1f;
        ImGui.GetIO().AddMousePosEvent((float)pos.X * sx, (float)pos.Y * sy);
    }

    [HarmonyPatch(Bridge, "OnMouseButton"), HarmonyPrefix]
    public static bool MouseButton(GlfwMouseButton button, GlfwButtonAction action, GlfwModifier mods)
    {
        if (!Overlay.Open || (int)button > 4)
        {
            return true;
        }
        bool captured = OverlayWantsMouse;
        AddMods(mods);
        ImGui.GetIO().AddMouseButtonEvent((int)button, action == GlfwButtonAction.Press);
        // releases always reach the game so nothing stays held
        return !(captured && action == GlfwButtonAction.Press);
    }

    [HarmonyPatch(Bridge, "OnScroll"), HarmonyPrefix]
    public static bool Scroll(double2 offset)
    {
        if (!Overlay.Open)
        {
            return true;
        }
        bool captured = OverlayWantsMouse;
        ImGui.GetIO().AddMouseWheelEvent((float)offset.X, (float)offset.Y);
        return !captured;
    }

    [HarmonyPatch(Bridge, "OnKey"), HarmonyPrefix]
    public static bool Key(GlfwKey key, GlfwKeyAction action, GlfwModifier mods)
    {
        Runtime rt = ReShadeMod.Runtime;
        if (rt == null)
        {
            return true;
        }
        Settings s = rt.Settings;
        if (Overlay.WaitingForKey != 0 && action == GlfwKeyAction.Press && key != GlfwKey.Unknown)
        {
            if (key != GlfwKey.Escape)
            {
                if (Overlay.WaitingForKey == 1)
                {
                    s.OverlayKey = (int)key;
                }
                else
                {
                    s.ScreenshotKey = (int)key;
                }
                s.Save();
            }
            Overlay.WaitingForKey = 0;
            return false;
        }
        if ((int)key == s.OverlayKey && !OverlayWantsTextOnly())
        {
            if (action == GlfwKeyAction.Press)
            {
                Overlay.Open = !Overlay.Open;
            }
            return false;
        }
        if ((int)key == s.ScreenshotKey && !OverlayWantsTextOnly())
        {
            if (action == GlfwKeyAction.Press)
            {
                ReShadeMod.TakeScreenshot();
            }
            return false;
        }
        if (!Overlay.Open)
        {
            return true;
        }
        bool captured = OverlayWantsKeyboard;
        ImGuiKey k = Translate(key);
        AddMods(mods);
        if (k != ImGuiKey.None)
        {
            ImGui.GetIO().AddKeyEvent(k, action != GlfwKeyAction.Release);
        }
        return !(captured && action != GlfwKeyAction.Release);
    }

    // while typing in a text box, the overlay and screenshot keys are just keys
    private static bool OverlayWantsTextOnly() => Overlay.Open && ImGui.GetIO().WantTextInput;

    [HarmonyPatch(Bridge, "OnChar"), HarmonyPrefix]
    public static bool Char(uint codepoint)
    {
        if (!Overlay.Open)
        {
            return true;
        }
        bool captured = OverlayWantsKeyboard;
        ImGui.GetIO().AddInputCharacter(codepoint);
        return !captured;
    }

    // draws the overlay into the game's ImGui frame, right before it is rendered
    [HarmonyPatch("RimWorldWaylith.Imgui.ImGuiHost", "EndFrame"), HarmonyPrefix]
    public static void EndFrame()
    {
        try
        {
            EnsureClipboard();
            Overlay.Draw();
        }
        catch (Exception e)
        {
            ReShadeMod.Runtime?.Feature.Say("[ReShade] overlay error: " + e);
            Overlay.Open = false;
        }
    }

    private static void AddMods(GlfwModifier mods)
    {
        var io = ImGui.GetIO();
        io.AddKeyEvent(ImGuiKey.Mod_Ctrl, (mods & GlfwModifier.Control) != 0);
        io.AddKeyEvent(ImGuiKey.Mod_Shift, (mods & GlfwModifier.Shift) != 0);
        io.AddKeyEvent(ImGuiKey.Mod_Alt, (mods & GlfwModifier.Alt) != 0);
        io.AddKeyEvent(ImGuiKey.Mod_Super, (mods & GlfwModifier.Super) != 0);
    }

    // same mapping as imgui_impl_glfw
    private static ImGuiKey Translate(GlfwKey key)
    {
        int k = (int)key;
        if (k >= (int)GlfwKey.A && k <= (int)GlfwKey.Z)
        {
            return ImGuiKey.A + (k - (int)GlfwKey.A);
        }
        if (k >= (int)GlfwKey.Number0 && k <= (int)GlfwKey.Number9)
        {
            return ImGuiKey._0 + (k - (int)GlfwKey.Number0);
        }
        if (k >= (int)GlfwKey.F1 && k <= (int)GlfwKey.F24)
        {
            return ImGuiKey.F1 + (k - (int)GlfwKey.F1);
        }
        if (k >= (int)GlfwKey.Kp0 && k <= (int)GlfwKey.Kp9)
        {
            return ImGuiKey.Keypad0 + (k - (int)GlfwKey.Kp0);
        }
        return key switch
        {
            GlfwKey.Tab => ImGuiKey.Tab,
            GlfwKey.Left => ImGuiKey.LeftArrow,
            GlfwKey.Right => ImGuiKey.RightArrow,
            GlfwKey.Up => ImGuiKey.UpArrow,
            GlfwKey.Down => ImGuiKey.DownArrow,
            GlfwKey.PageUp => ImGuiKey.PageUp,
            GlfwKey.PageDown => ImGuiKey.PageDown,
            GlfwKey.Home => ImGuiKey.Home,
            GlfwKey.End => ImGuiKey.End,
            GlfwKey.Insert => ImGuiKey.Insert,
            GlfwKey.Delete => ImGuiKey.Delete,
            GlfwKey.Backspace => ImGuiKey.Backspace,
            GlfwKey.Space => ImGuiKey.Space,
            GlfwKey.Enter => ImGuiKey.Enter,
            GlfwKey.KpEnter => ImGuiKey.KeypadEnter,
            GlfwKey.Escape => ImGuiKey.Escape,
            GlfwKey.Apostrophe => ImGuiKey.Apostrophe,
            GlfwKey.Comma => ImGuiKey.Comma,
            GlfwKey.Minus => ImGuiKey.Minus,
            GlfwKey.Period => ImGuiKey.Period,
            GlfwKey.Slash => ImGuiKey.Slash,
            GlfwKey.Semicolon => ImGuiKey.Semicolon,
            GlfwKey.Equal => ImGuiKey.Equal,
            GlfwKey.LeftBracket => ImGuiKey.LeftBracket,
            GlfwKey.Backslash => ImGuiKey.Backslash,
            GlfwKey.RightBracket => ImGuiKey.RightBracket,
            GlfwKey.GraveAccent => ImGuiKey.GraveAccent,
            GlfwKey.LeftControl => ImGuiKey.LeftCtrl,
            GlfwKey.RightControl => ImGuiKey.RightCtrl,
            GlfwKey.LeftShift => ImGuiKey.LeftShift,
            GlfwKey.RightShift => ImGuiKey.RightShift,
            GlfwKey.LeftAlt => ImGuiKey.LeftAlt,
            GlfwKey.RightAlt => ImGuiKey.RightAlt,
            GlfwKey.LeftSuper => ImGuiKey.LeftSuper,
            GlfwKey.RightSuper => ImGuiKey.RightSuper,
            _ => ImGuiKey.None
        };
    }

    // copy and paste in text boxes go through the game's clipboard support
    private static bool clipboardSet;
    private static byte[] clipboardText = new byte[1];
    private static GCHandle clipboardHandle;

    private static unsafe void EnsureClipboard()
    {
        if (clipboardSet)
        {
            return;
        }
        clipboardSet = true;
        ImGuiPlatformIOPtr pio = ImGui.GetPlatformIO();
        if (pio.Platform_GetClipboardTextFn == 0)
        {
            pio.Platform_GetClipboardTextFn = (nint)(delegate* unmanaged[Cdecl]<nint, byte*>)&GetClipboard;
        }
        if (pio.Platform_SetClipboardTextFn == 0)
        {
            pio.Platform_SetClipboardTextFn = (nint)(delegate* unmanaged[Cdecl]<nint, byte*, void>)&SetClipboard;
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static unsafe byte* GetClipboard(nint ctx)
    {
        string text = UnityEngine.GUIUtility.systemCopyBuffer ?? "";
        if (clipboardHandle.IsAllocated)
        {
            clipboardHandle.Free();
        }
        clipboardText = Encoding.UTF8.GetBytes(text + "\0");
        clipboardHandle = GCHandle.Alloc(clipboardText, GCHandleType.Pinned);
        return (byte*)clipboardHandle.AddrOfPinnedObject();
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static unsafe void SetClipboard(nint ctx, byte* text)
    {
        UnityEngine.GUIUtility.systemCopyBuffer = Marshal.PtrToStringUTF8((nint)text) ?? "";
    }
}
