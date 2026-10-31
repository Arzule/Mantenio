using System.Net;
using Mantenio.Api.Tests.Infrastructure;
using Mantenio.Shared.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Mantenio.Api.Tests.Security;

/// <summary>
/// Guards against an endpoint accidentally left open: everything requires sign-in unless it is in the reviewed
/// anonymous list below, and every /api/admin route requires an admin policy.
/// </summary>
public sealed class AuthorizationCoverageTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Reviewed: sign-in, registration, password reset, the apartment picker, health and public info.</summary>
    private static readonly string[] AnonymousRoutes =
    [
        "/health", "/alive", "/api/system/info", "/api/apartments/options", "/api/invitations/check", "/api/auth/register",
        "/api/files/{**key}",
    ];

    /// <summary>Reviewed: the /api/admin routes the gatehouse (portería) shares with admins.</summary>
    private static readonly string[] StaffRoutes =
    [
        "GET /api/admin/payments", "POST /api/admin/payments/register", "GET /api/admin/apartments/{id:guid}/residents",
        "POST /api/admin/payments/{id:guid}/approve", "POST /api/admin/payments/{id:guid}/reject", "POST /api/admin/payments/{id:guid}/void",
        "GET /api/admin/payments/{id:guid}/timeline", "POST /api/admin/invitations",
    ];

    private IEnumerable<RouteEndpoint> Endpoints() =>
        factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText is not null);

    private static string Route(RouteEndpoint endpoint) => "/" + endpoint.RoutePattern.RawText!.TrimStart('/');

    [Fact]
    public void Only_the_reviewed_endpoints_are_anonymous()
    {
        var anonymous = Endpoints()
            .Where(e => e.Metadata.GetMetadata<IAllowAnonymous>() is not null)
            .Select(Route)
            .Where(r => !r.StartsWith("/api/auth/", StringComparison.Ordinal) || r == "/api/auth/register")
            .Distinct()
            .Order()
            .ToList();

        anonymous.ShouldBe(AnonymousRoutes.Order().ToList());
    }

    [Fact]
    public void Every_admin_route_requires_an_admin_policy()
    {
        var unprotected = Endpoints()
            .Where(e => Route(e).StartsWith("/api/admin", StringComparison.Ordinal))
            .Where(e => !e.Metadata.GetOrderedMetadata<IAuthorizeData>()
                .Any(a => a.Policy is MantenioPolicies.AdminOnly or MantenioPolicies.FounderOnly))
            .Select(MethodAndRoute)
            .Order()
            .ToList();

        // Staff routes require StaffOnly instead, and nothing else is opened to the gatehouse.
        unprotected.ShouldBe(StaffRoutes.Order().ToList());
        Endpoints()
            .Where(e => StaffRoutes.Contains(MethodAndRoute(e)))
            .ShouldAllBe(e => e.Metadata.GetOrderedMetadata<IAuthorizeData>().Any(a => a.Policy == MantenioPolicies.StaffOnly));
    }

    private static string MethodAndRoute(RouteEndpoint endpoint) =>
        $"{endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods[0]} {Route(endpoint)}";

    [Fact]
    public async Task Api_responses_carry_security_headers()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync(new Uri("/api/system/info", UriKind.Relative), Ct);
        var denied = await client.GetAsync(new Uri("/api/payments/mine", UriKind.Relative), Ct);

        response.Headers.GetValues("X-Content-Type-Options").ShouldBe(["nosniff"]);
        response.Headers.GetValues("X-Frame-Options").ShouldBe(["DENY"]);
        response.Headers.GetValues("Content-Security-Policy").Single().ShouldContain("default-src 'none'");
        denied.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }
}
