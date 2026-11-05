using System.Text.Json;
using CohortCompanion.Shared;
using CohortCompanion.Web.Content;
using CohortCompanion.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace CohortCompanion.Web.Presentations;

public sealed record PresentationListItem(int Id, string Title, int SlideCount, DateTime UpdatedAtUtc, string OwnerName);

/// <summary>A deck as the editor works with it: content is unpacked, and every field is editable.</summary>
public sealed class PresentationDocument
{
    public int Id { get; set; }

    public string Title { get; set; } = "";

    public string ThemeId { get; set; } = SlideThemes.DefaultTheme;

    public List<EditableSlide> Slides { get; set; } = [];
}

public sealed class EditableSlide
{
    public SlideLayout Layout { get; set; } = SlideLayout.TitleContent;

    public SlideContent Content { get; set; } = new();

    public string SpeakerNotes { get; set; } = "";
}

/// <summary>Presentation storage. Every read and write is limited to the owner, except the explicit admin paths.</summary>
public sealed class PresentationService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    ContentSanitizer sanitizer,
    TimeProvider timeProvider)
{
    public const int MaxSlides = 200;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private DateTime UtcNow => timeProvider.GetUtcNow().UtcDateTime;

    /// <summary>Lists one instructor's decks, or every deck when <paramref name="ownerId"/> is null (admins only).</summary>
    public async Task<List<PresentationListItem>> ListAsync(string? ownerId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var rows = await db.Presentations
            .AsNoTracking()
            .Where(p => ownerId == null || p.OwnerId == ownerId)
            .Select(p => new PresentationListItem(p.Id, p.Title, p.Slides.Count, p.UpdatedAtUtc, p.Owner!.DisplayName))
            .ToListAsync(cancellationToken);
        return rows.OrderByDescending(p => p.UpdatedAtUtc).ToList();
    }

    public async Task<PresentationDocument?> GetAsync(int id, string ownerId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var presentation = await db.Presentations
            .AsNoTracking()
            .Include(p => p.Slides)
            .SingleOrDefaultAsync(p => p.Id == id && p.OwnerId == ownerId, cancellationToken);

        return presentation is null ? null : new PresentationDocument
        {
            Id = presentation.Id,
            Title = presentation.Title,
            ThemeId = presentation.ThemeId,
            Slides = presentation.Slides.OrderBy(s => s.Order).Select(s => new EditableSlide
            {
                Layout = s.Layout,
                Content = JsonSerializer.Deserialize<SlideContent>(s.Content, Json) ?? new SlideContent(),
                SpeakerNotes = s.SpeakerNotes,
            }).ToList(),
        };
    }

    public async Task<Presentation> CreateAsync(string ownerId, string title, CancellationToken cancellationToken = default)
    {
        var now = UtcNow;
        var cleanTitle = Text(title, 200);
        var presentation = new Presentation
        {
            OwnerId = ownerId,
            Title = cleanTitle,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            Slides =
            [
                new Slide
                {
                    Layout = SlideLayout.Title,
                    Content = JsonSerializer.Serialize(new SlideContent { Title = cleanTitle }, Json),
                },
            ],
        };

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        db.Presentations.Add(presentation);
        await db.SaveChangesAsync(cancellationToken);
        return presentation;
    }

    /// <summary>
    /// Saves the deck and its slides, sanitizing on the way in. Slides are replaced wholesale: live
    /// sessions run from a snapshot, so nothing depends on their ids staying the same.
    /// </summary>
    public async Task<bool> SaveAsync(PresentationDocument document, string ownerId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var existing = await db.Presentations
            .Include(p => p.Slides)
            .SingleOrDefaultAsync(p => p.Id == document.Id && p.OwnerId == ownerId, cancellationToken);
        if (existing is null)
        {
            return false;
        }

        existing.Title = Text(document.Title, 200);
        existing.ThemeId = SlideThemes.All.Any(t => t.Id == document.ThemeId) ? document.ThemeId : SlideThemes.DefaultTheme;
        existing.UpdatedAtUtc = UtcNow;

        db.Slides.RemoveRange(existing.Slides);
        existing.Slides = document.Slides.Take(MaxSlides).Select(CleanSlide).ToList();

        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <summary>Deletes a deck the caller owns, or any deck when <paramref name="isAdmin"/> is true.</summary>
    public async Task<bool> DeleteAsync(int id, string userId, bool isAdmin, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var deleted = await db.Presentations
            .Where(p => p.Id == id && (isAdmin || p.OwnerId == userId))
            .ExecuteDeleteAsync(cancellationToken);
        return deleted == 1;
    }

    private Slide CleanSlide(EditableSlide slide, int index)
    {
        var content = slide.Content;
        return new Slide
        {
            Order = index,
            Layout = slide.Layout,
            SpeakerNotes = Text(slide.SpeakerNotes, 10_000),
            Content = JsonSerializer.Serialize(
                new SlideContent
                {
                    Title = Text(content.Title, 300),
                    Body = sanitizer.Sanitize(content.Body),
                    Body2 = sanitizer.Sanitize(content.Body2),
                    ImageUrl = Images.ImageService.OwnImageOrNull(content.ImageUrl),
                    ImageAlt = Text(content.ImageAlt, 300),
                    Caption = Text(content.Caption, 500),
                    Code = content.Code.Length <= 20_000 ? content.Code : content.Code[..20_000],
                    CodeLanguage = string.IsNullOrWhiteSpace(content.CodeLanguage) ? null : Text(content.CodeLanguage, 30),
                    Background = SlideThemes.Backgrounds.Any(b => b.Id == content.Background) ? content.Background : SlideThemes.DefaultBackground,
                },
                Json),
        };
    }

    private static string Text(string? text, int maxLength)
    {
        var trimmed = (text ?? "").Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }
}
