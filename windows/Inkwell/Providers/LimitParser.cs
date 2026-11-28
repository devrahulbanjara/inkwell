using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Inkwell.Providers;

/// <summary>Latest plan-usage reading reported by a CLI (Claude Code's rate_limit_event).</summary>
public sealed record CliLimitInfo(string Window, double? Utilization, DateTime? ResetsAt, bool Rejected);

/// <summary>
/// Reads "which limit, and when does it reset" out of the text Claude Code and Codex print when a
/// plan limit (5-hour session, weekly, Opus weekly, extra usage…) is reached.
/// </summary>
public static class LimitParser
{
    private const RegexOptions Opt = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    /// <summary>Is this text a usage/rate-limit message?</summary>
    public static bool LooksLikeLimit(string text)
    {
        var l = Normalize(text).ToLowerInvariant();
        return l.Contains("usage limit") || l.Contains("rate limit") || l.Contains("limit reached")
            || l.Contains("hit your limit") || l.Contains("hit your weekly") || l.Contains("hit your session")
            || l.Contains("too many requests") || l.Contains("quota") || l.Contains("429")
            || l.Contains("usage_limit_reached") || Regex.IsMatch(l, @"\b(5-hour|weekly|session|opus|sonnet) limit\b");
    }

    /// <summary>Claude Code's names for its windows → words people use.</summary>
    public static string ClaudeWindow(string? rateLimitType) => rateLimitType switch
    {
        "five_hour" => "5-hour",
        "seven_day" => "weekly",
        "seven_day_opus" => "weekly Opus",
        "seven_day_sonnet" => "weekly Sonnet",
        "overage" => "extra usage",
        null or "" => "usage",
        _ when rateLimitType.StartsWith("seven_day", StringComparison.Ordinal) => "weekly",
        _ => rateLimitType.Replace('_', ' '),
    };

    public static DateTime? FromEpoch(double value)
    {
        if (value <= 0) return null;
        try
        {
            // Seconds (current CLI) or milliseconds (some wrappers).
            var ms = value > 1e12 ? value : value * 1000;
            return DateTimeOffset.FromUnixTimeMilliseconds((long)ms).LocalDateTime;
        }
        catch { return null; }
    }

    // MARK: Claude Code text: "Claude AI usage limit reached|1755615600", "You've hit your weekly limit · resets 11am (UTC)"

    public static (string? Window, DateTime? ResetsAt) ParseClaude(string text)
    {
        var t = Normalize(text);
        string? window = WindowFromText(t);
        DateTime? reset = null;

        var epoch = Regex.Match(t, @"\|(\d{10,13})\s*$", Opt | RegexOptions.Multiline);
        if (epoch.Success && double.TryParse(epoch.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var e))
            reset = FromEpoch(e);

        if (reset is null)
        {
            var m = Regex.Match(t, @"resets?\s+(?:at\s+|on\s+)?(?<when>[^()\n·∙.]+?)\s*(?:\((?<tz>[^)]+)\))?\s*(?:[.·∙\n]|$)", Opt);
            if (m.Success) reset = ParseWhen(m.Groups["when"].Value, m.Groups["tz"].Success ? m.Groups["tz"].Value : null);
        }
        return (window, reset);
    }

    // MARK: Codex text: "You've hit your usage limit. … or try again at 3:15 PM." / "… try again at Mar 20th, 2027 3:36 PM."

    public static (string? Window, DateTime? ResetsAt) ParseCodex(string text)
    {
        var t = Normalize(text);
        DateTime? reset = null;
        var m = Regex.Match(t, @"try again (?:at|on)\s+(?<when>.+?)\s*(?:\.\s*$|\.\s|$)", Opt | RegexOptions.Multiline);
        if (m.Success) reset = ParseWhen(m.Groups["when"].Value, null);
        else
        {
            // Older wording: "try again in 2 days 3 hours 4 minutes"
            var rel = Regex.Match(t, @"try again in\s+(?<rel>[0-9a-z ,]+)", Opt);
            if (rel.Success && ParseRelative(rel.Groups["rel"].Value) is { } span) reset = DateTime.Now.Add(span);
        }
        var window = WindowFromText(t);
        if (window is null && reset is { } r)
            window = r - DateTime.Now <= TimeSpan.FromHours(5.2) ? "5-hour" : "weekly";
        return (window, reset);
    }

    private static string? WindowFromText(string t)
    {
        var l = t.ToLowerInvariant();
        if (l.Contains("weekly") || l.Contains("week")) return l.Contains("opus") ? "weekly Opus" : l.Contains("sonnet") ? "weekly Sonnet" : "weekly";
        if (l.Contains("5-hour") || l.Contains("5 hour") || l.Contains("five_hour") || l.Contains("session")) return "5-hour";
        if (l.Contains("monthly") || l.Contains("month")) return "monthly";
        if (l.Contains("daily") || l.Contains("per day") || l.Contains("(rpd)") || l.Contains("(tpd)")) return "daily";
        return null;
    }

    private static string Normalize(string s) => s.Replace('’', '\'').Replace('‘', '\'');

    private static readonly Regex WhenRx = new(
        @"^(?:(?<dow>mon|tue|wed|thu|fri|sat|sun)[a-z]*,?\s+)?" +
        @"(?:(?<mon>jan|feb|mar|apr|may|jun|jul|aug|sep|oct|nov|dec)[a-z]*\.?\s+(?<day>\d{1,2})(?:st|nd|rd|th)?,?\s+(?:(?<year>\d{4}),?\s+)?)?" +
        @"(?:at\s+)?(?<h>\d{1,2})(?::(?<m>\d{2}))?\s*(?<ap>am|pm|a\.m\.|p\.m\.)?$", Opt);

    /// <summary>Parses "3:15 PM", "11am", "Mon 9am", "Mar 20th, 2027 3:36 PM" in an optional IANA/Windows time zone.</summary>
    public static DateTime? ParseWhen(string raw, string? tzName)
    {
        var s = raw.Trim().TrimEnd('.', ',').Trim();
        var m = WhenRx.Match(s);
        if (!m.Success) return null;

        var tz = ResolveZone(tzName);
        var nowInZone = TimeZoneInfo.ConvertTime(DateTime.Now, tz);
        int hour = int.Parse(m.Groups["h"].Value, CultureInfo.InvariantCulture);
        int minute = m.Groups["m"].Success ? int.Parse(m.Groups["m"].Value, CultureInfo.InvariantCulture) : 0;
        if (m.Groups["ap"].Success)
        {
            var pm = m.Groups["ap"].Value.StartsWith("p", StringComparison.OrdinalIgnoreCase);
            if (hour == 12) hour = pm ? 12 : 0;
            else if (pm) hour += 12;
        }
        if (hour > 23 || minute > 59) return null;

        DateTime target;
        if (m.Groups["mon"].Success)
        {
            int month = Array.FindIndex(new[] { "jan", "feb", "mar", "apr", "may", "jun", "jul", "aug", "sep", "oct", "nov", "dec" },
                x => m.Groups["mon"].Value.StartsWith(x, StringComparison.OrdinalIgnoreCase)) + 1;
            int day = int.Parse(m.Groups["day"].Value, CultureInfo.InvariantCulture);
            int year = m.Groups["year"].Success ? int.Parse(m.Groups["year"].Value, CultureInfo.InvariantCulture) : nowInZone.Year;
            try { target = new DateTime(year, month, day, hour, minute, 0); } catch { return null; }
            if (!m.Groups["year"].Success && target < nowInZone.AddDays(-1)) target = target.AddYears(1);
        }
        else
        {
            target = nowInZone.Date.AddHours(hour).AddMinutes(minute);
            if (m.Groups["dow"].Success)
            {
                var want = Array.FindIndex(new[] { "sun", "mon", "tue", "wed", "thu", "fri", "sat" },
                    x => m.Groups["dow"].Value.StartsWith(x, StringComparison.OrdinalIgnoreCase));
                int add = ((want - (int)nowInZone.DayOfWeek) + 7) % 7;
                target = target.AddDays(add);
                if (target <= nowInZone) target = target.AddDays(7);
            }
            else if (target <= nowInZone)
            {
                target = target.AddDays(1);
            }
        }

        try
        {
            return TimeZoneInfo.ConvertTime(DateTime.SpecifyKind(target, DateTimeKind.Unspecified), tz, TimeZoneInfo.Local);
        }
        catch { return null; }
    }

    private static TimeZoneInfo ResolveZone(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return TimeZoneInfo.Local;
        var n = name.Trim();
        if (n.Equals("UTC", StringComparison.OrdinalIgnoreCase) || n.Equals("GMT", StringComparison.OrdinalIgnoreCase)) return TimeZoneInfo.Utc;
        try { return TimeZoneInfo.FindSystemTimeZoneById(n); } // IANA ids work on Windows via ICU
        catch { return TimeZoneInfo.Local; }
    }

    /// <summary>"2 days 3 hours 4 minutes" → TimeSpan.</summary>
    private static TimeSpan? ParseRelative(string s)
    {
        double total = 0;
        bool any = false;
        foreach (Match m in Regex.Matches(s, @"(\d+)\s*(day|hour|hr|minute|min|second|sec)s?", Opt))
        {
            var v = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            var u = m.Groups[2].Value.ToLowerInvariant();
            total += u.StartsWith("day") ? v * 86400 : u.StartsWith("h") ? v * 3600 : u.StartsWith("min") ? v * 60 : v;
            any = true;
        }
        return any ? TimeSpan.FromSeconds(total) : null;
    }
}
