using CohortCompanion.Shared;
using CohortCompanion.Shared.Live;

namespace CohortCompanion.Web.Live;

/// <summary>Turns individual answers into totals that carry no nicknames.</summary>
public static class ResultsBuilder
{
    private const int MaxWords = 60;
    private const int MaxBlankAnswers = 8;

    /// <param name="question">The question being totalled.</param>
    /// <param name="answers">Answers from participants who are still in the session.</param>
    /// <param name="hiddenWords">Word cloud entries the instructor hid.</param>
    /// <param name="accepted">Accepted answers, including any the instructor added live.</param>
    /// <param name="reveal">Whether to mark which answers are correct.</param>
    public static QuestionResults Build(
        QuestionSnapshot question,
        IReadOnlyCollection<AnswerPayload> answers,
        IReadOnlySet<string> hiddenWords,
        IReadOnlyList<BlankAnswerSnapshot> accepted,
        bool reveal) => question.Type switch
        {
            QuestionType.MultipleChoice => new QuestionResults(
                question.Id,
                question.Type,
                answers.Count,
                Options: question.Options.Select((option, index) => new OptionResult(
                    index,
                    option.Text,
                    answers.Count(a => a.Options?.Contains(index) == true),
                    reveal ? option.IsCorrect : null)).ToList()),

            QuestionType.WordCloud => new QuestionResults(
                question.Id,
                question.Type,
                answers.Count,
                Words: answers
                    .SelectMany(a => a.Entries ?? [])
                    .Where(word => !hiddenWords.Contains(word) && !TextRules.IsProfane(word))
                    .GroupBy(word => word)
                    .Select(g => new WordCount(g.Key, g.Count()))
                    .OrderByDescending(w => w.Count)
                    .ThenBy(w => w.Word, StringComparer.Ordinal)
                    .Take(MaxWords)
                    .ToList()),

            _ => new QuestionResults(
                question.Id,
                question.Type,
                answers.Count,
                Blanks: Enumerable.Range(1, question.BlankCount)
                    .Select(blank => new BlankResult(blank, TopAnswers(answers, blank, accepted, hiddenWords, reveal)))
                    .ToList()),
        };

    private static List<BlankAnswerCount> TopAnswers(
        IReadOnlyCollection<AnswerPayload> answers,
        int blank,
        IReadOnlyList<BlankAnswerSnapshot> accepted,
        IReadOnlySet<string> hiddenWords,
        bool reveal) =>
        answers
            .Select(a => a.Blanks is { } blanks && blanks.Length >= blank ? blanks[blank - 1].Trim() : "")
            .Where(text => text.Length > 0 && !TextRules.IsProfane(text))
            .GroupBy(text => text, StringComparer.OrdinalIgnoreCase)
            .Where(g => !hiddenWords.Contains(g.Key.ToLowerInvariant()))
            .Select(g => new BlankAnswerCount(g.First(), g.Count(), reveal ? IsAccepted(g.First(), blank, accepted) : null))
            .OrderByDescending(a => a.Count)
            .ThenBy(a => a.Text, StringComparer.OrdinalIgnoreCase)
            .Take(MaxBlankAnswers)
            .ToList();

    public static bool IsAccepted(string text, int blank, IReadOnlyList<BlankAnswerSnapshot> accepted) =>
        accepted.Any(a => a.BlankIndex == blank && TextRules.Matches(text, a.AcceptedText, a.MatchMode));
}
