using CohortCompanion.Web.Auth;

namespace CohortCompanion.Tests;

public class InviteCodesTests
{
    [Fact]
    public void Generate_ReturnsCodeOfExpectedLengthWithoutAmbiguousCharacters()
    {
        for (var i = 0; i < 200; i++)
        {
            var code = InviteCodes.Generate();

            Assert.Equal(InviteCodes.Length, code.Length);
            Assert.DoesNotContain(code, c => "ILO01".Contains(c));
            Assert.Equal(code, InviteCodes.Normalize(code));
        }
    }

    [Theory]
    [InlineData("k7m4-qxr2", "K7M4QXR2")]
    [InlineData("  K7M4 QXR2 ", "K7M4QXR2")]
    [InlineData("K7M4QXR2", "K7M4QXR2")]
    [InlineData(null, "")]
    public void Normalize_UppercasesAndStripsSeparators(string? input, string expected) =>
        Assert.Equal(expected, InviteCodes.Normalize(input));

    [Fact]
    public void Format_InsertsDashInTheMiddle() =>
        Assert.Equal("K7M4-QXR2", InviteCodes.Format("K7M4QXR2"));

    [Fact]
    public void Format_LeavesUnexpectedLengthsAlone() =>
        Assert.Equal("ABC", InviteCodes.Format("ABC"));
}
