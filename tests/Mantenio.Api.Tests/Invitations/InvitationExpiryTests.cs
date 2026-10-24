using System.Net;
using System.Net.Http.Json;
using Mantenio.Api.Tests.Infrastructure;
using Mantenio.Shared.Auth;
using Mantenio.Shared.Errors;
using Mantenio.Shared.Invitations;

namespace Mantenio.Api.Tests.Invitations;

/// <summary>Moves the clock forward, so it has its own API instance.</summary>
public sealed class InvitationExpiryTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Codes_stop_working_after_thirty_minutes()
    {
        using var admin = factory.CreateClient(await factory.SignInFounderAsync());
        var code = await admin.CreateInvitationAsync(AccountRole.Resident);
        using var client = factory.CreateClient();

        factory.Time.Advance(TimeSpan.FromMinutes(29));
        await (await client.PostAsJsonAsync("/api/invitations/check", new CheckInvitationRequest(code), Ct)).ShouldBeAsync(HttpStatusCode.OK);

        factory.Time.Advance(TimeSpan.FromMinutes(2));
        var expired = await client.PostAsJsonAsync("/api/invitations/check", new CheckInvitationRequest(code), Ct);
        await expired.ShouldBeProblemAsync(HttpStatusCode.BadRequest, ErrorCodes.InvitationInvalid);
    }
}
