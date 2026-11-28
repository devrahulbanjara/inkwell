using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Interop;

namespace Inkwell.Platform;

/// <summary>
/// Global low-level keyboard + mouse hook that keeps a 64-char rolling buffer.
/// Uses ToUnicodeEx with flag 4 to translate scancodes into characters without
/// mutating the dead-key state of the foreground app.
/// </summary>
public sealed class KeyMonitor : IDisposable
{
    private const int MaxLength = 64;
    private IntPtr _keyHook;
    private IntPtr _mouseHook;
    private readonly Win32.HookProc _keyProc;
    private readonly Win32.HookProc _mouseProc;

    private string _buffer = "";
    public string Buffer => _buffer;
    public bool IsPaused { get; set; }
    public bool IsRunning => _keyHook != IntPtr.Zero;

    public event Action<string>? BufferChanged;

    public KeyMonitor()
    {
        // prevent GC of delegates while hooks are active
        _keyProc = KeyCallback;
        _mouseProc = MouseCallback;
    }

    public void Start()
    {
        if (_keyHook != IntPtr.Zero) return;
        var mod = Win32.GetModuleHandle(null);
        _keyHook = Win32.SetWindowsHookEx(Win32.WH_KEYBOARD_LL, _keyProc, mod, 0);
        _mouseHook = Win32.SetWindowsHookEx(Win32.WH_MOUSE_LL, _mouseProc, mod, 0);
    }

    public void Stop()
    {
        if (_keyHook != IntPtr.Zero) { Win32.UnhookWindowsHookEx(_keyHook); _keyHook = IntPtr.Zero; }
        if (_mouseHook != IntPtr.Zero) { Win32.UnhookWindowsHookEx(_mouseHook); _mouseHook = IntPtr.Zero; }
        Reset();
    }

    public void Reset() => _buffer = "";

    private IntPtr KeyCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && !IsPaused &&
            (wParam == (IntPtr)Win32.WM_KEYDOWN || wParam == (IntPtr)Win32.WM_SYSKEYDOWN))
        {
            var info = Marshal.PtrToStructure<Win32.KBDLLHOOKSTRUCT>(lParam);
            // Ignore injected keystrokes (from SendInput, etc.)
            // Skip only Inkwell's own keystrokes, so the touch keyboard, pen input and remote desktop still work.
            if (info.dwExtraInfo != Win32.InkwellInputMarker)
            {
                HandleKey(info);
            }
        }
        return Win32.CallNextHookEx(_keyHook, nCode, wParam, lParam);
    }

    private IntPtr MouseCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && (wParam == (IntPtr)Win32.WM_LBUTTONDOWN || wParam == (IntPtr)Win32.WM_RBUTTONDOWN))
        {
            Reset(); // cursor moved — context is lost
        }
        return Win32.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    private void HandleKey(Win32.KBDLLHOOKSTRUCT info)
    {
        ushort vk = (ushort)info.vkCode;

        // Modifier-only or nav keys: Ctrl/Cmd breaks => reset
        bool ctrl = (Win32.GetAsyncKeyState(Win32.VK_CONTROL) & 0x8000) != 0;
        bool win = (Win32.GetAsyncKeyState(Win32.VK_LWIN) & 0x8000) != 0
                 || (Win32.GetAsyncKeyState(Win32.VK_RWIN) & 0x8000) != 0;
        // AltGr arrives as Ctrl+Alt; it types characters (e.g. @ on German layouts), so it isn't a shortcut.
        bool altGr = (Win32.GetAsyncKeyState(Win32.VK_RMENU) & 0x8000) != 0;
        if ((ctrl && !altGr) || win) { Reset(); return; }

        switch (vk)
        {
            case Win32.VK_BACK:
                bool alt = (Win32.GetAsyncKeyState(Win32.VK_MENU) & 0x8000) != 0;
                if (alt) Reset();
                else if (_buffer.Length > 0) _buffer = _buffer[..^1];
                return;

            case Win32.VK_RETURN:
            case Win32.VK_TAB:
            case Win32.VK_ESCAPE:
            case Win32.VK_HOME:
            case Win32.VK_END:
            case Win32.VK_PRIOR:
            case Win32.VK_NEXT:
            case Win32.VK_DELETE:
            case Win32.VK_LEFT:
            case Win32.VK_RIGHT:
            case Win32.VK_UP:
            case Win32.VK_DOWN:
                Reset();
                return;

            case Win32.VK_SHIFT:
            case Win32.VK_LSHIFT:
            case Win32.VK_RSHIFT:
            case Win32.VK_CONTROL:
            case Win32.VK_LCONTROL:
            case Win32.VK_RCONTROL:
            case Win32.VK_MENU:
            case Win32.VK_LMENU:
            case Win32.VK_RMENU:
            case Win32.VK_CAPITAL:
            case Win32.VK_LWIN:
            case Win32.VK_RWIN:
                return; // ignore modifier-only presses
        }

        var ch = TranslateKey(info);
        if (string.IsNullOrEmpty(ch)) return;

        _buffer += ch;
        if (_buffer.Length > MaxLength) _buffer = _buffer[^MaxLength..];
        BufferChanged?.Invoke(_buffer);
    }

    /// <summary>
    /// Translate a virtual key into a Unicode string using the foreground window's keyboard layout.
    /// Flag 4 = don't change dead-key state.
    /// </summary>
    private static string TranslateKey(Win32.KBDLLHOOKSTRUCT info)
    {
        var buf = new StringBuilder(8);
        var keyState = new byte[256];

        // snapshot current modifier state
        // A low-level hook runs before the target app sees the key, so read the live (async) modifier state.
        keyState[Win32.VK_SHIFT] = (byte)((Win32.GetAsyncKeyState(Win32.VK_SHIFT) & 0x8000) != 0 ? 0x80 : 0);
        keyState[Win32.VK_CAPITAL] = (byte)((Win32.GetKeyState(Win32.VK_CAPITAL) & 0x0001) != 0 ? 0x01 : 0);
        keyState[Win32.VK_MENU] = (byte)((Win32.GetAsyncKeyState(Win32.VK_MENU) & 0x8000) != 0 ? 0x80 : 0);
        keyState[Win32.VK_RMENU] = (byte)((Win32.GetAsyncKeyState(Win32.VK_RMENU) & 0x8000) != 0 ? 0x80 : 0);
        // AltGr = Ctrl+RAlt
        if (keyState[Win32.VK_RMENU] != 0) keyState[Win32.VK_CONTROL] = 0x80;

        var hwnd = Win32.GetForegroundWindow();
        uint tid = Win32.GetWindowThreadProcessId(hwnd, out _);
        var layout = Win32.GetKeyboardLayout(tid);

        int result = Win32.ToUnicodeEx(info.vkCode, info.scanCode, keyState, buf, buf.Capacity, 4, layout);
        return result >= 1 ? buf.ToString(0, result) : "";
    }

    public void Dispose()
    {
        Stop();
    }
}
