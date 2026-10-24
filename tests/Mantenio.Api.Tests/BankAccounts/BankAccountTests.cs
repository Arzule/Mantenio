using System.Net;
using System.Net.Http.Json;
using Mantenio.Api.Tests.Infrastructure;
using Mantenio.Shared.Auth;
using Mantenio.Shared.BankAccounts;
using Mantenio.Shared.Errors;
using Mantenio.Shared.Notifications;
using Shouldly;

namespace Mantenio.Api.Tests.BankAccounts;

public sealed class BankAccountTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static SaveBankAccountRequest Bcp(string reason = "Cuenta de la junta") =>
        new("BCP", "Junta de Propietarios El Prado", "191-1234567-0-12", "002 191 001234567012 55", 1, reason);

    [Fact]
    public async Task A_new_account_is_visible_to_residents_and_they_must_acknowledge_the_change()
    {
        var resident = await factory.RegisterAsync(AccountRole.Resident, "bank1@test.local", "E-201");
        using var admin = factory.CreateClient(await factory.SignInFounderAsync());

        var created = await admin.PostAsJsonAsync("/api/admin/bank-accounts", Bcp() with { BankName = "Scotiabank" }, Ct);
        await created.ShouldBeAsync(HttpStatusCode.OK);

        using var client = factory.CreateClient(resident);
        var accounts = await client.GetFromJsonAsync<List<BankAccountItem>>("/api/bank-accounts", Ct);
        var account = accounts!.Single(a => a.BankName == "Scotiabank");
        account.Cci.ShouldBe("00219100123456701255");

        var pending = await client.GetFromJsonAsync<List<NotificationItem>>("/api/notifications/pending", Ct);
        var notice = pending!.Single(n => n.Data.GetProperty("bankName").GetString() == "Scotiabank");
        notice.Kind.ShouldBe(NotificationKind.BankAccountsChanged);
        notice.Data.GetProperty("change").GetString().ShouldBe(BankAccountChanges.Added);

        // "Mark all as read" does not dismiss a notice that needs an explicit acknowledgement.
        await client.PostAsync(new Uri("/api/notifications/read-all", UriKind.Relative), null, Ct);
        (await client.GetFromJsonAsync<List<NotificationItem>>("/api/notifications/pending", Ct))!.ShouldContain(n => n.Id == notice.Id);

        (await client.PostAsync(new Uri($"/api/notifications/{notice.Id}/acknowledge", UriKind.Relative), null, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await client.GetFromJsonAsync<List<NotificationItem>>("/api/notifications/pending", Ct))!.ShouldNotContain(n => n.Id == notice.Id);
    }

    [Fact]
    public async Task Deactivated_accounts_disappear_for_residents_but_stay_for_admins()
    {
        var resident = await factory.RegisterAsync(AccountRole.Resident, "bank2@test.local", "E-202");
        using var admin = factory.CreateClient(await factory.SignInFounderAsync());
        var account = await (await admin.PostAsJsonAsync("/api/admin/bank-accounts", Bcp() with { BankName = "Interbank" }, Ct))
            .Content.ReadFromJsonAsync<BankAccountItem>(Ct);

        await (await admin.PostAsJsonAsync($"/api/admin/bank-accounts/{account!.Id}/deactivate", new DeactivateBankAccountRequest("Cuenta cerrada"), Ct))
            .ShouldBeAsync(HttpStatusCode.NoContent);

        using var client = factory.CreateClient(resident);
        (await client.GetFromJsonAsync<List<BankAccountItem>>("/api/bank-accounts", Ct))!.ShouldNotContain(a => a.Id == account.Id);
        (await admin.GetFromJsonAsync<List<BankAccountItem>>("/api/admin/bank-accounts", Ct))!.Single(a => a.Id == account.Id).IsActive.ShouldBeFalse();
        (await client.GetFromJsonAsync<List<NotificationItem>>("/api/notifications/pending", Ct))!
            .ShouldContain(n => n.Data.GetProperty("change").GetString() == BankAccountChanges.Removed);
    }

    [Fact]
    public async Task Invalid_cci_is_rejected()
    {
        using var admin = factory.CreateClient(await factory.SignInFounderAsync());

        var response = await admin.PostAsJsonAsync("/api/admin/bank-accounts", Bcp() with { Cci = "12345" }, Ct);

        await response.ShouldBeProblemAsync(HttpStatusCode.BadRequest, ErrorCodes.InvalidBankAccount);
    }

    [Fact]
    public async Task Residents_cannot_change_accounts_or_touch_others_notices()
    {
        var resident = await factory.RegisterAsync(AccountRole.Resident, "bank3@test.local", "E-203");
        var other = await factory.RegisterAsync(AccountRole.Resident, "bank4@test.local", "E-204");
        using var admin = factory.CreateClient(await factory.SignInFounderAsync());
        await admin.PostAsJsonAsync("/api/admin/bank-accounts", Bcp() with { BankName = "BBVA" }, Ct);
        using var client = factory.CreateClient(resident);
        using var otherClient = factory.CreateClient(other);

        (await client.PostAsJsonAsync("/api/admin/bank-accounts", Bcp(), Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        var othersNotice = (await otherClient.GetFromJsonAsync<List<NotificationItem>>("/api/notifications/pending", Ct))![0];
        (await client.PostAsync(new Uri($"/api/notifications/{othersNotice.Id}/acknowledge", UriKind.Relative), null, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}
