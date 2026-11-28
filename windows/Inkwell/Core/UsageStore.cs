using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Inkwell.Core;

public sealed class UsageStore : ObservableObject
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        PropertyNameCaseInsensitive = true
    };

    private readonly List<UsageEvent> _events = new();
    private readonly string _filePath;
    private readonly bool _ephemeral;

    public string Folder { get; }
    public IReadOnlyList<UsageEvent> Events => _events;
    public event Action? Changed;

    public UsageStore(bool ephemeral = false, IEnumerable<UsageEvent>? seed = null)
    {
        _ephemeral = ephemeral;
        Folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Inkwell");
        Directory.CreateDirectory(Folder);
        _filePath = Path.Combine(Folder, "usage.json");

        if (ephemeral)
        {
            if (seed is not null) _events.AddRange(seed.OrderBy(e => e.Date));
        }
        else
        {
            Load();
        }
    }

    public void Record(UsageEvent ev)
    {
        _events.Add(ev);
        Save();
        OnPropertyChanged(nameof(Events));
        Changed?.Invoke();
    }

    public void Reset()
    {
        _events.Clear();
        Save();
        OnPropertyChanged(nameof(Events));
        Changed?.Invoke();
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_filePath)) return;
            var json = File.ReadAllText(_filePath);
            var list = JsonSerializer.Deserialize<List<UsageEvent>>(json, JsonOptions);
            if (list is not null)
            {
                _events.AddRange(list.OrderBy(e => e.Date));
            }
        }
        catch
        {
            // Ignore malformed history
        }
    }

    private void Save()
    {
        if (_ephemeral) return;
        try
        {
            Directory.CreateDirectory(Folder);
            var json = JsonSerializer.Serialize(_events, JsonOptions);
            var tmp = _filePath + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, _filePath, overwrite: true);
        }
        catch
        {
            // Ignore transient disk write failures
        }
    }

    public UsageSummary Summary(StatsRange range, DateTimeOffset? now = null)
    {
        var current = (now ?? DateTimeOffset.Now).LocalDateTime;
        var today = current.Date;
        DateTime start;
        var days = range.Days();
        if (days.HasValue)
        {
            start = today.AddDays(-(days.Value - 1));
        }
        else
        {
            start = _events.Count > 0 ? _events[0].Date.LocalDateTime.Date : today;
        }

        var inRange = _events.Where(e => e.Date.LocalDateTime >= start).ToList();
        var ok = inRange.Where(e => e.Success).ToList();

        var summary = new UsageSummary
        {
            Runs = ok.Count,
            Failures = inRange.Count - ok.Count,
            InputTokens = ok.Sum(e => e.InputTokens),
            OutputTokens = ok.Sum(e => e.OutputTokens),
            Words = ok.Sum(e => e.InputWords),
            AiTime = ok.Sum(e => e.Latency)
        };

        var runsPerDay = new Dictionary<DateTime, int>();
        var tokensPerDay = new Dictionary<DateTime, int>();
        foreach (var e in ok)
        {
            var d = e.Date.LocalDateTime.Date;
            runsPerDay[d] = runsPerDay.GetValueOrDefault(d) + 1;
            tokensPerDay[d] = tokensPerDay.GetValueOrDefault(d) + e.InputTokens + e.OutputTokens;
        }

        var day = start;
        while (day <= today)
        {
            summary.Daily.Add(new DayBucket(
                day,
                runsPerDay.GetValueOrDefault(day),
                tokensPerDay.GetValueOrDefault(day)));
            day = day.AddDays(1);
        }

        summary.TopTemplates = ok
            .GroupBy(e => e.TemplateName)
            .Select(g => new TemplateCount(g.Key, g.Count()))
            .OrderByDescending(t => t.Count)
            .Take(4)
            .ToList();

        return summary;
    }
}
