using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Backora.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RunProgress : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "progress_at",
                table: "backup_runs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "progress_bytes",
                table: "backup_runs",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "progress_bytes_per_second",
                table: "backup_runs",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "progress_stage",
                table: "backup_runs",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "progress_total_bytes",
                table: "backup_runs",
                type: "bigint",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "progress_at",
                table: "backup_runs");

            migrationBuilder.DropColumn(
                name: "progress_bytes",
                table: "backup_runs");

            migrationBuilder.DropColumn(
                name: "progress_bytes_per_second",
                table: "backup_runs");

            migrationBuilder.DropColumn(
                name: "progress_stage",
                table: "backup_runs");

            migrationBuilder.DropColumn(
                name: "progress_total_bytes",
                table: "backup_runs");
        }
    }
}
