using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using ClosedXML.Excel;
using Mantenio.Api.Infrastructure.Time;
using Mantenio.Api.Jobs;
using Mantenio.Api.Tests.Infrastructure;
using Mantenio.Domain.Audit;
using Mantenio.Domain.Ledger;
using Mantenio.Shared.Auth;
using Mantenio.Shared.Errors;
using Mantenio.Shared.Ledger;
using Mantenio.Shared.Reports;
using Mantenio.Shared.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Mantenio.Api.Tests.Ledger;

/// <summary>Payments loaded from the paper records, the file retention, the period report and the audit "people" filter.</summary>
public sealed class HistoricalPaymentTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private DateOnly Today => factory.Services.GetRequiredService<CondoClock>().Today;

    private async Task<HttpClient> FounderAsync() => factory.CreateClient(await factory.SignInFounderAsync());

    private static async Task<HttpResponseMessage> PostAsync(
        HttpClient admin, string path, decimal amount, DateOnly date, Guid? apartmentId = null, string operation = "", string paperReceipt = "",
        byte[]? image = null)
    {
        using var form = new MultipartFormDataContent
        {
            { new StringContent(amount.ToString(CultureInfo.InvariantCulture)), "amount" },
            { new StringContent(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)), "operationDate" },
            { new StringContent(""), "payerName" },
            { new StringContent(operation.Length > 0 ? "BCP" : ""), "bankName" },
            { new StringContent(operation), "operationNumber" },
            { new StringContent(paperReceipt), "paperReceiptNumber" },
        };
        if (apartmentId is { } id)
        {
            form.Add(new StringContent(id.ToString()), "apartmentId");
        }

        if (image is not null)
        {
            var file = new ByteArrayContent(image);
            file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            form.Add(file, "image", "voucher.png");
        }

        return await admin.PostAsync(new Uri(path, UriKind.Relative), form, Ct);
    }

    private static async Task<PaymentProofItem> OkAsync(HttpResponseMessage response)
    {
        await response.ShouldBeAsync(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<PaymentProofItem>(Ct))!;
    }

    private Task<string> ImageKeyAsync(Guid proofId) =>
        factory.WithDbAsync(db => db.PaymentProofs.Where(p => p.Id == proofId).Select(p => p.ImageObjectKey).SingleAsync(Ct));

    [Fact]
    public async Task Historical_payments_are_kept_for_the_record_without_changing_the_debt()
    {
        using var admin = await FounderAsync();
        var apartment = await admin.ApartmentAsync("C-201");
        await factory.AddChargeAsync(apartment.Id, 202611, 180m);

        var proof = await OkAsync(await PostAsync(admin, "/api/admin/payments/historical", 180m, Today.AddYears(-3), apartment.Id,
            paperReceipt: "0002-004543"));

        proof.Status.ShouldBe(ProofStatus.Historical);
        proof.HasImage.ShouldBeFalse();
        proof.PaperReceiptNumber.ShouldBe("0002-004543");
        var statement = await admin.StatementAsync(factory, apartment.Id);
        statement.TotalOutstanding.ShouldBe(180m);
        statement.Credit.ShouldBe(0m);
        statement.Payments.ShouldContain(p => p.Id == proof.Id);
        (await admin.GetJsonAsync<List<PaymentProofItem>>(factory, "/api/admin/payments?status=Historical")).ShouldContain(p => p.Id == proof.Id);
    }

    [Fact]
    public async Task The_photo_can_be_attached_later_and_a_new_one_replaces_it()
    {
        using var admin = await FounderAsync();
        var apartment = await admin.ApartmentAsync("C-202");
        var proof = await OkAsync(await PostAsync(admin, "/api/admin/payments/historical", 180m, Today.AddYears(-2), apartment.Id));
        var path = $"/api/admin/payments/{proof.Id}/historical";

        var withPhoto = await OkAsync(await PostAsync(admin, path, 185m, Today.AddYears(-2), operation: "7788", image: LedgerFlows.Png()));
        withPhoto.HasImage.ShouldBeTrue();
        withPhoto.DeclaredAmount.ShouldBe(185m);
        var first = await ImageKeyAsync(proof.Id);
        File.Exists(Path.Combine(factory.StoragePath, first)).ShouldBeTrue();

        await OkAsync(await PostAsync(admin, path, 185m, Today.AddYears(-2), operation: "7788", image: LedgerFlows.Png()));
        var second = await ImageKeyAsync(proof.Id);
        second.ShouldNotBe(first);
        File.Exists(Path.Combine(factory.StoragePath, first)).ShouldBeFalse();
        File.Exists(Path.Combine(factory.StoragePath, second)).ShouldBeTrue();
        (await factory.WithDbAsync(db => db.AuditEvents.CountAsync(e => e.EntityId == proof.Id.ToString() && e.Action == "HistoricalPhotoAttached", Ct)))
            .ShouldBe(2);
    }

    [Fact]
    public async Task Dates_beyond_the_retention_duplicates_and_residents_are_refused()
    {
        using var admin = await FounderAsync();
        var apartment = await admin.ApartmentAsync("C-203");

        (await PostAsync(admin, "/api/admin/payments/historical", 180m, Today.AddYears(-11), apartment.Id))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        await OkAsync(await PostAsync(admin, "/api/admin/payments/historical", 180m, Today.AddYears(-1), apartment.Id, operation: "555-001"));
        var duplicate = await PostAsync(admin, "/api/admin/payments/historical", 180m, Today.AddYears(-1), apartment.Id, operation: "555001");
        await duplicate.ShouldBeProblemAsync(HttpStatusCode.Conflict, ErrorCodes.DuplicateOperationNumber);

        using var resident = factory.CreateClient(await factory.RegisterAsync(AccountRole.Resident, "historical@test.local", "C-204"));
        (await PostAsync(resident, "/api/admin/payments/historical", 180m, Today.AddYears(-1), apartment.Id))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Files_older_than_ten_years_are_deleted_but_the_payment_stays()
    {
        using var admin = await FounderAsync();
        var apartment = await admin.ApartmentAsync("C-302");
        var proof = await OkAsync(await PostAsync(admin, "/api/admin/payments/historical", 180m, Today.AddYears(-2), apartment.Id, image: LedgerFlows.Png()));
        var key = await ImageKeyAsync(proof.Id);
        await factory.WithDbAsync(db => db.PaymentProofs.Where(p => p.Id == proof.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.OperationDate, Today.AddMonths(-121)), Ct));

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            (await JobCommands.CleanupAsync(scope.ServiceProvider, Ct)).ShouldContain("1 proof images");
        }

        File.Exists(Path.Combine(factory.StoragePath, key)).ShouldBeFalse();
        (await ImageKeyAsync(proof.Id)).ShouldBe("");
        (await admin.GetAsync(new Uri($"/api/payments/{proof.Id}/image", UriKind.Relative), Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task The_period_report_lists_the_charges_and_every_payment_of_those_months()
    {
        using var admin = await FounderAsync();
        var apartment = await admin.ApartmentAsync("C-301");
        var month = Domain.Common.Period.FromDate(Today.AddMonths(-1));
        await factory.AddChargeAsync(apartment.Id, month.Value, 180m);
        await OkAsync(await PostAsync(admin, "/api/admin/payments/historical", 180m, Today.AddDays(-20), apartment.Id, paperReceipt: "P-99"));

        var response = await admin.GetAsync(new Uri($"/api/admin/reports/period/excel?from={month.Value}&to={Domain.Common.Period.FromDate(Today).Value}", UriKind.Relative), Ct);
        await response.ShouldBeAsync(HttpStatusCode.OK);
        using var workbook = new XLWorkbook(await response.Content.ReadAsStreamAsync(Ct));

        workbook.Worksheets.Select(w => w.Name).ShouldContain("Cargos");
        workbook.Worksheet("Cargos").CellsUsed().Select(c => c.GetString()).ShouldContain("C-301");
        workbook.Worksheet("Pagos").CellsUsed().Select(c => c.GetString()).ShouldContain("P-99");

        (await admin.GetAsync(new Uri("/api/admin/reports/period/excel?from=202612&to=202601", UriKind.Relative), Ct))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await admin.GetAsync(new Uri("/api/admin/reports/period/excel?from=201601&to=202612", UriKind.Relative), Ct))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task The_people_filter_hides_the_scheduled_jobs()
    {
        using var admin = await FounderAsync();
        var apartment = await admin.ApartmentAsync("C-303");
        await OkAsync(await PostAsync(admin, "/api/admin/payments/historical", 180m, Today.AddYears(-1), apartment.Id));
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            await JobCommands.CleanupAsync(scope.ServiceProvider, Ct);
        }

        var all = await admin.GetJsonAsync<PagedResult<AuditItem>>(factory, "/api/admin/audit?pageSize=100");
        var people = await admin.GetJsonAsync<PagedResult<AuditItem>>(factory, "/api/admin/audit?source=People&pageSize=100");

        all.Items.ShouldContain(i => i.Source == nameof(AuditSource.System));
        people.Items.ShouldNotBeEmpty();
        people.Items.ShouldAllBe(i => i.Source != nameof(AuditSource.System));
    }
}
