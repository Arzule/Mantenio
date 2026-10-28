using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mantenio.Api.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class HistoricalPayments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PaymentProofs_UniqueOperation",
                table: "PaymentProofs");

            migrationBuilder.AddColumn<string>(
                name: "PaperReceiptNumber",
                table: "PaymentProofs",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_PaymentProofs_UniqueOperation",
                table: "PaymentProofs",
                columns: new[] { "BankName", "OperationNumber" },
                unique: true,
                filter: "\"Status\" <> 'Rejected' AND \"OperationNumber\" <> ''");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PaymentProofs_UniqueOperation",
                table: "PaymentProofs");

            migrationBuilder.DropColumn(
                name: "PaperReceiptNumber",
                table: "PaymentProofs");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentProofs_UniqueOperation",
                table: "PaymentProofs",
                columns: new[] { "BankName", "OperationNumber" },
                unique: true,
                filter: "\"Status\" <> 'Rejected'");
        }
    }
}
