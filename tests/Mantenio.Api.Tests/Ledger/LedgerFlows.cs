using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Mantenio.Api.Tests.Infrastructure;
using Mantenio.Domain.Common;
using Mantenio.Domain.Ledger;
using Mantenio.Shared.Ledger;
using SkiaSharp;

namespace Mantenio.Api.Tests.Ledger;

/// <summary>Ledger steps shared by the ledger test classes.</summary>
internal static class LedgerFlows
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A charge written straight to the database (keeps each test's apartment independent of the generation job).</summary>
    public static Task<Guid> AddChargeAsync(this ApiFactory factory, Guid apartmentId, int period, decimal amount) =>
        factory.WithDbAsync(async db =>
        {
            var charge = Domain.Ledger.Charge.Maintenance(apartmentId, Period.FromValue(period), amount, Period.FromValue(period).Next().FirstDay, factory.Time.GetUtcNow());
            db.Charges.Add(charge);
            await db.SaveChangesAsync(Ct);
            return charge.Id;
        });

    public static async Task<HttpResponseMessage> PostProofAsync(
        this HttpClient client, decimal amount, string operationNumber, string bank = "BCP", byte[]? image = null)
    {
        using var form = new MultipartFormDataContent
        {
            { new StringContent(amount.ToString(CultureInfo.InvariantCulture)), "amount" },
            { new StringContent(DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-2)).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)), "operationDate" },
            { new StringContent(bank), "bankName" },
            { new StringContent(operationNumber), "operationNumber" },
        };
        var file = new ByteArrayContent(image ?? Png());
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(file, "image", "captura.png");
        return await client.PostAsync(new Uri("/api/payments", UriKind.Relative), form, Ct);
    }

    public static async Task<PaymentProofItem> SubmitProofAsync(this HttpClient client, decimal amount, string operationNumber, string bank = "BCP")
    {
        var response = await client.PostProofAsync(amount, operationNumber, bank);
        await response.ShouldBeAsync(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<PaymentProofItem>(Ct))!;
    }

    public static Task<HttpResponseMessage> ApproveAsync(this HttpClient admin, PaymentProofItem proof, decimal? amount = null) =>
        admin.PostAsJsonAsync($"/api/admin/payments/{proof.Id}/approve", new ApproveProofRequest(amount ?? proof.DeclaredAmount, proof.RowVersion), Ct);

    public static async Task<PaymentProofItem> ApprovedAsync(this HttpClient admin, PaymentProofItem proof, decimal? amount = null)
    {
        var response = await admin.ApproveAsync(proof, amount);
        await response.ShouldBeAsync(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<PaymentProofItem>(Ct))!;
    }

    public static async Task<PaymentProofItem> VoidedAsync(this HttpClient admin, PaymentProofItem proof, string reason = "Pago devuelto")
    {
        var response = await admin.PostAsJsonAsync($"/api/admin/payments/{proof.Id}/void", new VoidProofRequest(reason), Ct);
        await response.ShouldBeAsync(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<PaymentProofItem>(Ct))!;
    }

    public static Task<AccountStatement> StatementAsync(this HttpClient client, ApiFactory factory, Guid apartmentId) =>
        client.GetJsonAsync<AccountStatement>(factory, $"/api/apartments/{apartmentId}/statement");

    public static ChargeItem Charge(this AccountStatement statement, int period) => statement.Charges.Single(c => c.Period == period);

    /// <summary>A small but real PNG (the server decodes and re-encodes it).</summary>
    public static byte[] Png()
    {
        using var bitmap = new SKBitmap(60, 40);
        bitmap.Erase(SKColors.SteelBlue);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}
