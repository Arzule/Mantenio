using Mantenio.Api.Infrastructure.Errors;
using Mantenio.Api.Infrastructure.Persistence;
using Mantenio.Api.Infrastructure.Time;
using Mantenio.Domain.Audit;
using Mantenio.Domain.Common;
using Mantenio.Domain.Ledger;
using Mantenio.Shared.Auth;
using Mantenio.Shared.Errors;
using Mantenio.Shared.Reports;
using Mantenio.Shared.Users;
using Microsoft.EntityFrameworkCore;

namespace Mantenio.Api.Features.Reports;

/// <summary>Admin reports (generated on demand, never stored), the admin dashboard and the audit viewer.</summary>
internal static class ReportEndpoints
{
    private const int MaxPageSize = 100;

    public static IEndpointRouteBuilder MapReportEndpoints(this IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup("/api/admin").WithTags("Reports").RequireAuthorization(MantenioPolicies.AdminOnly);
        admin.MapGet("/dashboard", DashboardAsync).Produces<AdminDashboard>();
        admin.MapGet("/reports/delinquency", DelinquencyAsync).Produces<DelinquencyReport>();
        admin.MapGet("/reports/delinquency/excel", DelinquencyExcelAsync);
        admin.MapGet("/reports/delinquency/pdf", DelinquencyPdfAsync);
        admin.MapGet("/reports/folio/{period:int}/pdf", FolioPdfAsync);
        admin.MapGet("/reports/period/excel", PeriodExcelAsync);
        admin.MapGet("/audit", AuditAsync).Produces<PagedResult<AuditItem>>();
        admin.MapGet("/audit/actors", ActorsAsync).Produces<IReadOnlyList<AuditActor>>();
        return app;
    }

    private static async Task<IResult> DashboardAsync(AppDbContext db, CondoClock clock, DelinquencyService delinquency, CancellationToken cancellationToken)
    {
        var report = await delinquency.BuildAsync(clock.Today, cancellationToken);
        var apartmentsByTower = await db.Apartments.AsNoTracking().GroupBy(a => a.Tower!.Name)
            .Select(g => new { Tower = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Tower, x => x.Count, cancellationToken);
        var period = clock.CurrentPeriod;
        var monthStart = CondoClock.StartOfDayUtc(period.FirstDay);
        var pending = await db.PaymentProofs.CountAsync(p => p.Status == PaymentProofStatus.Pending, cancellationToken);
        var collected = await db.PaymentProofs
            .Where(p => p.Status == PaymentProofStatus.Approved && p.ReviewedAt >= monthStart)
            .SumAsync(p => p.ApprovedAmount ?? 0, cancellationToken);
        var receipts = await db.Receipts.CountAsync(r => r.FolioPeriod == period.Value && r.Status == ReceiptStatus.Issued, cancellationToken);

        var towers = report.Towers
            .Select(t => new TowerDebt(t.Name, t.Rows.Count, apartmentsByTower.GetValueOrDefault(t.Name), t.Total))
            .ToList();
        return TypedResults.Ok(new AdminDashboard(pending, report.Total, report.DelinquentApartments, towers, period.Value, collected, receipts));
    }

    private static async Task<IResult> DelinquencyAsync(DateOnly? date, CondoClock clock, DelinquencyService delinquency, CancellationToken cancellationToken) =>
        TypedResults.Ok(await delinquency.BuildAsync(date ?? clock.Today, cancellationToken));

    private static async Task<IResult> DelinquencyExcelAsync(DateOnly? date, CondoClock clock, DelinquencyService delinquency, CancellationToken cancellationToken)
    {
        var report = await delinquency.BuildAsync(date ?? clock.Today, cancellationToken);
        return TypedResults.File(ReportFiles.DelinquencyExcel(report), ReportFiles.ExcelContentType, ReportFiles.DelinquencyFileName(report.Date, ".xlsx"));
    }

    private static async Task<IResult> DelinquencyPdfAsync(DateOnly? date, CondoClock clock, DelinquencyService delinquency, CancellationToken cancellationToken)
    {
        var report = await delinquency.BuildAsync(date ?? clock.Today, cancellationToken);
        return TypedResults.File(ReportFiles.DelinquencyPdf(report, clock.Now), ReportFiles.PdfContentType, ReportFiles.DelinquencyFileName(report.Date, ".pdf"));
    }

    /// <summary>The audit report of a range of months (from and to as yyyymm, at most ten years) as Excel.</summary>
    private static async Task<IResult> PeriodExcelAsync(int from, int to, PeriodReportService reports, CancellationToken cancellationToken)
    {
        if (!Period.TryFromValue(from, out var first) || !Period.TryFromValue(to, out var last) || from > to
            || (last.Year - first.Year) * 12 + last.Month - first.Month >= PeriodReportService.MaxMonths)
        {
            return ApiProblems.BadRequest(ErrorCodes.InvalidPeriod);
        }

        var report = await reports.BuildAsync(first, last, cancellationToken);
        return TypedResults.File(ReportFiles.PeriodExcel(report), ReportFiles.ExcelContentType, ReportFiles.PeriodFileName(from, to));
    }

    private static async Task<IResult> FolioPdfAsync(int period, AppDbContext db, CondoClock clock, CancellationToken cancellationToken)
    {
        if (!Period.TryFromValue(period, out _))
        {
            return ApiProblems.BadRequest(ErrorCodes.InvalidPeriod);
        }

        var rows = await (
                from r in db.Receipts.AsNoTracking()
                where r.FolioPeriod == period
                join p in db.PaymentProofs on r.PaymentProofId equals p.Id
                join a in db.Apartments on p.ApartmentId equals a.Id
                join u in db.Users on p.SubmittedById equals u.Id
                orderby r.Sequence
                select new
                {
                    r.Number, r.IssuedAt, Voided = r.Status == ReceiptStatus.Voided, ProofId = p.Id, a.Code,
                    Payer = u.FirstName + " " + u.LastName, Amount = p.ApprovedAmount ?? 0, p.BankName, p.OperationNumber,
                })
            .ToListAsync(cancellationToken);

        // Months each payment covered when it was issued (reversed allocations included, as on the receipt).
        var proofIds = rows.Select(r => r.ProofId).ToList();
        var periods = (await (
                    from al in db.PaymentAllocations.AsNoTracking()
                    where proofIds.Contains(al.PaymentProofId)
                    join c in db.Charges on al.ChargeId equals c.Id
                    select new { al.PaymentProofId, c.Period })
                .ToListAsync(cancellationToken))
            .GroupBy(x => x.PaymentProofId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<int>)g.Select(x => x.Period).Distinct().Order().ToList());

        var lines = rows.Select(r => new FolioLine(r.Number, CondoClock.ToLocal(r.IssuedAt), r.Code, r.Payer,
                periods.GetValueOrDefault(r.ProofId) ?? [], r.Amount, $"{r.OperationNumber} ({r.BankName})", r.Voided))
            .ToList();
        return TypedResults.File(ReportFiles.FolioPdf(period, lines, clock.Now), ReportFiles.PdfContentType, ReportFiles.FolioFileName(period));
    }

    private static async Task<IResult> AuditAsync(
        DateOnly? from, DateOnly? to, AuditSourceFilter? source, string? entityType, Guid? apartmentId, Guid? actorId,
        int? page, int? pageSize, AppDbContext db, CancellationToken cancellationToken)
    {
        var query = db.AuditEvents.AsNoTracking();
        if (from is { } start)
        {
            var since = CondoClock.StartOfDayUtc(start);
            query = query.Where(e => e.CreatedAt >= since);
        }

        if (to is { } end)
        {
            var until = CondoClock.EndOfDayUtc(end);
            query = query.Where(e => e.CreatedAt <= until);
        }

        if (source == AuditSourceFilter.People)
        {
            query = query.Where(e => e.Source != AuditSource.System);
        }
        else if (source is { } wanted)
        {
            var domainSource = (AuditSource)(int)wanted;
            query = query.Where(e => e.Source == domainSource);
        }

        if (!string.IsNullOrWhiteSpace(entityType))
        {
            query = query.Where(e => e.EntityType == entityType);
        }

        if (apartmentId is not null)
        {
            query = query.Where(e => e.ApartmentId == apartmentId);
        }

        if (actorId is not null)
        {
            query = query.Where(e => e.ActorUserId == actorId);
        }

        var size = Math.Clamp(pageSize ?? 25, 1, MaxPageSize);
        var number = Math.Max(page ?? 1, 1);
        var total = await query.CountAsync(cancellationToken);
        var rows = await (
                from e in query
                join u in db.Users on e.ActorUserId equals u.Id into actors
                from u in actors.DefaultIfEmpty()
                join a in db.Apartments on e.ApartmentId equals a.Id into apartments
                from a in apartments.DefaultIfEmpty()
                orderby e.CreatedAt descending, e.Id descending
                select new
                {
                    e.Id, e.CreatedAt, e.EntityType, e.Action, e.Source,
                    Actor = u == null ? null : u.FirstName + " " + u.LastName,
                    Apartment = a == null ? null : a.Code,
                    e.Reason, e.OldValues, e.NewValues,
                })
            .Skip((number - 1) * size).Take(size)
            .ToListAsync(cancellationToken);

        var items = rows.Select(r => new AuditItem(r.Id, r.CreatedAt, r.EntityType, r.Action, r.Source.ToString(), r.Actor, r.Apartment,
            r.Reason, r.OldValues, r.NewValues)).ToList();
        return TypedResults.Ok(new PagedResult<AuditItem>(items, total));
    }

    private static async Task<IResult> ActorsAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        var actors = await db.Users.AsNoTracking()
            .Where(u => db.AuditEvents.Any(e => e.ActorUserId == u.Id))
            .OrderBy(u => u.FirstName).ThenBy(u => u.LastName)
            .Select(u => new AuditActor(u.Id, u.FirstName + " " + u.LastName))
            .ToListAsync(cancellationToken);
        return TypedResults.Ok(actors);
    }
}
