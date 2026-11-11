using CohortCompanion.Shared;
using CohortCompanion.Web.Content;
using CohortCompanion.Web.Data;

namespace CohortCompanion.Web.Quizzes;

/// <summary>
/// What a quiz needs before it can run live. Quizzes can be saved as unfinished drafts, so these rules
/// are shown in the editor and only block starting a session.
/// </summary>
public static class QuizRules
{
    public const int MinOptions = 2;
    public const int MaxOptions = 6;
    public const int MaxWordCloudEntries = 10;
    public const int MinTimeLimitSeconds = 5;
    public const int MaxTimeLimitSeconds = 3600;

    public static IReadOnlyList<string> Validate(Quiz quiz)
    {
        var problems = new List<string>();

        if (string.IsNullOrWhiteSpace(quiz.Title))
        {
            problems.Add("The quiz needs a title.");
        }

        if (quiz.Questions.Count == 0)
        {
            problems.Add("Add at least one question.");
        }

        if (quiz.DefaultTimeLimitSeconds is { } limit && !IsValidTimeLimit(limit))
        {
            problems.Add($"The default time limit must be between {MinTimeLimitSeconds} and {MaxTimeLimitSeconds} seconds.");
        }

        if (quiz.ResultsDisplaySeconds is < 1 or > 60)
        {
            problems.Add("Results must stay up for between 1 and 60 seconds.");
        }

        var number = 0;
        foreach (var question in quiz.Questions.OrderBy(q => q.Order))
        {
            number++;
            ValidateQuestion(quiz, question, $"Question {number}", problems);
        }

        return problems;
    }

    private static void ValidateQuestion(Quiz quiz, Question question, string label, List<string> problems)
    {
        if (ContentSanitizer.ToPlainText(question.Prompt).Length == 0)
        {
            problems.Add($"{label} needs a prompt.");
        }

        if (question.Points < 0)
        {
            problems.Add($"{label}: points can't be negative.");
        }

        if (question.TimeLimitSeconds is { } own && !IsValidTimeLimit(own))
        {
            problems.Add($"{label}: the time limit must be between {MinTimeLimitSeconds} and {MaxTimeLimitSeconds} seconds.");
        }

        if (quiz.Mode == QuizMode.Timed && (question.TimeLimitSeconds ?? quiz.DefaultTimeLimitSeconds) is null)
        {
            problems.Add($"{label} needs a time limit, because this quiz is timed.");
        }

        switch (question.Type)
        {
            case QuestionType.MultipleChoice:
                ValidateMultipleChoice(question, label, problems);
                break;
            case QuestionType.WordCloud:
                if (question.MaxEntries is < 1 or > MaxWordCloudEntries)
                {
                    problems.Add($"{label}: entries per student must be between 1 and {MaxWordCloudEntries}.");
                }

                break;
            case QuestionType.FillInBlank:
                ValidateFillInBlank(question, label, problems);
                break;
        }
    }

    private static void ValidateMultipleChoice(Question question, string label, List<string> problems)
    {
        if (question.Options.Count is < MinOptions or > MaxOptions)
        {
            problems.Add($"{label} needs between {MinOptions} and {MaxOptions} options.");
        }

        if (question.Options.Any(o => string.IsNullOrWhiteSpace(o.Text)))
        {
            problems.Add($"{label} has an empty option.");
        }

        var correct = question.Options.Count(o => o.IsCorrect);
        if (correct == 0)
        {
            problems.Add($"{label} needs a correct option.");
        }
        else if (correct > 1 && !question.AllowMultipleSelect)
        {
            problems.Add($"{label} has several correct options, so it must allow multiple select.");
        }
    }

    private static void ValidateFillInBlank(Question question, string label, List<string> problems)
    {
        var blanks = BlankTokens.Find(question.Prompt);
        if (blanks.Count == 0)
        {
            problems.Add($"{label} needs at least one blank. Use Add blank in its prompt.");
            return;
        }

        if (!blanks.SequenceEqual(Enumerable.Range(1, blanks.Count)))
        {
            problems.Add($"{label}: its blanks aren't numbered in order. Save the quiz to renumber them.");
        }

        foreach (var blank in blanks)
        {
            if (!question.BlankAnswers.Any(a => a.BlankIndex == blank && !string.IsNullOrWhiteSpace(a.AcceptedText)))
            {
                problems.Add($"{label}: blank {blank} needs an accepted answer.");
            }
        }

        if (question.BlankAnswers.Any(a => !blanks.Contains(a.BlankIndex)))
        {
            problems.Add($"{label} has an accepted answer for a blank that isn't in the prompt.");
        }
    }

    private static bool IsValidTimeLimit(int seconds) => seconds is >= MinTimeLimitSeconds and <= MaxTimeLimitSeconds;
}
