using System.Text.Json;
using Bunit;
using Mantenio.Client;
using Mantenio.Shared.BankAccounts;
using Mantenio.Shared.Notifications;
using Mantenio.UI.Components;
using Mantenio.UI.Layout;
using Mantenio.UI.Pages;
using Mantenio.UI.Resources;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using Shouldly;

namespace Mantenio.UI.Tests;

public sealed class Phase3UiTests : BunitContext
{
    private readonly FakeApi _api = new();

    public Phase3UiTests()
    {
        Services.AddMudServices();
        Services.AddSingleton<IHttpClientFactory>(_api);
        Services.AddSingleton<MantenioApi>();
        JSInterop.Mode = JSRuntimeMode.Loose;
        AddAuthorization().SetAuthorized("Rosa");
    }

    [Fact]
    public void Money_and_months_are_written_the_peruvian_way()
    {
        Formats.Money(180m).ShouldBe("S/ 180.00");
        Formats.Month(202611).ShouldBe("Noviembre 2026");
        Formats.NextMonths(202611, 3).ShouldBe([202612, 202701, 202702]);
    }

    [Fact]
    public void Bank_account_notices_name_the_bank_and_the_reason()
    {
        var notice = Notice(NotificationKind.BankAccountsChanged, new { change = "removed", bankName = "BCP", reason = "Cuenta cerrada" });

        NoticeText.Title(notice).ShouldBe(UiStrings.NoticeBankTitle);
        var body = NoticeText.Body(notice);
        body.ShouldContain("BCP");
        body.ShouldContain("Cuenta cerrada");
        body.ShouldContain(UiStrings.NoticeBankCheck);
    }

    [Fact]
    public void Fee_notices_explain_the_new_amount_and_from_when()
    {
        var global = Notice(NotificationKind.FeeChanged, new { apartmentCode = (string?)null, oldAmount = 180m, newAmount = 200m, effectivePeriod = 202701, reason = "Vigilancia" });
        var removed = Notice(NotificationKind.FeeChanged, new { apartmentCode = "H-102", oldAmount = 90m, newAmount = (decimal?)null, effectivePeriod = 202702, reason = "Fin" });

        NoticeText.Body(global).ShouldStartWith("La cuota de mantenimiento será de S/ 200.00 desde Enero 2027.");
        NoticeText.Body(removed).ShouldContain("H-102");
        NoticeText.Body(removed).ShouldContain("Febrero 2027");
    }

    [Fact]
    public void Residents_see_each_bank_field_with_its_own_copy_button()
    {
        _api.Returns("/api/bank-accounts", new List<BankAccountItem>
        {
            new(Guid.NewGuid(), "BCP", "Junta de Propietarios", "191-1234567-0-12", "00219100123456701255", true, 1, DateTimeOffset.UtcNow),
        });

        var cut = Render<BankAccounts>();

        cut.WaitForAssertion(() => cut.Markup.ShouldContain("00219100123456701255"));
        cut.FindAll("button[aria-label^='" + UiStrings.Copy + "']").Count.ShouldBe(3);
    }

    [Fact]
    public void Phones_get_at_most_four_items_in_the_bottom_bar()
    {
        var admin = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
            [new System.Security.Claims.Claim(Shared.Auth.MantenioClaims.Role, nameof(Shared.Auth.AccountRole.Admin))], "jwt"));

        NavigationItems.For(admin).Count(i => i.InBottomBar).ShouldBeLessThanOrEqualTo(4);
        NavigationItems.AdminPages.ShouldContain(i => i.Href == "admin/apartments");
    }

    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    private static NotificationItem Notice(NotificationKind kind, object data) =>
        new(Guid.NewGuid(), kind, JsonSerializer.SerializeToElement(data, WebJson),
            kind == NotificationKind.BankAccountsChanged, DateTimeOffset.UtcNow, null, null);
}
