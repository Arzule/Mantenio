using System.Net;
using System.Net.Http.Json;
using Mantenio.Api.Tests.Infrastructure;
using Mantenio.Domain.Audit;
using Mantenio.Shared.Auth;
using Mantenio.Shared.Errors;
using Mantenio.Shared.Ledger;
using Mantenio.Shared.Notifications;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace Mantenio.Api.Tests.Ledger;

/// <summary>The ledger scenarios of docs/context.md §10. Each test works on its own apartment.</summary>
public sealed class LedgerTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Operation() => Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();

    private async Task<(HttpClient Client, Guid ApartmentId)> ResidentAsync(string email, string apartmentCode)
    {
        var client = factory.CreateClient(await factory.RegisterAsync(AccountRole.Resident, email, apartmentCode));
        return (client, (await client.ApartmentAsync(apartmentCode)).Id);
    }

    private async Task<HttpClient> FounderAsync() => factory.CreateClient(await factory.SignInFounderAsync());

    [Fact]
    public async Task An_exact_payment_pays_the_month_and_issues_a_receipt()
    {
        var (resident, apartmentId) = await ResidentAsync("l1@test.local", "B-101");
        using var admin = await FounderAsync();
        await factory.AddChargeAsync(apartmentId, 202611, 180m);

        var proof = await resident.SubmitProofAsync(180m, "00 12-34 " + Operation());
        proof.Status.ShouldBe(ProofStatus.Pending);
        proof.ApartmentCode.ShouldBe("B-101");
        (await admin.GetJsonAsync<List<PaymentProofItem>>(factory, "/api/admin/payments?status=Pending")).ShouldContain(p => p.Id == proof.Id);

        var approved = await admin.ApprovedAsync(proof);

        approved.Status.ShouldBe(ProofStatus.Approved);
        approved.ReceiptNumber.ShouldNotBeNull().ShouldMatch(@"^\d{4}-\d{6}$");
        approved.ReviewedByName.ShouldBe("Fiorella Fundadora");
        var statement = await resident.StatementAsync(factory, apartmentId);
        statement.Charge(202611).Status.ShouldBe(DebtStatus.Paid);
        statement.Credit.ShouldBe(0m);
        statement.TotalOutstanding.ShouldBe(0m);

        // Receipt PDF: stored, linked and emailed; the resident is told in the app.
        var link = await resident.GetJsonAsync<FileLink>(factory, $"/api/payments/{proof.Id}/receipt");
        var pdf = await resident.GetByteArrayAsync(new Uri(link.Url.PathAndQuery, UriKind.Relative), Ct);
        pdf[..4].ShouldBe("%PDF"u8.ToArray());
        var email = factory.Emails.To("l1@test.local").Last();
        email.Subject.ShouldContain(approved.ReceiptNumber);
        email.Attachments.ShouldNotBeNull().ShouldHaveSingleItem().ContentType.ShouldBe("application/pdf");
        var notices = await resident.GetJsonAsync<NotificationPage>(factory, "/api/notifications");
        notices.Items.ShouldContain(n => n.Kind == NotificationKind.PaymentApproved);
        (await factory.WithDbAsync(db => db.AuditEvents.CountAsync(e => e.ApartmentId == apartmentId && e.Source == AuditSource.Admin, Ct)))
            .ShouldBeGreaterThanOrEqualTo(3); // approved, payment applied, receipt issued
    }

    [Fact]
    public async Task A_partial_payment_leaves_the_month_partially_paid_until_a_second_payment()
    {
        var (resident, apartmentId) = await ResidentAsync("l2@test.local", "B-102");
        using var admin = await FounderAsync();
        await factory.AddChargeAsync(apartmentId, 202611, 180m);

        await admin.ApprovedAsync(await resident.SubmitProofAsync(100m, Operation()));
        var partial = (await resident.StatementAsync(factory, apartmentId)).Charge(202611);
        partial.Status.ShouldBe(DebtStatus.PartiallyPaid);
        partial.Paid.ShouldBe(100m);

        await admin.ApprovedAsync(await resident.SubmitProofAsync(80m, Operation()));
        (await resident.StatementAsync(factory, apartmentId)).Charge(202611).Status.ShouldBe(DebtStatus.Paid);
    }

    [Fact]
    public async Task A_payment_for_three_months_pays_the_oldest_first_and_the_rest_is_credit()
    {
        var (resident, apartmentId) = await ResidentAsync("l3@test.local", "B-103");
        using var admin = await FounderAsync();
        await factory.AddChargeAsync(apartmentId, 202701, 180m);
        await factory.AddChargeAsync(apartmentId, 202611, 180m);
        await factory.AddChargeAsync(apartmentId, 202612, 180m);
        await factory.AddChargeAsync(apartmentId, 202702, 180m);

        // The admin confirms what the screenshot really says (the resident typed 550).
        await admin.ApprovedAsync(await resident.SubmitProofAsync(550m, Operation()), 560m);

        var statement = await resident.StatementAsync(factory, apartmentId);
        statement.Charge(202611).Status.ShouldBe(DebtStatus.Paid);
        statement.Charge(202612).Status.ShouldBe(DebtStatus.Paid);
        statement.Charge(202701).Status.ShouldBe(DebtStatus.Paid);
        statement.Charge(202702).Paid.ShouldBe(20m);
        statement.Credit.ShouldBe(0m);
        statement.TotalOutstanding.ShouldBe(160m);
    }

    [Fact]
    public async Task Two_residents_of_the_same_apartment_paying_the_same_month_leaves_credit()
    {
        var (first, apartmentId) = await ResidentAsync("l5a@test.local", "B-104");
        var (second, _) = await ResidentAsync("l5b@test.local", "B-104");
        using var admin = await FounderAsync();
        await factory.AddChargeAsync(apartmentId, 202611, 180m);

        await admin.ApprovedAsync(await first.SubmitProofAsync(180m, Operation()));
        await admin.ApprovedAsync(await second.SubmitProofAsync(180m, Operation(), "Interbank"));

        var statement = await second.StatementAsync(factory, apartmentId);
        statement.Charge(202611).Status.ShouldBe(DebtStatus.Paid);
        statement.Credit.ShouldBe(180m);
        statement.Payments.Count.ShouldBe(2);
    }

    [Fact]
    public async Task The_same_bank_operation_twice_is_blocked_unless_the_first_was_rejected()
    {
        var (resident, _) = await ResidentAsync("l6@test.local", "B-201");
        using var admin = await FounderAsync();
        var operation = Operation();
        var first = await resident.SubmitProofAsync(180m, operation);

        var duplicate = await resident.PostProofAsync(180m, " " + operation[..4] + "-" + operation[4..].ToLowerInvariant());
        await duplicate.ShouldBeProblemAsync(HttpStatusCode.Conflict, ErrorCodes.DuplicateOperationNumber);

        // Same number at another bank is another operation.
        await resident.SubmitProofAsync(180m, operation, "Interbank");

        var reject = await admin.PostAsJsonAsync($"/api/admin/payments/{first.Id}/reject", new RejectProofRequest("La captura no se lee", first.RowVersion), Ct);
        await reject.ShouldBeAsync(HttpStatusCode.OK);
        await resident.SubmitProofAsync(180m, operation);
    }

    [Fact]
    public async Task A_rejected_proof_does_not_change_the_debt_and_the_resident_is_told_why()
    {
        var (resident, apartmentId) = await ResidentAsync("l7@test.local", "B-202");
        using var admin = await FounderAsync();
        await factory.AddChargeAsync(apartmentId, 202611, 180m);
        var proof = await resident.SubmitProofAsync(180m, Operation());

        var response = await admin.PostAsJsonAsync($"/api/admin/payments/{proof.Id}/reject", new RejectProofRequest("Monto ilegible", proof.RowVersion), Ct);

        await response.ShouldBeAsync(HttpStatusCode.OK);
        var statement = await resident.StatementAsync(factory, apartmentId);
        statement.Charge(202611).Status.ShouldBe(DebtStatus.Pending);
        statement.Payments.ShouldHaveSingleItem().RejectionReason.ShouldBe("Monto ilegible");
        var notice = (await resident.GetJsonAsync<NotificationPage>(factory, "/api/notifications")).Items
            .Single(n => n.Kind == NotificationKind.PaymentRejected);
        notice.Data.GetProperty("reason").GetString().ShouldBe("Monto ilegible");

        var again = await admin.ApproveAsync(proof with { RowVersion = (await resident.GetJsonAsync<List<PaymentProofItem>>(factory, "/api/payments/mine")).Single().RowVersion });
        await again.ShouldBeProblemAsync(HttpStatusCode.Conflict, ErrorCodes.ProofNotPending);
    }

    [Fact]
    public async Task Voiding_reopens_the_month_and_the_timeline_shows_approval_void_and_new_payment()
    {
        var (resident, apartmentId) = await ResidentAsync("l8@test.local", "B-203");
        using var admin = await FounderAsync();
        var chargeId = await factory.AddChargeAsync(apartmentId, 202611, 180m);
        var approved = await admin.ApprovedAsync(await resident.SubmitProofAsync(180m, Operation()));

        var voided = await admin.VoidedAsync(approved, "Pagó al departamento equivocado");

        voided.Status.ShouldBe(ProofStatus.Voided);
        voided.VoidReason.ShouldBe("Pagó al departamento equivocado");
        voided.ReceiptNumber.ShouldBe(approved.ReceiptNumber);
        (await resident.StatementAsync(factory, apartmentId)).Charge(202611).Status.ShouldBe(DebtStatus.Pending);
        var receipt = await factory.WithDbAsync(db => db.Receipts.SingleAsync(r => r.PaymentProofId == approved.Id, Ct));
        receipt.Status.ShouldBe(Domain.Ledger.ReceiptStatus.Voided);
        receipt.PdfObjectKey.ShouldEndWith("-anulado.pdf");
        File.Exists(Path.Combine(factory.StoragePath, receipt.PdfObjectKey)).ShouldBeTrue();
        (await resident.GetJsonAsync<NotificationPage>(factory, "/api/notifications")).Items.ShouldContain(n => n.Kind == NotificationKind.PaymentVoided);

        await admin.ApprovedAsync(await resident.SubmitProofAsync(180m, Operation()));

        var timeline = await admin.GetJsonAsync<List<TimelineEntry>>(factory, $"/api/admin/charges/{chargeId}/timeline");
        timeline.Select(t => t.Action).ShouldBe(["PaymentApplied", "PaymentReversed", "PaymentApplied"]);
        timeline[1].Reason.ShouldBe("Pagó al departamento equivocado");
        timeline[1].ActorName.ShouldBe("Fiorella Fundadora");
    }

    [Fact]
    public async Task Voiding_a_payment_that_covered_several_months_reapplies_the_other_credit()
    {
        var (resident, apartmentId) = await ResidentAsync("l9@test.local", "B-204");
        using var admin = await FounderAsync();
        await factory.AddChargeAsync(apartmentId, 202611, 180m);
        await factory.AddChargeAsync(apartmentId, 202612, 180m);
        var big = await admin.ApprovedAsync(await resident.SubmitProofAsync(360m, Operation()));
        await admin.ApprovedAsync(await resident.SubmitProofAsync(200m, Operation())); // all credit

        await admin.VoidedAsync(big);

        var statement = await resident.StatementAsync(factory, apartmentId);
        statement.Charge(202611).Status.ShouldBe(DebtStatus.Paid);
        statement.Charge(202612).Paid.ShouldBe(20m);
        statement.Credit.ShouldBe(0m);
        (await factory.WithDbAsync(db => db.PaymentAllocations.CountAsync(a => a.PaymentProofId == big.Id && a.ReversedAt != null, Ct))).ShouldBe(2);
    }

    [Fact]
    public async Task Two_admins_approving_the_same_proof_one_wins_and_the_other_gets_a_conflict()
    {
        var (resident, apartmentId) = await ResidentAsync("l16@test.local", "B-301");
        await factory.AddChargeAsync(apartmentId, 202611, 180m);
        var proof = await resident.SubmitProofAsync(180m, Operation());
        using var first = await FounderAsync();
        using var second = await FounderAsync();

        var responses = await Task.WhenAll(first.ApproveAsync(proof), second.ApproveAsync(proof));

        responses.Count(r => r.StatusCode == HttpStatusCode.OK).ShouldBe(1);
        await responses.Single(r => r.StatusCode != HttpStatusCode.OK).ShouldBeProblemAsync(HttpStatusCode.Conflict, ErrorCodes.Conflict);
        (await factory.WithDbAsync(db => db.Receipts.CountAsync(r => r.PaymentProofId == proof.Id, Ct))).ShouldBe(1);
        (await resident.StatementAsync(factory, apartmentId)).Credit.ShouldBe(0m);
    }

    [Fact]
    public async Task Twenty_concurrent_approvals_get_consecutive_receipt_numbers()
    {
        using var admin = await FounderAsync();
        await admin.GetJsonAsync<object>(factory, "/api/admin/settings");
        var proofs = new List<PaymentProofItem>();
        for (var i = 1; i <= 20; i++)
        {
            var (resident, _) = await ResidentAsync($"l17-{i}@test.local", $"C-{(i <= 4 ? 100 + i : i <= 8 ? 200 + i - 4 : i <= 12 ? 300 + i - 8 : i <= 16 ? 400 + i - 12 : 500 + i - 16)}");
            proofs.Add(await resident.SubmitProofAsync(180m, Operation()));
        }

        var responses = await Task.WhenAll(proofs.Select(p => admin.ApproveAsync(p)));

        foreach (var response in responses)
        {
            await response.ShouldBeAsync(HttpStatusCode.OK);
        }

        var ids = proofs.Select(p => p.Id).ToList();
        var sequences = await factory.WithDbAsync(db => db.Receipts.Where(r => ids.Contains(r.PaymentProofId)).Select(r => r.Sequence).ToListAsync(Ct));
        sequences.Distinct().Count().ShouldBe(20);
        (sequences.Max() - sequences.Min()).ShouldBe(19);
    }

    [Fact]
    public async Task An_admin_cannot_review_proofs_of_their_own_apartment()
    {
        var (resident, _) = await ResidentAsync("l18r@test.local", "D-101");
        var adminTokens = await factory.RegisterAsync(AccountRole.Admin, "l18a@test.local", "D-101");
        using var admin = factory.CreateClient(adminTokens);
        var proof = await resident.SubmitProofAsync(180m, Operation());

        await (await admin.ApproveAsync(proof)).ShouldBeProblemAsync(HttpStatusCode.Forbidden, ErrorCodes.OwnApartmentReview);
        var reject = await admin.PostAsJsonAsync($"/api/admin/payments/{proof.Id}/reject", new RejectProofRequest("No corresponde", proof.RowVersion), Ct);
        await reject.ShouldBeProblemAsync(HttpStatusCode.Forbidden, ErrorCodes.OwnApartmentReview);

        using var founder = await FounderAsync();
        await founder.ApprovedAsync(proof);
    }

    [Fact]
    public async Task Residents_only_see_their_own_apartment()
    {
        var (owner, apartmentId) = await ResidentAsync("idor1@test.local", "D-102");
        var (other, _) = await ResidentAsync("idor2@test.local", "D-103");
        var proof = await owner.SubmitProofAsync(180m, Operation());

        (await other.GetAsync(new Uri($"/api/apartments/{apartmentId}/statement", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await other.GetAsync(new Uri($"/api/payments/{proof.Id}/image", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await other.GetJsonAsync<List<PaymentProofItem>>(factory, "/api/payments/mine")).ShouldBeEmpty();
        (await other.GetAsync(new Uri("/api/admin/payments", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        var link = await owner.GetJsonAsync<FileLink>(factory, $"/api/payments/{proof.Id}/image");
        var jpeg = await owner.GetByteArrayAsync(new Uri(link.Url.PathAndQuery, UriKind.Relative), Ct);
        jpeg[..2].ShouldBe(new byte[] { 0xFF, 0xD8 });
        var tampered = link.Url.PathAndQuery.Replace("sig=", "sig=00", StringComparison.Ordinal);
        (await owner.GetAsync(new Uri(tampered, UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Files_that_are_not_images_are_rejected()
    {
        var (resident, _) = await ResidentAsync("img@test.local", "D-104");

        var response = await resident.PostProofAsync(180m, Operation(), image: "<html>no soy una imagen</html>"u8.ToArray());

        await response.ShouldBeProblemAsync(HttpStatusCode.BadRequest, ErrorCodes.InvalidImage);
    }
}
