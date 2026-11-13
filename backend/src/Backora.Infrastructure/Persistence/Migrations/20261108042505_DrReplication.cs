using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Backora.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DrReplication : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_offload_runs_one_active_per_backup_run",
                table: "offload_runs");

            migrationBuilder.AddColumn<long>(
                name: "source_offload_run_id",
                table: "offload_runs",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "backup_job_replication_targets",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    backup_job_id = table.Column<long>(type: "bigint", nullable: false),
                    s3_destination_id = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_backup_job_replication_targets", x => x.id);
                    table.ForeignKey(
                        name: "fk_backup_job_replication_targets_backup_jobs_backup_job_id",
                        column: x => x.backup_job_id,
                        principalTable: "backup_jobs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_backup_job_replication_targets_s3destinations_s3destination",
                        column: x => x.s3_destination_id,
                        principalTable: "s3_destinations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_offload_runs_source_offload_run_id",
                table: "offload_runs",
                column: "source_offload_run_id",
                filter: "source_offload_run_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ux_offload_runs_one_active_per_run_destination",
                table: "offload_runs",
                columns: new[] { "backup_run_id", "s3_destination_id" },
                unique: true,
                filter: "status IN ('Pending','Running','Verifying')");

            migrationBuilder.CreateIndex(
                name: "ix_backup_job_replication_targets_backup_job_id_s3_destination",
                table: "backup_job_replication_targets",
                columns: new[] { "backup_job_id", "s3_destination_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_backup_job_replication_targets_s3destination_id",
                table: "backup_job_replication_targets",
                column: "s3_destination_id");

            migrationBuilder.AddForeignKey(
                name: "fk_offload_runs_offload_runs_source_offload_run_id",
                table: "offload_runs",
                column: "source_offload_run_id",
                principalTable: "offload_runs",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_offload_runs_offload_runs_source_offload_run_id",
                table: "offload_runs");

            migrationBuilder.DropTable(
                name: "backup_job_replication_targets");

            migrationBuilder.DropIndex(
                name: "ix_offload_runs_source_offload_run_id",
                table: "offload_runs");

            migrationBuilder.DropIndex(
                name: "ux_offload_runs_one_active_per_run_destination",
                table: "offload_runs");

            migrationBuilder.DropColumn(
                name: "source_offload_run_id",
                table: "offload_runs");

            migrationBuilder.CreateIndex(
                name: "ux_offload_runs_one_active_per_backup_run",
                table: "offload_runs",
                column: "backup_run_id",
                unique: true,
                filter: "status IN ('Pending','Running','Verifying')");
        }
    }
}
