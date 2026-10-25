using System.Net;
using System.Net.Http.Json;
using ClosedXML.Excel;
using Mantenio.Api.Infrastructure.Time;
using Mantenio.Api.Tests.Infrastructure;
using Mantenio.Api.Tests.Ledger;
using Mantenio.Shared.Apartments;
using Mantenio.Shared.Auth;
using Mantenio.Shared.Errors;
using Mantenio.Shared.Ledger;
using Mantenio.Shared.Reports;
using Mantenio.Shared.Users;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Mantenio.Api.Tests.Reports;

/// <summary>Delinquency report, monthly folio, dashboard and audit viewer (docs/context.md Phase 5, §10 scenario 14).</summary>
public sealed class ReportTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private DateOnly Today => factory.Services.GetRequiredService<CondoClock>().Today;

    private async Task<(HttpClient Client, Guid ApartmentId)> ResidentAsync(string email, string apartmentCode)
    {
        var client = factory.CreateClient(await factory.RegisterAsync(AccountRole.Resident, email, apartmentCode));
        return (client, (await client.ApartmentAsync(apartmentCode)).Id);
    }

    private static DelinquencyRow? Row(DelinquencyReport report, string code) =>
        report.Towers.SelectMany(t => t.Rows).SingleOrDefault(r => r.Code == code);

    private async Task<DelinquencyReport> ReportAsync(HttpClient admin, DateOnly date) =>
        await admin.GetJsonAsync<DelinquencyReport>(factory, $"/api/admin/reports/delinquency?date={date:yyyy-MM-dd}");

    [Fact]
    public async Task Frozen_apartments_are_listed_and_marked_and_months_not_yet_due_are_not_debt()
    {
        using var admin = factory.CreateClient(await factory.SignInFounderAsync());
        var frozen = await admin.ApartmentAsync("E-102");
        var current = await admin.ApartmentAsync("E-103");
        await factory.AddChargeAsync(frozen.Id, 202606, 150m);
        var notDue = Period(Today).Value;
        await factory.AddChargeAsync(current.Id, notDue, 150m); // due next month
        await (await admin.PostAsJsonAsync($"/api/admin/apartments/{frozen.Id}/freeze", new FreezeApartmentRequest("Desocupado"), Ct))
            .ShouldBeAsync(HttpStatusCode.NoContent);

        var report = await ReportAsync(admin, Today);

        Row(report, "E-102").ShouldNotBeNull().IsFrozen.ShouldBeTrue();
        Row(report, "E-103").ShouldBeNull();
        report.Towers.Single(t => t.Name == "E").Total.ShouldBe(report.Towers.Single(t => t.Name == "E").Rows.Sum(r => r.Amount));
    }

    [Fact]
    public async Task The_report_exports_to_excel_with_one_sheet_per_tower_and_to_pdf()
    {
        using var admin = factory.CreateClient(await factory.SignInFounderAsync());
        await factory.AddChargeAsync((await admin.ApartmentAsync("F-101")).Id, 202605, 150m);
        await factory.AddChargeAsync((await admin.ApartmentAsync("G-101")).Id, 202605, 150m);

        var excel = await admin.GetAsync(new Uri($"/api/admin/reports/delinquency/excel?date={Today:yyyy-MM-dd}", UriKind.Relative), Ct);
        await excel.ShouldBeAsync(HttpStatusCode.OK);
        excel.Content.Headers.ContentDisposition!.FileNameStar.ShouldEndWith(".xlsx");
        using var workbook = new XLWorkbook(await excel.Content.ReadAsStreamAsync(Ct));
        workbook.Worksheets.Select(w => w.Name).ShouldContain("F");
        workbook.Worksheets.Select(w => w.Name).ShouldContain("G");
        workbook.Worksheet("F").CellsUsed().ShouldContain(c => c.GetString() == "F-101");

        var pdf = await admin.GetByteArrayAsync(new Uri($"/api/admin/reports/delinquency/pdf?date={Today:yyyy-MM-dd}", UriKind.Relative), Ct);
        pdf[..4].ShouldBe("%PDF"u8.ToArray());
    }

    [Fact]
    public async Task The_monthly_folio_is_a_pdf_of_the_receipts_of_that_month()
    {
        var (resident, apartmentId) = await ResidentAsync("folio@test.local", "H-101");
        using var admin = factory.CreateClient(await factory.SignInFounderAsync());
        await factory.AddChargeAsync(apartmentId, 202609, 180m);
        await admin.ApprovedAsync(await resident.SubmitProofAsync(180m, Guid.NewGuid().ToString("N")[..10]));

        var response = await admin.GetAsync(new Uri($"/api/admin/reports/folio/{Period(Today).Value}/pdf", UriKind.Relative), Ct);

        await response.ShouldBeAsync(HttpStatusCode.OK);
        (await response.Content.ReadAsByteArrayAsync(Ct))[..4].ShouldBe("%PDF"u8.ToArray());
        await (await admin.GetAsync(new Uri("/api/admin/reports/folio/202613/pdf", UriKind.Relative), Ct))
            .ShouldBeProblemAsync(HttpStatusCode.BadRequest, ErrorCodes.InvalidPeriod);
    }

    [Fact]
    public async Task The_dashboard_counts_pending_proofs_debt_and_this_months_collection()
    {
        var (resident, apartmentId) = await ResidentAsync("dash@test.local", "I-101");
        using var admin = factory.CreateClient(await factory.SignInFounderAsync());
        await factory.AddChargeAsync(apartmentId, 202604, 150m);
        var before = await admin.GetJsonAsync<AdminDashboard>(factory, "/api/admin/dashboard");

        await resident.SubmitProofAsync(50m, Guid.NewGuid().ToString("N")[..10]);
        await admin.ApprovedAsync(await resident.SubmitProofAsync(100m, Guid.NewGuid().ToString("N")[..10]));
        var after = await admin.GetJsonAsync<AdminDashboard>(factory, "/api/admin/dashboard");

        after.PendingProofs.ShouldBe(before.PendingProofs + 1);
        after.CollectedThisMonth.ShouldBe(before.CollectedThisMonth + 100m);
        after.ReceiptsThisMonth.ShouldBe(before.ReceiptsThisMonth + 1);
        after.OverdueDebt.ShouldBe(before.OverdueDebt - 100m);
        after.Towers.Single(t => t.Tower == "I").Apartments.ShouldBe(20);
    }

    [Fact]
    public async Task The_audit_viewer_filters_by_apartment_and_source_and_is_admin_only()
    {
        var (resident, apartmentId) = await ResidentAsync("audit@test.local", "I-102");
        using var admin = factory.CreateClient(await factory.SignInFounderAsync());
        await factory.AddChargeAsync(apartmentId, 202603, 150m);
        await admin.ApprovedAsync(await resident.SubmitProofAsync(150m, Guid.NewGuid().ToString("N")[..10]));

        var all = await admin.GetJsonAsync<PagedResult<AuditItem>>(factory, $"/api/admin/audit?apartmentId={apartmentId}");
        all.Items.Select(i => i.Action).ShouldContain("Submitted");
        all.Items.Select(i => i.Action).ShouldContain("Approved");
        all.Items.ShouldAllBe(i => i.ApartmentCode == "I-102");
        all.Items[0].CreatedAt.ShouldBeGreaterThanOrEqualTo(all.Items[^1].CreatedAt);

        var residentOnly = await admin.GetJsonAsync<PagedResult<AuditItem>>(factory, $"/api/admin/audit?apartmentId={apartmentId}&source=Resident");
        residentOnly.Items.Select(i => i.Action).ShouldBe(["Submitted", "Registered"]);
        residentOnly.Items.ShouldAllBe(i => i.Source == "Resident" && i.ActorName == "Nombre Apellido");

        var page = await admin.GetJsonAsync<PagedResult<AuditItem>>(factory, "/api/admin/audit?pageSize=2&page=1");
        page.Items.Count.ShouldBe(2);
        page.Total.ShouldBeGreaterThan(2);
        (await admin.GetJsonAsync<List<AuditActor>>(factory, "/api/admin/audit/actors")).ShouldContain(a => a.Name == "Fiorella Fundadora");

        (await resident.GetAsync(new Uri("/api/admin/audit", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await resident.GetAsync(new Uri("/api/admin/reports/delinquency", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    private static Domain.Common.Period Period(DateOnly date) => Domain.Common.Period.FromDate(date);
}

/// <summary>
/// §10 scenario 14 on its own database: it moves the fake clock forward, which would expire the sign-in codes of the
/// other report tests.
/// </summary>
public sealed class DelinquencyHistoryTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private DateOnly Today => factory.Services.GetRequiredService<CondoClock>().Today;

    private async Task<(HttpClient Client, Guid ApartmentId)> ResidentAsync(string email, string apartmentCode)
    {
        var client = factory.CreateClient(await factory.RegisterAsync(AccountRole.Resident, email, apartmentCode));
        return (client, (await client.ApartmentAsync(apartmentCode)).Id);
    }

    private static DelinquencyRow? Row(DelinquencyReport report, string code) =>
        report.Towers.SelectMany(t => t.Rows).SingleOrDefault(r => r.Code == code);

    private async Task<DelinquencyReport> ReportAsync(HttpClient admin, DateOnly date) =>
        await admin.GetJsonAsync<DelinquencyReport>(factory, $"/api/admin/reports/delinquency?date={date:yyyy-MM-dd}");

    [Fact]
    public async Task Delinquency_on_a_past_date_does_not_change_with_later_payments_or_voids()
    {
        var (resident, apartmentId) = await ResidentAsync("r14@test.local", "E-101");
        using var admin = factory.CreateClient(await factory.SignInFounderAsync());
        await factory.AddChargeAsync(apartmentId, 202607, 180m);
        await factory.AddChargeAsync(apartmentId, 202608, 180m);
        var day0 = Today;

        var before = Row(await ReportAsync(admin, day0), "E-101").ShouldNotBeNull();
        before.Periods.ShouldBe([202607, 202608]);
        before.Amount.ShouldBe(360m);

        factory.Time.Advance(TimeSpan.FromDays(2));
        var paid = await admin.ApprovedAsync(await resident.SubmitProofAsync(180m, Guid.NewGuid().ToString("N")[..10]));
        var day2 = Today;
        Row(await ReportAsync(admin, day2), "E-101")!.Periods.ShouldBe([202608]);

        factory.Time.Advance(TimeSpan.FromDays(2));
        await admin.VoidedAsync(paid);

        // Each past day keeps its own picture; today shows both months again.
        Row(await ReportAsync(admin, day0), "E-101")!.Amount.ShouldBe(360m);
        Row(await ReportAsync(admin, day2), "E-101")!.Amount.ShouldBe(180m);
        Row(await ReportAsync(admin, Today), "E-101")!.Amount.ShouldBe(360m);
    }
}
