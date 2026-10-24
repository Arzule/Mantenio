using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mantenio.Api.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCondoStructure : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CondoSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false),
                    MonthlyFee = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: false),
                    DueDay = table.Column<int>(type: "integer", nullable: false),
                    LateFeePercent = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    MaxAdmins = table.Column<int>(type: "integer", nullable: false),
                    TrackingStartPeriod = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CondoSettings", x => x.Id);
                    table.CheckConstraint("CK_CondoSettings_DueDay", "\"DueDay\" BETWEEN 1 AND 28");
                    table.CheckConstraint("CK_CondoSettings_Singleton", "\"Id\" = 1");
                });

            migrationBuilder.CreateTable(
                name: "Towers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Towers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Apartments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TowerId = table.Column<Guid>(type: "uuid", nullable: false),
                    Number = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Code = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    FeeOverride = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: true),
                    IsFrozen = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Apartments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Apartments_Towers_TowerId",
                        column: x => x.TowerId,
                        principalTable: "Towers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Apartments_Code",
                table: "Apartments",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Apartments_TowerId_Number",
                table: "Apartments",
                columns: new[] { "TowerId", "Number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Towers_Name",
                table: "Towers",
                column: "Name",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Apartments");

            migrationBuilder.DropTable(
                name: "CondoSettings");

            migrationBuilder.DropTable(
                name: "Towers");
        }
    }
}
