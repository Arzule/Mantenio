using Mantenio.Domain.Condo;
using Microsoft.EntityFrameworkCore;

namespace Mantenio.Api.Infrastructure.Persistence;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    /// <summary>Connection string name shared by Aspire (local) and Container Apps secrets (prod).</summary>
    public const string ConnectionName = "mantenio";

    public DbSet<CondoSettings> CondoSettings => Set<CondoSettings>();
    public DbSet<Tower> Towers => Set<Tower>();
    public DbSet<Apartment> Apartments => Set<Apartment>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // Money: numeric(10,2) everywhere unless a configuration says otherwise.
        configurationBuilder.Properties<decimal>().HavePrecision(10, 2);
    }
}
