using Ganss.Xss;

namespace CohortCompanion.Web.Content;

/// <summary>
/// Cleans rich text before it is saved or shown to students. Only simple formatting survives: no images,
/// scripts, or event handlers, and links can only be http, https, or mailto.
/// </summary>
public sealed class ContentSanitizer
{
    private readonly HtmlSanitizer sanitizer;

    public ContentSanitizer()
    {
        sanitizer = new HtmlSanitizer();

        sanitizer.AllowedTags.Clear();
        foreach (var tag in new[]
        {
            "p", "br", "div", "span", "strong", "b", "em", "i", "u", "s", "sub", "sup",
            "ul", "ol", "li", "a", "code", "pre", "blockquote", "h1", "h2", "h3", "h4", "font",
        })
        {
            sanitizer.AllowedTags.Add(tag);
        }

        sanitizer.AllowedAttributes.Clear();
        foreach (var attribute in new[] { "href", "title", "style", "color" })
        {
            sanitizer.AllowedAttributes.Add(attribute);
        }

        sanitizer.AllowedCssProperties.Clear();
        foreach (var property in new[] { "color", "background-color", "text-align" })
        {
            sanitizer.AllowedCssProperties.Add(property);
        }

        sanitizer.AllowedSchemes.Clear();
        foreach (var scheme in new[] { "http", "https", "mailto" })
        {
            sanitizer.AllowedSchemes.Add(scheme);
        }

        sanitizer.PostProcessNode += (_, e) =>
        {
            if (e.Node is AngleSharp.Dom.IElement { TagName: "A" } link)
            {
                link.SetAttribute("target", "_blank");
                link.SetAttribute("rel", "noopener noreferrer");
            }
        };
    }

    public string Sanitize(string? html) => string.IsNullOrWhiteSpace(html) ? "" : sanitizer.Sanitize(html).Trim();

    /// <summary>The visible text of a piece of HTML, used to check whether a prompt is really empty.</summary>
    public static string ToPlainText(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return "";
        }

        var withoutTags = System.Text.RegularExpressions.Regex.Replace(html, "<[^>]*>", " ");
        return System.Net.WebUtility.HtmlDecode(withoutTags).Replace(' ', ' ').Trim();
    }
}
