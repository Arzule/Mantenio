using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mantenio.Api.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Ledger : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "LastReceiptSequence",
                table: "CondoSettings",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.CreateTable(
                name: "Charges",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ApartmentId = table.Column<Guid>(type: "uuid", nullable: false),
                    Period = table.Column<int>(type: "integer", nullable: false),
                    Type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: false),
                    DueDate = table.Column<DateOnly>(type: "date", nullable: false),
                    ParentChargeId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Charges", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Charges_Apartments_ApartmentId",
                        column: x => x.ApartmentId,
                        principalTable: "Apartments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Charges_Charges_ParentChargeId",
                        column: x => x.ParentChargeId,
                        principalTable: "Charges",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PaymentProofs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ApartmentId = table.Column<Guid>(type: "uuid", nullable: false),
                    SubmittedById = table.Column<Guid>(type: "uuid", nullable: false),
                    DeclaredAmount = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: false),
                    ApprovedAmount = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: true),
                    OperationDate = table.Column<DateOnly>(type: "date", nullable: false),
                    BankName = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    OperationNumber = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    ImageObjectKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    SubmittedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ReviewedById = table.Column<Guid>(type: "uuid", nullable: true),
                    ReviewedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RejectionReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    VoidedById = table.Column<Guid>(type: "uuid", nullable: true),
                    VoidedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    VoidReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PaymentProofs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PaymentProofs_Apartments_ApartmentId",
                        column: x => x.ApartmentId,
                        principalTable: "Apartments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PaymentProofs_AspNetUsers_SubmittedById",
                        column: x => x.SubmittedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PaymentAllocations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PaymentProofId = table.Column<Guid>(type: "uuid", nullable: false),
                    ChargeId = table.Column<Guid>(type: "uuid", nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ReversedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PaymentAllocations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PaymentAllocations_Charges_ChargeId",
                        column: x => x.ChargeId,
                        principalTable: "Charges",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PaymentAllocations_PaymentProofs_PaymentProofId",
                        column: x => x.PaymentProofId,
                        principalTable: "PaymentProofs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Receipts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PaymentProofId = table.Column<Guid>(type: "uuid", nullable: false),
                    FolioPeriod = table.Column<int>(type: "integer", nullable: false),
                    Sequence = table.Column<long>(type: "bigint", nullable: false),
                    Number = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    PdfObjectKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    IssuedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    VoidedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Receipts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Receipts_PaymentProofs_PaymentProofId",
                        column: x => x.PaymentProofId,
                        principalTable: "PaymentProofs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Charges_ApartmentId_Period",
                table: "Charges",
                columns: new[] { "ApartmentId", "Period" });

            migrationBuilder.CreateIndex(
                name: "IX_Charges_OneMaintenancePerMonth",
                table: "Charges",
                columns: new[] { "ApartmentId", "Period", "Type" },
                unique: true,
                filter: "\"Type\" = 'Maintenance'");

            migrationBuilder.CreateIndex(
                name: "IX_Charges_ParentChargeId",
                table: "Charges",
                column: "ParentChargeId",
                unique: true,
                filter: "\"ParentChargeId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentAllocations_ChargeId",
                table: "PaymentAllocations",
                column: "ChargeId");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentAllocations_PaymentProofId",
                table: "PaymentAllocations",
                column: "PaymentProofId");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentProofs_ApartmentId",
                table: "PaymentProofs",
                column: "ApartmentId");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentProofs_Status_SubmittedAt",
                table: "PaymentProofs",
                columns: new[] { "Status", "SubmittedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_PaymentProofs_SubmittedById",
                table: "PaymentProofs",
                column: "SubmittedById");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentProofs_UniqueOperation",
                table: "PaymentProofs",
                columns: new[] { "BankName", "OperationNumber" },
                unique: true,
                filter: "\"Status\" <> 'Rejected'");

            migrationBuilder.CreateIndex(
                name: "IX_Receipts_FolioPeriod",
                table: "Receipts",
                column: "FolioPeriod");

            migrationBuilder.CreateIndex(
                name: "IX_Receipts_Number",
                table: "Receipts",
                column: "Number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Receipts_PaymentProofId",
                table: "Receipts",
                column: "PaymentProofId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Receipts_Sequence",
                table: "Receipts",
                column: "Sequence",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PaymentAllocations");

            migrationBuilder.DropTable(
                name: "Receipts");

            migrationBuilder.DropTable(
                name: "Charges");

            migrationBuilder.DropTable(
                name: "PaymentProofs");

            migrationBuilder.DropColumn(
                name: "LastReceiptSequence",
                table: "CondoSettings");
        }
    }
}
