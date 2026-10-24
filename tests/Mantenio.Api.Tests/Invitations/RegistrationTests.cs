using System.Net;
using System.Net.Http.Json;
using Mantenio.Api.Infrastructure.Identity;
using Mantenio.Api.Tests.Infrastructure;
using Mantenio.Domain.Identity;
using Mantenio.Shared.Auth;
using Mantenio.Shared.Errors;
using Mantenio.Shared.Invitations;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Mantenio.Api.Tests.Invitations;

public sealed class RegistrationTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_resident_registers_with_a_code_confirms_the_email_and_is_signed_in()
    {
        using var admin = factory.CreateClient(await factory.SignInFounderAsync());
        var code = await admin.CreateInvitationAsync(AccountRole.Resident);
        using var client = factory.CreateClient();

        var check = await client.PostAsJsonAsync("/api/invitations/check", new CheckInvitationRequest(code), Ct);
        await check.ShouldBeAsync(HttpStatusCode.OK);
        var checkResult = (await check.Content.ReadFromJsonAsync<CheckInvitationResponse>(Ct))!;
        checkResult.Role.ShouldBe(AccountRole.Resident);
        checkResult.Apartment.ShouldBeNull();

        var apartment = await client.ApartmentAsync("H-102");
        var register = await client.PostAsJsonAsync("/api/auth/register",
            new RegisterRequest(code, "Rosa", "Quispe", "rosa@test.local", TestFlows.ResidentPassword, apartment.Id, KeepSignedIn: true), Ct);
        await register.ShouldBeAsync(HttpStatusCode.OK);
        var pending = (await register.Content.ReadFromJsonAsync<SignInResponse>(Ct))!;
        pending.Step.ShouldBe(SignInStep.EmailConfirmationRequired);
        pending.Tokens.ShouldBeNull();

        var tokens = await client.VerifyCodeAsync("/api/auth/email/confirm", pending.ChallengeToken!, factory.Emails.LastCodeFor("rosa@test.local"));
        (tokens.RefreshTokenExpiresAt - factory.Time.GetUtcNow()).ShouldBe(TimeSpan.FromDays(90), TimeSpan.FromMinutes(1));

        var user = await factory.WithDbAsync(db => db.Users.Include(u => u.Apartment).SingleAsync(u => u.Email == "rosa@test.local", Ct));
        var founderId = await factory.WithDbAsync(db => db.Users.Where(u => u.IsFounder).Select(u => u.Id).SingleAsync(Ct));
        user.AuthorizedById.ShouldBe(founderId);
        user.Apartment!.Code.ShouldBe("H-102");
        user.EmailConfirmed.ShouldBeTrue();
        (await factory.WithDbAsync(db => db.AuditEvents.AnyAsync(e => e.EntityId == user.Id.ToString() && e.Action == "Registered", Ct)))
            .ShouldBeTrue();

        // Single use.
        var again = await client.PostAsJsonAsync("/api/invitations/check", new CheckInvitationRequest(code), Ct);
        await again.ShouldBeProblemAsync(HttpStatusCode.BadRequest, ErrorCodes.InvitationInvalid);
    }

    [Fact]
    public async Task Until_the_email_is_confirmed_login_asks_for_the_code_again()
    {
        using var admin = factory.CreateClient(await factory.SignInFounderAsync());
        var code = await admin.CreateInvitationAsync(AccountRole.Resident);
        using var client = factory.CreateClient();
        var apartment = await client.ApartmentAsync("C-301");
        await client.PostAsJsonAsync("/api/auth/register",
            new RegisterRequest(code, "Luis", "Rojas", "luis@test.local", TestFlows.ResidentPassword, apartment.Id, false), Ct);

        var login = await client.LoginAsync("luis@test.local", TestFlows.ResidentPassword);

        login.Step.ShouldBe(SignInStep.EmailConfirmationRequired);
        await client.VerifyCodeAsync("/api/auth/email/confirm", login.ChallengeToken!, factory.Emails.LastCodeFor("luis@test.local"));
    }

    [Fact]
    public async Task A_code_bound_to_an_apartment_ignores_the_apartment_chosen_in_the_form()
    {
        using var admin = factory.CreateClient(await factory.SignInFounderAsync());
        var bound = await admin.ApartmentAsync("D-404");
        var code = await admin.CreateInvitationAsync(AccountRole.Resident, bound.Id);
        using var client = factory.CreateClient();

        var check = await (await client.PostAsJsonAsync("/api/invitations/check", new CheckInvitationRequest(code), Ct))
            .Content.ReadFromJsonAsync<CheckInvitationResponse>(Ct);
        check!.Apartment!.Code.ShouldBe("D-404");

        var other = await client.ApartmentAsync("E-101");
        await client.PostAsJsonAsync("/api/auth/register",
            new RegisterRequest(code, "Eva", "Soto", "eva@test.local", TestFlows.ResidentPassword, other.Id, false), Ct);

        var user = await factory.WithDbAsync(db => db.Users.SingleAsync(u => u.Email == "eva@test.local", Ct));
        user.ApartmentId.ShouldBe(bound.Id);
    }

    [Fact]
    public async Task Residents_must_pick_an_apartment_and_the_code_is_not_spent_on_failure()
    {
        using var admin = factory.CreateClient(await factory.SignInFounderAsync());
        var code = await admin.CreateInvitationAsync(AccountRole.Resident);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/auth/register",
            new RegisterRequest(code, "Sin", "Depa", "sindepa@test.local", TestFlows.ResidentPassword, null, false), Ct);

        await response.ShouldBeProblemAsync(HttpStatusCode.BadRequest, ErrorCodes.ApartmentRequired);
        (await client.PostAsJsonAsync("/api/invitations/check", new CheckInvitationRequest(code), Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await factory.WithDbAsync(db => db.Users.AnyAsync(u => u.Email == "sindepa@test.local", Ct))).ShouldBeFalse();
    }

    [Fact]
    public async Task An_email_can_only_have_one_account()
    {
        await factory.RegisterAsync(AccountRole.Resident, "dup@test.local", "F-101");
        using var admin = factory.CreateClient(await factory.SignInFounderAsync());
        var code = await admin.CreateInvitationAsync(AccountRole.Resident);
        using var client = factory.CreateClient();
        var apartment = await client.ApartmentAsync("F-102");

        var response = await client.PostAsJsonAsync("/api/auth/register",
            new RegisterRequest(code, "Otro", "Más", "DUP@test.local", TestFlows.ResidentPassword, apartment.Id, false), Ct);

        await response.ShouldBeProblemAsync(HttpStatusCode.Conflict, ErrorCodes.EmailTaken);
    }

    [Fact]
    public async Task Unknown_codes_are_rejected()
    {
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/invitations/check", new CheckInvitationRequest("123456"), Ct);

        await response.ShouldBeProblemAsync(HttpStatusCode.BadRequest, ErrorCodes.InvitationInvalid);
    }

    [Fact]
    public async Task Admins_register_without_an_apartment_and_then_sign_in_with_two_factor()
    {
        await factory.RegisterAsync(AccountRole.Admin, "tesorero@test.local", apartmentCode: null);

        var tokens = await factory.SignInAdminAsync("tesorero@test.local", TestFlows.ResidentPassword);
        using var client = factory.CreateClient(tokens);
        var me = await client.GetFromJsonAsync<CurrentUserResponse>("/api/account/me", Ct);

        me!.Role.ShouldBe(AccountRole.Admin);
        me.ApartmentId.ShouldBeNull();
        me.IsFounder.ShouldBeFalse();
    }

    [Fact]
    public async Task No_admin_codes_are_accepted_once_the_admin_limit_is_reached()
    {
        // A fresh factory keeps the admin count of this test independent from the others.
        await using var isolated = new ApiFactory();
        await isolated.InitializeAsync();
        using var admin = isolated.CreateClient(await isolated.SignInFounderAsync());
        var pendingCode = await admin.CreateInvitationAsync(AccountRole.Admin);

        // Founder + 9 more = 10 active admins.
        await isolated.WithScopeAsync(async services =>
        {
            var users = services.GetRequiredService<UserManager<AppUser>>();
            for (var i = 0; i < 9; i++)
            {
                var user = AppUser.Create($"admin{i}@test.local", "Admin", $"{i}", UserRole.Admin, DateTimeOffset.UtcNow);
                (await users.CreateAsync(user, "Admin2026")).Succeeded.ShouldBeTrue();
            }
        });

        var newCode = await admin.PostAsJsonAsync("/api/admin/invitations", new CreateInvitationRequest(AccountRole.Admin, null), Ct);
        await newCode.ShouldBeProblemAsync(HttpStatusCode.Conflict, ErrorCodes.AdminLimitReached);

        using var client = isolated.CreateClient();
        var register = await client.PostAsJsonAsync("/api/auth/register",
            new RegisterRequest(pendingCode, "Once", "Avo", "eleventh@test.local", TestFlows.ResidentPassword, null, false), Ct);
        await register.ShouldBeProblemAsync(HttpStatusCode.Conflict, ErrorCodes.AdminLimitReached);

        // Resident codes are not limited.
        (await admin.PostAsJsonAsync("/api/admin/invitations", new CreateInvitationRequest(AccountRole.Resident, null), Ct))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}
