using System.Net;
using System.Net.Http.Json;
using Mantenio.Api.Features.Reports;
using Mantenio.Api.Jobs;
using Mantenio.Api.Tests.Infrastructure;
using Mantenio.Api.Tests.Ledger;
using Mantenio.Domain.Audit;
using Mantenio.Shared.Auth;
using Mantenio.Shared.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;

namespace Mantenio.Api.Tests.Security;

/// <summary>
/// Protections against tampering: the database refuses to delete ledger rows or edit the audit log, the board gets a
/// monthly summary with the audit fingerprint, and the fingerprint changes when the log changes.
/// </summary>
public sealed class LedgerProtectionTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("DELETE FROM \"AuditEvents\"")]
    [InlineData("UPDATE \"AuditEvents\" SET \"Reason\" = 'cambiado'")]
    [InlineData("TRUNCATE \"AuditEvents\"")]
    [InlineData("DELETE FROM \"Charges\"")]
    [InlineData("DELETE FROM \"PaymentProofs\"")]
    [InlineData("TRUNCATE \"Receipts\" CASCADE")]
    public async Task Sql_cannot_delete_ledger_rows_or_edit_the_audit_log(string sql)
    {
        // Make sure there is something to touch.
        await factory.SignInFounderAsync();
        var apartment = await factory.CreateClient().ApartmentAsync("E-101");
        if (!await factory.WithDbAsync(db => db.Charges.AnyAsync(c => c.ApartmentId == apartment.Id, Ct)))
        {
            await factory.AddChargeAsync(apartment.Id, 202611, 180m);
            await factory.WithDbAsync(async db =>
            {
                var founderId = await db.Users.Select(u => u.Id).FirstAsync(Ct);
                db.PaymentProofs.Add(Domain.Ledger.PaymentProof.Historical(apartment.Id, founderId, 180m, new DateOnly(2025, 1, 5), null, null, null, null,
                    null, factory.Time.GetUtcNow()));
                return await db.SaveChangesAsync(Ct);
            });
        }

        var error = await Should.ThrowAsync<PostgresException>(() => factory.WithDbAsync(db => db.Database.ExecuteSqlRawAsync(sql, Ct)));

        error.MessageText.ShouldContain("append-only");
    }

    [Fact]
    public async Task Only_the_founder_sets_the_board_emails_and_the_board_gets_the_monthly_summary_once()
    {
        using var founder = factory.CreateClient(await factory.SignInFounderAsync());
        using var resident = factory.CreateClient(await factory.RegisterAsync(AccountRole.Resident, "board-resident@test.local", "E-102"));
        var request = new ChangeBoardEmailsRequest(["presidencia@test.local", "tesoreria@test.local"], "Junta 2026");

        (await resident.PutAsJsonAsync("/api/admin/settings/board-emails", request, Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await founder.PutAsJsonAsync("/api/admin/settings/board-emails", new ChangeBoardEmailsRequest(["no es un correo"], "Junta 2026"), Ct))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        await (await founder.PutAsJsonAsync("/api/admin/settings/board-emails", request, Ct)).ShouldBeAsync(HttpStatusCode.NoContent);
        (await founder.GetJsonAsync<CondoSettingsResponse>(factory, "/api/admin/settings")).BoardEmails.ShouldBe(["presidencia@test.local", "tesoreria@test.local"]);

        int first, second;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            first = await scope.ServiceProvider.GetRequiredService<BoardSummaryJob>().SendAsync(Ct);
        }

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            second = await scope.ServiceProvider.GetRequiredService<BoardSummaryJob>().SendAsync(Ct);
        }

        first.ShouldBe(2);
        second.ShouldBe(0);
        var email = factory.Emails.To("tesoreria@test.local").ShouldHaveSingleItem();
        email.Attachments!.Select(a => a.ContentType).ShouldBe([ReportFiles.PdfContentType, ReportFiles.ExcelContentType]);
        email.TextBody.ShouldContain("Huella del historial");
        (await factory.WithDbAsync(db => db.AuditEvents.AnyAsync(e => e.Action == "BoardSummarySent" && e.Source == AuditSource.System, Ct))).ShouldBeTrue();
    }

    [Fact]
    public async Task The_fingerprint_is_stable_and_changes_when_the_log_grows()
    {
        var now = factory.Time.GetUtcNow();
        var before = await factory.WithDbAsync(db => AuditFingerprint.ComputeAsync(db, now, Ct));
        (await factory.WithDbAsync(db => AuditFingerprint.ComputeAsync(db, now, Ct))).ShouldBe(before);

        factory.Time.Advance(TimeSpan.FromSeconds(1));
        await factory.WithDbAsync(async db =>
        {
            db.AuditEvents.Add(new AuditEvent("Test", "x", "Probe", AuditSource.System, null, factory.Time.GetUtcNow()));
            return await db.SaveChangesAsync(Ct);
        });
        var after = await factory.WithDbAsync(db => AuditFingerprint.ComputeAsync(db, factory.Time.GetUtcNow(), Ct));

        after.Events.ShouldBe(before.Events + 1);
        after.Hash.ShouldNotBe(before.Hash);
        (await factory.WithDbAsync(db => AuditFingerprint.ComputeAsync(db, now, Ct))).ShouldBe(before);
    }
}
