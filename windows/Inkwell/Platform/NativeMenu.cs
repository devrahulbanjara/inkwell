using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;

namespace Inkwell.Platform;

/// <summary>
/// A real Win32 popup menu (what Explorer and every tray icon use), so it gets Windows 11's own rounded menu
/// styling, keyboard navigation, accessibility and dark mode. Items run closures.
/// </summary>
public sealed class NativeMenu : IDisposable
{
    private readonly IntPtr _handle = Win32.CreatePopupMenu();
    private readonly Root _root;
    private readonly List<NativeMenu> _children = new();

    private sealed class Root
    {
        public readonly Dictionary<uint, Action> Actions = new();
        public readonly List<IntPtr> Bitmaps = new();
        public uint NextId = 1;
    }

    public NativeMenu() => _root = new Root();
    private NativeMenu(Root root) => _root = root;

    /// <summary>A greyed-out line of text, e.g. "Listening · Gemini".</summary>
    public void Header(string text) => Win32.AppendMenu(_handle, Win32.MF_STRING | Win32.MF_GRAYED, UIntPtr.Zero, Escape(text));

    public void Item(string text, Action? action, bool isChecked = false, bool enabled = true, BitmapSource? icon = null,
        bool radio = false, string? shortcut = null)
    {
        uint id = _root.NextId++;
        if (action is not null) _root.Actions[id] = action;
        var label = Escape(text) + (shortcut is null ? "" : "\t" + shortcut);
        uint flags = Win32.MF_STRING | (enabled && action is not null ? 0 : Win32.MF_GRAYED) | (isChecked ? Win32.MF_CHECKED : 0);
        Win32.AppendMenu(_handle, flags, (UIntPtr)id, label);
        Decorate(Win32.GetMenuItemCount(_handle) - 1, icon, radio && isChecked);
    }

    public NativeMenu Submenu(string text, BitmapSource? icon = null, bool enabled = true)
    {
        var sub = new NativeMenu(_root);
        _children.Add(sub);
        Win32.AppendMenu(_handle, Win32.MF_POPUP | (enabled ? 0 : Win32.MF_GRAYED), (UIntPtr)(ulong)sub._handle, Escape(text));
        Decorate(Win32.GetMenuItemCount(_handle) - 1, icon, false);
        return sub;
    }

    public void Separator() => Win32.AppendMenu(_handle, Win32.MF_SEPARATOR, UIntPtr.Zero, null);

    /// <summary>Shows the menu at a screen point (pixels) and runs the chosen item's action.</summary>
    public void Show(IntPtr owner, int x, int y, bool alignBottomRight = false)
    {
        // Required so the menu closes when the user clicks elsewhere (documented TrackPopupMenu quirk).
        Win32.SetForegroundWindow(owner);
        uint flags = Win32.TPM_RIGHTBUTTON | Win32.TPM_RETURNCMD | Win32.TPM_NONOTIFY;
        if (alignBottomRight) flags |= Win32.TPM_BOTTOMALIGN | Win32.TPM_RIGHTALIGN;
        uint cmd = Win32.TrackPopupMenuEx(_handle, flags, x, y, owner, IntPtr.Zero);
        Win32.PostMessage(owner, 0, IntPtr.Zero, IntPtr.Zero); // WM_NULL
        if (cmd != 0 && _root.Actions.TryGetValue(cmd, out var action))
        {
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(action);
        }
    }

    private void Decorate(int position, BitmapSource? icon, bool radio)
    {
        if (position < 0 || (icon is null && !radio)) return;
        var info = new Win32.MENUITEMINFO { cbSize = Marshal.SizeOf<Win32.MENUITEMINFO>() };
        if (radio)
        {
            info.fMask |= Win32.MIIM_FTYPE | Win32.MIIM_STATE;
            info.fType = Win32.MFT_RADIOCHECK;
            info.fState = Win32.MFS_CHECKED;
        }
        if (icon is not null)
        {
            var hbmp = ToHBitmap(icon);
            if (hbmp != IntPtr.Zero)
            {
                _root.Bitmaps.Add(hbmp);
                info.fMask |= Win32.MIIM_BITMAP;
                info.hbmpItem = hbmp;
            }
        }
        Win32.SetMenuItemInfo(_handle, (uint)position, true, ref info);
    }

    /// <summary>32-bit premultiplied top-down DIB, which menus draw with alpha.</summary>
    private static IntPtr ToHBitmap(BitmapSource source)
    {
        var src = source.Format == System.Windows.Media.PixelFormats.Pbgra32
            ? source
            : new FormatConvertedBitmap(source, System.Windows.Media.PixelFormats.Pbgra32, null, 0);
        int w = src.PixelWidth, h = src.PixelHeight;
        var pixels = new byte[w * h * 4];
        src.CopyPixels(pixels, w * 4, 0);
        var bmi = new Win32.BITMAPINFOHEADER
        {
            biSize = Marshal.SizeOf<Win32.BITMAPINFOHEADER>(), biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32,
        };
        var hbmp = Win32.CreateDIBSection(IntPtr.Zero, ref bmi, 0, out var bits, IntPtr.Zero, 0);
        if (hbmp == IntPtr.Zero || bits == IntPtr.Zero) return IntPtr.Zero;
        Marshal.Copy(pixels, 0, bits, pixels.Length);
        return hbmp;
    }

    /// <summary>"&" marks a mnemonic in menus; show it literally.</summary>
    private static string Escape(string text) => text.Replace("&", "&&");

    public void Dispose()
    {
        foreach (var c in _children) c.DisposeHandleOnly();
        Win32.DestroyMenu(_handle);
        foreach (var b in _root.Bitmaps) Win32.DeleteObject(b);
        _root.Bitmaps.Clear();
    }

    private void DisposeHandleOnly()
    {
        foreach (var c in _children) c.DisposeHandleOnly();
        // Submenus are destroyed together with their parent menu.
    }
}
