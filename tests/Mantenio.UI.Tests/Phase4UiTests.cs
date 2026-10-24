using System.Security.Claims;
using System.Text.Json;
using Bunit;
using Mantenio.Client;
using Mantenio.Shared.Auth;
using Mantenio.Shared.Ledger;
using Mantenio.Shared.Notifications;
using Mantenio.UI.Components;
using Mantenio.UI.Pages;
using Mantenio.UI.Pages.Admin;
using Mantenio.UI.Platform;
using Mantenio.UI.Resources;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using Shouldly;

namespace Mantenio.UI.Tests;

public sealed class Phase4UiTests : BunitContext
{
    private static readonly Guid ApartmentId = Guid.NewGuid();
    private readonly FakeApi _api = new();

    public Phase4UiTests()
    {
        Services.AddMudServices();
        Services.AddSingleton<IHttpClientFactory>(_api);
        Services.AddSingleton<MantenioApi>();
        Services.AddSingleton<IFileOpener, RecordingFileOpener>();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private static PaymentProofItem Proof(ProofStatus status, string? receipt = null, string? rejection = null) =>
        new(Guid.NewGuid(), ApartmentId, "H-102", "Rosa Quispe", 180m, status == ProofStatus.Approved ? 180m : null,
            new DateOnly(2026, 11, 3), "BCP", "00123456", status, DateTimeOffset.UtcNow, null, null, rejection, null, receipt, 7);

    [Fact]
    public void Residents_see_their_debt_charges_and_proofs()
    {
        AddAuthorization().SetAuthorized("Rosa").SetClaims(new Claim(MantenioClaims.ApartmentId, ApartmentId.ToString()));
        _api.Returns($"/api/apartments/{ApartmentId}/statement", new AccountStatement(
            ApartmentId, "H-102", false,
            [
                new ChargeItem(Guid.NewGuid(), 202612, "Maintenance", 180m, 80m, DebtStatus.PartiallyPaid, new DateOnly(2027, 1, 1), false),
                new ChargeItem(Guid.NewGuid(), 202611, "Maintenance", 180m, 0m, DebtStatus.Pending, new DateOnly(2026, 12, 1), true),
            ],
            [Proof(ProofStatus.Approved, "0003-000001"), Proof(ProofStatus.Rejected, rejection: "Captura borrosa")],
            0m, 180m, 280m));

        var cut = Render<Payments>();

        cut.WaitForAssertion(() => cut.Markup.ShouldContain("Noviembre 2026"));
        cut.Markup.ShouldContain(UiStrings.Overdue);
        cut.Markup.ShouldContain(Formats.Money(280m));
        cut.Markup.ShouldContain(Formats.Format(UiStrings.PaidOf, Formats.Money(80m), Formats.Money(180m)));
        cut.Markup.ShouldContain(UiStrings.UploadProof);
        cut.Markup.ShouldNotContain(UiStrings.ChargeHistory); // timeline is for admins
    }

    [Fact]
    public void Users_without_apartment_are_told_to_ask_for_one()
    {
        AddAuthorization().SetAuthorized("Ana");

        var cut = Render<Payments>();

        cut.Markup.ShouldContain(UiStrings.PaymentsNoApartment);
        _api.Calls.ShouldBeEmpty();
    }

    [Fact]
    public void A_proof_card_shows_the_status_reason_and_receipt()
    {
        AddAuthorization().SetAuthorized("Rosa");

        var rejected = Render<ProofCard>(p => p.Add(c => c.Item, Proof(ProofStatus.Rejected, rejection: "Captura borrosa")));
        var approved = Render<ProofCard>(p => p.Add(c => c.Item, Proof(ProofStatus.Approved, "0003-000001")));

        rejected.Markup.ShouldContain(UiStrings.ProofStatus_Rejected);
        rejected.Markup.ShouldContain("Captura borrosa");
        approved.Markup.ShouldContain(Formats.Format(UiStrings.ReceiptNumber, "0003-000001"));
    }

    [Fact]
    public void The_review_queue_offers_approve_and_reject_with_the_screenshot()
    {
        AddAuthorization().SetAuthorized("Admin").SetPolicies(MantenioPolicies.AdminOnly);
        var proof = Proof(ProofStatus.Pending);
        _api.Returns("/api/admin/payments", new List<PaymentProofItem> { proof })
            .Returns($"/api/payments/{proof.Id}/image", new FileLink(new Uri("https://files.test/proof.jpg")));

        var cut = Render<PaymentReview>();

        cut.WaitForAssertion(() => cut.Find("img[src='https://files.test/proof.jpg']"));
        cut.Markup.ShouldContain("H-102");
        cut.FindAll("button").Select(b => b.TextContent.Trim()).ShouldContain(UiStrings.Approve);
        cut.FindAll("button").Select(b => b.TextContent.Trim()).ShouldContain(UiStrings.Reject);
    }

    [Fact]
    public void Payment_notices_say_the_amount_receipt_and_reason()
    {
        var approved = Notice(NotificationKind.PaymentApproved, new { paymentProofId = Guid.NewGuid(), amount = 180m, receiptNumber = "0003-000004", reason = (string?)null });
        var rejected = Notice(NotificationKind.PaymentRejected, new { paymentProofId = Guid.NewGuid(), amount = 180m, receiptNumber = (string?)null, reason = "Captura borrosa" });

        NoticeText.Title(approved).ShouldBe(UiStrings.NoticePaymentApprovedTitle);
        NoticeText.Body(approved).ShouldBe($"Tu pago de {Formats.Money(180m)} fue aprobado. Recibo 0003-000004.");
        NoticeText.Body(rejected).ShouldContain("Motivo: Captura borrosa.");
    }

    [Fact]
    public void Every_ledger_state_and_import_problem_has_a_spanish_label()
    {
        foreach (var status in Enum.GetValues<ProofStatus>())
        {
            LedgerText.Proof(status).ShouldNotStartWith("ProofStatus_");
        }

        foreach (var status in Enum.GetValues<DebtStatus>())
        {
            LedgerText.Debt(status).ShouldNotStartWith("DebtStatus_");
        }

        foreach (var type in new[] { "Opening", "Maintenance", "LateFee" })
        {
            LedgerText.ChargeType(type).ShouldNotStartWith("ChargeType_");
        }

        foreach (var problem in new[] { "columns", "apartment", "period", "amount", "duplicate" })
        {
            LedgerText.OpeningError($"3:{problem}").ShouldStartWith("Línea 3");
        }
    }

    private static NotificationItem Notice(NotificationKind kind, object data) =>
        new(Guid.NewGuid(), kind, JsonSerializer.SerializeToElement(data, JsonSerializerOptions.Web), false, DateTimeOffset.UtcNow, null, null);

    private sealed class RecordingFileOpener : IFileOpener
    {
        public Task OpenAsync(Uri url) => Task.CompletedTask;
    }
}
