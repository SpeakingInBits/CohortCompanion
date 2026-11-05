using CohortCompanion.Shared;
using CohortCompanion.Shared.Live;

namespace CohortCompanion.Web.Live;

public readonly record struct ScoredAnswer(bool? IsCorrect, int Score);

/// <summary>Marks an answer. Word clouds are polls, so they have no right answer and score nothing.</summary>
public static class AnswerScorer
{
    /// <param name="question">The question being answered.</param>
    /// <param name="answer">The student's cleaned answer.</param>
    /// <param name="accepted">Accepted blank answers, including any the instructor added live.</param>
    /// <param name="scoringEnabled">Whether this quiz keeps score. Answers are still marked when it doesn't.</param>
    /// <param name="speedBonusEnabled">Whether faster correct answers earn more.</param>
    /// <param name="elapsedMs">How long the student took.</param>
    public static ScoredAnswer Score(
        QuestionSnapshot question,
        AnswerPayload answer,
        IReadOnlyList<BlankAnswerSnapshot> accepted,
        bool scoringEnabled,
        bool speedBonusEnabled,
        int elapsedMs)
    {
        var fraction = question.Type switch
        {
            QuestionType.MultipleChoice => ChoiceFraction(question, answer),
            QuestionType.FillInBlank => BlankFraction(question, answer, accepted),
            _ => (double?)null,
        };

        if (fraction is not { } earned)
        {
            return new ScoredAnswer(null, 0);
        }

        var isCorrect = earned >= 1;
        if (!scoringEnabled || earned <= 0)
        {
            return new ScoredAnswer(isCorrect, 0);
        }

        var points = question.Points * earned;
        if (speedBonusEnabled && question.TimeLimitSeconds is { } limit and > 0)
        {
            var share = Math.Clamp(elapsedMs / (limit * 1000.0), 0, 1);
            points *= 1 - (0.5 * share);
        }

        return new ScoredAnswer(isCorrect, (int)Math.Round(points));
    }

    private static double ChoiceFraction(QuestionSnapshot question, AnswerPayload answer)
    {
        var chosen = answer.Options ?? [];
        var correct = Enumerable.Range(0, question.Options.Count).Where(i => question.Options[i].IsCorrect).ToHashSet();
        if (correct.SetEquals(chosen))
        {
            return 1;
        }

        if (!question.PartialCredit || correct.Count == 0)
        {
            return 0;
        }

        // A wrong pick cancels a right one, so ticking every box doesn't earn credit.
        var right = chosen.Count(correct.Contains);
        var wrong = chosen.Length - right;
        return Math.Max(0, (right - wrong) / (double)correct.Count);
    }

    private static double BlankFraction(QuestionSnapshot question, AnswerPayload answer, IReadOnlyList<BlankAnswerSnapshot> accepted)
    {
        if (question.BlankCount == 0)
        {
            return 0;
        }

        var blanks = answer.Blanks ?? [];
        var right = Enumerable.Range(1, question.BlankCount)
            .Count(blank => blank <= blanks.Length && ResultsBuilder.IsAccepted(blanks[blank - 1], blank, accepted));
        return right / (double)question.BlankCount;
    }
}
