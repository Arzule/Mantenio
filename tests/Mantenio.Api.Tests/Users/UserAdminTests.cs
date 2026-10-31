using System.Net;
using System.Net.Http.Json;
using Mantenio.Api.Tests.Infrastructure;
using Mantenio.Shared.Auth;
using Mantenio.Shared.Errors;
using Mantenio.Shared.Users;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace Mantenio.Api.Tests.Users;

public sealed class UserAdminTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_deactivated_account_is_signed_out_and_cannot_sign_in()
    {
        var session = await factory.RegisterAsync(AccountRole.Resident, "moved@test.local", "I-101");
        using var admin = factory.CreateClient(await factory.SignInFounderAsync());
        var userId = await UserIdAsync("moved@test.local");

        var response = await admin.PostAsJsonAsync($"/api/admin/users/{userId}/deactivate", new ChangeUserStatusRequest("Se mudó"), Ct);
        await response.ShouldBeAsync(HttpStatusCode.NoContent);

        using var client = factory.CreateClient();
        var refresh = await client.PostAsJsonAsync("/api/auth/refresh", new RefreshRequest(session.RefreshToken), Ct);
        await refresh.ShouldBeProblemAsync(HttpStatusCode.Unauthorized, ErrorCodes.InvalidRefreshToken);
        var login = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest("moved@test.local", TestFlows.ResidentPassword, false), Ct);
        await login.ShouldBeProblemAsync(HttpStatusCode.Forbidden, ErrorCodes.AccountInactive);

        var audit = await factory.WithDbAsync(db => db.AuditEvents.SingleAsync(e => e.EntityId == userId.ToString() && e.Action == "Deactivated", Ct));
        audit.Reason.ShouldBe("Se mudó");
        audit.ActorUserId.ShouldNotBeNull();

        await admin.PostAsJsonAsync($"/api/admin/users/{userId}/reactivate", new ChangeUserStatusRequest("Volvió"), Ct);
        (await client.LoginAsync("moved@test.local", TestFlows.ResidentPassword)).Step.ShouldBe(SignInStep.SignedIn);
    }

    [Fact]
    public async Task The_founder_and_yourself_cannot_be_deactivated()
    {
        await factory.RegisterAsync(AccountRole.Admin, "admin2@test.local", "F-504");
        using var otherAdmin = factory.CreateClient(await factory.SignInAdminAsync("admin2@test.local", TestFlows.ResidentPassword));
        var founderId = await UserIdAsync(ApiFactory.FounderEmail);
        var selfId = await UserIdAsync("admin2@test.local");

        var founder = await otherAdmin.PostAsJsonAsync($"/api/admin/users/{founderId}/deactivate", new ChangeUserStatusRequest("Golpe de estado"), Ct);
        await founder.ShouldBeProblemAsync(HttpStatusCode.Forbidden, ErrorCodes.FounderProtected);

        var self = await otherAdmin.PostAsJsonAsync($"/api/admin/users/{selfId}/deactivate", new ChangeUserStatusRequest("Me voy"), Ct);
        await self.ShouldBeProblemAsync(HttpStatusCode.BadRequest, ErrorCodes.CannotModifySelf);
    }

    [Fact]
    public async Task Admins_change_a_residents_apartment_with_a_reason()
    {
        await factory.RegisterAsync(AccountRole.Resident, "wrongflat@test.local", "I-201");
        using var admin = factory.CreateClient(await factory.SignInFounderAsync());
        var userId = await UserIdAsync("wrongflat@test.local");
        var target = await admin.ApartmentAsync("I-202");

        var none = await admin.PutAsJsonAsync($"/api/admin/users/{userId}/apartment", new ChangeUserApartmentRequest(null, "Sin depa"), Ct);
        await none.ShouldBeProblemAsync(HttpStatusCode.BadRequest, ErrorCodes.ApartmentRequired);

        var moved = await admin.PutAsJsonAsync($"/api/admin/users/{userId}/apartment", new ChangeUserApartmentRequest(target.Id, "Se equivocó al registrarse"), Ct);
        await moved.ShouldBeAsync(HttpStatusCode.NoContent);

        var page = await admin.GetFromJsonAsync<PagedResult<UserSummary>>("/api/admin/users?search=I-202", Ct);
        page!.Items.ShouldHaveSingleItem().Email.ShouldBe("wrongflat@test.local");
        (await factory.WithDbAsync(db => db.AuditEvents.AnyAsync(e => e.Action == "ApartmentChanged" && e.ApartmentId == target.Id, Ct)))
            .ShouldBeTrue();
    }

    [Fact]
    public async Task The_user_list_shows_who_authorized_each_account()
    {
        await factory.RegisterAsync(AccountRole.Resident, "listed@test.local", "I-301");
        using var admin = factory.CreateClient(await factory.SignInFounderAsync());

        var page = await admin.GetFromJsonAsync<PagedResult<UserSummary>>("/api/admin/users?search=listed", Ct);

        var user = page!.Items.ShouldHaveSingleItem();
        user.AuthorizedByName.ShouldBe("Fiorella Fundadora");
        user.ApartmentCode.ShouldBe("I-301");
        user.Role.ShouldBe(AccountRole.Resident);
    }

    private Task<Guid> UserIdAsync(string email) =>
        factory.WithDbAsync(db => db.Users.Where(u => u.Email == email).Select(u => u.Id).SingleAsync(Ct));
}

/// <summary>Uses tiny limits, so it has its own API instance.</summary>
public sealed class RateLimitTests(RateLimitTests.Factory factory) : IClassFixture<RateLimitTests.Factory>
{
    public sealed class Factory : ApiFactory
    {
        protected override int RateLimit => 3;
    }

    [Fact]
    public async Task Hammering_the_login_returns_too_many_requests()
    {
        using var client = factory.CreateClient();
        HttpResponseMessage? last = null;

        for (var i = 0; i < 4; i++)
        {
            last = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest("x@test.local", "whatever1", false), TestContext.Current.CancellationToken);
        }

        await last!.ShouldBeProblemAsync(HttpStatusCode.TooManyRequests, ErrorCodes.TooManyRequests);
    }
}
