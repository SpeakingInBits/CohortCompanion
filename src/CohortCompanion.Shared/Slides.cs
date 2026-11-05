namespace CohortCompanion.Shared;

public enum SlideLayout
{
    Title = 0,
    TitleContent = 1,
    TwoColumns = 2,
    Image = 3,
    ImageCaption = 4,
    Code = 5,
    SectionHeader = 6,
    Blank = 7,
}

/// <summary>What's on a slide. Each layout uses the fields it needs and leaves the others alone.</summary>
public sealed class SlideContent
{
    public string Title { get; set; } = "";

    /// <summary>Sanitized HTML.</summary>
    public string Body { get; set; } = "";

    /// <summary>Sanitized HTML for the second column.</summary>
    public string Body2 { get; set; } = "";

    public string? ImageUrl { get; set; }

    public string ImageAlt { get; set; } = "";

    public string Caption { get; set; } = "";

    public string Code { get; set; } = "";

    public string? CodeLanguage { get; set; }

    /// <summary>One of <see cref="SlideThemes.Backgrounds"/>.</summary>
    public string Background { get; set; } = SlideThemes.DefaultBackground;
}

/// <summary>A slide ready to show. Speaker notes never leave the server in this form.</summary>
public sealed record SlideView(int Index, SlideLayout Layout, SlideContent Content);

/// <summary>Built-in looks, so instructors never have to fiddle with fonts or colors.</summary>
public static class SlideThemes
{
    public const string DefaultTheme = "classic";
    public const string DefaultBackground = "theme";

    public static readonly IReadOnlyList<(string Id, string Name)> All =
    [
        ("classic", "Classic"),
        ("midnight", "Midnight"),
        ("forest", "Forest"),
        ("sunset", "Sunset"),
        ("mono", "Monochrome"),
    ];

    public static readonly IReadOnlyList<(string Id, string Name)> Backgrounds =
    [
        ("theme", "Theme default"),
        ("accent", "Accent color"),
        ("plain", "Plain"),
    ];

    public static string LayoutName(SlideLayout layout) => layout switch
    {
        SlideLayout.Title => "Title",
        SlideLayout.TitleContent => "Title + content",
        SlideLayout.TwoColumns => "Two columns",
        SlideLayout.Image => "Image",
        SlideLayout.ImageCaption => "Image + caption",
        SlideLayout.Code => "Code",
        SlideLayout.SectionHeader => "Section header",
        _ => "Blank",
    };
}
