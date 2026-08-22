using System;
using System.Text.Json;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace AuditService.Storage.Migrations
{
    /// <inheritdoc />
    public partial class InitialPostgreSqlAuditSnapshots : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "audit_snapshots",
                columns: table => new
                {
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    correlation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    responsible_service = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    occurred_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    payload = table.Column<JsonElement>(type: "jsonb", nullable: false),
                    previous_event_id = table.Column<Guid>(type: "uuid", nullable: true),
                    actor = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    status_code = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    sequence_number = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_audit_snapshots", x => x.event_id);
                    table.UniqueConstraint("ak_audit_snapshots_correlation_event", x => new { x.correlation_id, x.event_id });
                    table.CheckConstraint("ck_audit_snapshots_actor_not_blank", "length(btrim(actor)) > 0");
                    table.CheckConstraint("ck_audit_snapshots_responsible_service_not_blank", "length(btrim(responsible_service)) > 0");
                    table.CheckConstraint("ck_audit_snapshots_sequence_number_positive", "sequence_number > 0");
                    table.ForeignKey(
                        name: "fk_audit_snapshots_previous_event",
                        columns: x => new { x.correlation_id, x.previous_event_id },
                        principalTable: "audit_snapshots",
                        principalColumns: new[] { "correlation_id", "event_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_audit_snapshots_correlation_id_previous_event_id",
                table: "audit_snapshots",
                columns: new[] { "correlation_id", "previous_event_id" });

            migrationBuilder.CreateIndex(
                name: "ix_audit_snapshots_correlation_timeline",
                table: "audit_snapshots",
                columns: new[] { "correlation_id", "occurred_at", "sequence_number" });

            migrationBuilder.CreateIndex(
                name: "ux_audit_snapshots_sequence_number",
                table: "audit_snapshots",
                column: "sequence_number",
                unique: true);

            migrationBuilder.Sql(
                """
                CREATE FUNCTION reject_audit_snapshot_mutation()
                RETURNS trigger
                LANGUAGE plpgsql
                AS $$
                BEGIN
                    RAISE EXCEPTION 'Audit snapshots are append-only and cannot be changed or deleted.';
                END;
                $$;

                CREATE TRIGGER trg_audit_snapshots_reject_update_delete
                BEFORE UPDATE OR DELETE ON audit_snapshots
                FOR EACH ROW
                EXECUTE FUNCTION reject_audit_snapshot_mutation();

                CREATE TRIGGER trg_audit_snapshots_reject_truncate
                BEFORE TRUNCATE ON audit_snapshots
                FOR EACH STATEMENT
                EXECUTE FUNCTION reject_audit_snapshot_mutation();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "audit_snapshots");

            migrationBuilder.Sql(
                "DROP FUNCTION IF EXISTS reject_audit_snapshot_mutation();");
        }
    }
}
