using System.Globalization;
using System.Net.Sockets;
using Mantenio.Api.Infrastructure.Email;
using Mantenio.Api.Infrastructure.Push;
using Mantenio.Api.Infrastructure.Storage;
using Mantenio.Api.Resources;
using Mantenio.Shared.Condos;
using Mantenio.Shared.Errors;
using Microsoft.AspNetCore.Diagnostics;
using Npgsql;

namespace Mantenio.Api.Infrastructure.Errors;

/// <summary>"Alerts" section: who is told when the database cannot be reached.</summary>
public sealed class AlertOptions
{
    public const string SectionName = "Alerts";

    /// <summary>Usually the founder's email. Empty = no alert emails.</summary>
    public string Email { get; set; } = "";
}

/// <summary>
/// The database cannot be reached (Aiven Free powered off after inactivity, network, maintenance): the API answers
/// <c>503 database_unavailable</c> instead of a generic error, so the app shows a clear message, and the admin gets
/// an email and the founder a push notification (at most once an hour) to turn the service back on. The founder's
/// phones are read from file storage (<see cref="FounderDevices"/>), since the database is the thing that is down.
/// </summary>
internal sealed partial class DatabaseUnavailableHandler(
    IProblemDetailsService problems, IServiceScopeFactory scopes, TimeProvider time, IConfiguration configuration,
    ILogger<DatabaseUnavailableHandler> logger) : IExceptionHandler
{
    private static readonly TimeSpan AlertInterval = TimeSpan.FromHours(1);
    private static long _lastAlertTicks;

    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        if (!IsDatabaseUnavailable(exception))
        {
            return false;
        }

        LogUnavailable(exception);
        httpContext.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        await problems.WriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails = { Status = StatusCodes.Status503ServiceUnavailable, Extensions = { ["code"] = ErrorCodes.DatabaseUnavailable } },
        });
        await AlertAsync(cancellationToken);
        return true;
    }

    public static bool IsDatabaseUnavailable(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is NpgsqlException { IsTransient: true } or SocketException or TimeoutException
                || current is NpgsqlException { InnerException: SocketException or TimeoutException or IOException })
            {
                return true;
            }
        }

        return false;
    }

    private async Task AlertAsync(CancellationToken cancellationToken)
    {
        var to = configuration[$"{AlertOptions.SectionName}:{nameof(AlertOptions.Email)}"];
        var now = time.GetUtcNow().UtcTicks;
        var last = Interlocked.Read(ref _lastAlertTicks);
        if (now - last < AlertInterval.Ticks || Interlocked.CompareExchange(ref _lastAlertTicks, now, last) != last)
        {
            return;
        }

        await using var scope = scopes.CreateAsyncScope();
        await PushFounderAsync(scope.ServiceProvider, cancellationToken);
        if (string.IsNullOrWhiteSpace(to))
        {
            return;
        }

        try
        {
            var condo = CondoProfile.Current.DisplayName;
            await scope.ServiceProvider.GetRequiredService<IEmailSender>().SendAsync(new EmailMessage(
                to, condo, Format(EmailTexts.DatabaseDownSubject, condo),
                Format(EmailTexts.DatabaseDownBody, condo, time.GetUtcNow().ToString("u", CultureInfo.InvariantCulture))),
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogAlertFailed(ex);
        }
    }

    private async Task PushFounderAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        try
        {
            var sender = services.GetRequiredService<IPushSender>();
            if (!sender.IsEnabled)
            {
                return;
            }

            var condo = CondoProfile.Current.DisplayName;
            var data = new Dictionary<string, string> { ["kind"] = "DatabaseDown" };
            foreach (var token in await FounderDevices.ReadAsync(services.GetRequiredService<IFileStorage>(), cancellationToken))
            {
                await sender.SendAsync(new PushMessage(token, Format(PushTexts.DatabaseDownTitle, condo), PushTexts.DatabaseDownBody, data),
                    cancellationToken);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogAlertFailed(ex);
        }
    }

    private static string Format(string template, params object[] args) =>
        string.Format(CultureInfo.GetCultureInfo(CondoProfile.Current.Culture), template, args);

    [LoggerMessage(Level = LogLevel.Error, Message = "Database unavailable")]
    private partial void LogUnavailable(Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Could not send the database-down alert")]
    private partial void LogAlertFailed(Exception exception);
}
