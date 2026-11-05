using System.Text.RegularExpressions;

namespace CohortCompanion.Shared;

/// <summary>Finds the <c>{{1}}</c> style blanks in a fill-in-the-blank prompt.</summary>
public static partial class BlankTokens
{
    [GeneratedRegex(@"\{\{\s*(\d{1,2})\s*\}\}")]
    private static partial Regex Token();

    /// <summary>The distinct blank numbers in <paramref name="prompt"/>, in ascending order.</summary>
    public static IReadOnlyList<int> Find(string? prompt) =>
        string.IsNullOrEmpty(prompt)
            ? []
            : Token().Matches(prompt).Select(m => int.Parse(m.Groups[1].Value)).Distinct().Order().ToList();

    /// <summary>Replaces each blank with a visible placeholder. Safe on sanitized HTML: it adds only plain text.</summary>
    public static string ToDisplay(string prompt) => Token().Replace(prompt, m => $"____({m.Groups[1].Value})____");
}
