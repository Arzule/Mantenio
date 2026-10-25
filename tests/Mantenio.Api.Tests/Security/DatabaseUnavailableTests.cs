using System.Net;
using Mantenio.Api.Infrastructure.Email;
using Mantenio.Api.Infrastructure.Persistence;
using Mantenio.Api.Tests.Infrastructure;
using Mantenio.Shared.Errors;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Shouldly;

namespace Mantenio.Api.Tests.Security;

/// <summary>The database is off (e.g. Aiven Free powered it down): clear 503 and one alert email to the admin.</summary>
public sealed class DatabaseUnavailableTests : IClassFixture<DatabaseUnavailableTests.UnreachableDatabaseFactory>
{
    private readonly UnreachableDatabaseFactory _factory;

    public DatabaseUnavailableTests(UnreachableDatabaseFactory factory) => _factory = factory;

    [Fact]
    public async Task Requests_get_503_database_unavailable_and_the_admin_one_email_an_hour()
    {
        using var client = _factory.CreateClient();
        var ct = TestContext.Current.CancellationToken;

        var first = await client.GetAsync(new Uri("/api/apartments/options?search=a", UriKind.Relative), ct);
        var second = await client.GetAsync(new Uri("/api/apartments/options?search=b", UriKind.Relative), ct);

        await first.ShouldBeProblemAsync(HttpStatusCode.ServiceUnavailable, ErrorCodes.DatabaseUnavailable);
        await second.ShouldBeProblemAsync(HttpStatusCode.ServiceUnavailable, ErrorCodes.DatabaseUnavailable);
        _factory.Emails.To("alerts@test.local").ShouldHaveSingleItem().Subject.ShouldContain("base de datos");
    }

    public sealed class UnreachableDatabaseFactory : WebApplicationFactory<Program>
    {
        public CapturingEmailSender Emails { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting($"ConnectionStrings:{AppDbContext.ConnectionName}", "Host=127.0.0.1;Port=1;Database=none;Username=x;Password=x;Timeout=2");
            builder.UseSetting("Aspire:Npgsql:EntityFrameworkCore:PostgreSQL:DisableRetry", "true");
            builder.UseSetting("Database:MigrateOnStartup", "false");
            builder.UseSetting("Jwt:SigningKey", "test-signing-key-that-is-long-enough-0123456789");
            builder.UseSetting("Push:BackgroundDispatch", "false");
            builder.UseSetting("Alerts:Email", "alerts@test.local");
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IEmailSender>();
                services.AddSingleton<IEmailSender>(Emails);
            });
        }
    }
}
