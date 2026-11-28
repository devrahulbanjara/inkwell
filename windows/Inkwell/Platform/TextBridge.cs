using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Automation;

namespace Inkwell.Platform;

/// <summary>
/// Reads and writes text in whatever app currently has keyboard focus.
/// Strategy: read via UI Automation (ValuePattern / TextPattern) when the app supports it; otherwise fall back
/// to Ctrl+A / Ctrl+C. Writing always goes through the clipboard (Ctrl+V) so the target app's Undo works,
/// and the user's clipboard (every format, not just text) is restored afterwards.
/// All methods must be called on the UI (STA) thread; UI Automation work is moved to the thread pool.
/// </summary>
public static class TextBridge
{
    public sealed class Captured
    {
        public string Text { get; init; } = "";
        public AutomationElement? Element { get; init; }
        public bool ViaClipboard { get; init; }
    }

    /// <summary>A window handle the clipboard can be opened with (SetClipboardData fails without an owner).</summary>
    public static IntPtr OwnerHandle { get; set; }

    // MARK: Reading

    public static async Task<Captured?> CaptureAsync(string trigger)
    {
        // Browsers and Electron apps update the text they report to UI Automation a moment after the keystroke
        // lands, and build their accessibility tree lazily. So read a few times before giving up on it.
        AutomationElement? element = null;
        for (int attempt = 0; attempt < 8; attempt++)
        {
            var (el, value) = await ReadFocusedAsync();
            element = el ?? element;
            if (value is not null && value.Contains(trigger, StringComparison.Ordinal))
            {
                Log($"Read {value.Length} chars via UI Automation (attempt {attempt + 1})");
                return new Captured { Text = value, Element = el, ViaClipboard = false };
            }
            if (attempt < 7) await Task.Delay(50);
        }
        Log("UI Automation read missed the trigger; falling back to the clipboard");

        var copied = await CopyAllViaClipboardAsync();
        if (copied is null)
        {
            Log("Clipboard fallback copied nothing");
            return null;
        }
        // Ctrl+A leaves everything selected; collapse it so the cursor goes back to the end either way.
        SendKey(Win32.VK_RIGHT);
        await Task.Delay(30);
        if (!copied.Contains(trigger, StringComparison.Ordinal))
        {
            Log($"Clipboard fallback got {copied.Length} chars without the trigger");
            return null;
        }
        return new Captured { Text = copied, Element = element, ViaClipboard = true };
    }

    public static async Task<AutomationElement?> FocusedElementAsync() => (await ReadFocusedAsync(readValue: false)).Element;

    private static async Task<(AutomationElement? Element, string? Value)> ReadFocusedAsync(bool readValue = true)
    {
        var work = Task.Run(() =>
        {
            try
            {
                var el = AutomationElement.FocusedElement;
                if (el is null) return ((AutomationElement?)null, (string?)null);
                if (!readValue) return (el, null);
                // Never read password fields.
                if (el.Current.IsPassword) return (el, null);
                return (el, GetValue(el));
            }
            catch
            {
                return (null, null);
            }
        });
        try { return await work.WaitAsync(TimeSpan.FromSeconds(2)); }
        catch { return (null, null); }
    }

    /// <summary>
    /// Where to anchor the floating pill, in screen pixels: the focused field, or the text caret when the field is
    /// a whole document (taller than ~40% of the screen). Null when neither can be found.
    /// </summary>
    public static async Task<System.Drawing.Rectangle?> AnchorRectAsync(AutomationElement? element)
    {
        System.Drawing.Rectangle? bounds = null;
        if (element is not null)
        {
            try
            {
                var r = await Task.Run(() =>
                {
                    try { return element.Current.BoundingRectangle; } catch { return System.Windows.Rect.Empty; }
                }).WaitAsync(TimeSpan.FromSeconds(1));
                if (!r.IsEmpty && !double.IsInfinity(r.Width) && r.Width > 4 && r.Height > 4)
                    bounds = new System.Drawing.Rectangle((int)r.X, (int)r.Y, (int)r.Width, (int)r.Height);
            }
            catch { }
        }
        var caret = Win32.CaretRect();
        if (bounds is { } b)
        {
            var screen = System.Windows.Forms.Screen.FromRectangle(b).WorkingArea;
            if (b.Height > screen.Height * 0.4)
                return caret is { } c && b.Contains(c.Location) ? new System.Drawing.Rectangle(c.X - 100, c.Y, 200 + c.Width, c.Height) : caret;
            return b;
        }
        return caret;
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
            {
                var v = ((ValuePattern)vp).Current.Value;
                if (!string.IsNullOrEmpty(v)) return v;
            }
            if (element.TryGetCurrentPattern(TextPattern.Pattern, out var tp))
            {
                var text = ((TextPattern)tp).DocumentRange.GetText(-1);
                if (!string.IsNullOrEmpty(text)) return text.Replace("\r\n", "\n").Replace('\r', '\n');
            }
        }
        catch { /* element may be gone */ }
        return null;
    }

    private static async Task<string?> CopyAllViaClipboardAsync()
    {
        var saved = ClipboardSnapshot.Take();
        uint before = Win32.GetClipboardSequenceNumber();
        await WaitForModifiersReleasedAsync();
        SendKey(Win32.VK_A, ctrl: true);
        await Task.Delay(40);
        SendKey(Win32.VK_C, ctrl: true);
        for (int i = 0; i < 25 && Win32.GetClipboardSequenceNumber() == before; i++)
            await Task.Delay(20);
        string? text = Win32.GetClipboardSequenceNumber() != before ? ClipboardSnapshot.ReadText() : null;
        saved.Restore();
        return text?.Replace("\r\n", "\n");
    }

    // MARK: Writing

    public static async Task ReplaceAllAsync(string text)
    {
        await WaitForModifiersReleasedAsync();
        SendKey(Win32.VK_A, ctrl: true);
        await Task.Delay(40);
        await PasteAsync(text);
    }

    public static async Task DeleteBackwardAsync(int count)
    {
        await WaitForModifiersReleasedAsync();
        for (int i = 0; i < count; i++) SendKey(Win32.VK_BACK);
        await Task.Delay(30);
    }

    public static async Task PasteAsync(string text)
    {
        var saved = ClipboardSnapshot.Take();
        // Windows apps expect CRLF line breaks on the clipboard.
        var normalized = text.Replace("\r\n", "\n").Replace("\n", "\r\n");
        ClipboardSnapshot.SetText(normalized, transient: true);
        await WaitForModifiersReleasedAsync();
        SendKey(Win32.VK_V, ctrl: true);
        await Task.Delay(250);
        saved.Restore();
    }

    public static void CopyToClipboard(string text) =>
        ClipboardSnapshot.SetText(text.Replace("\r\n", "\n").Replace("\n", "\r\n"), transient: false);

    // MARK: Keystrokes via SendInput

    /// <summary>
    /// The trigger's last key (often typed with Shift) may still be held. Ctrl+A with Shift down is a different
    /// shortcut, so wait briefly for the physical modifiers to come up.
    /// </summary>
    private static async Task WaitForModifiersReleasedAsync()
    {
        for (int i = 0; i < 40; i++)
        {
            if (!IsDown(Win32.VK_SHIFT) && !IsDown(Win32.VK_CONTROL) && !IsDown(Win32.VK_MENU)
                && !IsDown(Win32.VK_LWIN) && !IsDown(Win32.VK_RWIN)) return;
            await Task.Delay(15);
        }
        static bool IsDown(int vk) => (Win32.GetAsyncKeyState(vk) & 0x8000) != 0;
    }

    public static void SendKey(ushort vk, bool ctrl = false)
    {
        var inputs = new List<Win32.INPUT>();
        if (ctrl) inputs.Add(KeyInput(Win32.VK_CONTROL, false));
        inputs.Add(KeyInput(vk, false));
        inputs.Add(KeyInput(vk, true));
        if (ctrl) inputs.Add(KeyInput(Win32.VK_CONTROL, true));
        var arr = inputs.ToArray();
        Win32.SendInput((uint)arr.Length, arr, Win32.INPUT.Size);
    }

    private static Win32.INPUT KeyInput(ushort vk, bool up)
    {
        var input = new Win32.INPUT { type = Win32.INPUT_KEYBOARD };
        input.U.ki.wVk = vk;
        input.U.ki.wScan = (ushort)Win32.MapVirtualKey(vk, 0);
        uint flags = up ? Win32.KEYEVENTF_KEYUP : 0;
        if (vk is Win32.VK_LEFT or Win32.VK_RIGHT or Win32.VK_UP or Win32.VK_DOWN or Win32.VK_DELETE
            or Win32.VK_HOME or Win32.VK_END or Win32.VK_PRIOR or Win32.VK_NEXT)
            flags |= Win32.KEYEVENTF_EXTENDEDKEY;
        input.U.ki.dwFlags = flags;
        input.U.ki.dwExtraInfo = Win32.InkwellInputMarker;
        return input;
    }

    [Conditional("DEBUG")]
    private static void Log(string message) => Debug.WriteLine("[Inkwell capture] " + message);

    // MARK: Clipboard (raw Win32, so every format survives a round trip)

    private sealed class ClipboardSnapshot
    {
        private readonly List<(uint Format, byte[] Data)> _items = new();

        // GDI-handle formats can't be copied as bytes; their HGLOBAL equivalents (CF_DIB…) carry the same content.
        private static readonly HashSet<uint> Skip = new() { 2, 3, 9, 14, 0x80, 0x82, 0x83, 0x8E };

        public static ClipboardSnapshot Take()
        {
            var snap = new ClipboardSnapshot();
            if (!Open()) return snap;
            try
            {
                uint fmt = 0;
                while ((fmt = Win32.EnumClipboardFormats(fmt)) != 0)
                {
                    if (Skip.Contains(fmt)) continue;
                    var h = Win32.GetClipboardData(fmt);
                    if (h == IntPtr.Zero) continue;
                    var size = (long)(ulong)Win32.GlobalSize(h);
                    if (size <= 0 || size > 64L * 1024 * 1024) continue;
                    var p = Win32.GlobalLock(h);
                    if (p == IntPtr.Zero) continue;
                    try
                    {
                        var bytes = new byte[(int)size];
                        Marshal.Copy(p, bytes, 0, (int)size);
                        snap._items.Add((fmt, bytes));
                    }
                    finally { Win32.GlobalUnlock(h); }
                }
            }
            catch { }
            finally { Win32.CloseClipboard(); }
            return snap;
        }

        public void Restore()
        {
            if (!Open()) return;
            try
            {
                Win32.EmptyClipboard();
                foreach (var (fmt, data) in _items) Put(fmt, data);
            }
            catch { }
            finally { Win32.CloseClipboard(); }
        }

        public static void SetText(string text, bool transient)
        {
            if (!Open()) return;
            try
            {
                Win32.EmptyClipboard();
                var bytes = new byte[(text.Length + 1) * 2];
                System.Text.Encoding.Unicode.GetBytes(text, 0, text.Length, bytes, 0);
                Put(Win32.CF_UNICODETEXT, bytes);
                if (transient)
                {
                    // Keep this temporary entry out of Win+V history, cloud clipboard and clipboard managers.
                    var zero = BitConverter.GetBytes(0);
                    Put(Win32.RegisterClipboardFormat("CanIncludeInClipboardHistory"), zero);
                    Put(Win32.RegisterClipboardFormat("CanUploadToCloudClipboard"), zero);
                    Put(Win32.RegisterClipboardFormat("ExcludeClipboardContentFromMonitorProcessing"), zero);
                    Put(Win32.RegisterClipboardFormat("Clipboard Viewer Ignore"), zero);
                }
            }
            catch { }
            finally { Win32.CloseClipboard(); }
        }

        public static string? ReadText()
        {
            if (!Open()) return null;
            try
            {
                var h = Win32.GetClipboardData(Win32.CF_UNICODETEXT);
                if (h == IntPtr.Zero) return null;
                var p = Win32.GlobalLock(h);
                if (p == IntPtr.Zero) return null;
                try { return Marshal.PtrToStringUni(p); }
                finally { Win32.GlobalUnlock(h); }
            }
            catch { return null; }
            finally { Win32.CloseClipboard(); }
        }

        private static void Put(uint fmt, byte[] data)
        {
            var h = Win32.GlobalAlloc(Win32.GMEM_MOVEABLE, (UIntPtr)Math.Max(1, data.Length));
            if (h == IntPtr.Zero) return;
            var p = Win32.GlobalLock(h);
            if (p == IntPtr.Zero) { Win32.GlobalFree(h); return; }
            Marshal.Copy(data, 0, p, data.Length);
            Win32.GlobalUnlock(h);
            if (Win32.SetClipboardData(fmt, h) == IntPtr.Zero) Win32.GlobalFree(h);
        }

        /// <summary>Another app may briefly hold the clipboard; retry for up to ~250 ms.</summary>
        private static bool Open()
        {
            for (int i = 0; i < 25; i++)
            {
                if (Win32.OpenClipboard(OwnerHandle)) return true;
                System.Threading.Thread.Sleep(10);
            }
            return false;
        }
    }
}
