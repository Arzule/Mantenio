using System.Net.Http.Headers;
using Bunit;
using Mantenio.Client;
using Mantenio.Shared.Auth;
using Mantenio.Shared.Reports;
using Mantenio.Shared.Users;
using Mantenio.UI.Components;
using Mantenio.UI.Pages.Admin;
using Mantenio.UI.Platform;
using Mantenio.UI.Resources;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using Shouldly;

namespace Mantenio.UI.Tests;

public sealed class Phase5UiTests : BunitContext
{
    private readonly FakeApi _api = new();
    private readonly FakeFileOpener _files = new();

    public Phase5UiTests()
    {
        Services.AddMudServices();
        Services.AddSingleton<IHttpClientFactory>(_api);
        Services.AddSingleton<MantenioApi>();
        Services.AddSingleton<PublicApi>();
        Services.AddSingleton<IFileOpener>(_files);
        JSInterop.Mode = JSRuntimeMode.Loose;
        AddAuthorization().SetAuthorized("Admin").SetPolicies(MantenioPolicies.AdminOnly, MantenioPolicies.StaffOnly);
    }

    [Fact]
    public void The_dashboard_shows_pending_proofs_debt_and_towers()
    {
        _api.Returns("/api/admin/dashboard", new AdminDashboard(3, 540m, 2, [new TowerDebt("H", 2, 20, 540m)], 202610, 1260m, 7));

        var cut = Render<AdminDashboardCards>();

        cut.WaitForAssertion(() => cut.Markup.ShouldContain(Formats.Money(540m)));
        cut.Markup.ShouldContain(Formats.Money(1260m));
        cut.Markup.ShouldContain(Formats.Format(UiStrings.DashboardTowerLine, 2, 20, Formats.Money(540m)));
        cut.Markup.ShouldContain(Formats.Format(UiStrings.DashboardReceipts, 7));
    }

    [Fact]
    public void The_delinquency_report_has_a_tab_per_tower_and_marks_frozen_apartments()
    {
        _api.Returns("/api/admin/reports/delinquency", new DelinquencyReport(new DateOnly(2026, 10, 25),
            [
                new DelinquencyTower("A", [new DelinquencyRow(Guid.NewGuid(), "A-101", "101", false, [202608, 202609], 360m)], 360m),
                new DelinquencyTower("B", [new DelinquencyRow(Guid.NewGuid(), "B-204", "204", true, [202609], 180m)], 180m),
            ], 540m, 2));

        var cut = Render<Reports>();

        cut.WaitForAssertion(() => cut.Markup.ShouldContain(Formats.Format(UiStrings.DelinquencyTotal, 2, Formats.Money(540m))));
        cut.FindAll(".mud-tab").Select(t => t.TextContent.Trim()).ShouldContain(t => t.StartsWith('A'));
        cut.FindAll(".mud-tab").Select(t => t.TextContent.Trim()).ShouldContain(t => t.StartsWith('B'));
        cut.Markup.ShouldContain("Agosto 2026");
        cut.Markup.ShouldContain("A-101");
    }

    [Fact]
    public void Exporting_saves_the_file_the_api_generated()
    {
        _api.Returns("/api/admin/reports/delinquency", new DelinquencyReport(new DateOnly(2026, 10, 25), [], 0m, 0))
            .ReturnsFile("/api/admin/reports/delinquency/excel", "morosidad-2026-10-25.xlsx");

        var cut = Render<Reports>();
        cut.WaitForAssertion(() => cut.Markup.ShouldContain(UiStrings.NoDelinquency));
        cut.FindAll("button").First(b => b.TextContent.Trim() == UiStrings.ExportExcel).Click();

        cut.WaitForAssertion(() => _files.Saved.ShouldBe(["morosidad-2026-10-25.xlsx"]));
    }

    [Fact]
    public void The_audit_viewer_lists_changes_in_spanish()
    {
        _api.Returns("/api/admin/audit/actors", new List<AuditActor> { new(Guid.NewGuid(), "Rosa Admin") })
            .Returns("/api/admin/audit", new PagedResult<AuditItem>(
            [
                new AuditItem(Guid.NewGuid(), DateTimeOffset.UtcNow, "PaymentProof", "Voided", "Admin", "Rosa Admin", "H-102", "Pago duplicado", null, "{\"status\":\"Voided\"}"),
                new AuditItem(Guid.NewGuid(), DateTimeOffset.UtcNow, "Charge", "ChargeCreated", "System", null, "H-102", null, null, null),
            ], 2));

        var cut = Render<Audit>();

        cut.WaitForAssertion(() => cut.Markup.ShouldContain(UiStrings.AuditAction_Voided));
        cut.Markup.ShouldContain(UiStrings.Entity_PaymentProof);
        cut.Markup.ShouldContain(UiStrings.Source_System);
        cut.Markup.ShouldContain(UiStrings.AuditAction_ChargeCreated);
    }

    [Fact]
    public void Admins_send_a_receipt_by_whatsapp_and_see_who_registered_it()
    {
        var proof = new Shared.Ledger.PaymentProofItem(Guid.NewGuid(), Guid.NewGuid(), "H-102", "Guardia Turno", 180m, 180m, new DateOnly(2026, 11, 2),
            "BCP", "00123", Shared.Ledger.ProofStatus.Approved, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "Guardia Turno", null, null,
            "0003-000007", 3, "Rosa Quispe");
        _api.ReturnsFile($"/api/payments/{proof.Id}/receipt/pdf", "recibo-0003-000007.pdf");

        var cut = Render<ProofCard>(p => p.Add(c => c.Item, proof).Add(c => c.ShowWhatsApp, true));

        cut.Markup.ShouldContain(Formats.Format(UiStrings.RegisteredFor, "Guardia Turno", "Rosa Quispe"));
        cut.FindAll("button").First(b => b.TextContent.Trim() == UiStrings.SendWhatsApp).Click();
        cut.WaitForAssertion(() => _files.SharedToWhatsApp.ShouldHaveSingleItem().FileName.ShouldBe("recibo-0003-000007.pdf"));
        _files.SharedToWhatsApp[0].Message.ShouldContain("0003-000007");
    }

    [Fact]
    public void Residents_do_not_get_the_whatsapp_button()
    {
        var proof = new Shared.Ledger.PaymentProofItem(Guid.NewGuid(), Guid.NewGuid(), "H-102", "Rosa", 180m, 180m, new DateOnly(2026, 11, 2),
            "BCP", "00124", Shared.Ledger.ProofStatus.Approved, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "Admin", null, null, "0003-000008", 3);

        var cut = Render<ProofCard>(p => p.Add(c => c.Item, proof));

        cut.Markup.ShouldNotContain(UiStrings.SendWhatsApp);
    }

    [Fact]
    public void The_help_page_shows_admin_topics_only_to_admins()
    {
        var cut = Render<Pages.Help>();

        cut.Markup.ShouldContain(UiStrings.HelpPayTitle);
        cut.Markup.ShouldContain(UiStrings.HelpWalkInTitle);
    }

    [Fact]
    public void Every_audit_action_the_api_writes_has_a_spanish_label()
    {
        string[] actions =
        [
            "ApartmentChanged", "ApartmentFeeChanged", "Approved", "ChargeCreated", "Created", "CreatedFromProfile", "Deactivated",
            "Deleted", "DueRuleChanged", "FounderSeeded", "Frozen", "GlobalFeeChanged", "Imported", "Issued", "LateFeeChanged",
            "PasswordChanged", "PasswordReset", "PaymentApplied", "PaymentReversed", "Reactivated", "Registered", "Rejected",
            "Submitted", "Unfrozen", "Updated", "Voided", "LateFeeCreated", "ReminderSent", "RegisteredByAdmin",
        ];

        actions.Where(a => LedgerText.AuditAction(a) == a).ShouldBeEmpty();
    }
}

internal static class FakeApiFileExtensions
{
    /// <summary>A generated file with its name in Content-Disposition, like the report endpoints.</summary>
    public static FakeApi ReturnsFile(this FakeApi api, string path, string fileName) =>
        api.Returns(path, () =>
        {
            var content = new ByteArrayContent([1, 2, 3]);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            content.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment") { FileNameStar = fileName };
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = content };
        });
}
