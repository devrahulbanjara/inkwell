using System;
using System.Diagnostics;
using System.IO;
using System.Media;
using Microsoft.Win32;

namespace Inkwell.Platform;

/// <summary>Success "pop" (like macOS's Pop) and the system beep on failure.</summary>
public static class Sounds
{
    private static readonly Lazy<SoundPlayer?> Pop = new(() =>
    {
        try { var p = new SoundPlayer(new MemoryStream(MakePop())); p.Load(); return p; }
        catch { return null; }
    });

    public static void Success()
    {
        try { Pop.Value?.Play(); } catch { }
    }

    public static void Failure() => Win32.MessageBeep(Win32.MB_OK);

    /// <summary>A soft 70 ms "pop": a falling sine with a fast decay, rendered as 16-bit mono WAV.</summary>
    private static byte[] MakePop()
    {
        const int rate = 44100;
        int n = rate * 70 / 1000;
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write("RIFF"u8); w.Write(36 + n * 2); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8); w.Write(n * 2);
        double phase = 0;
        for (int i = 0; i < n; i++)
        {
            double t = (double)i / n;
            double freq = 900 - 380 * t;
            phase += 2 * Math.PI * freq / rate;
            double attack = Math.Min(1, i / (rate * 0.003));
            double env = attack * Math.Exp(-6.5 * t);
            w.Write((short)(Math.Sin(phase) * env * 0.32 * short.MaxValue));
        }
        w.Flush();
        return ms.ToArray();
    }
}

/// <summary>"Launch at login" via the per-user Run key (what Windows Settings → Apps → Startup lists).</summary>
public static class LaunchAtLogin
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Name = "Inkwell";

    public static string ExecutablePath => Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? "";

    public static bool IsEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey);
                return key?.GetValue(Name) is string s && s.Contains(ExecutablePath, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (value) key.SetValue(Name, $"\"{ExecutablePath}\" --background");
            else key.DeleteValue(Name, throwOnMissingValue: false);
        }
    }
}

/// <summary>Opens URLs and folders with the user's default handler.</summary>
public static class Shell
{
    public static void Open(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch { }
    }
}
