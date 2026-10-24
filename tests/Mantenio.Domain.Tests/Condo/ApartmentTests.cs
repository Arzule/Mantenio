using Mantenio.Domain.Condo;
using Shouldly;

namespace Mantenio.Domain.Tests.Condo;

public sealed class ApartmentTests
{
    [Fact]
    public void Code_combines_tower_and_number_in_upper_case()
    {
        var tower = new Tower(" h ", sortOrder: 8);

        var apartment = new Apartment(tower, " 102 ");

        apartment.Code.ShouldBe("H-102");
        apartment.Number.ShouldBe("102");
        apartment.TowerId.ShouldBe(tower.Id);
        apartment.IsFrozen.ShouldBeFalse();
    }
}
