using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Inkwell.Providers;

namespace Inkwell.Core;

public sealed class SettingsStore : ObservableObject
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public string DataFolder { get; }
    private readonly string _prefsPath;
    private readonly string _vaultPath;
    private readonly bool _ephemeral;
    private Dictionary<string, string> _secrets = new();

    public Preferences Prefs { get; private set; }

    public event Action? Changed;

    public SettingsStore(bool ephemeral = false)
    {
        _ephemeral = ephemeral;
        DataFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Inkwell");
        _prefsPath = Path.Combine(DataFolder, "preferences.json");
        _vaultPath = Path.Combine(DataFolder, "keys.dat");

        if (ephemeral)
        {
            Prefs = new Preferences();
            return;
        }

        Directory.CreateDirectory(DataFolder);
        Prefs = LoadPreferences() ?? new Preferences();
        EnsureDefaults();
        _secrets = LoadSecrets();
    }

    private Preferences? LoadPreferences()
    {
        try
        {
            if (!File.Exists(_prefsPath)) return null;
            var json = File.ReadAllText(_prefsPath);
            return JsonSerializer.Deserialize<Preferences>(json, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private void EnsureDefaults()
    {
        Prefs.Gemini ??= new APIProviderSettings();
        if (Prefs.Gemini.Models.Count == 0) Prefs.Gemini.Models = ModelCatalog.DefaultModelIDs(ProviderKind.Gemini);

        Prefs.Groq ??= new APIProviderSettings();
        if (Prefs.Groq.Models.Count == 0) Prefs.Groq.Models = ModelCatalog.DefaultModelIDs(ProviderKind.Groq);

        Prefs.Claude ??= new CLIProviderSettings { Model = "haiku" };
        Prefs.Codex ??= new CLIProviderSettings();
        if (Prefs.Templates is null || Prefs.Templates.Count == 0) Prefs.Templates = Template.CreateDefaults();
    }

    private Dictionary<string, string> LoadSecrets()
    {
        try
        {
            var json = DpapiVault.Read(_vaultPath);
            if (string.IsNullOrWhiteSpace(json)) return new();
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new();
        }
        catch
        {
            return new();
        }
    }

    public void Save()
    {
        if (!_ephemeral)
        {
            try
            {
                Directory.CreateDirectory(DataFolder);
                var json = JsonSerializer.Serialize(Prefs, JsonOptions);
                var tmp = _prefsPath + ".tmp";
                File.WriteAllText(tmp, json);
                File.Move(tmp, _prefsPath, overwrite: true);
            }
            catch
            {
                // Ignore transient disk write failures
            }
        }
        NotifyChanged();
    }

    public void NotifyChanged()
    {
        OnPropertyChanged(nameof(Prefs));
        OnPropertyChanged(nameof(ActiveSummary));
        Changed?.Invoke();
    }

    private void SaveSecrets()
    {
        if (_ephemeral) return;
        try
        {
            var json = JsonSerializer.Serialize(_secrets);
            DpapiVault.Write(_vaultPath, json);
        }
        catch
        {
            // Ignore write errors
        }
    }

    // MARK: Provider settings

    public APIProviderSettings Api(ProviderKind kind) => kind == ProviderKind.Groq ? Prefs.Groq : Prefs.Gemini;

    public void UpdateApi(ProviderKind kind, Action<APIProviderSettings> change)
    {
        change(Api(kind));
        Save();
    }

    public CLIProviderSettings Cli(ProviderKind kind) => kind == ProviderKind.Codex ? Prefs.Codex : Prefs.Claude;

    public void UpdateCli(ProviderKind kind, Action<CLIProviderSettings> change)
    {
        change(Cli(kind));
        Save();
    }

    public void SetActiveProvider(ProviderKind kind)
    {
        Prefs.ActiveProvider = kind;
        Save();
    }

    public void SetEnabled(bool enabled)
    {
        Prefs.IsEnabled = enabled;
        Save();
    }

    // MARK: Keys

    public string Secret(Guid id) =>
        _secrets.TryGetValue(id.ToString(), out var raw) ? raw.Trim() : "";

    public APIKeyMeta? AddKey(ProviderKind kind, string rawSecret, string label)
    {
        var secret = rawSecret.Trim();
        if (string.IsNullOrEmpty(secret)) return null;
        var cfg = Api(kind);
        if (cfg.Keys.Any(k => Secret(k.Id) == secret)) return null;

        var hint = secret.Length >= 4 ? secret[^4..] : secret;
        var meta = new APIKeyMeta
        {
            Label = string.IsNullOrWhiteSpace(label) ? $"Key {cfg.Keys.Count + 1}" : label.Trim(),
            Hint = hint
        };
        _secrets[meta.Id.ToString()] = secret;
        SaveSecrets();
        cfg.Keys.Add(meta);
        Save();
        return meta;
    }

    public void RemoveKey(ProviderKind kind, Guid id)
    {
        _secrets.Remove(id.ToString());
        SaveSecrets();
        Api(kind).Keys.RemoveAll(k => k.Id == id);
        Save();
    }

    public void SetKeyEnabled(ProviderKind kind, Guid id, bool enabled)
    {
        var meta = Api(kind).Keys.FirstOrDefault(k => k.Id == id);
        if (meta is null) return;
        meta.IsEnabled = enabled;
        Save();
    }

    public bool HasUsableKey(ProviderKind kind) =>
        Api(kind).Keys.Any(k => k.IsEnabled && !string.IsNullOrEmpty(Secret(k.Id)));

    public string? FirstSecret(ProviderKind kind) =>
        Api(kind).Keys.Select(k => Secret(k.Id)).FirstOrDefault(s => !string.IsNullOrEmpty(s));

    // MARK: Model order

    public List<string> EffectiveModels(ProviderKind kind)
    {
        var m = Api(kind).Models;
        return m.Count == 0 ? ModelCatalog.DefaultModelIDs(kind) : new List<string>(m);
    }

    public void SetPrimaryModel(ProviderKind kind, string id)
    {
        if (!kind.UsesApiKeys())
        {
            UpdateCli(kind, c => c.Model = id);
            return;
        }
        var models = EffectiveModels(kind);
        models.RemoveAll(m => m == id);
        models.Insert(0, id);
        UpdateApi(kind, a => a.Models = models);
    }

    public void ToggleModel(ProviderKind kind, string id)
    {
        var models = EffectiveModels(kind);
        var idx = models.IndexOf(id);
        if (idx >= 0)
        {
            if (models.Count <= 1) return;
            models.RemoveAt(idx);
        }
        else
        {
            models.Add(id);
        }
        UpdateApi(kind, a => a.Models = models);
    }

    public void MoveModelUp(ProviderKind kind, string id)
    {
        var models = EffectiveModels(kind);
        var idx = models.IndexOf(id);
        if (idx <= 0) return;
        (models[idx], models[idx - 1]) = (models[idx - 1], models[idx]);
        UpdateApi(kind, a => a.Models = models);
    }

    public List<Template> ActiveTemplates =>
        Prefs.Templates
            .Where(t => t.IsEnabled && !string.IsNullOrWhiteSpace(t.Trigger))
            .OrderByDescending(t => t.Trigger.Length)
            .ToList();

    public string ActiveSummary
    {
        get
        {
            var k = Prefs.ActiveProvider;
            string model;
            if (k.UsesApiKeys())
            {
                model = EffectiveModels(k).FirstOrDefault() ?? "";
            }
            else
            {
                var m = Cli(k).Model.Trim();
                model = string.IsNullOrEmpty(m) ? "default model" : m;
            }
            return $"{k.DisplayName()} · {model}";
        }
    }
}
