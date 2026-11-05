using System.Text;
using CohortCompanion.Shared;

namespace CohortCompanion.Web.Live;

/// <summary>Cleans and compares the short pieces of text students type.</summary>
public static class TextRules
{
    // A deliberately short list of unambiguous words. It catches the obvious cases on a projector;
    // the instructor can still hide words and rename participants live.
    private static readonly HashSet<string> BlockedWords = new(StringComparer.Ordinal)
    {
        "ass", "asshole", "bastard", "bitch", "bollocks", "bullshit", "cock", "cunt", "damn", "dick",
        "dickhead", "douche", "fag", "faggot", "fuck", "fucker", "fucking", "motherfucker", "nazi",
        "nigger", "nigga", "penis", "piss", "prick", "pussy", "retard", "shit", "shitty", "slut",
        "tits", "twat", "vagina", "wanker", "whore",
    };

    private static readonly string[] BlockedInsideWords =
        ["fuck", "shit", "nigger", "nigga", "faggot", "bitch"];

    /// <summary>Trims, lowercases, removes punctuation, and collapses spaces. Keeps # and + for C# and C++.</summary>
    public static string NormalizeEntry(string? text)
    {
        var builder = new StringBuilder();
        var pendingSpace = false;
        foreach (var c in (text ?? "").Trim().ToLowerInvariant())
        {
            if (char.IsWhiteSpace(c))
            {
                pendingSpace = builder.Length > 0;
            }
            else if (char.IsLetterOrDigit(c) || c is '#' or '+')
            {
                if (pendingSpace)
                {
                    builder.Append(' ');
                    pendingSpace = false;
                }

                builder.Append(c);
            }
        }

        return builder.ToString();
    }

    public static bool IsProfane(string? text)
    {
        var normalized = NormalizeEntry(text);
        if (normalized.Length == 0)
        {
            return false;
        }

        // Checked word by word, so that innocent phrases like "push it" don't match across the space.
        return normalized.Split(' ').Any(word => BlockedWords.Contains(word) || BlockedInsideWords.Any(word.Contains));
    }

    /// <summary>Whether <paramref name="answer"/> matches an accepted answer under its match mode.</summary>
    public static bool Matches(string? answer, string accepted, MatchMode mode)
    {
        var typed = (answer ?? "").Trim();
        var expected = accepted.Trim();
        if (typed.Length == 0)
        {
            return false;
        }

        return mode switch
        {
            MatchMode.Exact => string.Equals(typed, expected, StringComparison.Ordinal),
            MatchMode.CaseInsensitive => string.Equals(typed, expected, StringComparison.OrdinalIgnoreCase),
            _ => string.Equals(typed, expected, StringComparison.OrdinalIgnoreCase)
                || (expected.Length >= 5 && IsWithinOneEdit(typed.ToLowerInvariant(), expected.ToLowerInvariant())),
        };
    }

    /// <summary>Levenshtein distance of at most 1, without building the full table.</summary>
    public static bool IsWithinOneEdit(string a, string b)
    {
        if (Math.Abs(a.Length - b.Length) > 1)
        {
            return false;
        }

        var (shorter, longer) = a.Length <= b.Length ? (a, b) : (b, a);
        var i = 0;
        while (i < shorter.Length && shorter[i] == longer[i])
        {
            i++;
        }

        // After the first difference, the rest must match, either skipping one character or replacing one.
        return shorter.Length == longer.Length
            ? shorter.AsSpan(Math.Min(i + 1, shorter.Length)).SequenceEqual(longer.AsSpan(Math.Min(i + 1, longer.Length)))
            : shorter.AsSpan(i).SequenceEqual(longer.AsSpan(i + 1));
    }
}
