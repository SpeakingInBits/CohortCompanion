using CohortCompanion.Shared;
using CohortCompanion.Web.Live;

namespace CohortCompanion.Tests;

public class TextRulesTests
{
    [Theory]
    [InlineData("  Hello,   World! ", "hello world")]
    [InlineData("C#", "c#")]
    [InlineData("C++", "c++")]
    [InlineData("...", "")]
    public void NormalizeEntry(string input, string expected) =>
        Assert.Equal(expected, TextRules.NormalizeEntry(input));

    [Theory]
    [InlineData("shit", true)]
    [InlineData("Big SHIT!", true)]
    [InlineData("shithead", true)]
    [InlineData("Scunthorpe", false)]
    [InlineData("push it", false)]
    [InlineData("classic", false)]
    [InlineData("assignment", false)]
    [InlineData("Dickens", false)]
    public void IsProfane(string input, bool expected) =>
        Assert.Equal(expected, TextRules.IsProfane(input));

    [Theory]
    [InlineData("const", "const", MatchMode.Exact, true)]
    [InlineData("Const", "const", MatchMode.Exact, false)]
    [InlineData("Const", "const", MatchMode.CaseInsensitive, true)]
    [InlineData("  const ", "const", MatchMode.Exact, true)]
    [InlineData("", "const", MatchMode.CaseInsensitive, false)]
    public void Matches_ExactAndCaseInsensitive(string answer, string accepted, MatchMode mode, bool expected) =>
        Assert.Equal(expected, TextRules.Matches(answer, accepted, mode));

    [Theory]
    [InlineData("constnt", "constant", true)]
    [InlineData("constaant", "constant", true)]
    [InlineData("constamt", "constant", true)]
    [InlineData("CONSTANT", "constant", true)]
    [InlineData("cnstnt", "constant", false)]
    [InlineData("cat", "cats", false)]
    [InlineData("hous", "house", true)]
    [InlineData("ct", "cat", false)]
    public void Matches_FuzzyAllowsOneEditForAnswersOfFiveOrMoreCharacters(string answer, string accepted, bool expected) =>
        Assert.Equal(expected, TextRules.Matches(answer, accepted, MatchMode.Fuzzy));

    [Theory]
    [InlineData("abc", "abc", true)]
    [InlineData("abc", "abd", true)]
    [InlineData("abc", "ab", true)]
    [InlineData("abc", "xbc", true)]
    [InlineData("abc", "abcd", true)]
    [InlineData("abc", "xbd", false)]
    [InlineData("abc", "abcde", false)]
    [InlineData("", "a", true)]
    public void IsWithinOneEdit(string a, string b, bool expected) =>
        Assert.Equal(expected, TextRules.IsWithinOneEdit(a, b));
}
