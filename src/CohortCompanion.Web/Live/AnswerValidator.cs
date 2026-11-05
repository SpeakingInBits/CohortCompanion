using CohortCompanion.Shared;
using CohortCompanion.Shared.Live;

namespace CohortCompanion.Web.Live;

/// <summary>Checks what a student sent. Anything the client claims is treated as untrusted.</summary>
public static class AnswerValidator
{
    /// <summary>Returns a cleaned copy of the answer, or null when it isn't valid for this question.</summary>
    public static AnswerPayload? Clean(QuestionSnapshot question, AnswerPayload? answer)
    {
        if (answer is null)
        {
            return null;
        }

        return question.Type switch
        {
            QuestionType.MultipleChoice => CleanChoice(question, answer),
            QuestionType.WordCloud => CleanEntries(question, answer),
            _ => CleanBlanks(question, answer),
        };
    }

    private static AnswerPayload? CleanChoice(QuestionSnapshot question, AnswerPayload answer)
    {
        var options = answer.Options?.Distinct().Order().ToArray() ?? [];
        var isValid = options.Length > 0
            && options.All(o => o >= 0 && o < question.Options.Count)
            && (question.AllowMultipleSelect || options.Length == 1);
        return isValid ? new AnswerPayload(Options: options) : null;
    }

    private static AnswerPayload? CleanEntries(QuestionSnapshot question, AnswerPayload answer)
    {
        if (answer.Entries is not { Length: > 0 } raw || raw.Length > question.MaxEntries)
        {
            return null;
        }

        var entries = raw
            .Select(TextRules.NormalizeEntry)
            .Where(e => e.Length is > 0 and <= AnswerLimits.MaxEntryLength)
            .Distinct()
            .ToArray();
        return entries.Length > 0 ? new AnswerPayload(Entries: entries) : null;
    }

    private static AnswerPayload? CleanBlanks(QuestionSnapshot question, AnswerPayload answer)
    {
        if (answer.Blanks is not { } raw || raw.Length != question.BlankCount)
        {
            return null;
        }

        var blanks = raw.Select(b => (b ?? "").Trim()).ToArray();
        return blanks.All(b => b.Length <= AnswerLimits.MaxBlankLength) && blanks.Any(b => b.Length > 0)
            ? new AnswerPayload(Blanks: blanks)
            : null;
    }
}
