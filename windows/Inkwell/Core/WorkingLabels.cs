using System;
using System.Collections.Generic;
using System.Linq;

namespace Inkwell.Core;

/// <summary>
/// Turns a template name into what's happening right now: "Fix grammar" → "Fixing grammar",
/// "Make formal" → "Making it formal", "To English" → "Translating to English", "Summarize" → "Summarizing".
/// </summary>
public static class WorkingLabels
{
    private static readonly Dictionary<string, string> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Fix grammar"] = "Fixing grammar",
        ["Rewrite"] = "Rewriting",
        ["Make formal"] = "Making it formal",
        ["Make shorter"] = "Making it shorter",
        ["Ask"] = "Thinking",
        ["Reply"] = "Writing a reply",
        ["To English"] = "Translating to English",
    };

    // Irregular or doubled-consonant verbs; everything else follows the usual spelling rules.
    private static readonly Dictionary<string, string> Irregular = new(StringComparer.OrdinalIgnoreCase)
    {
        ["be"] = "being", ["see"] = "seeing", ["run"] = "running", ["get"] = "getting", ["put"] = "putting",
        ["set"] = "setting", ["cut"] = "cutting", ["plan"] = "planning", ["stop"] = "stopping", ["trim"] = "trimming",
        ["swap"] = "swapping", ["map"] = "mapping", ["tag"] = "tagging", ["chat"] = "chatting", ["jot"] = "jotting",
    };

    private static readonly HashSet<string> NotVerbs = new(StringComparer.OrdinalIgnoreCase)
    {
        "to", "in", "into", "as", "more", "less", "the", "a", "an", "my", "new", "untitled",
    };

    public static string For(string name)
    {
        var n = (name ?? "").Trim();
        if (n.Length == 0) return "Working";
        if (Known.TryGetValue(n, out var known)) return known;

        var words = n.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        var first = words[0];

        if (first.Equals("to", StringComparison.OrdinalIgnoreCase) && words.Count > 1)
            return "Translating " + n[0..1].ToLowerInvariant() + n[1..]; // "To Spanish" → "Translating to Spanish"
        if (first.Equals("make", StringComparison.OrdinalIgnoreCase) && words.Count > 1)
            return "Making it " + string.Join(' ', words.Skip(1)).ToLowerInvariant();
        if (NotVerbs.Contains(first) || first.EndsWith("ing", StringComparison.OrdinalIgnoreCase) || !first.All(char.IsLetter))
            return n;

        var rest = words.Count > 1 ? " " + string.Join(' ', words.Skip(1)) : "";
        return Capitalize(Gerund(first.ToLowerInvariant())) + rest;
    }

    private static string Gerund(string verb)
    {
        if (Irregular.TryGetValue(verb, out var g)) return g;
        if (verb.EndsWith("ie")) return verb[..^2] + "ying";
        if (verb.EndsWith("ee") || verb.EndsWith("ye") || verb.EndsWith("oe")) return verb + "ing";
        if (verb.EndsWith('e') && verb.Length > 2) return verb[..^1] + "ing";
        return verb + "ing";
    }

    private static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
}
