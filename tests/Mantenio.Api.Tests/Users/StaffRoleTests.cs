using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Mantenio.Api.Infrastructure.Identity;
using Mantenio.Api.Tests.Infrastructure;
using Mantenio.Api.Tests.Ledger;
using Mantenio.Domain.Audit;
using Mantenio.Domain.Identity;
using Mantenio.Shared.Auth;
using Mantenio.Shared.Errors;
using Mantenio.Shared.Invitations;
using Mantenio.Shared.Ledger;
using Mantenio.Shared.Users;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Mantenio.Api.Tests.Users;

/// <summary>
/// The gatehouse (portería) and the apartment rules for staff: admins need an apartment to act on payments, nobody
/// reviews what they sent, and the gatehouse never has an apartment.
/// </summary>
public sealed class StaffRoleTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Operation() => Guid.NewGuid().ToString("N")[..12];

    [Fact]
    public async Task The_gatehouse_registers_without_an_apartment_and_signs_in_with_two_factor()
    {
        using var founder = factory.CreateClient(await factory.SignInFounderAsync());
        var withApartment = await founder.PostAsJsonAsync("/api/admin/invitations",
            new CreateInvitationRequest(AccountRole.Guard, (await founder.ApartmentAsync("H-501")).Id), Ct);
        await withApartment.ShouldBeProblemAsync(HttpStatusCode.BadRequest, ErrorCodes.GuardWithoutApartment);

        // An apartment sent with the form is ignored.
        await factory.RegisterAsync(AccountRole.Guard, "porteria1@test.local", "H-501");
        using var guard = factory.CreateClient(await factory.SignInAdminAsync("porteria1@test.local", TestFlows.ResidentPassword));

        var me = await guard.GetFromJsonAsync<CurrentUserResponse>("/api/account/me", Ct);
        me!.Role.ShouldBe(AccountRole.Guard);
        me.ApartmentId.ShouldBeNull();
        (await factory.WithDbAsync(db => db.AuditEvents.AnyAsync(e => e.Action == "Registered" && e.Source == AuditSource.Guard, Ct)))
            .ShouldBeTrue();
    }

    [Fact]
    public async Task The_gatehouse_invites_residents_and_gatehouse_staff_but_not_admins()
    {
        using var guard = await GuardAsync("porteria2@test.local");

        (await guard.CreateInvitationAsync(AccountRole.Resident)).Length.ShouldBe(6);
        (await guard.CreateInvitationAsync(AccountRole.Guard)).Length.ShouldBe(6);
        var admin = await guard.PostAsJsonAsync("/api/admin/invitations", new CreateInvitationRequest(AccountRole.Admin, null), Ct);
        await admin.ShouldBeProblemAsync(HttpStatusCode.Forbidden, ErrorCodes.Forbidden);
    }

    [Fact]
    public async Task The_gatehouse_reviews_registers_voids_and_sees_the_history_of_a_payment()
    {
        using var resident = factory.CreateClient(await factory.RegisterAsync(AccountRole.Resident, "h502@test.local", "H-502"));
        using var guard = await GuardAsync("porteria3@test.local");
        var apartmentId = (await guard.ApartmentAsync("H-502")).Id;
        await factory.AddChargeAsync(apartmentId, 202611, 180m);

        var pending = await resident.SubmitProofAsync(180m, Operation());
        (await guard.GetJsonAsync<List<PaymentProofItem>>(factory, "/api/admin/payments?status=Pending")).ShouldContain(p => p.Id == pending.Id);
        (await guard.GetAsync(new Uri($"/api/payments/{pending.Id}/image", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var approved = await guard.ApprovedAsync(pending);
        approved.ReceiptNumber.ShouldNotBeNull();

        var rejected = await resident.SubmitProofAsync(50m, Operation());
        await (await guard.PostAsJsonAsync($"/api/admin/payments/{rejected.Id}/reject", new RejectProofRequest("Captura ilegible", rejected.RowVersion), Ct))
            .ShouldBeAsync(HttpStatusCode.OK);

        // Registered in person: approved at once; the same guard may void it (alone on duty).
        var response = await RegisterAsync(guard, apartmentId, "Vecino H-502", 70m, Operation());
        await response.ShouldBeAsync(HttpStatusCode.OK);
        var registered = (await response.Content.ReadFromJsonAsync<PaymentProofItem>(Ct))!;
        registered.Status.ShouldBe(ProofStatus.Approved);
        (await guard.VoidedAsync(registered, "Monto equivocado")).Status.ShouldBe(ProofStatus.Voided);

        var timeline = await guard.GetJsonAsync<List<TimelineEntry>>(factory, $"/api/admin/payments/{approved.Id}/timeline");
        timeline.Select(t => t.Action).ShouldBe(["Submitted", "Approved", "PaymentApplied", "Issued"]);
        timeline.Where(t => t.Action != "Submitted").ShouldAllBe(t => t.Source == nameof(AuditSource.Guard));

        // Nothing else: no statements, reports, users or charge history.
        (await guard.GetAsync(new Uri($"/api/apartments/{apartmentId}/statement", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        foreach (var path in new[] { "/api/admin/users", "/api/admin/audit", "/api/admin/dashboard", "/api/admin/settings" })
        {
            (await guard.GetAsync(new Uri(path, UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden, path);
        }
    }

    [Fact]
    public async Task An_admin_without_an_apartment_cannot_act_on_payments()
    {
        using var resident = factory.CreateClient(await factory.RegisterAsync(AccountRole.Resident, "h503@test.local", "H-503"));
        var proof = await resident.SubmitProofAsync(180m, Operation());
        await factory.RegisterAsync(AccountRole.Admin, "noflat-admin@test.local", "H-504");
        await factory.WithDbAsync(async db =>
        {
            var user = await db.Users.SingleAsync(u => u.Email == "noflat-admin@test.local", Ct);
            user.ApartmentId = null; // as the production superadmin was before this rule
            return await db.SaveChangesAsync(Ct);
        });
        using var admin = factory.CreateClient(await factory.SignInAdminAsync("noflat-admin@test.local", TestFlows.ResidentPassword));

        await (await admin.ApproveAsync(proof)).ShouldBeProblemAsync(HttpStatusCode.Forbidden, ErrorCodes.AdminApartmentRequired);
        await (await RegisterAsync(admin, proof.ApartmentId, "Alguien", 180m, Operation()))
            .ShouldBeProblemAsync(HttpStatusCode.Forbidden, ErrorCodes.AdminApartmentRequired);
    }

    [Fact]
    public async Task Nobody_approves_a_proof_they_sent()
    {
        // An admin sends their own payment, then moves: the proof stays theirs.
        await factory.RegisterAsync(AccountRole.Admin, "mover-admin@test.local", "D-501");
        using var admin = factory.CreateClient(await factory.SignInAdminAsync("mover-admin@test.local", TestFlows.ResidentPassword));
        var proof = await admin.SubmitProofAsync(180m, Operation());
        using var founder = factory.CreateClient(await factory.SignInFounderAsync());
        var adminId = await UserIdAsync("mover-admin@test.local");
        await (await founder.PutAsJsonAsync($"/api/admin/users/{adminId}/apartment",
            new ChangeUserApartmentRequest((await founder.ApartmentAsync("D-502")).Id, "Se mudó"), Ct)).ShouldBeAsync(HttpStatusCode.NoContent);

        await (await admin.ApproveAsync(proof)).ShouldBeProblemAsync(HttpStatusCode.Forbidden, ErrorCodes.OwnSubmissionReview);
        (await founder.ApprovedAsync(proof)).Status.ShouldBe(ProofStatus.Approved);
    }

    [Fact]
    public async Task Apartment_changes_follow_the_role_rules()
    {
        await factory.RegisterAsync(AccountRole.Guard, "porteria4@test.local", null);
        await factory.RegisterAsync(AccountRole.Admin, "self-admin@test.local", "D-503");
        using var founder = factory.CreateClient(await factory.SignInFounderAsync());
        using var admin = factory.CreateClient(await factory.SignInAdminAsync("self-admin@test.local", TestFlows.ResidentPassword));
        var other = (await founder.ApartmentAsync("D-504")).Id;

        var guard = await founder.PutAsJsonAsync($"/api/admin/users/{await UserIdAsync("porteria4@test.local")}/apartment",
            new ChangeUserApartmentRequest(other, "Vive aquí"), Ct);
        await guard.ShouldBeProblemAsync(HttpStatusCode.BadRequest, ErrorCodes.GuardWithoutApartment);

        var adminId = await UserIdAsync("self-admin@test.local");
        await (await admin.PutAsJsonAsync($"/api/admin/users/{adminId}/apartment", new ChangeUserApartmentRequest(other, "Me mudé"), Ct))
            .ShouldBeProblemAsync(HttpStatusCode.BadRequest, ErrorCodes.CannotModifySelf);
        await (await founder.PutAsJsonAsync($"/api/admin/users/{adminId}/apartment", new ChangeUserApartmentRequest(null, "Sin depa"), Ct))
            .ShouldBeProblemAsync(HttpStatusCode.BadRequest, ErrorCodes.ApartmentRequired);

        // The superadmin changes their own apartment, with a reason, and it is audited.
        var founderId = await UserIdAsync(ApiFactory.FounderEmail);
        var founderApartment = (await founder.ApartmentAsync(ApiFactory.FounderApartment)).Id;
        await (await founder.PutAsJsonAsync($"/api/admin/users/{founderId}/apartment", new ChangeUserApartmentRequest(other, "Me mudé"), Ct))
            .ShouldBeAsync(HttpStatusCode.NoContent);
        await (await founder.PutAsJsonAsync($"/api/admin/users/{founderId}/apartment", new ChangeUserApartmentRequest(founderApartment, "Regresé"), Ct))
            .ShouldBeAsync(HttpStatusCode.NoContent);
        (await factory.WithDbAsync(db => db.AuditEvents.CountAsync(e => e.Action == "ApartmentChanged" && e.EntityId == founderId.ToString(), Ct)))
            .ShouldBe(2);
    }

    [Fact]
    public async Task No_gatehouse_codes_are_accepted_once_the_limit_is_reached()
    {
        await using var isolated = new ApiFactory();
        await isolated.InitializeAsync();
        using var admin = isolated.CreateClient(await isolated.SignInFounderAsync());
        var pendingCode = await admin.CreateInvitationAsync(AccountRole.Guard);

        await isolated.WithScopeAsync(async services =>
        {
            var users = services.GetRequiredService<UserManager<AppUser>>();
            for (var i = 0; i < Domain.Condo.CondoSettings.MaxGuards; i++)
            {
                var user = AppUser.Create($"guard{i}@test.local", "Porteria", $"{i}", UserRole.Guard, DateTimeOffset.UtcNow);
                (await users.CreateAsync(user, "Guard2026")).Succeeded.ShouldBeTrue();
            }
        });

        var newCode = await admin.PostAsJsonAsync("/api/admin/invitations", new CreateInvitationRequest(AccountRole.Guard, null), Ct);
        await newCode.ShouldBeProblemAsync(HttpStatusCode.Conflict, ErrorCodes.GuardLimitReached);

        using var client = isolated.CreateClient();
        var register = await client.PostAsJsonAsync("/api/auth/register",
            new RegisterRequest(pendingCode, "Veinte", "Uno", "guard21@test.local", TestFlows.ResidentPassword, null, false), Ct);
        await register.ShouldBeProblemAsync(HttpStatusCode.Conflict, ErrorCodes.GuardLimitReached);

        // Admin codes have their own limit.
        (await admin.PostAsJsonAsync("/api/admin/invitations", new CreateInvitationRequest(AccountRole.Admin, null), Ct))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private async Task<HttpClient> GuardAsync(string email)
    {
        await factory.RegisterAsync(AccountRole.Guard, email, null);
        return factory.CreateClient(await factory.SignInAdminAsync(email, TestFlows.ResidentPassword));
    }

    private Task<Guid> UserIdAsync(string email) =>
        factory.WithDbAsync(db => db.Users.Where(u => u.Email == email).Select(u => u.Id).SingleAsync(Ct));

    private static Task<HttpResponseMessage> RegisterAsync(HttpClient staff, Guid apartmentId, string payer, decimal amount, string operation)
    {
        var form = new MultipartFormDataContent
        {
            { new StringContent(apartmentId.ToString()), "apartmentId" },
            { new StringContent(payer), "payerName" },
            { new StringContent(amount.ToString(CultureInfo.InvariantCulture)), "amount" },
            { new StringContent(DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-2)).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)), "operationDate" },
            { new StringContent("BCP"), "bankName" },
            { new StringContent(operation), "operationNumber" },
        };
        var file = new ByteArrayContent(LedgerFlows.Png());
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(file, "image", "voucher.png");
        return staff.PostAsync(new Uri("/api/admin/payments/register", UriKind.Relative), form, Ct);
    }
}
