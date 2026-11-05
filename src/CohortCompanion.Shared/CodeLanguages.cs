namespace CohortCompanion.Shared;

/// <summary>Languages offered for code snippets. The ids are highlight.js language names.</summary>
public static class CodeLanguages
{
    public static readonly IReadOnlyList<(string Id, string Name)> All =
    [
        ("csharp", "C#"),
        ("java", "Java"),
        ("python", "Python"),
        ("javascript", "JavaScript"),
        ("typescript", "TypeScript"),
        ("sql", "SQL"),
        ("xml", "HTML / XML"),
        ("css", "CSS"),
        ("cpp", "C++"),
        ("c", "C"),
        ("go", "Go"),
        ("rust", "Rust"),
        ("php", "PHP"),
        ("ruby", "Ruby"),
        ("bash", "Bash"),
        ("json", "JSON"),
        ("plaintext", "Plain text"),
    ];
}
