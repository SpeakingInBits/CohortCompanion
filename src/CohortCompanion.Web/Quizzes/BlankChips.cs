using System.Text.RegularExpressions;
using CohortCompanion.Shared;

namespace CohortCompanion.Web.Quizzes;

/// <summary>
/// Converts a fill-in-the-blank prompt between how it's stored and how the prompt editor shows it. Stored, a blank
/// is the text <c>{{1}}</c>. In the editor it's a chip: a small labelled box that is added with a button, can't be
/// typed into, and is deleted like a single character.
/// </summary>
/// <remarks>
/// A chip's number is only its identity while editing, so it stays the same when blanks are added or removed around
/// it. <see cref="BlankTokens.Renumber"/> turns those into the 1, 2, 3 that is stored and shown.
/// </remarks>
public static partial class BlankChips
{
    /// <summary>The most blanks one question's editor will add.</summary>
    public const int Max = 20;

    [GeneratedRegex(@"<span\b[^>]*\bdata-blank=""(\d{1,2})""[^>]*>.*?</span>", RegexOptions.Singleline)]
    private static partial Regex Chip();

    /// <summary>The chip for one blank. Its number is drawn by the page's styles, so the label can change without touching the text.</summary>
    public static string Html(int id) => $"<span class=\"blank-chip\" data-blank=\"{id}\" contenteditable=\"false\">Blank</span>";

    public static string ToEditor(string? prompt) => BlankTokens.Replace(prompt, Html);

    [GeneratedRegex(@"&nbsp;(?=\{\{\d)|(?<=\d\}\})&nbsp;")]
    private static partial Regex HardSpaceBesideBlank();

    /// <summary>
    /// The stored form of the editor's text. Browsers put a non-breaking space beside a chip, which would stop a
    /// line wrapping there on a phone, so those become ordinary spaces.
    /// </summary>
    public static string FromEditor(string? html)
    {
        if (string.IsNullOrEmpty(html))
        {
            return "";
        }

        var stored = Chip().Replace(html, m => BlankTokens.Write(int.Parse(m.Groups[1].Value)));
        return HardSpaceBesideBlank().Replace(stored, " ");
    }

    /// <summary>Takes one blank out of the editor's text, wherever it appears.</summary>
    public static string Remove(string? html, int id)
    {
        var withoutChips = string.IsNullOrEmpty(html) ? "" : Chip().Replace(html, m => int.Parse(m.Groups[1].Value) == id ? "" : m.Value);
        return BlankTokens.Replace(withoutChips, number => number == id ? "" : BlankTokens.Write(number));
    }
}
