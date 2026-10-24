using Mantenio.Domain.Audit;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Mantenio.Api.Infrastructure.Persistence.Configurations;

internal sealed class AuditEventConfiguration : IEntityTypeConfiguration<AuditEvent>
{
    public void Configure(EntityTypeBuilder<AuditEvent> builder)
    {
        builder.ToTable("AuditEvents");
        builder.Property(x => x.EntityType).HasMaxLength(50);
        builder.Property(x => x.EntityId).HasMaxLength(64);
        builder.Property(x => x.Action).HasMaxLength(50);
        builder.Property(x => x.Source).HasConversion<string>().HasMaxLength(20);
        builder.Property(x => x.OldValues).HasColumnType("jsonb");
        builder.Property(x => x.NewValues).HasColumnType("jsonb");
        builder.Property(x => x.Reason).HasMaxLength(500);
        builder.HasIndex(x => new { x.EntityType, x.EntityId });
        builder.HasIndex(x => x.ChargeId);
        builder.HasIndex(x => x.ApartmentId);
        builder.HasIndex(x => x.CreatedAt);
    }
}
