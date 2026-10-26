using Mantenio.Shared.Ledger;
using Mantenio.UI.Components;
using Shouldly;

namespace Mantenio.UI.Tests;

public sealed class ProofScanFillTests
{
    private static readonly ProofScanResult Scan = new(true, 180m, new DateOnly(2026, 10, 20), "07654321");

    [Fact]
    public void Empty_fields_take_what_was_read()
    {
        var (amount, date, operation) = ProofScanFill.Apply(Scan, null, DateTime.Today, "");

        amount.ShouldBe(180m);
        date.ShouldBe(new DateTime(2026, 10, 20));
        operation.ShouldBe("07654321");
    }

    [Fact]
    public void What_the_person_typed_is_kept()
    {
        var typedDate = DateTime.Today.AddDays(-3);

        var (amount, date, operation) = ProofScanFill.Apply(Scan, 200m, typedDate, "111222");

        amount.ShouldBe(200m);
        date.ShouldBe(typedDate);
        operation.ShouldBe("111222");
    }
}
