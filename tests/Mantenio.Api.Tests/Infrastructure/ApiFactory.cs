using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Mantenio.Api.Features.Setup;
using Mantenio.Api.Infrastructure.Email;
using Mantenio.Api.Infrastructure.Persistence;
using Mantenio.Api.Infrastructure.Push;
using Mantenio.Api.Infrastructure.Vision;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Mantenio.Api.Tests.Infrastructure;

/// <summary>
/// Boots the real API against PostgreSQL 18 (same major version as Aiven). One container serves the whole test run;
/// every factory gets its own empty database, seeded with the condo apartments and a founder. Requires Docker.
/// </summary>
public class ApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string FounderEmail = "founder@test.local";
    public const string FounderPassword = "Founder2026";

    /// <summary>Admins need an apartment to review payments; no test uses this one for residents.</summary>
    public const string FounderApartment = "G-503";

    private readonly string _databaseName = "test_" + Guid.NewGuid().ToString("N");
    private string _connectionString = "";
    private string? _migrationConnectionString;

    /// <summary>Payment-proof images and receipt PDFs of this factory (a temp folder).</summary>
    public string StoragePath { get; } = Path.Combine(Path.GetTempPath(), "mantenio-tests", Guid.NewGuid().ToString("N"));

    public FakeTimeProvider Time { get; } = new(DateTimeOffset.UtcNow);
    public CapturingEmailSender Emails { get; } = new();

    public CapturingPushSender Pushes { get; } = new();

    public FakeProofScanner Scanner { get; } = new();

    /// <summary>Server-side errors logged while the tests ran (handy when an endpoint answers 500).</summary>
    public ConcurrentQueue<string> ServerErrors { get; } = new();

    /// <summary>Generous by default so tests do not trip over each other; RateLimitTests lowers them.</summary>
    protected virtual int RateLimit => 10_000;

    /// <summary>When true the founder gets a random temporary password (like `seed-founder`).</summary>
    protected virtual bool FounderHasTemporaryPassword => false;

    public string? FounderTemporaryPassword { get; private set; }

    /// <summary>When true the app runs with a login that does not own the tables and migrates with the owner (like production).</summary>
    protected virtual bool SeparateRuntimeLogin => false;

    /// <summary>The runtime login's name when <see cref="SeparateRuntimeLogin"/> is on.</summary>
    public string RuntimeRole => "rt_" + _databaseName;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting($"ConnectionStrings:{AppDbContext.ConnectionName}", _connectionString);
        builder.UseSetting("Database:MigrateOnStartup", "true");
        if (_migrationConnectionString is not null)
        {
            builder.UseSetting("Database:MigrationConnectionString", _migrationConnectionString);
        }

        builder.UseSetting("Push:BackgroundDispatch", "false");
        builder.UseSetting("Storage:Provider", "Local");
        builder.UseSetting("Storage:LocalPath", StoragePath);
        builder.UseSetting("Jwt:SigningKey", "test-signing-key-that-is-long-enough-0123456789");
        foreach (var name in new[] { "AuthPerMinute", "EmailPer15Minutes", "InvitationPerMinute", "RefreshPerMinute", "UploadsPerHour", "AdminUploadsPerHour" })
        {
            builder.UseSetting($"RateLimiting:{name}", RateLimit.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        builder.ConfigureLogging(logging => logging.AddProvider(new ErrorCaptureProvider(ServerErrors)));
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Time);
            services.RemoveAll<IEmailSender>();
            services.AddSingleton<IEmailSender>(Emails);
            services.RemoveAll<IPushSender>();
            services.AddSingleton<IPushSender>(Pushes);
            services.RemoveAll<IProofScanner>();
            services.AddSingleton<IProofScanner>(Scanner);
        });
    }

    public async ValueTask InitializeAsync()
    {
        var server = await PostgresServer.GetAsync();
        await using (var connection = new NpgsqlConnection(server))
        {
            await connection.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE \"{_databaseName}\"", connection);
            await create.ExecuteNonQueryAsync();
        }

        _connectionString = new NpgsqlConnectionStringBuilder(server) { Database = _databaseName }.ConnectionString;
        if (SeparateRuntimeLogin)
        {
            // Same split as deploy/aiven_runtime_user.py: read/write on the owner's future tables, nothing else.
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();
            var owner = new NpgsqlConnectionStringBuilder(server).Username;
            await using var grant = new NpgsqlCommand($"""
                CREATE ROLE "{RuntimeRole}" LOGIN PASSWORD 'runtime';
                GRANT USAGE ON SCHEMA public TO "{RuntimeRole}";
                ALTER DEFAULT PRIVILEGES FOR ROLE "{owner}" IN SCHEMA public GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO "{RuntimeRole}";
                ALTER DEFAULT PRIVILEGES FOR ROLE "{owner}" IN SCHEMA public GRANT USAGE, SELECT, UPDATE ON SEQUENCES TO "{RuntimeRole}";
                """, connection);
            await grant.ExecuteNonQueryAsync();
            _migrationConnectionString = _connectionString;
            _connectionString = new NpgsqlConnectionStringBuilder(_connectionString) { Username = RuntimeRole, Password = "runtime" }.ConnectionString;
        }

        // Starting the server applies the migrations; then seed what every test expects.
        await using var scope = Services.CreateAsyncScope();
        var seeding = scope.ServiceProvider.GetRequiredService<Seeding>();
        await seeding.SeedApartmentsAsync(Seeding.EmbeddedApartmentList()!, CancellationToken.None);
        var founder = await seeding.CreateFounderAsync(
            FounderEmail, "Fiorella", "Fundadora", FounderHasTemporaryPassword ? null : FounderPassword, CancellationToken.None,
            FounderApartment);
        FounderTemporaryPassword = FounderHasTemporaryPassword ? founder!.Value.Password : null;
    }

    public async Task<T> WithDbAsync<T>(Func<AppDbContext, Task<T>> query)
    {
        await using var scope = Services.CreateAsyncScope();
        return await query(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    public async Task WithScopeAsync(Func<IServiceProvider, Task> work)
    {
        await using var scope = Services.CreateAsyncScope();
        await work(scope.ServiceProvider);
    }
}

internal static class PostgresServer
{
    private static readonly Lazy<Task<string>> Server = new(StartAsync);

    public static Task<string> GetAsync() => Server.Value;

    private static async Task<string> StartAsync()
    {
        // Reaped by Testcontainers (Ryuk) when the test process exits.
        var container = new PostgreSqlBuilder("postgres:18").Build();
        await container.StartAsync();
        return container.GetConnectionString();
    }
}

/// <summary>Keeps every email the API sends so tests can read the 6-digit codes.</summary>
public sealed partial class CapturingEmailSender : IEmailSender
{
    private readonly ConcurrentQueue<EmailMessage> _messages = new();

    public IReadOnlyCollection<EmailMessage> Messages => _messages;

    public Task SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        _messages.Enqueue(message);
        return Task.CompletedTask;
    }

    public IEnumerable<EmailMessage> To(string email) =>
        _messages.Where(m => string.Equals(m.To, email, StringComparison.OrdinalIgnoreCase));

    public string LastCodeFor(string email)
    {
        var message = To(email).LastOrDefault() ?? throw new InvalidOperationException($"No email was sent to {email}.");
        return SixDigits().Match(message.TextBody).Value;
    }

    [GeneratedRegex(@"(?m)^\d{6}$")]
    private static partial Regex SixDigits();
}

/// <summary>Keeps every push message; tokens starting with "bad" behave like an uninstalled app.</summary>
public sealed class CapturingPushSender : IPushSender
{
    private readonly ConcurrentQueue<PushMessage> _messages = new();

    public IReadOnlyCollection<PushMessage> Messages => _messages;

    public bool IsEnabled => true;

    public Task<PushResult> SendAsync(PushMessage message, CancellationToken cancellationToken)
    {
        if (message.Token.StartsWith("bad", StringComparison.Ordinal))
        {
            return Task.FromResult(PushResult.InvalidToken);
        }

        _messages.Enqueue(message);
        return Task.FromResult(PushResult.Sent);
    }
}

/// <summary>Answers every image with <see cref="Lines"/>; null lines behave like a failed call.</summary>
public sealed class FakeProofScanner : IProofScanner
{
    public bool IsEnabled { get; set; } = true;

    public IReadOnlyList<string>? Lines { get; set; } = [];

    public Task<IReadOnlyList<string>?> ReadLinesAsync(byte[] jpeg, CancellationToken cancellationToken) => Task.FromResult(Lines);
}

internal sealed class ErrorCaptureProvider(ConcurrentQueue<string> errors) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new ErrorCaptureLogger(errors);

    public void Dispose()
    {
    }

    private sealed class ErrorCaptureLogger(ConcurrentQueue<string> errors) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Error;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
            {
                errors.Enqueue(formatter(state, exception) + Environment.NewLine + exception);
            }
        }
    }
}
