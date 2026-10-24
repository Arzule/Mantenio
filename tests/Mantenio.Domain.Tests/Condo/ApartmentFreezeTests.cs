using Mantenio.Domain.Condo;
using Shouldly;

namespace Mantenio.Domain.Tests.Condo;

public sealed class ApartmentFreezeTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Freezing_and_unfreezing_keeps_who_and_why()
    {
        var apartment = new Apartment(new Tower("H", 8), "102");
        var admin = Guid.NewGuid();

        var freeze = apartment.Freeze("Proceso judicial", admin, Now);
        apartment.IsFrozen.ShouldBeTrue();
        freeze.IsActive.ShouldBeTrue();

        apartment.Unfreeze(freeze, "Acuerdo de pago", admin, Now.AddDays(30));

        apartment.IsFrozen.ShouldBeFalse();
        freeze.IsActive.ShouldBeFalse();
        freeze.Reason.ShouldBe("Proceso judicial");
        freeze.EndReason.ShouldBe("Acuerdo de pago");
        freeze.EndedAt.ShouldBe(Now.AddDays(30));
    }

    [Fact]
    public void An_apartment_cannot_be_frozen_twice_or_unfrozen_when_not_frozen()
    {
        var apartment = new Apartment(new Tower("H", 8), "102");
        var freeze = apartment.Freeze("Motivo", Guid.NewGuid(), Now);

        Should.Throw<InvalidOperationException>(() => apartment.Freeze("Otra vez", Guid.NewGuid(), Now));
        apartment.Unfreeze(freeze, "Fin", Guid.NewGuid(), Now);
        Should.Throw<InvalidOperationException>(() => apartment.Unfreeze(freeze, "Fin", Guid.NewGuid(), Now));
    }
}
