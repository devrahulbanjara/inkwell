using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace Inkwell.Core;

/// <summary>
/// Looks for a newer GitHub release at launch and every few hours. Installing is one click:
/// the release's Inkwell-Setup.exe is downloaded and run silently; it closes Inkwell, updates it in place
/// and starts it again. Settings and keys live in %LOCALAPPDATA%\Inkwell, so they carry over.
/// </summary>
public sealed class UpdateChecker : ObservableObject
{
    public const string Repo = "devrahulbanjara/inkwell";
    public const string ReleasesPage = "https://github.com/" + Repo + "/releases/latest";
    private const string SetupAssetName = "Inkwell-Setup.exe";

    private static readonly HttpClient Http = CreateClient();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromHours(6) };

    public static Version CurrentVersion { get; } = ReadCurrentVersion();
    public static string CurrentVersionText => $"{CurrentVersion.Major}.{CurrentVersion.Minor}.{Math.Max(0, CurrentVersion.Build)}";

    private string? _availableVersion;
    /// <summary>The newer version's number ("0.1.2"), or null when Inkwell is up to date.</summary>
    public string? AvailableVersion { get => _availableVersion; private set { if (SetProperty(ref _availableVersion, value)) Changed?.Invoke(); } }

    private string? _status;
    /// <summary>Progress / error text while installing ("Downloading…").</summary>
    public string? Status { get => _status; private set { if (SetProperty(ref _status, value)) Changed?.Invoke(); } }

    public bool IsInstalling { get; private set; }

    private bool _dismissed;
    /// <summary>The dashboard banner was closed for this version (the tray menu keeps offering it).</summary>
    public bool Dismissed { get => _dismissed; set { if (SetProperty(ref _dismissed, value)) Changed?.Invoke(); } }

    private string? _setupUrl;
    private string _pageUrl = ReleasesPage;

    public event Action? Changed;

    public void Start()
    {
        _timer.Tick += async (_, _) => await CheckAsync();
        _timer.Start();
        // Don't slow down login: first check shortly after launch.
        var first = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
        first.Tick += async (_, _) => { first.Stop(); await CheckAsync(); };
        first.Start();
    }

    public void Stop() => _timer.Stop();

    /// <summary>Returns true when an update is available.</summary>
    public async Task<bool> CheckAsync()
    {
        try
        {
            using var resp = await Http.GetAsync($"https://api.github.com/repos/{Repo}/releases/latest");
            if (!resp.IsSuccessStatusCode) return AvailableVersion is not null;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var root = doc.RootElement;
            if (root.TryGetProperty("draft", out var d) && d.GetBoolean()) return false;
            var tag = root.GetProperty("tag_name").GetString() ?? "";
            if (!TryParse(tag, out var latest)) return false;

            _pageUrl = root.TryGetProperty("html_url", out var h) ? h.GetString() ?? ReleasesPage : ReleasesPage;
            _setupUrl = null;
            if (root.TryGetProperty("assets", out var assets))
            {
                _setupUrl = assets.EnumerateArray()
                    .Where(a => string.Equals(a.GetProperty("name").GetString(), SetupAssetName, StringComparison.OrdinalIgnoreCase))
                    .Select(a => a.GetProperty("browser_download_url").GetString())
                    .FirstOrDefault();
            }

            // A release without the Windows installer (e.g. a Mac-only fix) isn't an update for this app.
            if (latest > CurrentVersion && _setupUrl is not null)
            {
                var text = $"{latest.Major}.{latest.Minor}.{Math.Max(0, latest.Build)}";
                if (text != AvailableVersion) Dismissed = false;
                AvailableVersion = text;
                return true;
            }
            AvailableVersion = null;
            return false;
        }
        catch
        {
            return AvailableVersion is not null; // offline etc. — try again later
        }
    }

    /// <summary>Downloads and runs the installer, then quits so it can replace the app.</summary>
    public async Task InstallAsync()
    {
        if (IsInstalling) return;
        if (_setupUrl is null)
        {
            Platform.Shell.Open(_pageUrl);
            return;
        }
        IsInstalling = true;
        try
        {
            Status = "Downloading update…";
            var dir = Path.Combine(Path.GetTempPath(), "InkwellUpdate");
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, $"Inkwell-Setup-{AvailableVersion}.exe");
            using (var resp = await Http.GetAsync(_setupUrl, HttpCompletionOption.ResponseHeadersRead))
            {
                resp.EnsureSuccessStatusCode();
                await using var fs = File.Create(file);
                await resp.Content.CopyToAsync(fs);
            }

            Status = "Installing…";
            Process.Start(new ProcessStartInfo(file, "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS")
            {
                UseShellExecute = true
            });
            System.Windows.Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            Status = $"Update failed: {ex.Message}";
            IsInstalling = false;
            Platform.Shell.Open(_pageUrl);
        }
    }

    private static bool TryParse(string tag, out Version version)
    {
        var s = tag.Trim().TrimStart('v', 'V');
        var dash = s.IndexOfAny(new[] { '-', '+' });
        if (dash >= 0) s = s[..dash];
        if (!Version.TryParse(s.Count(c => c == '.') == 0 ? s + ".0" : s, out var v)) { version = new Version(0, 0); return false; }
        version = new Version(v.Major, v.Minor, Math.Max(0, v.Build));
        return true;
    }

    private static Version ReadCurrentVersion()
    {
        var info = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (info is not null && TryParse(info, out var v)) return v;
        var a = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);
        return new Version(a.Major, a.Minor, Math.Max(0, a.Build));
    }

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("Inkwell-Windows-Updater");
        c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return c;
    }
}
