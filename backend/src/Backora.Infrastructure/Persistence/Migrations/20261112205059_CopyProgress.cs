using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Backora.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CopyProgress : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "progress_bytes",
                table: "offload_runs",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "progress_stage",
                table: "offload_runs",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "progress_stage_started_at",
                table: "offload_runs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "progress_total_bytes",
                table: "offload_runs",
                type: "bigint",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "progress_bytes",
                table: "offload_runs");

            migrationBuilder.DropColumn(
                name: "progress_stage",
                table: "offload_runs");

            migrationBuilder.DropColumn(
                name: "progress_stage_started_at",
                table: "offload_runs");

            migrationBuilder.DropColumn(
                name: "progress_total_bytes",
                table: "offload_runs");
        }
    }
}
