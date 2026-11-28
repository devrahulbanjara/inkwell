using System;
using System.Linq;
using System.Threading.Tasks;
using Inkwell.Platform;
using Inkwell.Providers;
using Inkwell.Views;

namespace Inkwell.Core;

public sealed class InkwellException : Exception
{
    public InkwellException(string message) : base(message) { }
    public static InkwellException NoText() => new("Couldn't read the text in this field.");
    public static InkwellException FocusChanged() => new("You switched fields, so the result was copied to your clipboard.");
}

/// <summary>
/// The app's brain, same as the Mac AppState: watches typing for triggers, runs the AI, writes the result back,
/// and records usage. Everything here runs on the UI thread.
/// </summary>
public sealed class AppState : ObservableObject
{
    public SettingsStore Settings { get; }
    public UsageStore Usage { get; }
    public QuotaTracker Quota { get; } = new();
    public CLIStatusStore CliStatus { get; } = new();
    public AIRouter Router { get; }
    public UpdateChecker Updates { get; }

    private readonly KeyMonitor _monitor = new();

    private bool _isWorking;
    public bool IsWorking { get => _isWorking; private set { if (SetProperty(ref _isWorking, value)) OnPropertyChanged(nameof(StatusText)); } }

    private string? _lastError;
    public string? LastError { get => _lastError; set => SetProperty(ref _lastError, value); }

    /// <summary>Windows needs no Accessibility-style permission; kept for parity with the Mac UI.</summary>
    public bool HasAccessibility => true;

    public AppState(SettingsStore? settings = null, UsageStore? usage = null)
    {
        Settings = settings ?? new SettingsStore();
        Usage = usage ?? new UsageStore();
        Router = new AIRouter(Settings, Quota);
        Updates = new UpdateChecker();
        Settings.Changed += () => OnPropertyChanged(nameof(StatusText));
    }

    public void Start()
    {
        _monitor.BufferChanged += Check;
        _monitor.Start();
        Updates.Start();
        _ = CliStatus.RefreshAllAsync(Settings);
    }

    public void Stop()
    {
        _monitor.Stop();
        Updates.Stop();
    }

    public string StatusText
    {
        get
        {
            if (IsWorking) return "Working…";
            if (!Settings.Prefs.IsEnabled) return "Paused";
            return $"Listening · {Settings.ActiveSummary}";
        }
    }

    // MARK: Trigger handling

    private void Check(string buffer)
    {
        if (!Settings.Prefs.IsEnabled || IsWorking) return;
        var template = Settings.ActiveTemplates.FirstOrDefault(t => buffer.EndsWith(t.Trigger, StringComparison.Ordinal));
        if (template is null) return;
        _monitor.Reset();
        _ = RunAsync(template);
    }

    private async Task RunAsync(Template template)
    {
        IsWorking = true;
        LastError = null;
        _monitor.IsPaused = true;

        var started = DateTime.Now;
        int inputWords = 0;
        var provider = Settings.Prefs.ActiveProvider;
        var model = Settings.ActiveSummary;
        var hud = Settings.Prefs.ShowHud;
        Router.OnStatus = message => { if (hud) HudController.Working(message); };

        try
        {
            // Let the target app finish inserting the trigger's last character.
            await Task.Delay(80);
            // Float the pill just above the field being edited.
            if (hud) HudController.Anchor(await TextBridge.AnchorRectAsync(await TextBridge.FocusedElementAsync()));

            var captured = await TextBridge.CaptureAsync(template.Trigger) ?? throw InkwellException.NoText();
            var text = captured.Text;
            var idx = text.LastIndexOf(template.Trigger, StringComparison.Ordinal);
            if (idx >= 0) text = text.Remove(idx, template.Trigger.Length);
            var input = text.Trim();
            if (input.Length == 0) throw InkwellException.NoText();
            inputWords = input.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

            if (hud) HudController.Working(template.WorkingLabel);
            var result = await Router.CompleteAsync(Prompts.System, Prompts.User(template, input));
            model = result.Model;

            var nowFocused = await TextBridge.FocusedElementAsync();
            if (!TextBridge.IsSameElement(captured.Element, nowFocused))
            {
                TextBridge.CopyToClipboard(result.Text);
                throw InkwellException.FocusChanged();
            }

            if (template.Mode == OutputMode.Replace)
            {
                await TextBridge.ReplaceAllAsync(result.Text);
            }
            else
            {
                await TextBridge.DeleteBackwardAsync(template.Trigger.Length);
                await TextBridge.PasteAsync("\n\n" + result.Text);
            }

            Usage.Record(new UsageEvent
            {
                Date = started, TemplateName = template.Name, Provider = provider.RawValue(), Model = model,
                InputTokens = result.InputTokens, OutputTokens = result.OutputTokens,
                Latency = (DateTime.Now - started).TotalSeconds, InputWords = inputWords, Success = true,
                KeyLabel = result.KeyLabel
            });
            if (hud) HudController.Done("Done");
            if (Settings.Prefs.PlaySounds) Sounds.Success();
        }
        catch (Exception ex)
        {
            var message = ex is InkwellException ? ex.Message : AIException.From(ex).Message;
            LastError = message;
            Usage.Record(new UsageEvent
            {
                Date = started, TemplateName = template.Name, Provider = provider.RawValue(), Model = model,
                Latency = (DateTime.Now - started).TotalSeconds, InputWords = inputWords, Success = false,
                ErrorKind = ex is InkwellException ? "capture" : AIException.From(ex).StatsKind
            });
            if (hud) HudController.Failed(message);
            if (Settings.Prefs.PlaySounds) Sounds.Failure();
        }
        finally
        {
            IsWorking = false;
            _monitor.IsPaused = false;
            _monitor.Reset();
        }
    }
}
