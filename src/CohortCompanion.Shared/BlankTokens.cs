using System.Text.RegularExpressions;

namespace CohortCompanion.Shared;

/// <summary>
/// The blanks in a fill-in-the-blank prompt. A prompt is stored with each blank written as <c>{{1}}</c>,
/// <c>{{2}}</c> and so on. Instructors never see that form: the editor shows each one as a labelled chip.
/// </summary>
public static partial class BlankTokens
{
    [GeneratedRegex(@"\{\{\s*(\d{1,2})\s*\}\}")]
    private static partial Regex Token();

    /// <summary>How blank <paramref name="number"/> is written in a stored prompt.</summary>
    public static string Write(int number) => $"{{{{{number}}}}}";

    /// <summary>The distinct blank numbers in <paramref name="prompt"/>, in ascending order.</summary>
    public static IReadOnlyList<int> Find(string? prompt) =>
        string.IsNullOrEmpty(prompt)
            ? []
            : Token().Matches(prompt).Select(m => int.Parse(m.Groups[1].Value)).Distinct().Order().ToList();

    /// <summary>Replaces each blank with whatever <paramref name="replacement"/> returns for its number.</summary>
    public static string Replace(string? prompt, Func<int, string> replacement) =>
        string.IsNullOrEmpty(prompt) ? "" : Token().Replace(prompt, m => replacement(int.Parse(m.Groups[1].Value)));

    /// <summary>Replaces each blank with a visible placeholder. Safe on sanitized HTML: it adds only plain text.</summary>
    public static string ToDisplay(string prompt) => Replace(prompt, number => $"____({number})____");

    /// <summary>
    /// Numbers the blanks 1, 2, 3 in the order students read them, so the numbers never have gaps and never run
    /// backwards. A blank that appears twice keeps one number.
    /// </summary>
    /// <returns>
    /// The renumbered prompt, and the number each blank had before: <c>Was[0]</c> is the old number of the new
    /// blank 1. Use it to move each blank's accepted answers along with it.
    /// </returns>
    public static (string Prompt, List<int> Was) Renumber(string? prompt)
    {
        var was = new List<int>();
        var renumbered = Replace(prompt, old =>
        {
            var index = was.IndexOf(old);
            if (index < 0)
            {
                was.Add(old);
                index = was.Count - 1;
            }

            return Write(index + 1);
        });

        return (renumbered, was);
    }
}
