using Mantenio.Domain.Banking;
using Shouldly;

namespace Mantenio.Domain.Tests.Banking;

public sealed class BankAccountTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Account_number_and_cci_are_normalized()
    {
        var account = new BankAccount(" BCP ", "Junta de Propietarios", "191 1234567 0 12", "002-191-001234567012-55", 1, Guid.NewGuid(), Now);

        account.BankName.ShouldBe("BCP");
        account.AccountNumber.ShouldBe("1911234567012");
        account.Cci.ShouldBe("00219100123456701255");
        account.IsActive.ShouldBeTrue();
    }

    [Theory]
    [InlineData("0021910012345670125")] // 19 digits
    [InlineData("002191001234567012555")] // 21 digits
    [InlineData("")]
    public void The_cci_must_have_20_digits(string cci) =>
        Should.Throw<ArgumentException>(() => new BankAccount("BCP", "Junta", "191-1234567-0-12", cci, 1, Guid.NewGuid(), Now));

    [Theory]
    [InlineData("abc")]
    [InlineData("----")]
    [InlineData("1234567890123456789012345678901")]
    public void The_account_number_must_be_digits_and_dashes(string number) =>
        Should.Throw<ArgumentException>(() => new BankAccount("BCP", "Junta", number, "00219100123456701255", 1, Guid.NewGuid(), Now));

    [Fact]
    public void Deactivating_keeps_the_account_and_records_who()
    {
        var admin = Guid.NewGuid();
        var account = new BankAccount("BCP", "Junta", "191-1234567-0-12", "00219100123456701255", 1, Guid.NewGuid(), Now);

        account.Deactivate(admin, Now.AddDays(1));

        account.IsActive.ShouldBeFalse();
        account.UpdatedById.ShouldBe(admin);
        account.UpdatedAt.ShouldBe(Now.AddDays(1));
    }
}
