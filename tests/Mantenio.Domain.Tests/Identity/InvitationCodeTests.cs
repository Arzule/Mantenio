using Mantenio.Domain.Identity;
using Shouldly;

namespace Mantenio.Domain.Tests.Identity;

public sealed class InvitationCodeTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 24, 15, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_new_code_expires_after_twenty_four_hours()
    {
        var code = new InvitationCode("hash", UserRole.Resident, null, Guid.NewGuid(), Now);

        code.ExpiresAt.ShouldBe(Now.AddHours(24));
        code.IsRedeemable(Now.AddHours(24).AddSeconds(-1)).ShouldBeTrue();
        code.IsRedeemable(Now.AddHours(24)).ShouldBeFalse();
        code.IsRedeemable(Now.AddHours(25)).ShouldBeFalse();
    }

    [Fact]
    public void A_code_can_be_redeemed_only_once()
    {
        var code = new InvitationCode("hash", UserRole.Admin, null, Guid.NewGuid(), Now);
        var userId = Guid.NewGuid();

        code.Redeem(userId, Now.AddMinutes(5));

        code.UsedById.ShouldBe(userId);
        code.UsedAt.ShouldBe(Now.AddMinutes(5));
        code.IsRedeemable(Now.AddMinutes(6)).ShouldBeFalse();
        Should.Throw<InvalidOperationException>(() => code.Redeem(Guid.NewGuid(), Now.AddMinutes(6)));
    }

    [Fact]
    public void An_expired_code_cannot_be_redeemed()
    {
        var code = new InvitationCode("hash", UserRole.Resident, null, Guid.NewGuid(), Now);

        Should.Throw<InvalidOperationException>(() => code.Redeem(Guid.NewGuid(), Now.AddHours(25)));
    }

    [Fact]
    public void Generated_codes_are_six_digits()
    {
        for (var i = 0; i < 200; i++)
        {
            InvitationCode.IsWellFormed(InvitationCode.GenerateCode()).ShouldBeTrue();
        }
    }

    [Theory]
    [InlineData("012345", true)]
    [InlineData("12345", false)]
    [InlineData("1234567", false)]
    [InlineData("12a456", false)]
    [InlineData("１２３４５６", false)]
    [InlineData(null, false)]
    public void Only_six_ascii_digits_are_well_formed(string? code, bool expected) =>
        InvitationCode.IsWellFormed(code).ShouldBe(expected);
}
