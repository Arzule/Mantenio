using System.Net;
using System.Net.Http.Json;
using Mantenio.Api.Infrastructure.Email;
using Mantenio.Api.Infrastructure.Push;
using Mantenio.Api.Infrastructure.Time;
using Mantenio.Api.Jobs;
using Mantenio.Api.Tests.Infrastructure;
using Mantenio.Api.Tests.Ledger;
using Mantenio.Domain.Audit;
using Mantenio.Domain.Ledger;
using Mantenio.Shared.Apartments;
using Mantenio.Shared.Auth;
using Mantenio.Shared.Ledger;
using Mantenio.Shared.Notifications;
using Mantenio.Shared.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Mantenio.Api.Tests.Jobs;

/// <summary>Scheduled jobs and push (docs/context.md Phase 6, §10 "Notificaciones y jobs" and scenario 13).</summary>
public sealed class JobTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<T> JobAsync<T>(Func<LedgerJobs, Task<T>> run)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await run(scope.ServiceProvider.GetRequiredService<LedgerJobs>());
    }

    private Task<int> DispatchAsync() => factory.Services.GetRequiredService<PushDispatcher>().DispatchPendingAsync(Ct);

    private async Task<(HttpClient Client, Guid ApartmentId)> ResidentAsync(string email, string apartmentCode)
    {
        var client = factory.CreateClient(await factory.RegisterAsync(AccountRole.Resident, email, apartmentCode));
        return (client, (await client.ApartmentAsync(apartmentCode)).Id);
    }

    [Fact]
    public async Task Late_fees_apply_once_per_overdue_monthly_fee_only_when_the_founder_set_a_percentage()
    {
        using var founder = factory.CreateClient(await factory.SignInFounderAsync());
        var overdue = (await founder.ApartmentAsync("A-401")).Id;
        var paid = (await founder.ApartmentAsync("A-402")).Id;
        var frozen = (await founder.ApartmentAsync("A-403")).Id;
        var overdueCharge = await factory.AddChargeAsync(overdue, 202607, 180m);
        await factory.AddChargeAsync(frozen, 202607, 180m);
        var paidCharge = await factory.AddChargeAsync(paid, 202607, 180m);
        await factory.WithDbAsync(async db =>
        {
            db.Charges.Add(Charge.Opening(overdue, Domain.Common.Period.FromValue(202605), 300m, new DateOnly(2026, 6, 1), factory.Time.GetUtcNow()));
            return await db.SaveChangesAsync(Ct);
        });
        var (payer, _) = await ResidentAsync("latefee-paid@test.local", "A-402");
        await founder.ApprovedAsync(await payer.SubmitProofAsync(180m, Guid.NewGuid().ToString("N")[..10]));
        await (await founder.PostAsJsonAsync($"/api/admin/apartments/{frozen}/freeze", new FreezeApartmentRequest("Desocupado"), Ct))
            .ShouldBeAsync(HttpStatusCode.NoContent);

        (await JobAsync(j => j.ApplyLateFeesAsync(Ct))).ShouldBe(0); // 0 % by default

        await (await founder.PutAsJsonAsync("/api/admin/settings/late-fee", new ChangeLateFeeRequest(5m, "Acuerdo de asamblea"), Ct))
            .ShouldBeAsync(HttpStatusCode.NoContent);
        await JobAsync(j => j.ApplyLateFeesAsync(Ct));
        (await JobAsync(j => j.ApplyLateFeesAsync(Ct))).ShouldBe(0); // once

        // Only this test's apartments: other tests of the class leave overdue fees elsewhere.
        Guid[] mine = [overdue, paid, frozen];
        var fees = await factory.WithDbAsync(db => db.Charges.Where(c => c.Type == ChargeType.LateFee && mine.Contains(c.ApartmentId)).ToListAsync(Ct));
        var fee = fees.ShouldHaveSingleItem(); // not on the opening debt, the paid month or the frozen apartment
        fee.ParentChargeId.ShouldBe(overdueCharge);
        fee.Amount.ShouldBe(9m);
        fee.ApartmentId.ShouldBe(overdue);
        fees.ShouldNotContain(f => f.ParentChargeId == paidCharge);

        var timeline = await founder.GetJsonAsync<List<TimelineEntry>>(factory, $"/api/admin/charges/{overdueCharge}/timeline");
        timeline.ShouldContain(t => t.Action == "LateFeeCreated" && t.Source == "System" && t.ActorName == null);

        await (await founder.PutAsJsonAsync("/api/admin/settings/late-fee", new ChangeLateFeeRequest(0m, "Fin de la prueba"), Ct))
            .ShouldBeAsync(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Reminders_reach_only_apartments_with_overdue_debt_that_are_not_frozen_once_a_month()
    {
        var (debtor, debtorApartment) = await ResidentAsync("reminder-debt@test.local", "B-401");
        var (upToDate, _) = await ResidentAsync("reminder-ok@test.local", "B-402");
        var (frozenResident, frozenApartment) = await ResidentAsync("reminder-frozen@test.local", "B-403");
        using var founder = factory.CreateClient(await factory.SignInFounderAsync());
        await factory.AddChargeAsync(debtorApartment, 202606, 180m);
        await factory.AddChargeAsync(debtorApartment, 202607, 180m);
        await factory.AddChargeAsync(frozenApartment, 202606, 180m);
        await (await founder.PostAsJsonAsync($"/api/admin/apartments/{frozenApartment}/freeze", new FreezeApartmentRequest("Desocupado"), Ct))
            .ShouldBeAsync(HttpStatusCode.NoContent);

        await JobAsync(j => j.SendDebtRemindersAsync(Ct));
        (await JobAsync(j => j.SendDebtRemindersAsync(Ct))).ShouldBe(0);

        var notice = (await debtor.GetJsonAsync<NotificationPage>(factory, "/api/notifications")).Items.Single(n => n.Kind == NotificationKind.DebtReminder);
        notice.Data.GetProperty("amount").GetDecimal().ShouldBe(360m);
        notice.Data.GetProperty("months").GetInt32().ShouldBe(2);
        (await upToDate.GetJsonAsync<NotificationPage>(factory, "/api/notifications")).Items.ShouldNotContain(n => n.Kind == NotificationKind.DebtReminder);
        (await frozenResident.GetJsonAsync<NotificationPage>(factory, "/api/notifications")).Items.ShouldNotContain(n => n.Kind == NotificationKind.DebtReminder);

        var email = factory.Emails.To("reminder-debt@test.local").Single(m => m.Subject.Contains("Recordatorio", StringComparison.Ordinal));
        email.TextBody.ShouldContain("B-401");
        factory.Emails.To("reminder-ok@test.local").ShouldNotContain(m => m.Subject.Contains("Recordatorio", StringComparison.Ordinal));
        (await factory.WithDbAsync(db => db.AuditEvents.SingleAsync(e => e.Action == "ReminderSent" && e.ApartmentId == debtorApartment, Ct)))
            .Source.ShouldBe(AuditSource.System);
    }

    [Fact]
    public async Task Notices_go_out_as_push_to_the_users_phones_and_dead_tokens_are_forgotten()
    {
        var (resident, apartmentId) = await ResidentAsync("push@test.local", "C-401");
        using var founder = factory.CreateClient(await factory.SignInFounderAsync());
        await (await resident.PostAsJsonAsync("/api/devices", new RegisterDeviceRequest("token-push-1", "android"), Ct)).ShouldBeAsync(HttpStatusCode.NoContent);
        await (await resident.PostAsJsonAsync("/api/devices", new RegisterDeviceRequest("bad-token-push", "android"), Ct)).ShouldBeAsync(HttpStatusCode.NoContent);
        await factory.AddChargeAsync(apartmentId, 202608, 180m);
        await DispatchAsync();

        await founder.ApprovedAsync(await resident.SubmitProofAsync(180m, Guid.NewGuid().ToString("N")[..10]));
        (await DispatchAsync()).ShouldBe(1);
        (await DispatchAsync()).ShouldBe(0); // already pushed

        var message = factory.Pushes.Messages.Single(m => m.Token == "token-push-1");
        message.Title.ShouldBe("Pago aprobado");
        message.Body.ShouldContain("180.00");
        message.Data["kind"].ShouldBe("PaymentApproved");
        (await factory.WithDbAsync(db => db.UserDevices.Select(d => d.Token).ToListAsync(Ct))).ShouldNotContain("bad-token-push");

        await (await resident.PostAsJsonAsync("/api/devices/unregister", new UnregisterDeviceRequest("token-push-1"), Ct)).ShouldBeAsync(HttpStatusCode.NoContent);
        (await factory.WithDbAsync(db => db.UserDevices.AnyAsync(d => d.Token == "token-push-1", Ct))).ShouldBeFalse();
    }

    [Fact]
    public async Task Only_the_founders_phones_are_copied_outside_the_database_for_the_outage_alert()
    {
        var (resident, _) = await ResidentAsync("founder-copy@test.local", "D-401");
        using var founder = factory.CreateClient(await factory.SignInFounderAsync());

        await (await founder.PostAsJsonAsync("/api/devices", new RegisterDeviceRequest("founder-token-1", "android"), Ct)).ShouldBeAsync(HttpStatusCode.NoContent);
        await (await resident.PostAsJsonAsync("/api/devices", new RegisterDeviceRequest("resident-token-1", "android"), Ct)).ShouldBeAsync(HttpStatusCode.NoContent);

        var copy = Path.Combine(factory.StoragePath, "system", "founder-devices.json");
        File.ReadAllText(copy).ShouldContain("founder-token-1");
        File.ReadAllText(copy).ShouldNotContain("resident-token-1");

        await (await founder.PostAsJsonAsync("/api/devices/unregister", new UnregisterDeviceRequest("founder-token-1"), Ct)).ShouldBeAsync(HttpStatusCode.NoContent);
        File.ReadAllText(copy).ShouldNotContain("founder-token-1");
    }

    [Fact]
    public async Task Cleanup_is_audited_as_a_system_job()
    {
        await using var scope = factory.Services.CreateAsyncScope();

        var summary = await JobCommands.CleanupAsync(scope.ServiceProvider, Ct);

        summary.ShouldStartWith("Removed");
        (await factory.WithDbAsync(db => db.AuditEvents.AnyAsync(e => e.Action == "Cleanup" && e.Source == AuditSource.System, Ct))).ShouldBeTrue();
    }

    [Fact]
    public void Emails_have_a_branded_html_version_with_the_code_large()
    {
        var html = EmailLayout.Html("Tu código", "Hola, Ana:\n\nTu código es:\n\n123456\n\nVence en unos minutos <pronto>.");

        html.ShouldContain(System.Net.WebUtility.HtmlEncode(Shared.Condos.CondoProfile.Current.DisplayName));
        html.ShouldContain("font-size:32px");
        html.ShouldContain("&lt;pronto&gt;");
    }
}

/// <summary>The monthly charge job on its own database: it moves the fake clock into a tracked month.</summary>
public sealed class ChargeJobTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<T> JobAsync<T>(Func<LedgerJobs, Task<T>> run)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await run(scope.ServiceProvider.GetRequiredService<LedgerJobs>());
    }

    [Fact]
    public async Task Generating_charges_is_a_system_action_and_runs_once_per_month()
    {
        // The job never charges months before the condo starts tracking (October 2026 < November 2026 here).
        var clock = factory.Services.GetRequiredService<CondoClock>();
        var start = await factory.WithDbAsync(db => db.CondoSettings.Select(s => s.TrackingStartPeriod).SingleOrDefaultAsync(Ct));
        if (start == 0 || clock.CurrentPeriod.Value < start)
        {
            (await JobAsync(j => j.GenerateChargesAsync(Ct))).Created.ShouldBe(0);
            factory.Time.Advance(TimeSpan.FromDays(40)); // into a tracked month
        }

        var first = await JobAsync(j => j.GenerateChargesAsync(Ct));
        var second = await JobAsync(j => j.GenerateChargesAsync(Ct));

        var period = factory.Services.GetRequiredService<CondoClock>().CurrentPeriod.Value;
        first.Period.ShouldBe(period);
        second.Created.ShouldBe(0);
        var audits = await factory.WithDbAsync(db => db.AuditEvents.Where(e => e.Action == "ChargeCreated").ToListAsync(Ct));
        audits.Count.ShouldBe(first.Created);
        audits.ShouldAllBe(e => e.Source == AuditSource.System && e.ActorUserId == null);
    }
}
