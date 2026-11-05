using CohortCompanion.Web.Content;

namespace CohortCompanion.Tests;

public class ContentSanitizerTests
{
    private readonly ContentSanitizer sanitizer = new();

    [Theory]
    [InlineData("<script>alert(1)</script>Hi", "Hi")]
    [InlineData("<p onmouseover=\"x()\">Hi</p>", "<p>Hi</p>")]
    [InlineData("<img src=x onerror=alert(1)>", "")]
    [InlineData("<iframe src=\"https://example.com\"></iframe>", "")]
    public void RemovesDangerousContent(string input, string expected) =>
        Assert.Equal(expected, sanitizer.Sanitize(input));

    [Fact]
    public void KeepsSimpleFormatting() =>
        Assert.Equal("<p><strong>a</strong> <em>b</em></p>", sanitizer.Sanitize("<p><strong>a</strong> <em>b</em></p>"));

    [Fact]
    public void RemovesJavascriptLinks() =>
        Assert.DoesNotContain("javascript", sanitizer.Sanitize("<a href=\"javascript:alert(1)\">x</a>"));

    [Fact]
    public void ExternalLinksOpenSafely()
    {
        var html = sanitizer.Sanitize("<a href=\"https://example.com\">x</a>");

        Assert.Contains("rel=\"noopener noreferrer\"", html);
    }

    [Fact]
    public void KeepsColorButNotPositioning()
    {
        var html = sanitizer.Sanitize("<span style=\"color: red; position: fixed\">x</span>");

        Assert.Contains("color", html);
        Assert.DoesNotContain("position", html);
    }
}
