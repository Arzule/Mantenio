using Mantenio.Domain.Identity;
using Shouldly;

namespace Mantenio.Domain.Tests.Identity;

public sealed class SessionPolicyTests
{
    [Theory]
    [InlineData(UserRole.Admin, true, 30 * 24)]
    [InlineData(UserRole.Admin, false, 12)]
    [InlineData(UserRole.Resident, true, 90 * 24)]
    [InlineData(UserRole.Resident, false, 12)]
    public void Refresh_token_lifetime_depends_on_role_and_keep_signed_in(UserRole role, bool keepSignedIn, int hours) =>
        SessionPolicy.RefreshTokenLifetime(role, keepSignedIn).ShouldBe(TimeSpan.FromHours(hours));

    [Fact]
    public void Only_admins_need_a_second_factor()
    {
        SessionPolicy.RequiresTwoFactor(UserRole.Admin).ShouldBeTrue();
        SessionPolicy.RequiresTwoFactor(UserRole.Resident).ShouldBeFalse();
    }

    [Fact]
    public void Access_tokens_are_short_lived() =>
        SessionPolicy.AccessTokenLifetime.ShouldBe(TimeSpan.FromMinutes(15));
}
