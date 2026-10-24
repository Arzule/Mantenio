using Mantenio.Client.Auth;
using Mantenio.Shared.Auth;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;

namespace Mantenio.Client;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// HTTP clients, session and Blazor authorization for both hosts (web and Android).
    /// The host registers its own <see cref="ITokenStore"/>.
    /// </summary>
    public static IServiceCollection AddMantenioClient(this IServiceCollection services, Uri apiBaseAddress)
    {
        services.AddSingleton<AuthSession>();
        services.AddTransient<BearerTokenHandler>();
        services.AddHttpClient(MantenioHttpClients.Public, client => client.BaseAddress = apiBaseAddress);
        services.AddHttpClient(MantenioHttpClients.Authenticated, client => client.BaseAddress = apiBaseAddress)
            .AddHttpMessageHandler<BearerTokenHandler>();
        services.AddSingleton<PublicApi>();
        services.AddSingleton<MantenioApi>();

        services.AddAuthorizationCore(options =>
        {
            options.AddPolicy(MantenioPolicies.AdminOnly, policy => policy.RequireClaim(MantenioClaims.Role, nameof(AccountRole.Admin)));
            options.AddPolicy(MantenioPolicies.FounderOnly, policy => policy
                .RequireClaim(MantenioClaims.Role, nameof(AccountRole.Admin))
                .RequireClaim(MantenioClaims.Founder, "true"));
        });
        services.AddCascadingAuthenticationState();
        services.AddSingleton<MantenioAuthenticationStateProvider>();
        services.AddSingleton<AuthenticationStateProvider>(sp => sp.GetRequiredService<MantenioAuthenticationStateProvider>());

        return services;
    }
}
