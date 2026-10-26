using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Mantenio.Shared.Apartments;
using Mantenio.Shared.BankAccounts;
using Mantenio.Shared.Notifications;
using Mantenio.Shared.Settings;
using Mantenio.Shared.Auth;
using Mantenio.Shared.Invitations;
using Mantenio.Shared.Ledger;
using Mantenio.Shared.Reports;
using Mantenio.Shared.Users;

namespace Mantenio.Client;

public static class MantenioHttpClients
{
    /// <summary>No token: sign-in, registration, password reset and the apartment picker.</summary>
    public const string Public = "Mantenio.Public";

    /// <summary>Bearer token with automatic renewal.</summary>
    public const string Authenticated = "Mantenio.Authenticated";
}

/// <summary>Calls that work without being signed in.</summary>
public sealed class PublicApi(IHttpClientFactory httpClientFactory)
{
    public Task<SignInResponse> LoginAsync(LoginRequest request, CancellationToken cancellationToken = default) =>
        PostAsync<SignInResponse>("/api/auth/login", request, cancellationToken);

    public Task<SignInResponse> VerifyTwoFactorAsync(VerifyCodeRequest request, CancellationToken cancellationToken = default) =>
        PostAsync<SignInResponse>("/api/auth/two-factor", request, cancellationToken);

    public Task<SignInResponse> ResendTwoFactorAsync(string challengeToken, CancellationToken cancellationToken = default) =>
        PostAsync<SignInResponse>("/api/auth/two-factor/resend", new ResendCodeRequest(challengeToken), cancellationToken);

    public Task<SignInResponse> ConfirmEmailAsync(VerifyCodeRequest request, CancellationToken cancellationToken = default) =>
        PostAsync<SignInResponse>("/api/auth/email/confirm", request, cancellationToken);

    public Task<SignInResponse> ResendEmailConfirmationAsync(string challengeToken, CancellationToken cancellationToken = default) =>
        PostAsync<SignInResponse>("/api/auth/email/resend", new ResendCodeRequest(challengeToken), cancellationToken);

    public Task<CheckInvitationResponse> CheckInvitationAsync(string code, CancellationToken cancellationToken = default) =>
        PostAsync<CheckInvitationResponse>("/api/invitations/check", new CheckInvitationRequest(code), cancellationToken);

    public Task<SignInResponse> RegisterAsync(RegisterRequest request, CancellationToken cancellationToken = default) =>
        PostAsync<SignInResponse>("/api/auth/register", request, cancellationToken);

    public Task ForgotPasswordAsync(string email, CancellationToken cancellationToken = default) =>
        PostAsync("/api/auth/password/forgot", new ForgotPasswordRequest(email), cancellationToken);

    public Task ResetPasswordAsync(ResetPasswordRequest request, CancellationToken cancellationToken = default) =>
        PostAsync("/api/auth/password/reset", request, cancellationToken);

    public async Task<IReadOnlyList<ApartmentOption>> SearchApartmentsAsync(string? search, CancellationToken cancellationToken = default)
    {
        using var client = httpClientFactory.CreateClient(MantenioHttpClients.Public);
        var url = $"/api/apartments/options?search={Uri.EscapeDataString(search ?? "")}";
        using var response = await client.GetAsync(new Uri(url, UriKind.Relative), cancellationToken).SendSafeAsync();
        return await response.ReadAsync<List<ApartmentOption>>(cancellationToken);
    }

    /// <summary>Every apartment, for the tower and number comboboxes.</summary>
    public async Task<IReadOnlyList<ApartmentOption>> ListApartmentOptionsAsync(CancellationToken cancellationToken = default)
    {
        using var client = httpClientFactory.CreateClient(MantenioHttpClients.Public);
        var url = $"/api/apartments/options?limit={ApartmentOptionLimits.Max}";
        using var response = await client.GetAsync(new Uri(url, UriKind.Relative), cancellationToken).SendSafeAsync();
        return await response.ReadAsync<List<ApartmentOption>>(cancellationToken);
    }

    private async Task<T> PostAsync<T>(string path, object body, CancellationToken cancellationToken)
    {
        using var client = httpClientFactory.CreateClient(MantenioHttpClients.Public);
        using var response = await client.PostAsJsonAsync(path, body, cancellationToken).SendSafeAsync();
        return await response.ReadAsync<T>(cancellationToken);
    }

    private async Task PostAsync(string path, object body, CancellationToken cancellationToken)
    {
        using var client = httpClientFactory.CreateClient(MantenioHttpClients.Public);
        using var response = await client.PostAsJsonAsync(path, body, cancellationToken).SendSafeAsync();
        await response.EnsureSuccessAsync(cancellationToken);
    }
}

/// <summary>Calls for a signed-in user. Admin-only calls answer 403 to residents.</summary>
public sealed class MantenioApi(IHttpClientFactory httpClientFactory)
{
    public Task<CurrentUserResponse> GetCurrentUserAsync(CancellationToken cancellationToken = default) =>
        GetAsync<CurrentUserResponse>("/api/account/me", cancellationToken);

    public Task<AuthTokens> ChangePasswordAsync(ChangePasswordRequest request, CancellationToken cancellationToken = default) =>
        SendAsync<AuthTokens>(HttpMethod.Post, "/api/account/password", request, cancellationToken);

    public Task<CreateInvitationResponse> CreateInvitationAsync(CreateInvitationRequest request, CancellationToken cancellationToken = default) =>
        SendAsync<CreateInvitationResponse>(HttpMethod.Post, "/api/admin/invitations", request, cancellationToken);

    public Task<PagedResult<UserSummary>> ListUsersAsync(
        string? search, bool includeInactive, int page, int pageSize, CancellationToken cancellationToken = default) =>
        GetAsync<PagedResult<UserSummary>>(
            $"/api/admin/users?search={Uri.EscapeDataString(search ?? "")}&includeInactive={(includeInactive ? "true" : "false")}&page={page}&pageSize={pageSize}",
            cancellationToken);

    public Task DeactivateUserAsync(Guid userId, string reason, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, $"/api/admin/users/{userId}/deactivate", new ChangeUserStatusRequest(reason), cancellationToken);

    public Task ReactivateUserAsync(Guid userId, string reason, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, $"/api/admin/users/{userId}/reactivate", new ChangeUserStatusRequest(reason), cancellationToken);

    public Task ChangeUserApartmentAsync(Guid userId, Guid? apartmentId, string reason, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Put, $"/api/admin/users/{userId}/apartment", new ChangeUserApartmentRequest(apartmentId, reason), cancellationToken);

    // Settings and fees
    public Task<CondoSettingsResponse> GetSettingsAsync(CancellationToken cancellationToken = default) =>
        GetAsync<CondoSettingsResponse>("/api/admin/settings", cancellationToken);

    public Task<MyFeeResponse> GetMyFeeAsync(CancellationToken cancellationToken = default) =>
        GetAsync<MyFeeResponse>("/api/fees/mine", cancellationToken);

    public Task<List<FeeChangeItem>> GetFeeHistoryAsync(Guid? apartmentId = null, CancellationToken cancellationToken = default) =>
        GetAsync<List<FeeChangeItem>>(apartmentId is null ? "/api/admin/fees/history" : $"/api/admin/fees/history?apartmentId={apartmentId}", cancellationToken);

    public Task ChangeGlobalFeeAsync(ChangeGlobalFeeRequest request, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, "/api/admin/fees/global", request, cancellationToken);

    public Task ChangeApartmentFeeAsync(Guid apartmentId, ChangeApartmentFeeRequest request, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, $"/api/admin/fees/apartments/{apartmentId}", request, cancellationToken);

    public Task ChangeDueRuleAsync(ChangeDueRuleRequest request, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Put, "/api/admin/settings/due-rule", request, cancellationToken);

    public Task ChangeLateFeeAsync(ChangeLateFeeRequest request, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Put, "/api/admin/settings/late-fee", request, cancellationToken);

    // Towers and apartments
    public Task<List<TowerItem>> ListTowersAsync(CancellationToken cancellationToken = default) =>
        GetAsync<List<TowerItem>>("/api/admin/towers", cancellationToken);

    public Task<TowerItem> CreateTowerAsync(string name, CancellationToken cancellationToken = default) =>
        SendAsync<TowerItem>(HttpMethod.Post, "/api/admin/towers", new CreateTowerRequest(name), cancellationToken);

    public Task<List<ApartmentAdminItem>> ListApartmentsAsync(string? search, bool? frozen, CancellationToken cancellationToken = default) =>
        GetAsync<List<ApartmentAdminItem>>(
            $"/api/admin/apartments?search={Uri.EscapeDataString(search ?? "")}{(frozen is null ? "" : frozen.Value ? "&frozen=true" : "&frozen=false")}",
            cancellationToken);

    public Task<ApartmentAdminItem> CreateApartmentAsync(Guid towerId, string number, CancellationToken cancellationToken = default) =>
        SendAsync<ApartmentAdminItem>(HttpMethod.Post, "/api/admin/apartments", new CreateApartmentRequest(towerId, number), cancellationToken);

    public async Task DeleteApartmentAsync(Guid apartmentId, CancellationToken cancellationToken = default)
    {
        using var client = httpClientFactory.CreateClient(MantenioHttpClients.Authenticated);
        using var response = await client.DeleteAsync(new Uri($"/api/admin/apartments/{apartmentId}", UriKind.Relative), cancellationToken).SendSafeAsync();
        await response.EnsureSuccessAsync(cancellationToken);
    }

    public Task<ImportApartmentsPreview> PreviewApartmentImportAsync(string csv, CancellationToken cancellationToken = default) =>
        SendAsync<ImportApartmentsPreview>(HttpMethod.Post, "/api/admin/apartments/import/preview", new ImportApartmentsRequest(csv), cancellationToken);

    public Task<ImportApartmentsResult> ImportApartmentsAsync(string csv, CancellationToken cancellationToken = default) =>
        SendAsync<ImportApartmentsResult>(HttpMethod.Post, "/api/admin/apartments/import", new ImportApartmentsRequest(csv), cancellationToken);

    public Task FreezeApartmentAsync(Guid apartmentId, string reason, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, $"/api/admin/apartments/{apartmentId}/freeze", new FreezeApartmentRequest(reason), cancellationToken);

    public Task UnfreezeApartmentAsync(Guid apartmentId, string reason, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, $"/api/admin/apartments/{apartmentId}/unfreeze", new FreezeApartmentRequest(reason), cancellationToken);

    // Bank accounts
    public Task<List<BankAccountItem>> ListBankAccountsAsync(CancellationToken cancellationToken = default) =>
        GetAsync<List<BankAccountItem>>("/api/bank-accounts", cancellationToken);

    public Task<List<BankAccountItem>> ListAllBankAccountsAsync(CancellationToken cancellationToken = default) =>
        GetAsync<List<BankAccountItem>>("/api/admin/bank-accounts", cancellationToken);

    public Task<BankAccountItem> CreateBankAccountAsync(SaveBankAccountRequest request, CancellationToken cancellationToken = default) =>
        SendAsync<BankAccountItem>(HttpMethod.Post, "/api/admin/bank-accounts", request, cancellationToken);

    public Task<BankAccountItem> UpdateBankAccountAsync(Guid id, SaveBankAccountRequest request, CancellationToken cancellationToken = default) =>
        SendAsync<BankAccountItem>(HttpMethod.Put, $"/api/admin/bank-accounts/{id}", request, cancellationToken);

    public Task DeactivateBankAccountAsync(Guid id, string reason, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, $"/api/admin/bank-accounts/{id}/deactivate", new DeactivateBankAccountRequest(reason), cancellationToken);

    // Notifications
    public Task<NotificationPage> ListNotificationsAsync(CancellationToken cancellationToken = default) =>
        GetAsync<NotificationPage>("/api/notifications", cancellationToken);

    public Task<List<NotificationItem>> ListPendingNotificationsAsync(CancellationToken cancellationToken = default) =>
        GetAsync<List<NotificationItem>>("/api/notifications/pending", cancellationToken);

    public Task AcknowledgeNotificationAsync(Guid id, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, $"/api/notifications/{id}/acknowledge", new { }, cancellationToken);

    public Task MarkAllNotificationsReadAsync(CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, "/api/notifications/read-all", new { }, cancellationToken);

    // Payments and account statement
    public async Task<PaymentProofItem> SubmitPaymentProofAsync(
        decimal amount, DateOnly operationDate, string bankName, string operationNumber, byte[] image, string contentType,
        CancellationToken cancellationToken = default)
    {
        using var form = new MultipartFormDataContent
        {
            { new StringContent(amount.ToString(CultureInfo.InvariantCulture)), "amount" },
            { new StringContent(operationDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)), "operationDate" },
            { new StringContent(bankName), "bankName" },
            { new StringContent(operationNumber), "operationNumber" },
        };
        var file = new ByteArrayContent(image);
        file.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        form.Add(file, "image", "proof" + (contentType == "image/png" ? ".png" : ".jpg"));
        using var client = httpClientFactory.CreateClient(MantenioHttpClients.Authenticated);
        using var response = await client.PostAsync(new Uri("/api/payments", UriKind.Relative), form, cancellationToken).SendSafeAsync();
        return await response.ReadAsync<PaymentProofItem>(cancellationToken);
    }

    /// <summary>Admin: registers the voucher a resident brought in person; it comes back approved, with its receipt.</summary>
    public async Task<PaymentProofItem> RegisterPaymentProofAsync(
        Guid apartmentId, string payerName, decimal amount, DateOnly operationDate, string bankName, string operationNumber, byte[] image,
        string contentType, CancellationToken cancellationToken = default)
    {
        using var form = new MultipartFormDataContent
        {
            { new StringContent(apartmentId.ToString()), "apartmentId" },
            { new StringContent(payerName), "payerName" },
            { new StringContent(amount.ToString(CultureInfo.InvariantCulture)), "amount" },
            { new StringContent(operationDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)), "operationDate" },
            { new StringContent(bankName), "bankName" },
            { new StringContent(operationNumber), "operationNumber" },
        };
        var file = new ByteArrayContent(image);
        file.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        form.Add(file, "image", "voucher" + (contentType == "image/png" ? ".png" : ".jpg"));
        using var client = httpClientFactory.CreateClient(MantenioHttpClients.Authenticated);
        using var response = await client.PostAsync(new Uri("/api/admin/payments/register", UriKind.Relative), form, cancellationToken).SendSafeAsync();
        return await response.ReadAsync<PaymentProofItem>(cancellationToken);
    }

    public Task<List<string>> ListApartmentResidentsAsync(Guid apartmentId, CancellationToken cancellationToken = default) =>
        GetAsync<List<string>>($"/api/admin/apartments/{apartmentId}/residents", cancellationToken);

    /// <summary>The receipt PDF (to share it by WhatsApp).</summary>
    public Task<DownloadedFile> DownloadReceiptAsync(Guid proofId, CancellationToken cancellationToken = default) =>
        DownloadAsync($"/api/payments/{proofId}/receipt/pdf", cancellationToken);

    public Task<List<PaymentProofItem>> ListMyPaymentsAsync(CancellationToken cancellationToken = default) =>
        GetAsync<List<PaymentProofItem>>("/api/payments/mine", cancellationToken);

    public Task<FileLink> GetPaymentImageAsync(Guid proofId, CancellationToken cancellationToken = default) =>
        GetAsync<FileLink>($"/api/payments/{proofId}/image", cancellationToken);

    public Task<FileLink> GetReceiptAsync(Guid proofId, CancellationToken cancellationToken = default) =>
        GetAsync<FileLink>($"/api/payments/{proofId}/receipt", cancellationToken);

    public Task<AccountStatement> GetStatementAsync(Guid apartmentId, CancellationToken cancellationToken = default) =>
        GetAsync<AccountStatement>($"/api/apartments/{apartmentId}/statement", cancellationToken);

    public Task<List<PaymentProofItem>> ListPaymentsAsync(ProofStatus? status, CancellationToken cancellationToken = default) =>
        GetAsync<List<PaymentProofItem>>(status is null ? "/api/admin/payments" : $"/api/admin/payments?status={status}", cancellationToken);

    public Task<PaymentProofItem> ApprovePaymentAsync(Guid proofId, ApproveProofRequest request, CancellationToken cancellationToken = default) =>
        SendAsync<PaymentProofItem>(HttpMethod.Post, $"/api/admin/payments/{proofId}/approve", request, cancellationToken);

    public Task<PaymentProofItem> RejectPaymentAsync(Guid proofId, RejectProofRequest request, CancellationToken cancellationToken = default) =>
        SendAsync<PaymentProofItem>(HttpMethod.Post, $"/api/admin/payments/{proofId}/reject", request, cancellationToken);

    public Task<PaymentProofItem> VoidPaymentAsync(Guid proofId, VoidProofRequest request, CancellationToken cancellationToken = default) =>
        SendAsync<PaymentProofItem>(HttpMethod.Post, $"/api/admin/payments/{proofId}/void", request, cancellationToken);

    public Task<List<TimelineEntry>> GetChargeTimelineAsync(Guid chargeId, CancellationToken cancellationToken = default) =>
        GetAsync<List<TimelineEntry>>($"/api/admin/charges/{chargeId}/timeline", cancellationToken);

    public Task<GenerateChargesResult> GenerateChargesAsync(int period, CancellationToken cancellationToken = default) =>
        SendAsync<GenerateChargesResult>(HttpMethod.Post, "/api/admin/charges/generate", new GenerateChargesRequest(period), cancellationToken);

    public Task<ImportOpeningDebtResult> ImportOpeningDebtAsync(string csv, CancellationToken cancellationToken = default) =>
        SendAsync<ImportOpeningDebtResult>(HttpMethod.Post, "/api/admin/charges/opening", new ImportOpeningDebtRequest(csv), cancellationToken);

    // Push devices
    public Task RegisterDeviceAsync(string token, string platform, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, "/api/devices", new RegisterDeviceRequest(token, platform), cancellationToken);

    public Task UnregisterDeviceAsync(string token, CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Post, "/api/devices/unregister", new UnregisterDeviceRequest(token), cancellationToken);

    // Reports and audit
    public Task<AdminDashboard> GetDashboardAsync(CancellationToken cancellationToken = default) =>
        GetAsync<AdminDashboard>("/api/admin/dashboard", cancellationToken);

    public Task<DelinquencyReport> GetDelinquencyAsync(DateOnly date, CancellationToken cancellationToken = default) =>
        GetAsync<DelinquencyReport>($"/api/admin/reports/delinquency?date={Iso(date)}", cancellationToken);

    public Task<DownloadedFile> DownloadDelinquencyAsync(DateOnly date, bool excel, CancellationToken cancellationToken = default) =>
        DownloadAsync($"/api/admin/reports/delinquency/{(excel ? "excel" : "pdf")}?date={Iso(date)}", cancellationToken);

    public Task<DownloadedFile> DownloadFolioAsync(int period, CancellationToken cancellationToken = default) =>
        DownloadAsync($"/api/admin/reports/folio/{period}/pdf", cancellationToken);

    public Task<PagedResult<AuditItem>> ListAuditAsync(AuditFilter filter, int page, int pageSize, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var query = new List<string> { $"page={page}", $"pageSize={pageSize}" };
        if (filter.From is { } from) query.Add($"from={Iso(from)}");
        if (filter.To is { } to) query.Add($"to={Iso(to)}");
        if (filter.Source is { } source) query.Add($"source={source}");
        if (!string.IsNullOrEmpty(filter.EntityType)) query.Add($"entityType={Uri.EscapeDataString(filter.EntityType)}");
        if (filter.ApartmentId is { } apartment) query.Add($"apartmentId={apartment}");
        if (filter.ActorId is { } actor) query.Add($"actorId={actor}");
        return GetAsync<PagedResult<AuditItem>>("/api/admin/audit?" + string.Join('&', query), cancellationToken);
    }

    public Task<List<AuditActor>> ListAuditActorsAsync(CancellationToken cancellationToken = default) =>
        GetAsync<List<AuditActor>>("/api/admin/audit/actors", cancellationToken);

    private static string Iso(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private async Task<DownloadedFile> DownloadAsync(string path, CancellationToken cancellationToken)
    {
        using var client = httpClientFactory.CreateClient(MantenioHttpClients.Authenticated);
        using var response = await client.GetAsync(new Uri(path, UriKind.Relative), cancellationToken).SendSafeAsync();
        await response.EnsureSuccessAsync(cancellationToken);
        var headers = response.Content.Headers;
        return new DownloadedFile(
            headers.ContentDisposition?.FileNameStar ?? headers.ContentDisposition?.FileName?.Trim('"') ?? "archivo",
            headers.ContentType?.MediaType ?? "application/octet-stream",
            await response.Content.ReadAsByteArrayAsync(cancellationToken));
    }

    private async Task<T> GetAsync<T>(string path, CancellationToken cancellationToken)
    {
        using var client = httpClientFactory.CreateClient(MantenioHttpClients.Authenticated);
        using var response = await client.GetAsync(new Uri(path, UriKind.Relative), cancellationToken).SendSafeAsync();
        return await response.ReadAsync<T>(cancellationToken);
    }

    private async Task<T> SendAsync<T>(HttpMethod method, string path, object body, CancellationToken cancellationToken)
    {
        using var response = await SendCoreAsync(method, path, body, cancellationToken);
        return await response.ReadAsync<T>(cancellationToken);
    }

    private async Task SendAsync(HttpMethod method, string path, object body, CancellationToken cancellationToken)
    {
        using var response = await SendCoreAsync(method, path, body, cancellationToken);
        await response.EnsureSuccessAsync(cancellationToken);
    }

    private async Task<HttpResponseMessage> SendCoreAsync(HttpMethod method, string path, object body, CancellationToken cancellationToken)
    {
        using var client = httpClientFactory.CreateClient(MantenioHttpClients.Authenticated);
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative)) { Content = JsonContent.Create(body) };
        return await client.SendAsync(request, cancellationToken).SendSafeAsync();
    }
}

/// <summary>A report generated by the API (Excel or PDF).</summary>
public sealed record DownloadedFile(string FileName, string ContentType, byte[] Content);

/// <summary>Audit viewer filters; null = any.</summary>
public sealed record AuditFilter(DateOnly? From, DateOnly? To, AuditSourceFilter? Source, string? EntityType, Guid? ApartmentId, Guid? ActorId);
