using Mantenio.Domain.Banking;
using Mantenio.Domain.Condo;
using Mantenio.Domain.Fees;
using Mantenio.Domain.Notifications;
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
            t.HasCheckConstraint("CK_CondoSettings_DueMonthOffset", "\"DueMonthOffset\" BETWEEN 0 AND 1");
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

internal sealed class ApartmentFreezeConfiguration : IEntityTypeConfiguration<ApartmentFreeze>
{
    public void Configure(EntityTypeBuilder<ApartmentFreeze> builder)
    {
        builder.ToTable("ApartmentFreezes");
        builder.Ignore(x => x.IsActive);
        builder.Property(x => x.Reason).HasMaxLength(500);
        builder.Property(x => x.EndReason).HasMaxLength(500);
        builder.HasOne<Apartment>().WithMany().HasForeignKey(x => x.ApartmentId).OnDelete(DeleteBehavior.Restrict);

        // At most one active freeze per apartment.
        builder.HasIndex(x => x.ApartmentId).IsUnique().HasFilter("\"EndedAt\" IS NULL").HasDatabaseName("IX_ApartmentFreezes_Active");
    }
}

internal sealed class FeeChangeConfiguration : IEntityTypeConfiguration<FeeChange>
{
    public void Configure(EntityTypeBuilder<FeeChange> builder)
    {
        builder.ToTable("FeeChanges");
        builder.Property(x => x.Scope).HasConversion<string>().HasMaxLength(20);
        builder.Property(x => x.Reason).HasMaxLength(500);
        builder.HasOne<Apartment>().WithMany().HasForeignKey(x => x.ApartmentId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => new { x.Scope, x.ApartmentId, x.EffectivePeriod });
    }
}

internal sealed class BankAccountConfiguration : IEntityTypeConfiguration<BankAccount>
{
    public void Configure(EntityTypeBuilder<BankAccount> builder)
    {
        builder.ToTable("BankAccounts");
        builder.Property(x => x.BankName).HasMaxLength(80);
        builder.Property(x => x.HolderName).HasMaxLength(150);
        builder.Property(x => x.AccountNumber).HasMaxLength(BankAccount.MaxAccountNumberLength);
        builder.Property(x => x.Cci).HasMaxLength(BankAccount.CciLength);
    }
}

internal sealed class UserNotificationConfiguration : IEntityTypeConfiguration<UserNotification>
{
    public void Configure(EntityTypeBuilder<UserNotification> builder)
    {
        builder.ToTable("Notifications");
        builder.Ignore(x => x.IsPendingAcknowledgement);
        builder.Property(x => x.Type).HasConversion<string>().HasMaxLength(40);
        builder.Property(x => x.Data).HasColumnType("jsonb");
        builder.HasIndex(x => new { x.UserId, x.CreatedAt });
    }
}
