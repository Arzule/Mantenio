using System.Globalization;
using Mantenio.Api.Features.Ledger;
using Mantenio.Api.Features.Settings;
using Mantenio.Api.Infrastructure.Email;
using Mantenio.Api.Infrastructure.Notifications;
using Mantenio.Api.Infrastructure.Persistence;
using Mantenio.Api.Infrastructure.Time;
using Mantenio.Api.Resources;
using Mantenio.Domain.Audit;
using Mantenio.Domain.Ledger;
using Mantenio.Domain.Notifications;
using Mantenio.Shared.Condos;
using Mantenio.Shared.Ledger;
using Mantenio.Shared.Notifications;
using Microsoft.EntityFrameworkCore;

namespace Mantenio.Api.Jobs;

/// <summary>
/// Scheduled work (Container Apps Jobs, cron in UTC, see docs §8). Every change is audited with
/// <c>Source = System</c> and no actor, and every job can run twice without doing anything twice.
/// </summary>
internal sealed partial class LedgerJobs(
    AppDbContext db, TimeProvider time, CondoClock clock, LedgerService ledger, NotificationService notifications,
    IEmailSender email, ILogger<LedgerJobs> logger)
{
    private static readonly CultureInfo Culture = CultureInfo.GetCultureInfo(CondoProfile.Current.Culture);

    /// <summary>Day 1: this month's fee for every non-frozen apartment, paid right away from any credit.</summary>
    public Task<GenerateChargesResult> GenerateChargesAsync(CancellationToken cancellationToken) =>
        db.InTransactionAsync(ct => ledger.GenerateChargesAsync(clock.CurrentPeriod, AuditSource.System, null, ct), cancellationToken);

    /// <summary>
    /// Daily: one late fee per overdue monthly fee (once, never on opening debt or on late fees), while the
    /// percentage is above 0 and the apartment is not frozen. The fee falls due with the current month's charge.
    /// </summary>
    public Task<int> ApplyLateFeesAsync(CancellationToken cancellationToken) =>
        db.InTransactionAsync(async ct =>
        {
            var settings = await CondoSettingsStore.GetOrCreateAsync(db, time, ct);
            if (settings.LateFeePercent <= 0 || !CondoProfile.Current.Modules.LateFees)
            {
                return 0;
            }

            var today = clock.Today;
            var frozen = await db.Apartments.Where(a => a.IsFrozen).Select(a => a.Id).ToListAsync(ct);
            var candidates = await db.Charges
                .Where(c => c.Type == ChargeType.Maintenance && c.DueDate < today && !frozen.Contains(c.ApartmentId)
                    && !db.Charges.Any(fee => fee.ParentChargeId == c.Id))
                .ToListAsync(ct);
            var ids = candidates.Select(c => c.Id).ToList();
            var paid = await db.PaymentAllocations.Where(a => ids.Contains(a.ChargeId) && a.ReversedAt == null)
                .GroupBy(a => a.ChargeId).Select(g => new { g.Key, Paid = g.Sum(a => a.Amount) })
                .ToDictionaryAsync(x => x.Key, x => x.Paid, ct);

            var now = time.GetUtcNow();
            var dueDate = settings.DueDateFor(clock.CurrentPeriod);
            var apartments = new HashSet<Guid>();
            var created = 0;
            foreach (var charge in candidates.Where(c => paid.GetValueOrDefault(c.Id) < c.Amount && LateFeePolicy.Applies(c.Type)))
            {
                var amount = LateFeePolicy.Amount(charge.Amount, settings.LateFeePercent);
                if (amount <= 0)
                {
                    continue;
                }

                var fee = Charge.LateFee(charge, amount, dueDate, now);
                db.Charges.Add(fee);
                db.AuditEvents.Add(new AuditEvent(AuditEntities.Charge, fee.Id.ToString(), "LateFeeCreated", AuditSource.System, null, now,
                    newValues: AuditLog.Json(new { fee.Period, fee.Amount, fee.DueDate, ParentChargeId = charge.Id, settings.LateFeePercent }),
                    apartmentId: charge.ApartmentId, chargeId: fee.Id));

                // Also on the overdue charge's own timeline.
                db.AuditEvents.Add(new AuditEvent(AuditEntities.Charge, charge.Id.ToString(), "LateFeeCreated", AuditSource.System, null, now,
                    newValues: AuditLog.Json(new { LateFeeChargeId = fee.Id, fee.Amount }), apartmentId: charge.ApartmentId, chargeId: charge.Id));
                apartments.Add(charge.ApartmentId);
                created++;
            }

            await db.SaveChangesAsync(ct);
            foreach (var apartmentId in apartments)
            {
                await ledger.SettleAsync(apartmentId, AuditSource.System, null, ct);
            }

            await db.SaveChangesAsync(ct);
            return created;
        }, cancellationToken);

    /// <summary>
    /// Day 15: a neutral reminder (in-app notice, push and email) to every user of an apartment with overdue debt
    /// that is not frozen. Once per apartment and month.
    /// </summary>
    public async Task<int> SendDebtRemindersAsync(CancellationToken cancellationToken)
    {
        var today = clock.Today;
        var now = time.GetUtcNow();
        var monthStart = CondoClock.StartOfDayUtc(clock.CurrentPeriod.FirstDay);
        var alreadyReminded = (await db.AuditEvents
                .Where(e => e.EntityType == AuditEntities.Apartment && e.Action == "ReminderSent" && e.CreatedAt >= monthStart)
                .Select(e => e.ApartmentId)
                .ToListAsync(cancellationToken))
            .ToHashSet();

        var apartments = await db.Apartments.AsNoTracking().Where(a => !a.IsFrozen).Select(a => new { a.Id, a.Code }).ToListAsync(cancellationToken);
        var charges = await db.Charges.AsNoTracking().Where(c => c.DueDate < today)
            .Select(c => new { c.Id, c.ApartmentId, c.Period, c.Amount, c.DueDate, c.CreatedAt })
            .ToListAsync(cancellationToken);
        var chargeIds = charges.Select(c => c.Id).ToList();
        var allocations = await db.PaymentAllocations.AsNoTracking().Where(a => chargeIds.Contains(a.ChargeId))
            .Select(a => new LedgerAllocation(a.ChargeId, a.Amount, a.CreatedAt, a.ReversedAt))
            .ToListAsync(cancellationToken);
        var balances = DebtCalculator.BalancesAt(charges.Select(c => new LedgerCharge(c.Id, c.Amount, c.DueDate, c.CreatedAt)), allocations, now)
            .Where(b => b.Outstanding > 0)
            .ToDictionary(b => b.ChargeId);

        var reminded = 0;
        foreach (var apartment in apartments.Where(a => !alreadyReminded.Contains(a.Id)))
        {
            var overdue = charges.Where(c => c.ApartmentId == apartment.Id && balances.ContainsKey(c.Id)).ToList();
            if (overdue.Count == 0)
            {
                continue;
            }

            var amount = overdue.Sum(c => balances[c.Id].Outstanding);
            var months = overdue.Select(c => c.Period).Distinct().Count();
            var users = await db.Users.AsNoTracking().Where(u => u.IsActive && u.ApartmentId == apartment.Id)
                .Select(u => new { u.Email, u.FirstName, u.LastName }).ToListAsync(cancellationToken);

            await db.InTransactionAsync(async ct =>
            {
                await notifications.NotifyApartmentAsync(apartment.Id, NotificationType.DebtReminder,
                    new DebtReminderData(apartment.Code, amount, months), ct);
                db.AuditEvents.Add(new AuditEvent(AuditEntities.Apartment, apartment.Id.ToString(), "ReminderSent", AuditSource.System, null, now,
                    newValues: AuditLog.Json(new { Amount = amount, Months = months, Users = users.Count }), apartmentId: apartment.Id));
                await db.SaveChangesAsync(ct);
                return 0;
            }, cancellationToken);

            if (CondoProfile.Current.Modules.EmailReminders)
            {
                foreach (var user in users.Where(u => u.Email is not null))
                {
                    await SendReminderEmailAsync(user.Email!, $"{user.FirstName} {user.LastName}", user.FirstName, apartment.Code, amount, months, cancellationToken);
                }
            }

            reminded++;
        }

        return reminded;
    }

    private async Task SendReminderEmailAsync(
        string to, string name, string firstName, string apartment, decimal amount, int months, CancellationToken cancellationToken)
    {
        var condo = CondoProfile.Current.DisplayName;
        var money = amount.ToString("C2", Culture);
        try
        {
            await email.SendAsync(new EmailMessage(to, name,
                Format(EmailTexts.ReminderSubject, firstName, condo, apartment, money, months),
                Format(EmailTexts.ReminderBody, firstName, condo, apartment, money, months)), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The in-app notice and the push already went out; one bad address must not stop the others.
            LogReminderEmailFailed(ex, apartment);
        }
    }

    private static string Format(string template, params object[] args) => string.Format(Culture, template, args);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not email the reminder of {Apartment}")]
    private partial void LogReminderEmailFailed(Exception exception, string apartment);
}
