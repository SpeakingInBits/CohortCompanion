using CohortCompanion.Shared;

namespace CohortCompanion.Web.Data;

public class Quiz
{
    public int Id { get; set; }

    public string OwnerId { get; set; } = "";

    public ApplicationUser? Owner { get; set; }

    public string Title { get; set; } = "";

    public QuizMode Mode { get; set; } = QuizMode.InstructorPaced;

    public bool ScoringEnabled { get; set; }

    public bool SpeedBonusEnabled { get; set; }

    public bool ShowLeaderboard { get; set; }

    /// <summary>Time limit for questions without their own. Required in timed mode, optional otherwise.</summary>
    public int? DefaultTimeLimitSeconds { get; set; }

    /// <summary>How long results and the answer stay up in timed mode before moving on.</summary>
    public int ResultsDisplaySeconds { get; set; } = 5;

    public DateTime CreatedAtUtc { get; set; }

    public DateTime UpdatedAtUtc { get; set; }

    public List<Question> Questions { get; set; } = [];
}

public class Question
{
    public const int DefaultPoints = 1000;

    public int Id { get; set; }

    public int QuizId { get; set; }

    public int Order { get; set; }

    public QuestionType Type { get; set; }

    /// <summary>Sanitized HTML.</summary>
    public string Prompt { get; set; } = "";

    public string? CodeSnippet { get; set; }

    public string? CodeLanguage { get; set; }

    public string? ImageUrl { get; set; }

    /// <summary>Overrides the quiz default when set.</summary>
    public int? TimeLimitSeconds { get; set; }

    public int Points { get; set; } = DefaultPoints;

    /// <summary>Multiple choice: students may select more than one option.</summary>
    public bool AllowMultipleSelect { get; set; }

    /// <summary>Multiple choice with multiple select: award part of the points for a partly correct answer.</summary>
    public bool PartialCredit { get; set; }

    /// <summary>Word cloud: how many entries each student may submit.</summary>
    public int MaxEntries { get; set; } = 1;

    public List<ChoiceOption> Options { get; set; } = [];

    public List<BlankAnswer> BlankAnswers { get; set; } = [];
}

public class ChoiceOption
{
    public int Id { get; set; }

    public int QuestionId { get; set; }

    public int Order { get; set; }

    public string Text { get; set; } = "";

    public bool IsCorrect { get; set; }

    public string? CodeSnippet { get; set; }
}

public class BlankAnswer
{
    public int Id { get; set; }

    public int QuestionId { get; set; }

    /// <summary>Matches the number in the prompt's <c>{{n}}</c> token.</summary>
    public int BlankIndex { get; set; }

    public string AcceptedText { get; set; } = "";

    public MatchMode MatchMode { get; set; } = MatchMode.CaseInsensitive;
}
