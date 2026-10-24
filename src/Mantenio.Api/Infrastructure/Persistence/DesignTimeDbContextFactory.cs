using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Mantenio.Api.Infrastructure.Persistence;

/// <summary>
/// Used only by `dotnet ef` (migrations). It never connects to a real database.
/// </summary>
internal sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=localhost;Database=design_time;Username=design_time")
            .Options;
        return new AppDbContext(options);
    }
}
