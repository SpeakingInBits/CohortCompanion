using CohortCompanion.Shared;

namespace CohortCompanion.Web.Data;

public class Presentation
{
    public int Id { get; set; }

    public string OwnerId { get; set; } = "";

    public ApplicationUser? Owner { get; set; }

    public string Title { get; set; } = "";

    public string ThemeId { get; set; } = SlideThemes.DefaultTheme;

    public DateTime CreatedAtUtc { get; set; }

    public DateTime UpdatedAtUtc { get; set; }

    public List<Slide> Slides { get; set; } = [];
}

public class Slide
{
    public int Id { get; set; }

    public int PresentationId { get; set; }

    public int Order { get; set; }

    public SlideLayout Layout { get; set; }

    /// <summary>Serialized <see cref="SlideContent"/>.</summary>
    public string Content { get; set; } = "{}";

    public string SpeakerNotes { get; set; } = "";
}
