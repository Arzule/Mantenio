using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Mantenio.Api.Tests.Infrastructure;
using Mantenio.Shared.Auth;
using Mantenio.Shared.Errors;
using Mantenio.Shared.Ledger;
using Shouldly;

namespace Mantenio.Api.Tests.Ledger;

/// <summary>Reading a voucher image to pre-fill the form. Changes the shared fake scanner, so it has its own API instance.</summary>
public sealed class ProofScanTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_amount_date_and_operation_number_are_read_from_the_image()
    {
        using var resident = factory.CreateClient(await factory.RegisterAsync(AccountRole.Resident, "scan@test.local", "C-301"));
        var today = DateOnly.FromDateTime(factory.Time.GetUtcNow().AddHours(-5).DateTime);
        factory.Scanner.IsEnabled = true;
        factory.Scanner.Lines = ["¡Operación exitosa!", "S/ 180.00", $"{today.AddDays(-1):dd/MM/yyyy}", "Número de operación", "07654321"];

        var result = await ScanAsync(resident, LedgerFlows.Png());

        result.ShouldBe(new ProofScanResult(true, 180.00m, today.AddDays(-1), "07654321"));
    }

    [Fact]
    public async Task Without_the_reading_service_the_form_is_left_to_the_person()
    {
        using var admin = factory.CreateClient(await factory.SignInFounderAsync());
        factory.Scanner.IsEnabled = true;
        factory.Scanner.Lines = null;

        (await ScanAsync(admin, LedgerFlows.Png())).ShouldBe(new ProofScanResult(false, null, null, null));
    }

    [Fact]
    public async Task Only_images_are_read_and_only_by_signed_in_users()
    {
        using var admin = factory.CreateClient(await factory.SignInFounderAsync());
        var notAnImage = await PostAsync(admin, "no soy una imagen"u8.ToArray());
        await notAnImage.ShouldBeProblemAsync(HttpStatusCode.BadRequest, ErrorCodes.InvalidImage);

        using var anonymous = factory.CreateClient();
        (await PostAsync(anonymous, LedgerFlows.Png())).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    private static async Task<ProofScanResult> ScanAsync(HttpClient client, byte[] image)
    {
        var response = await PostAsync(client, image);
        await response.ShouldBeAsync(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<ProofScanResult>(Ct))!;
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, byte[] image)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(image);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(file, "image", "voucher.png");
        return await client.PostAsync(new Uri("/api/payments/scan", UriKind.Relative), form, Ct);
    }
}
