using Mantenio.Api.Infrastructure.Identity;
using Mantenio.Domain.Audit;
using Mantenio.Domain.Banking;
using Mantenio.Domain.Condo;
using Mantenio.Domain.Fees;
using Mantenio.Domain.Identity;
using Mantenio.Domain.Notifications;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Mantenio.Api.Infrastructure.Persistence;

/// <summary>Identity user tables (no roles: the role is a column on the user) plus the condo data.</summary>
public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : IdentityUserContext<AppUser, Guid>(options)
{
    /// <summary>Connection string name shared by Aspire (local) and Container Apps secrets (prod).</summary>
    public const string ConnectionName = "mantenio";

    public DbSet<CondoSettings> CondoSettings => Set<CondoSettings>();
    public DbSet<Tower> Towers => Set<Tower>();
    public DbSet<Apartment> Apartments => Set<Apartment>();
    public DbSet<InvitationCode> InvitationCodes => Set<InvitationCode>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();
    public DbSet<FeeChange> FeeChanges => Set<FeeChange>();
    public DbSet<ApartmentFreeze> ApartmentFreezes => Set<ApartmentFreeze>();
    public DbSet<BankAccount> BankAccounts => Set<BankAccount>();
    public DbSet<UserNotification> Notifications => Set<UserNotification>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // Money: numeric(10,2) everywhere unless a configuration says otherwise.
        configurationBuilder.Properties<decimal>().HavePrecision(10, 2);
    }
}
