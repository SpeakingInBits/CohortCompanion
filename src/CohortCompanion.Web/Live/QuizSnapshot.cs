using CohortCompanion.Shared;
using CohortCompanion.Shared.Live;
using CohortCompanion.Web.Data;

namespace CohortCompanion.Web.Live;

/// <summary>
/// A frozen copy of a quiz, stored with its session. It includes the correct answers, so it stays on
/// the server: students only ever receive <see cref="StudentQuestion"/>.
/// </summary>
public sealed record QuizSnapshot(
    string Title,
    QuizMode Mode,
    bool ScoringEnabled,
    bool SpeedBonusEnabled,
    bool ShowLeaderboard,
    int ResultsDisplaySeconds,
    IReadOnlyList<QuestionSnapshot> Questions)
{
    public static QuizSnapshot From(Quiz quiz) => new(
        quiz.Title,
        quiz.Mode,
        quiz.ScoringEnabled,
        quiz.SpeedBonusEnabled,
        quiz.ShowLeaderboard,
        quiz.ResultsDisplaySeconds,
        quiz.Questions.OrderBy(q => q.Order).Select((q, index) => new QuestionSnapshot(
            index + 1,
            q.Type,
            q.Prompt,
            q.CodeSnippet,
            q.CodeLanguage,
            q.ImageUrl,
            q.TimeLimitSeconds ?? quiz.DefaultTimeLimitSeconds,
            q.Points,
            q.AllowMultipleSelect,
            q.PartialCredit,
            q.MaxEntries,
            BlankTokens.Find(q.Prompt).Count,
            q.Options.OrderBy(o => o.Order).Select(o => new OptionSnapshot(o.Text, o.CodeSnippet, o.IsCorrect)).ToList(),
            q.BlankAnswers.Select(a => new BlankAnswerSnapshot(a.BlankIndex, a.AcceptedText, a.MatchMode)).ToList())).ToList());
}

public sealed record QuestionSnapshot(
    int Id,
    QuestionType Type,
    string Prompt,
    string? CodeSnippet,
    string? CodeLanguage,
    string? ImageUrl,
    int? TimeLimitSeconds,
    int Points,
    bool AllowMultipleSelect,
    bool PartialCredit,
    int MaxEntries,
    int BlankCount,
    IReadOnlyList<OptionSnapshot> Options,
    IReadOnlyList<BlankAnswerSnapshot> BlankAnswers)
{
    public StudentQuestion ToStudent() => new(
        Id,
        Type,
        Type == QuestionType.FillInBlank ? BlankTokens.ToDisplay(Prompt) : Prompt,
        CodeSnippet,
        CodeLanguage,
        ImageUrl,
        AllowMultipleSelect,
        MaxEntries,
        BlankCount,
        Options.Select((o, i) => new StudentOption(i, o.Text, o.CodeSnippet)).ToList());
}

public sealed record OptionSnapshot(string Text, string? CodeSnippet, bool IsCorrect);

public sealed record BlankAnswerSnapshot(int BlankIndex, string AcceptedText, MatchMode MatchMode);
