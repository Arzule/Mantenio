using Mantenio.Domain.Condo;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Mantenio.Api.Infrastructure.Persistence.Configurations;

internal sealed class CondoSettingsConfiguration : IEntityTypeConfiguration<CondoSettings>
{
    public void Configure(EntityTypeBuilder<CondoSettings> builder)
    {
        builder.ToTable("CondoSettings", t =>
        {
            t.HasCheckConstraint("CK_CondoSettings_Singleton", $"\"Id\" = {CondoSettings.SingletonId}");
            t.HasCheckConstraint("CK_CondoSettings_DueDay", "\"DueDay\" BETWEEN 1 AND 28");
        });
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.LateFeePercent).HasPrecision(5, 2);
    }
}

internal sealed class TowerConfiguration : IEntityTypeConfiguration<Tower>
{
    public void Configure(EntityTypeBuilder<Tower> builder)
    {
        builder.ToTable("Towers");
        builder.Property(x => x.Name).HasMaxLength(50);
        builder.HasIndex(x => x.Name).IsUnique();
        builder.HasMany(x => x.Apartments)
            .WithOne(x => x.Tower)
            .HasForeignKey(x => x.TowerId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class ApartmentConfiguration : IEntityTypeConfiguration<Apartment>
{
    public void Configure(EntityTypeBuilder<Apartment> builder)
    {
        builder.ToTable("Apartments");
        builder.Property(x => x.Number).HasMaxLength(20);
        builder.Property(x => x.Code).HasMaxLength(80);
        builder.HasIndex(x => x.Code).IsUnique();
        builder.HasIndex(x => new { x.TowerId, x.Number }).IsUnique();
    }
}
