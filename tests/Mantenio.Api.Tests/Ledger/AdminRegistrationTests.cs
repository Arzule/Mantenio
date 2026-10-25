using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
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

/// <summary>An admin (the guard on duty) registers the voucher a resident brought in person; it is approved at once.</summary>
public sealed class AdminRegistrationTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Operation() => Guid.NewGuid().ToString("N")[..12];

    private static Task<HttpResponseMessage> RegisterAsync(
        HttpClient admin, Guid apartmentId, string payer, decimal amount, string operation, bool withImage = true)
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
        if (withImage)
        {
            var file = new ByteArrayContent(LedgerFlows.Png());
            file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            form.Add(file, "image", "voucher.png");
        }

        return admin.PostAsync(new Uri("/api/admin/payments/register", UriKind.Relative), form, Ct);
    }

    [Fact]
    public async Task A_registered_voucher_is_approved_at_once_and_the_receipt_goes_to_the_residents()
    {
        var residentTokens = await factory.RegisterAsync(AccountRole.Resident, "walkin@test.local", "E-301");
        using var resident = factory.CreateClient(residentTokens);
        using var guard = factory.CreateClient(await factory.SignInFounderAsync());
        var apartmentId = (await guard.ApartmentAsync("E-301")).Id;
        await factory.AddChargeAsync(apartmentId, 202611, 180m);
        (await guard.GetJsonAsync<List<string>>(factory, $"/api/admin/apartments/{apartmentId}/residents")).ShouldBe(["Nombre Apellido"]);

        var response = await RegisterAsync(guard, apartmentId, "Rosa Quispe", 180m, Operation());

        await response.ShouldBeAsync(HttpStatusCode.OK);
        var item = (await response.Content.ReadFromJsonAsync<PaymentProofItem>(Ct))!;
        item.Status.ShouldBe(ProofStatus.Approved);
        item.RegisteredByAdmin.ShouldBeTrue();
        item.PayerName.ShouldBe("Rosa Quispe");
        item.SubmittedByName.ShouldBe("Fiorella Fundadora");
        item.ReceiptNumber.ShouldNotBeNull();
        (await resident.StatementAsync(factory, apartmentId)).Charge(202611).Status.ShouldBe(DebtStatus.Paid);

        // The resident sees it, gets the notice and the receipt by email; the admin can download the PDF to share it.
        (await resident.GetJsonAsync<List<PaymentProofItem>>(factory, "/api/payments/mine")).ShouldContain(p => p.Id == item.Id);
        (await resident.GetJsonAsync<NotificationPage>(factory, "/api/notifications")).Items.ShouldContain(n => n.Kind == NotificationKind.PaymentApproved);
        factory.Emails.To("walkin@test.local").ShouldContain(m => m.Subject.Contains(item.ReceiptNumber, StringComparison.Ordinal) && m.Attachments != null);
        var pdf = await guard.GetAsync(new Uri($"/api/payments/{item.Id}/receipt/pdf", UriKind.Relative), Ct);
        await pdf.ShouldBeAsync(HttpStatusCode.OK);
        pdf.Content.Headers.ContentDisposition!.FileNameStar.ShouldBe($"recibo-{item.ReceiptNumber}.pdf");
        var actions = await factory.WithDbAsync(db => db.AuditEvents.Where(e => e.EntityId == item.Id.ToString()).Select(e => e.Action).ToListAsync(Ct));
        actions.ShouldBe(["RegisteredByAdmin", "Approved"], ignoreOrder: true);
        (await factory.WithDbAsync(db => db.AuditEvents.Where(e => e.EntityId == item.Id.ToString()).AllAsync(e => e.Source == AuditSource.Admin, Ct)))
            .ShouldBeTrue();
    }

    [Fact]
    public async Task The_photo_is_required_and_the_apartment_must_exist()
    {
        using var guard = factory.CreateClient(await factory.SignInFounderAsync());
        var apartmentId = (await guard.ApartmentAsync("E-302")).Id;

        await (await RegisterAsync(guard, apartmentId, "Ana", 180m, Operation(), withImage: false))
            .ShouldBeProblemAsync(HttpStatusCode.BadRequest, ErrorCodes.InvalidImage);
        await (await RegisterAsync(guard, Guid.NewGuid(), "Ana", 180m, Operation()))
            .ShouldBeProblemAsync(HttpStatusCode.BadRequest, ErrorCodes.ApartmentNotFound);
        await (await RegisterAsync(guard, apartmentId, " ", 180m, Operation()))
            .ShouldBeProblemAsync(HttpStatusCode.BadRequest, ErrorCodes.ValidationFailed);
    }

    [Fact]
    public async Task Duplicates_and_the_admins_own_apartment_are_blocked_and_residents_cannot_register()
    {
        using var guard = factory.CreateClient(await factory.SignInFounderAsync());
        var apartmentId = (await guard.ApartmentAsync("E-303")).Id;
        var operation = Operation();
        await (await RegisterAsync(guard, apartmentId, "Luis", 180m, operation)).ShouldBeAsync(HttpStatusCode.OK);
        await (await RegisterAsync(guard, apartmentId, "Luis", 180m, operation))
            .ShouldBeProblemAsync(HttpStatusCode.Conflict, ErrorCodes.DuplicateOperationNumber);

        using var neighborAdmin = factory.CreateClient(await factory.RegisterAsync(AccountRole.Admin, "guard-e304@test.local", "E-304"));
        var own = (await guard.ApartmentAsync("E-304")).Id;
        await (await RegisterAsync(neighborAdmin, own, "Yo mismo", 180m, Operation()))
            .ShouldBeProblemAsync(HttpStatusCode.Forbidden, ErrorCodes.OwnApartmentReview);

        using var resident = factory.CreateClient(await factory.RegisterAsync(AccountRole.Resident, "walkin-r@test.local", "E-401"));
        (await RegisterAsync(resident, apartmentId, "X", 1m, Operation())).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }
}
