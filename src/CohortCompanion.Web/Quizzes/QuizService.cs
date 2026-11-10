using CohortCompanion.Shared;
using CohortCompanion.Web.Content;
using CohortCompanion.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace CohortCompanion.Web.Quizzes;

/// <summary>A row in a quiz list. <c>IsReady</c> says whether it can run live, and is only worked out for the caller's own list.</summary>
public sealed record QuizListItem(int Id, string Title, QuizMode Mode, int QuestionCount, DateTime UpdatedAtUtc, string OwnerName, bool? IsReady = null);

/// <summary>Quiz storage. Every read and write is limited to the owner, except the explicit admin paths.</summary>
public sealed class QuizService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    ContentSanitizer sanitizer,
    TimeProvider timeProvider)
{
    private DateTime UtcNow => timeProvider.GetUtcNow().UtcDateTime;

    /// <summary>
    /// Lists the caller's own quizzes. An admin can ask for everyone's instead; from anyone else that request is
    /// ignored, so the rule holds wherever this is called from.
    /// </summary>
    public async Task<List<QuizListItem>> ListAsync(string userId, bool isAdmin = false, bool everyone = false, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        if (everyone && isAdmin)
        {
            var all = await db.Quizzes
                .AsNoTracking()
                .Select(q => new QuizListItem(q.Id, q.Title, q.Mode, q.Questions.Count, q.UpdatedAtUtc, q.Owner!.DisplayName, null))
                .ToListAsync(cancellationToken);
            return all.OrderByDescending(q => q.UpdatedAtUtc).ToList();
        }

        // One instructor's quizzes are few enough to load whole, so the list can say which ones need fixing
        // before they'll run, rather than finding out from the Run live button.
        var quizzes = await db.Quizzes
            .AsNoTracking()
            .AsSplitQuery()
            .Include(q => q.Owner)
            .Include(q => q.Questions).ThenInclude(q => q.Options)
            .Include(q => q.Questions).ThenInclude(q => q.BlankAnswers)
            .Where(q => q.OwnerId == userId)
            .ToListAsync(cancellationToken);
        return quizzes
            .OrderByDescending(q => q.UpdatedAtUtc)
            .Select(q => new QuizListItem(q.Id, q.Title, q.Mode, q.Questions.Count, q.UpdatedAtUtc, q.Owner!.DisplayName, QuizRules.Validate(q).Count == 0))
            .ToList();
    }

    public async Task<Quiz?> GetAsync(int id, string ownerId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var quiz = await db.Quizzes
            .AsNoTracking()
            .AsSplitQuery()
            .Include(q => q.Questions).ThenInclude(q => q.Options)
            .Include(q => q.Questions).ThenInclude(q => q.BlankAnswers)
            .SingleOrDefaultAsync(q => q.Id == id && q.OwnerId == ownerId, cancellationToken);

        if (quiz is not null)
        {
            quiz.Questions = quiz.Questions.OrderBy(q => q.Order).ToList();
            foreach (var question in quiz.Questions)
            {
                question.Options = question.Options.OrderBy(o => o.Order).ToList();
                question.BlankAnswers = question.BlankAnswers.OrderBy(a => a.BlankIndex).ThenBy(a => a.Id).ToList();
            }
        }

        return quiz;
    }

    public async Task<Quiz> CreateAsync(string ownerId, string title, CancellationToken cancellationToken = default)
    {
        var now = UtcNow;
        var quiz = new Quiz { OwnerId = ownerId, Title = Clean(title, 200), CreatedAtUtc = now, UpdatedAtUtc = now };
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        db.Quizzes.Add(quiz);
        await db.SaveChangesAsync(cancellationToken);
        return quiz;
    }

    /// <summary>
    /// Saves the quiz and all its questions, sanitizing rich text on the way in. Questions are replaced
    /// wholesale: live sessions run from a snapshot, so nothing depends on their ids staying the same.
    /// </summary>
    public async Task<bool> SaveAsync(Quiz edited, string ownerId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var existing = await db.Quizzes
            .Include(q => q.Questions)
            .SingleOrDefaultAsync(q => q.Id == edited.Id && q.OwnerId == ownerId, cancellationToken);
        if (existing is null)
        {
            return false;
        }

        existing.Title = Clean(edited.Title, 200);
        existing.Mode = edited.Mode;
        existing.ScoringEnabled = edited.ScoringEnabled;
        existing.SpeedBonusEnabled = edited.ScoringEnabled && edited.SpeedBonusEnabled;
        existing.ShowLeaderboard = edited.ScoringEnabled && edited.ShowLeaderboard;
        existing.DefaultTimeLimitSeconds = edited.DefaultTimeLimitSeconds;
        existing.ResultsDisplaySeconds = edited.ResultsDisplaySeconds;
        existing.UpdatedAtUtc = UtcNow;

        db.Questions.RemoveRange(existing.Questions);
        existing.Questions = edited.Questions.OrderBy(q => q.Order).Select(CleanCopy).ToList();

        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <summary>Deletes a quiz the caller owns, or any quiz when <paramref name="isAdmin"/> is true.</summary>
    public async Task<bool> DeleteAsync(int id, string userId, bool isAdmin, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var deleted = await db.Quizzes
            .Where(q => q.Id == id && (isAdmin || q.OwnerId == userId))
            .ExecuteDeleteAsync(cancellationToken);
        return deleted == 1;
    }

    private Question CleanCopy(Question question, int index) => new()
    {
        Order = index,
        Type = question.Type,
        Prompt = sanitizer.Sanitize(question.Prompt),
        CodeSnippet = Blank(question.CodeSnippet),
        CodeLanguage = Blank(question.CodeSnippet) is null ? null : Blank(question.CodeLanguage),
        ImageUrl = Images.ImageService.OwnImageOrNull(question.ImageUrl),
        TimeLimitSeconds = question.TimeLimitSeconds,
        Points = question.Points,
        AllowMultipleSelect = question.Type == QuestionType.MultipleChoice && question.AllowMultipleSelect,
        PartialCredit = question.Type == QuestionType.MultipleChoice && question.AllowMultipleSelect && question.PartialCredit,
        MaxEntries = question.Type == QuestionType.WordCloud ? question.MaxEntries : 1,
        Options = question.Type != QuestionType.MultipleChoice
            ? []
            : question.Options.OrderBy(o => o.Order).Select((o, i) => new ChoiceOption
            {
                Order = i,
                Text = Clean(o.Text, 500),
                IsCorrect = o.IsCorrect,
                CodeSnippet = Blank(o.CodeSnippet),
            }).ToList(),
        BlankAnswers = question.Type != QuestionType.FillInBlank
            ? []
            : question.BlankAnswers.Select(a => new BlankAnswer
            {
                BlankIndex = a.BlankIndex,
                AcceptedText = Clean(a.AcceptedText, 200),
                MatchMode = a.MatchMode,
            }).ToList(),
    };

    private static string Clean(string? text, int maxLength)
    {
        var trimmed = (text ?? "").Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }

    private static string? Blank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text;
}
