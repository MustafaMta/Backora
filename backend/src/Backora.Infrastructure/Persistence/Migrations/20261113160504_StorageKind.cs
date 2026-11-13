using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Backora.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class StorageKind : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "kind",
                table: "s3_destinations",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "local_path",
                table: "s3_destinations",
                type: "character varying(1024)",
                maxLength: 1024,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "kind",
                table: "s3_destinations");

            migrationBuilder.DropColumn(
                name: "local_path",
                table: "s3_destinations");
        }
    }
}
