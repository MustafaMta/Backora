using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Backora.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AuditAppendOnly : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION backora_audit_events_immutable() RETURNS trigger
                LANGUAGE plpgsql AS $$
                BEGIN
                    RAISE EXCEPTION 'audit_events is append-only (% blocked)', TG_OP USING ERRCODE = 'insufficient_privilege';
                END;
                $$;
                CREATE TRIGGER trg_audit_events_no_update_delete BEFORE UPDATE OR DELETE ON audit_events FOR EACH ROW EXECUTE FUNCTION backora_audit_events_immutable();
                CREATE TRIGGER trg_audit_events_no_truncate BEFORE TRUNCATE ON audit_events FOR EACH STATEMENT EXECUTE FUNCTION backora_audit_events_immutable();
                """);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS trg_audit_events_no_truncate ON audit_events;
                DROP TRIGGER IF EXISTS trg_audit_events_no_update_delete ON audit_events;
                DROP FUNCTION IF EXISTS backora_audit_events_immutable();
                """);
        }
    }
}
