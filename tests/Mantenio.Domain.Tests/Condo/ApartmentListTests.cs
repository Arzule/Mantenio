using Mantenio.Domain.Condo;
using Shouldly;

namespace Mantenio.Domain.Tests.Condo;

public sealed class ApartmentListTests
{
    [Fact]
    public void Parses_rows_skipping_header_comments_and_blank_lines()
    {
        const string csv = "# towers A-B\r\ntower,number\r\nA,101\r\n\r\n A , 102 \r\nB,101\r\n";

        var entries = ApartmentList.Parse(csv);

        entries.ShouldBe([new("A", "101"), new("A", "102"), new("B", "101")]);
    }

    [Fact]
    public void The_header_is_optional()
    {
        ApartmentList.Parse("H,102").ShouldBe([new("H", "102")]);
    }

    [Fact]
    public void Repeated_apartments_are_rejected_regardless_of_case()
    {
        var error = Should.Throw<FormatException>(() => ApartmentList.Parse("a,101\nA,101"));

        error.Message.ShouldContain("Line 2");
    }

    [Theory]
    [InlineData("A")]
    [InlineData("A,101,extra")]
    [InlineData(",101")]
    public void Malformed_rows_are_rejected(string csv) =>
        Should.Throw<FormatException>(() => ApartmentList.Parse(csv));

    [Fact]
    public void The_demo_list_has_180_apartments_in_9_towers()
    {
        var csv = File.ReadAllText(Path.Combine(RepoRoot(), "condos", "demo", "apartments.csv"));

        var entries = ApartmentList.Parse(csv);

        entries.Count.ShouldBe(180);
        entries.Select(e => e.Tower).Distinct().ShouldBe(["A", "B", "C", "D", "E", "F", "G", "H", "I"]);
        entries.ShouldContain(new ApartmentListEntry("H", "102"));
        entries.ShouldContain(new ApartmentListEntry("I", "504"));
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Mantenio.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
