using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using WinForms = System.Windows.Forms;

namespace Inkwell.Platform;

/// <summary>
/// Reads and writes text in the focused field of any Windows app.
/// Strategy: read via UI Automation (ValuePattern / TextPattern); fall back to Ctrl+A, Ctrl+C.
/// Writing always goes through the clipboard (Ctrl+V) for undo support.
/// The user's clipboard is snapshot/restored around every operation.
/// </summary>
public static class TextBridge
{
    public sealed class Captured
    {
        public string Text { get; init; } = "";
        public AutomationElement? Element { get; init; }
        public bool ViaClipboard { get; init; }
    }

    // MARK: Reading

    public static async Task<Captured?> CaptureAsync(string trigger)
    {
        var element = FocusedElement();

        // Try UI Automation first
        if (element is not null)
        {
            var value = GetValue(element);
            if (value is not null && value.Contains(trigger, StringComparison.Ordinal))
                return new Captured { Text = value, Element = element, ViaClipboard = false };
        }

        // Fallback: Ctrl+A, Ctrl+C
        var copied = await CopyAllViaClipboard();
        if (copied is not null && copied.Contains(trigger, StringComparison.Ordinal))
        {
            SendKey(Win32.VK_RIGHT); // collapse selection
            await Task.Delay(30);
            return new Captured { Text = copied, Element = element, ViaClipboard = true };
        }

        return null;
    }

    public static AutomationElement? FocusedElement()
    {
        try { return AutomationElement.FocusedElement; }
        catch { return null; }
    }

    public static bool IsSameElement(AutomationElement? a, AutomationElement? b)
    {
        if (a is null || b is null) return true; // can't tell — assume yes
        try { return Automation.Compare(a, b); }
        catch { return true; }
    }

    private static string? GetValue(AutomationElement element)
    {
        try
        {
            if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var vp))
                return ((ValuePattern)vp).Current.Value;
            if (element.TryGetCurrentPattern(TextPattern.Pattern, out var tp))
            {
                var text = ((TextPattern)tp).DocumentRange.GetText(-1);
                if (!string.IsNullOrEmpty(text)) return text;
            }
        }
        catch { /* element may be gone */ }
        return null;
    }

    private static async Task<string?> CopyAllViaClipboard()
    {
        string? saved = null;
        bool hadData = false;
        try
        {
            // Snapshot clipboard
            var data = WinForms.Clipboard.GetDataObject();
            if (data is not null && data.GetFormats().Length > 0)
            {
                hadData = true;
                saved = WinForms.Clipboard.GetText();
            }
        }
        catch { /* clipboard busy */ }

        uint before = Win32.GetClipboardSequenceNumber();

        SendKey(Win32.VK_A, ctrl: true);
        await Task.Delay(50);
        SendKey(Win32.VK_C, ctrl: true);

        // Wait up to 500ms for clipboard to change
        for (int i = 0; i < 25; i++)
        {
            if (Win32.GetClipboardSequenceNumber() != before) break;
            await Task.Delay(20);
        }

        string? result = null;
        try
        {
            if (Win32.GetClipboardSequenceNumber() != before)
                result = WinForms.Clipboard.GetText();
        }
        catch { /* clipboard busy */ }

        // Restore clipboard
        try
        {
            if (hadData && saved is not null)
                WinForms.Clipboard.SetText(saved);
        }
        catch { }

        return result;
    }

    // MARK: Writing

    public static async Task ReplaceAllAsync(string text)
    {
        SendKey(Win32.VK_A, ctrl: true);
        await Task.Delay(50);
        await PasteAsync(text);
    }

    public static async Task DeleteBackwardAsync(int count)
    {
        for (int i = 0; i < count; i++) SendKey(Win32.VK_BACK);
        await Task.Delay(30);
    }

    public static async Task PasteAsync(string text)
    {
        // Save clipboard, set transient data, paste, restore
        string? saved = null;
        bool hadData = false;
        try
        {
            var data = WinForms.Clipboard.GetDataObject();
            if (data is not null && data.GetFormats().Length > 0)
            {
                hadData = true;
                saved = WinForms.Clipboard.GetText();
            }
        }
        catch { }

        try
        {
            var obj = new DataObject();
            obj.SetData(DataFormats.UnicodeText, text);
            // Tell clipboard managers / Win+V history to ignore this entry
            SetTransientFlags(obj);
            WinForms.Clipboard.SetDataObject(obj, true);
        }
        catch { }

        SendKey(Win32.VK_V, ctrl: true);
        await Task.Delay(250);

        // Restore
        try
        {
            if (hadData && saved is not null)
                WinForms.Clipboard.SetText(saved);
        }
        catch { }
    }

    public static void CopyToClipboard(string text)
    {
        try { WinForms.Clipboard.SetText(text); }
        catch { }
    }

    // MARK: Keystrokes via SendInput

    public static void SendKey(ushort vk, bool ctrl = false, bool shift = false, bool alt = false)
    {
        var inputs = new System.Collections.Generic.List<Win32.INPUT>();

        if (ctrl) inputs.Add(KeyInput(Win32.VK_CONTROL, false));
        if (shift) inputs.Add(KeyInput(Win32.VK_SHIFT, false));
        if (alt) inputs.Add(KeyInput(Win32.VK_MENU, false));

        inputs.Add(KeyInput(vk, false));
        inputs.Add(KeyInput(vk, true));

        if (alt) inputs.Add(KeyInput(Win32.VK_MENU, true));
        if (shift) inputs.Add(KeyInput(Win32.VK_SHIFT, true));
        if (ctrl) inputs.Add(KeyInput(Win32.VK_CONTROL, true));

        var arr = inputs.ToArray();
        Win32.SendInput((uint)arr.Length, arr, Win32.INPUT.Size);
    }

    private static Win32.INPUT KeyInput(ushort vk, bool up)
    {
        var input = new Win32.INPUT { type = Win32.INPUT_KEYBOARD };
        input.U.ki.wVk = vk;
        input.U.ki.dwFlags = up ? Win32.KEYEVENTF_KEYUP : 0;
        return input;
    }

    private static void SetTransientFlags(DataObject obj)
    {
        // These DWORD=0 MemoryStreams tell Windows 10/11 clipboard history to skip this entry
        var zero = new MemoryStream(BitConverter.GetBytes(0));
        obj.SetData("CanIncludeInClipboardHistory", false, zero);
        obj.SetData("CanUploadToCloudClipboard", false, new MemoryStream(BitConverter.GetBytes(0)));
        obj.SetData("ExcludeClipboardContentFromMonitorProcessing", false, new MemoryStream(BitConverter.GetBytes(0)));
    }

    private static class Task
    {
        public static System.Threading.Tasks.Task Delay(int ms) => System.Threading.Tasks.Task.Delay(ms);
    }
}
