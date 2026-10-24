using System.Net;
using System.Text;
using System.Text.Json;
using Bunit;
using Mantenio.Client;
using Mantenio.Client.Auth;
using Mantenio.Shared.Apartments;
using Mantenio.Shared.Auth;
using Mantenio.Shared.Errors;
using Mantenio.Shared.Invitations;
using Mantenio.UI.Pages.Auth;
using Mantenio.UI.Resources;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using Shouldly;

namespace Mantenio.UI.Tests;

public sealed class AuthPageTests : BunitContext
{
    private readonly FakeApi _api = new();
    private readonly MemoryTokenStore _store = new();

    public AuthPageTests()
    {
        Services.AddMudServices();
        Services.AddSingleton<IHttpClientFactory>(_api);
        Services.AddSingleton<ITokenStore>(_store);
        Services.AddSingleton(new ClientPreferences(KeepSignedInByDefault: true));
        Services.AddSingleton<AuthSession>();
        Services.AddSingleton<PublicApi>();
        JSInterop.Mode = JSRuntimeMode.Loose;
        Render<MudBlazor.MudPopoverProvider>();
    }

    [Fact]
    public void Login_validates_in_spanish_before_calling_the_api()
    {
        var cut = Render<Login>();

        cut.Find("form").Submit();

        cut.Markup.ShouldContain(UiStrings.Required);
        _api.Calls.ShouldBeEmpty();
    }

    [Fact]
    public void Keep_signed_in_starts_with_the_host_default()
    {
        var cut = Render<Login>();

        cut.Find("input[type=checkbox]").HasAttribute("checked").ShouldBeTrue();
    }

    [Fact]
    public async Task A_resident_login_stores_the_session_and_goes_home()
    {
        _api.Returns("/api/auth/login", new SignInResponse(SignInStep.SignedIn, Tokens("Rosa")));
        var navigation = Services.GetRequiredService<NavigationManager>();
        var cut = Render<Login>();

        await FillAndSubmitAsync(cut, "rosa@test.local", "Vecino2026");

        _store.Session.ShouldNotBeNull().KeepSignedIn.ShouldBeTrue();
        Services.GetRequiredService<AuthSession>().User.Identity!.Name.ShouldBe("Rosa Quispe");
        navigation.Uri.ShouldBe(navigation.BaseUri);
    }

    [Fact]
    public async Task An_admin_login_asks_for_the_emailed_code()
    {
        _api.Returns("/api/auth/login", new SignInResponse(SignInStep.TwoFactorRequired, ChallengeToken: "challenge", MaskedEmail: "f•••••r@test.local"));
        var cut = Render<Login>();

        await FillAndSubmitAsync(cut, "founder@test.local", "Founder2026");

        cut.Find("h1").TextContent.ShouldBe(UiStrings.TwoFactorTitle);
        cut.Markup.ShouldContain("f•••••r@test.local");
        _store.Session.ShouldBeNull();
    }

    [Fact]
    public async Task Api_errors_are_shown_in_spanish()
    {
        _api.Fails("/api/auth/login", HttpStatusCode.Unauthorized, ErrorCodes.InvalidCredentials);
        var cut = Render<Login>();

        await FillAndSubmitAsync(cut, "rosa@test.local", "wrong1234");

        cut.Markup.ShouldContain(UiStrings.Error_invalid_credentials);
    }

    [Fact]
    public async Task Registration_starts_with_the_invitation_code()
    {
        _api.Returns("/api/invitations/check", new CheckInvitationResponse(AccountRole.Resident, new ApartmentOption(Guid.NewGuid(), "H-102", "H", "102")));
        var cut = Render<Register>();

        cut.Find("input").Change("123456");
        await cut.Find("form").SubmitAsync();

        cut.Markup.ShouldContain(UiStrings.RegisterAsResident);
        // Shown with a non-breaking hyphen so "H-102" never splits across lines.
        cut.Markup.ShouldContain(UiStrings.ApartmentLocked.Replace("{0}", $"H{(char)0x2011}102", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_invalid_invitation_code_explains_what_to_do()
    {
        _api.Fails("/api/invitations/check", HttpStatusCode.BadRequest, ErrorCodes.InvitationInvalid);
        var cut = Render<Register>();

        cut.Find("input").Change("123456");
        await cut.Find("form").SubmitAsync();

        cut.Markup.ShouldContain(UiStrings.Error_invitation_invalid);
    }

    private static async Task FillAndSubmitAsync(IRenderedComponent<Login> cut, string email, string password)
    {
        var inputs = cut.FindAll("input:not([type=checkbox])");
        inputs[0].Change(email);
        inputs[1].Change(password);
        await cut.Find("form").SubmitAsync();
    }

    /// <summary>Unsigned token with the claims the client reads; only the API checks signatures.</summary>
    private static AuthTokens Tokens(string firstName)
    {
        static string Encode(object value) =>
            Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value))).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        var jwt = $"{Encode(new { alg = "HS256" })}.{Encode(new Dictionary<string, string>
        {
            [MantenioClaims.UserId] = Guid.NewGuid().ToString(),
            [MantenioClaims.Name] = $"{firstName} Quispe",
            [MantenioClaims.FirstName] = firstName,
            [MantenioClaims.Role] = nameof(AccountRole.Resident),
        })}.signature";
        var now = DateTimeOffset.UtcNow;
        return new AuthTokens(jwt, now.AddMinutes(15), "refresh", now.AddDays(90));
    }
}
