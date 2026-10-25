using System.Text.RegularExpressions;
using Shouldly;

namespace Mantenio.UI.Tests;

/// <summary>Basic accessibility rules checked on the Razor sources: screen readers need a name for every control.</summary>
public sealed partial class AccessibilityTests
{
    private static readonly string UiSources = Path.Combine(RepoRoot(), "src", "Mantenio.UI");

    [Fact]
    public void Icon_only_buttons_have_an_accessible_name() =>
        Offenders(IconButton(), tag => tag.Contains("aria-label", StringComparison.Ordinal) || tag.Contains("AriaLabel", StringComparison.Ordinal))
            .ShouldBeEmpty();

    [Fact]
    public void Images_have_alternative_text() =>
        Offenders(Image(), tag => tag.Contains("alt=", StringComparison.Ordinal)).ShouldBeEmpty();

    private static List<string> Offenders(Regex tags, Func<string, bool> isAccessible) =>
        Directory.EnumerateFiles(UiSources, "*.razor", SearchOption.AllDirectories)
            .SelectMany(file => tags.Matches(File.ReadAllText(file))
                .Where(m => !isAccessible(m.Value))
                .Select(m => $"{Path.GetFileName(file)}: {m.Value[..Math.Min(m.Value.Length, 80)]}"))
            .ToList();

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "global.json")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    [GeneratedRegex(@"<MudIconButton\b[^>]*>", RegexOptions.Singleline)]
    private static partial Regex IconButton();

    [GeneratedRegex(@"<img\b[^>]*>", RegexOptions.Singleline)]
    private static partial Regex Image();
}
