using System.Net;
using System.Net.Http.Json;
using Mantenio.Api.Infrastructure.Persistence;
using Mantenio.Api.Tests.Infrastructure;
using Mantenio.Shared.Condos;
using Mantenio.Shared.SystemInfo;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Mantenio.Api.Tests.SystemInfo;

public sealed class SystemInfoTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task Health_is_healthy_when_the_database_is_reachable()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync(new Uri("/health", UriKind.Relative), TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldBe("Healthy");
    }

    [Fact]
    public async Task Info_returns_the_compiled_condo_profile()
    {
        using var client = factory.CreateClient();

        var info = await client.GetFromJsonAsync<SystemInfoResponse>(
            new Uri("/api/system/info", UriKind.Relative), TestContext.Current.CancellationToken);

        info.ShouldNotBeNull();
        info.CondoSlug.ShouldBe(CondoProfile.Current.Slug);
        info.CondoName.ShouldBe(CondoProfile.Current.DisplayName);
    }

    [Fact]
    public async Task Migrations_are_applied_and_the_condo_apartments_seeded()
    {
        _ = factory.CreateClient();
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var pending = await db.Database.GetPendingMigrationsAsync(TestContext.Current.CancellationToken);

        pending.ShouldBeEmpty();
        (await db.Towers.CountAsync(TestContext.Current.CancellationToken)).ShouldBe(9);
        (await db.Apartments.CountAsync(TestContext.Current.CancellationToken)).ShouldBe(180);
    }

    [Fact]
    public async Task Unknown_routes_return_problem_details_with_a_code()
    {
        var ct = TestContext.Current.CancellationToken;

        // Deny by default: without a session nobody can probe which routes exist.
        using var anonymous = factory.CreateClient();
        var probe = await anonymous.GetAsync(new Uri("/api/does-not-exist", UriKind.Relative), ct);
        probe.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        using var client = factory.CreateClient(await factory.SignInFounderAsync());
        var response = await client.GetAsync(new Uri("/api/does-not-exist", UriKind.Relative), ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
        var body = await response.Content.ReadAsStringAsync(ct);
        body.ShouldContain("\"code\":\"not_found\"");
    }
}
