using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mantenio.Api.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CondoConfiguration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FeeOverride",
                table: "Apartments");

            migrationBuilder.AddColumn<int>(
                name: "DueMonthOffset",
                table: "CondoSettings",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "ApartmentFreezes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ApartmentId = table.Column<Guid>(type: "uuid", nullable: false),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    StartedById = table.Column<Guid>(type: "uuid", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    EndedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    EndedById = table.Column<Guid>(type: "uuid", nullable: true),
                    EndReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApartmentFreezes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ApartmentFreezes_Apartments_ApartmentId",
                        column: x => x.ApartmentId,
                        principalTable: "Apartments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "BankAccounts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BankName = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    HolderName = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    AccountNumber = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Cci = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedById = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BankAccounts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "FeeChanges",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Scope = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ApartmentId = table.Column<Guid>(type: "uuid", nullable: true),
                    OldAmount = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: true),
                    NewAmount = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: true),
                    EffectivePeriod = table.Column<int>(type: "integer", nullable: false),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    ChangedById = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FeeChanges", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FeeChanges_Apartments_ApartmentId",
                        column: x => x.ApartmentId,
                        principalTable: "Apartments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Notifications",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Type = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Data = table.Column<string>(type: "jsonb", nullable: false),
                    RequiresAcknowledgement = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ReadAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    AcknowledgedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Notifications", x => x.Id);
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_CondoSettings_DueMonthOffset",
                table: "CondoSettings",
                sql: "\"DueMonthOffset\" BETWEEN 0 AND 1");

            migrationBuilder.CreateIndex(
                name: "IX_ApartmentFreezes_Active",
                table: "ApartmentFreezes",
                column: "ApartmentId",
                unique: true,
                filter: "\"EndedAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_FeeChanges_ApartmentId",
                table: "FeeChanges",
                column: "ApartmentId");

            migrationBuilder.CreateIndex(
                name: "IX_FeeChanges_Scope_ApartmentId_EffectivePeriod",
                table: "FeeChanges",
                columns: new[] { "Scope", "ApartmentId", "EffectivePeriod" });

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_UserId_CreatedAt",
                table: "Notifications",
                columns: new[] { "UserId", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ApartmentFreezes");

            migrationBuilder.DropTable(
                name: "BankAccounts");

            migrationBuilder.DropTable(
                name: "FeeChanges");

            migrationBuilder.DropTable(
                name: "Notifications");

            migrationBuilder.DropCheckConstraint(
                name: "CK_CondoSettings_DueMonthOffset",
                table: "CondoSettings");

            migrationBuilder.DropColumn(
                name: "DueMonthOffset",
                table: "CondoSettings");

            migrationBuilder.AddColumn<decimal>(
                name: "FeeOverride",
                table: "Apartments",
                type: "numeric(10,2)",
                precision: 10,
                scale: 2,
                nullable: true);
        }
    }
}
