using Mantenio.Api.Infrastructure.Persistence;
using Mantenio.Api.Infrastructure.Time;
using Mantenio.Domain.Ledger;
using Mantenio.Shared.Reports;
using Microsoft.EntityFrameworkCore;

namespace Mantenio.Api.Features.Reports;

/// <summary>
/// Overdue debt on a date, rebuilt from the ledger as it was at the end of that day (Lima): later payments, voids
/// and charges do not change a past report. Frozen apartments are listed (marked) because their debt is still owed.
/// </summary>
internal sealed class DelinquencyService(AppDbContext db)
{
    public async Task<DelinquencyReport> BuildAsync(DateOnly date, CancellationToken cancellationToken)
    {
        var endOfDay = CondoClock.EndOfDayUtc(date);
        var apartments = await db.Apartments.AsNoTracking().Include(a => a.Tower)
            .Select(a => new { a.Id, a.Code, a.Number, Tower = a.Tower!.Name, TowerOrder = a.Tower.SortOrder })
            .ToListAsync(cancellationToken);
        var charges = await db.Charges.AsNoTracking().Where(c => c.CreatedAt <= endOfDay)
            .Select(c => new { c.Id, c.ApartmentId, c.Period, c.Amount, c.DueDate, c.CreatedAt })
            .ToListAsync(cancellationToken);
        var allocations = await db.PaymentAllocations.AsNoTracking().Where(a => a.CreatedAt <= endOfDay)
            .Select(a => new LedgerAllocation(a.ChargeId, a.Amount, a.CreatedAt, a.ReversedAt))
            .ToListAsync(cancellationToken);
        var frozen = (await db.ApartmentFreezes.AsNoTracking()
                .Where(f => f.StartedAt <= endOfDay && (f.EndedAt == null || f.EndedAt > endOfDay))
                .Select(f => f.ApartmentId)
                .ToListAsync(cancellationToken))
            .ToHashSet();

        var balances = DebtCalculator.BalancesAt(
                charges.Select(c => new LedgerCharge(c.Id, c.Amount, c.DueDate, c.CreatedAt)), allocations, endOfDay)
            .Where(b => b.DueDate < date && b.Outstanding > 0)
            .ToDictionary(b => b.ChargeId);
        var overdueByApartment = charges
            .Where(c => balances.ContainsKey(c.Id))
            .GroupBy(c => c.ApartmentId)
            .ToDictionary(g => g.Key, g => (
                Periods: g.Select(c => c.Period).Distinct().Order().ToList(),
                Amount: g.Sum(c => balances[c.Id].Outstanding)));

        var towers = apartments
            .Where(a => overdueByApartment.ContainsKey(a.Id))
            .GroupBy(a => (a.Tower, a.TowerOrder))
            .OrderBy(g => g.Key.TowerOrder).ThenBy(g => g.Key.Tower, StringComparer.Ordinal)
            .Select(g =>
            {
                var rows = g
                    .OrderBy(a => a.Number.Length).ThenBy(a => a.Number, StringComparer.Ordinal)
                    .Select(a => new DelinquencyRow(a.Id, a.Code, a.Number, frozen.Contains(a.Id),
                        overdueByApartment[a.Id].Periods, overdueByApartment[a.Id].Amount))
                    .ToList();
                return new DelinquencyTower(g.Key.Tower, rows, rows.Sum(r => r.Amount));
            })
            .ToList();

        return new DelinquencyReport(date, towers, towers.Sum(t => t.Total), towers.Sum(t => t.Rows.Count));
    }
}
