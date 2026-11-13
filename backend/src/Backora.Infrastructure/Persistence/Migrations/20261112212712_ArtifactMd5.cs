using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Backora.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ArtifactMd5 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "md5",
                table: "backup_artifacts",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "md5",
                table: "backup_artifacts");
        }
    }
}
