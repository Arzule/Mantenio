using Mantenio.Api.Infrastructure.Identity;
using Mantenio.Domain.Condo;
using Mantenio.Domain.Ledger;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Mantenio.Api.Infrastructure.Persistence.Configurations;

internal sealed class ChargeConfiguration : IEntityTypeConfiguration<Charge>
{
    public void Configure(EntityTypeBuilder<Charge> builder)
    {
        builder.ToTable("Charges");
        builder.Property(x => x.Type).HasConversion<string>().HasMaxLength(20);
        builder.HasOne<Apartment>().WithMany().HasForeignKey(x => x.ApartmentId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Charge>().WithMany().HasForeignKey(x => x.ParentChargeId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => new { x.ApartmentId, x.Period });

        // One monthly fee per apartment and month, even if the generation job runs twice.
        builder.HasIndex(x => new { x.ApartmentId, x.Period, x.Type })
            .IsUnique()
            .HasFilter("\"Type\" = 'Maintenance'")
            .HasDatabaseName("IX_Charges_OneMaintenancePerMonth");

        // One late fee per overdue charge.
        builder.HasIndex(x => x.ParentChargeId).IsUnique().HasFilter("\"ParentChargeId\" IS NOT NULL");
    }
}

internal sealed class PaymentProofConfiguration : IEntityTypeConfiguration<PaymentProof>
{
    public void Configure(EntityTypeBuilder<PaymentProof> builder)
    {
        builder.ToTable("PaymentProofs");
        builder.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
        builder.Property(x => x.BankName).HasMaxLength(80);
        builder.Property(x => x.OperationNumber).HasMaxLength(40);
        builder.Property(x => x.ImageObjectKey).HasMaxLength(200);
        builder.Property(x => x.PayerName).HasMaxLength(120);
        builder.Ignore(x => x.IsRegisteredByAdmin);
        builder.Ignore(x => x.HasImage);
        builder.Property(x => x.PaperReceiptNumber).HasMaxLength(40);
        builder.Property(x => x.RejectionReason).HasMaxLength(500);
        builder.Property(x => x.VoidReason).HasMaxLength(500);
        builder.Property<uint>("RowVersion").IsRowVersion();
        builder.HasOne<Apartment>().WithMany().HasForeignKey(x => x.ApartmentId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<AppUser>().WithMany().HasForeignKey(x => x.SubmittedById).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => new { x.Status, x.SubmittedAt });
        builder.HasIndex(x => x.ApartmentId);

        // Same bank operation twice is a duplicate, unless the earlier one was rejected. Historical payments
        // may have no operation number, so empty ones are not compared.
        builder.HasIndex(x => new { x.BankName, x.OperationNumber })
            .IsUnique()
            .HasFilter("\"Status\" <> 'Rejected' AND \"OperationNumber\" <> ''")
            .HasDatabaseName("IX_PaymentProofs_UniqueOperation");
    }
}

internal sealed class PaymentAllocationConfiguration : IEntityTypeConfiguration<PaymentAllocation>
{
    public void Configure(EntityTypeBuilder<PaymentAllocation> builder)
    {
        builder.ToTable("PaymentAllocations");
        builder.Ignore(x => x.IsActive);
        builder.HasOne<PaymentProof>().WithMany().HasForeignKey(x => x.PaymentProofId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Charge>().WithMany().HasForeignKey(x => x.ChargeId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => x.ChargeId);
        builder.HasIndex(x => x.PaymentProofId);
    }
}

internal sealed class ReceiptConfiguration : IEntityTypeConfiguration<Receipt>
{
    public void Configure(EntityTypeBuilder<Receipt> builder)
    {
        builder.ToTable("Receipts");
        builder.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
        builder.Property(x => x.Number).HasMaxLength(20);
        builder.Property(x => x.PdfObjectKey).HasMaxLength(200);
        builder.HasOne<PaymentProof>().WithOne().HasForeignKey<Receipt>(x => x.PaymentProofId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => x.Number).IsUnique();
        builder.HasIndex(x => x.Sequence).IsUnique();
        builder.HasIndex(x => x.FolioPeriod);
    }
}
