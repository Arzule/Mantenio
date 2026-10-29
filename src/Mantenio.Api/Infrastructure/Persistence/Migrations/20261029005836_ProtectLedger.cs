using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mantenio.Api.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ProtectLedger : Migration
    {
        private static readonly string[] LedgerTables = ["Charges", "PaymentProofs", "PaymentAllocations", "Receipts"];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BoardEmails",
                table: "CondoSettings",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: false,
                defaultValue: "");

            // The ledger and its history are append-only, also for anyone with SQL access: deleting or truncating
            // ledger rows, and editing or deleting audit events, raises an error. Disabling these triggers needs the
            // owner login and a deliberate ALTER TABLE, which the app's runtime login does not have.
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION mantenio_forbid_change() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    RAISE EXCEPTION 'Mantenio: % on % is not allowed (the ledger and its history are append-only)', TG_OP, TG_TABLE_NAME
                        USING ERRCODE = 'insufficient_privilege';
                END $$;
                """);
            foreach (var table in LedgerTables)
            {
                migrationBuilder.Sql($"""
                    CREATE TRIGGER mantenio_forbid_delete BEFORE DELETE ON "{table}" FOR EACH ROW EXECUTE FUNCTION mantenio_forbid_change();
                    CREATE TRIGGER mantenio_forbid_truncate BEFORE TRUNCATE ON "{table}" FOR EACH STATEMENT EXECUTE FUNCTION mantenio_forbid_change();
                    """);
            }

            migrationBuilder.Sql("""
                CREATE TRIGGER mantenio_forbid_change BEFORE UPDATE OR DELETE ON "AuditEvents" FOR EACH ROW EXECUTE FUNCTION mantenio_forbid_change();
                CREATE TRIGGER mantenio_forbid_truncate BEFORE TRUNCATE ON "AuditEvents" FOR EACH STATEMENT EXECUTE FUNCTION mantenio_forbid_change();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (var table in LedgerTables)
            {
                migrationBuilder.Sql($"""
                    DROP TRIGGER IF EXISTS mantenio_forbid_delete ON "{table}";
                    DROP TRIGGER IF EXISTS mantenio_forbid_truncate ON "{table}";
                    """);
            }

            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS mantenio_forbid_change ON "AuditEvents";
                DROP TRIGGER IF EXISTS mantenio_forbid_truncate ON "AuditEvents";
                DROP FUNCTION IF EXISTS mantenio_forbid_change();
                """);

            migrationBuilder.DropColumn(
                name: "BoardEmails",
                table: "CondoSettings");
        }
    }
}
