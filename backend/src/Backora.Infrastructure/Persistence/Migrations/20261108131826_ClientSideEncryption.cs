using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Backora.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ClientSideEncryption : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_restore_runs_claimable",
                table: "restore_runs");

            migrationBuilder.DropIndex(
                name: "ux_restore_runs_one_active_per_point",
                table: "restore_runs");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "encryption_key_activated_at",
                table: "s3_destinations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "encryption_key_fingerprint",
                table: "s3_destinations",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "encryption_mode",
                table: "s3_destinations",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "encryption_recipient",
                table: "s3_destinations",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "decryption_key_fingerprint",
                table: "restore_runs",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "key_request_expires_at",
                table: "restore_runs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "key_requested_at",
                table: "restore_runs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "requires_decryption_key",
                table: "restore_runs",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "encrypted",
                table: "offload_runs",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "encrypted_objects_json",
                table: "offload_runs",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "encryption_format",
                table: "offload_runs",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "encryption_key_fingerprint",
                table: "offload_runs",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "encryption_recipient",
                table: "offload_runs",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "s3_destination_encryption_keys",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    s3_destination_id = table.Column<long>(type: "bigint", nullable: false),
                    recipient = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    fingerprint = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    activated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    retired_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_s3_destination_encryption_keys", x => x.id);
                    table.ForeignKey(
                        name: "fk_s3_destination_encryption_keys_s3_destinations_s3destinatio",
                        column: x => x.s3_destination_id,
                        principalTable: "s3_destinations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_s3_destinations_encryption",
                table: "s3_destinations",
                sql: "encryption_mode = 0 OR (encryption_mode = 1 AND encryption_recipient IS NOT NULL AND encryption_key_fingerprint IS NOT NULL)");

            migrationBuilder.CreateIndex(
                name: "ix_restore_runs_claimable",
                table: "restore_runs",
                columns: new[] { "status", "next_attempt_at" },
                filter: "status IN ('Pending','Running','Verifying','Cancelling','AwaitingKey')");

            migrationBuilder.CreateIndex(
                name: "ux_restore_runs_one_active_per_point",
                table: "restore_runs",
                column: "offload_run_id",
                unique: true,
                filter: "status IN ('Pending','Running','Verifying','Cancelling','AwaitingKey')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_offload_runs_encryption",
                table: "offload_runs",
                sql: "NOT encrypted OR (encryption_format IS NOT NULL AND encryption_recipient IS NOT NULL AND encryption_key_fingerprint IS NOT NULL)");

            migrationBuilder.CreateIndex(
                name: "ix_s3_destination_encryption_keys_s3_destination_id_activated_",
                table: "s3_destination_encryption_keys",
                columns: new[] { "s3_destination_id", "activated_at" });

            migrationBuilder.CreateIndex(
                name: "ux_s3_destination_encryption_keys_one_active",
                table: "s3_destination_encryption_keys",
                column: "s3_destination_id",
                unique: true,
                filter: "retired_at IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "s3_destination_encryption_keys");

            migrationBuilder.DropCheckConstraint(
                name: "ck_s3_destinations_encryption",
                table: "s3_destinations");

            migrationBuilder.DropIndex(
                name: "ix_restore_runs_claimable",
                table: "restore_runs");

            migrationBuilder.DropIndex(
                name: "ux_restore_runs_one_active_per_point",
                table: "restore_runs");

            migrationBuilder.DropCheckConstraint(
                name: "ck_offload_runs_encryption",
                table: "offload_runs");

            migrationBuilder.DropColumn(
                name: "encryption_key_activated_at",
                table: "s3_destinations");

            migrationBuilder.DropColumn(
                name: "encryption_key_fingerprint",
                table: "s3_destinations");

            migrationBuilder.DropColumn(
                name: "encryption_mode",
                table: "s3_destinations");

            migrationBuilder.DropColumn(
                name: "encryption_recipient",
                table: "s3_destinations");

            migrationBuilder.DropColumn(
                name: "decryption_key_fingerprint",
                table: "restore_runs");

            migrationBuilder.DropColumn(
                name: "key_request_expires_at",
                table: "restore_runs");

            migrationBuilder.DropColumn(
                name: "key_requested_at",
                table: "restore_runs");

            migrationBuilder.DropColumn(
                name: "requires_decryption_key",
                table: "restore_runs");

            migrationBuilder.DropColumn(
                name: "encrypted",
                table: "offload_runs");

            migrationBuilder.DropColumn(
                name: "encrypted_objects_json",
                table: "offload_runs");

            migrationBuilder.DropColumn(
                name: "encryption_format",
                table: "offload_runs");

            migrationBuilder.DropColumn(
                name: "encryption_key_fingerprint",
                table: "offload_runs");

            migrationBuilder.DropColumn(
                name: "encryption_recipient",
                table: "offload_runs");

            migrationBuilder.CreateIndex(
                name: "ix_restore_runs_claimable",
                table: "restore_runs",
                columns: new[] { "status", "next_attempt_at" },
                filter: "status IN ('Pending','Running','Verifying','Cancelling')");

            migrationBuilder.CreateIndex(
                name: "ux_restore_runs_one_active_per_point",
                table: "restore_runs",
                column: "offload_run_id",
                unique: true,
                filter: "status IN ('Pending','Running','Verifying','Cancelling')");
        }
    }
}
