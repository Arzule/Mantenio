using System.Globalization;
using System.Text.Json;
using System.Threading.Channels;
using Mantenio.Api.Infrastructure.Persistence;
using Mantenio.Api.Resources;
using Mantenio.Domain.Notifications;
using Mantenio.Shared.Condos;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Mantenio.Api.Infrastructure.Push;

/// <summary>
/// Sends the push copy of in-app notices after their transaction commits (outbox: <c>PushedAt</c> is null until
/// sent). <see cref="NotificationService"/> signals new notices; a sweep every few minutes catches the rest. Jobs
/// call <see cref="DispatchPendingAsync"/> directly before they exit.
/// </summary>
public sealed partial class PushDispatcher(IServiceScopeFactory scopes, IOptions<PushOptions> options, ILogger<PushDispatcher> logger)
    : BackgroundService
{
    private static readonly TimeSpan CommitDelay = TimeSpan.FromSeconds(1.5);
    private static readonly TimeSpan Sweep = TimeSpan.FromMinutes(5);

    /// <summary>Older notices are not pushed any more (they would arrive out of context).</summary>
    private static readonly TimeSpan MaxAge = TimeSpan.FromDays(2);

    private static readonly CultureInfo Culture = CultureInfo.GetCultureInfo(CondoProfile.Current.Culture);
    private readonly Channel<bool> _signals = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

    public void Signal() => _signals.Writer.TryWrite(true);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.BackgroundDispatch)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            using (var wait = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken))
            {
                wait.CancelAfter(Sweep);
                try
                {
                    await _signals.Reader.ReadAsync(wait.Token);
                    await Task.Delay(CommitDelay, stoppingToken);
                }
                catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
                {
                    // Periodic sweep.
                }
            }

            try
            {
                await DispatchPendingAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogDispatchFailed(ex);
            }
        }
    }

    /// <summary>Pushes every pending notice to its user's phones. Returns how many messages were sent.</summary>
    public async Task<int> DispatchPendingAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var sender = scope.ServiceProvider.GetRequiredService<IPushSender>();
        var now = scope.ServiceProvider.GetRequiredService<TimeProvider>().GetUtcNow();
        var oldest = now - MaxAge;

        await db.Notifications.Where(n => n.PushedAt == null && n.CreatedAt < oldest)
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.PushedAt, now), cancellationToken);
        var pending = await db.Notifications.Where(n => n.PushedAt == null).OrderBy(n => n.CreatedAt).Take(500).ToListAsync(cancellationToken);
        if (pending.Count == 0)
        {
            return 0;
        }

        var userIds = pending.Select(n => n.UserId).Distinct().ToList();
        var devices = sender.IsEnabled
            ? await db.UserDevices.Where(d => userIds.Contains(d.UserId)).ToListAsync(cancellationToken)
            : [];
        var sent = 0;
        foreach (var notice in pending)
        {
            var (title, body) = Text(notice);
            foreach (var device in devices.Where(d => d.UserId == notice.UserId && db.Entry(d).State != EntityState.Deleted))
            {
                var data = new Dictionary<string, string> { ["noticeId"] = notice.Id.ToString(), ["kind"] = notice.Type.ToString() };
                var result = await sender.SendAsync(new PushMessage(device.Token, title, body, data), cancellationToken);
                if (result == PushResult.Sent)
                {
                    sent++;
                }
                else if (result == PushResult.InvalidToken)
                {
                    db.UserDevices.Remove(device);
                }
            }

            notice.MarkPushed(now);
        }

        await db.SaveChangesAsync(cancellationToken);
        return sent;
    }

    /// <summary>Spanish title and body (Resources/PushTexts.resx), the same wording as the in-app notice.</summary>
    public static (string Title, string Body) Text(UserNotification notice)
    {
        ArgumentNullException.ThrowIfNull(notice);
        using var document = JsonDocument.Parse(notice.Data);
        var data = document.RootElement;
        return notice.Type switch
        {
            NotificationType.BankAccountsChanged => (PushTexts.BankTitle, Format(Str(data, "change") switch
            {
                "added" => PushTexts.Bank_added,
                "removed" => PushTexts.Bank_removed,
                _ => PushTexts.Bank_updated,
            }, Str(data, "bankName"))),
            NotificationType.FeeChanged => (PushTexts.FeeTitle, Format(PushTexts.FeeBody, Month(Int(data, "effectivePeriod")))),
            NotificationType.PaymentApproved => (PushTexts.ApprovedTitle, Format(PushTexts.ApprovedBody, Money(data), Str(data, "receiptNumber"))),
            NotificationType.PaymentRejected => (PushTexts.RejectedTitle, Format(PushTexts.RejectedBody, Money(data), Str(data, "reason"))),
            NotificationType.PaymentVoided => (PushTexts.VoidedTitle, Format(PushTexts.VoidedBody, Money(data), Str(data, "reason"))),
            NotificationType.DebtReminder => (PushTexts.ReminderTitle, Format(PushTexts.ReminderBody, Str(data, "apartmentCode"), Money(data))),
            _ => (PushTexts.GenericTitle, PushTexts.GenericBody),
        };
    }

    private static string Str(JsonElement data, string name) =>
        data.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    private static int Int(JsonElement data, string name) =>
        data.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt32() : 0;

    private static string Money(JsonElement data) =>
        (data.TryGetProperty("amount", out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDecimal() : 0m).ToString("C2", Culture);

    private static string Month(int period) =>
        period < 200001 ? "" : new DateTime(period / 100, period % 100, 1).ToString("MMMM yyyy", Culture);

    private static string Format(string template, params object[] args) => string.Format(Culture, template, args);

    [LoggerMessage(Level = LogLevel.Error, Message = "Push dispatch failed")]
    private partial void LogDispatchFailed(Exception exception);
}
