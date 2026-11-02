using System.Security.Cryptography;

namespace CohortCompanion.Web.Auth;

/// <summary>Generation and formatting of invite codes.</summary>
public static class InviteCodes
{
    // No I, L, O, 0 or 1, so codes can be read aloud and typed without confusion.
    private const string Alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";

    public const int Length = 8;

    /// <summary>Returns a new random code in normalized form, for example <c>K7M4QXR2</c>.</summary>
    public static string Generate() => new(RandomNumberGenerator.GetItems<char>(Alphabet, Length));

    /// <summary>Uppercases and strips separators and whitespace, so <c>k7m4-qxr2</c> becomes <c>K7M4QXR2</c>.</summary>
    public static string Normalize(string? code) =>
        code is null ? "" : new string(code.Where(char.IsAsciiLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    /// <summary>Formats a normalized code for display, for example <c>K7M4-QXR2</c>.</summary>
    public static string Format(string normalizedCode) =>
        normalizedCode.Length == Length ? $"{normalizedCode[..4]}-{normalizedCode[4..]}" : normalizedCode;

    public static string NormalizeEmail(string email) => email.Trim().ToLowerInvariant();
}
