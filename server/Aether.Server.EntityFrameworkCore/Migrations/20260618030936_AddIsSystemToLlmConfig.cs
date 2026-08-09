using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aether.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddIsSystemToLlmConfig : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsSystem",
                table: "AppLlmConfigs",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "IX_AppLlmConfigs_CreatorId_IsSystem",
                table: "AppLlmConfigs",
                columns: new[] { "CreatorId", "IsSystem" });

            migrationBuilder.CreateIndex(
                name: "IX_AppLlmConfigs_IsSystem_IsDefault",
                table: "AppLlmConfigs",
                columns: new[] { "IsSystem", "IsDefault" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AppLlmConfigs_CreatorId_IsSystem",
                table: "AppLlmConfigs");

            migrationBuilder.DropIndex(
                name: "IX_AppLlmConfigs_IsSystem_IsDefault",
                table: "AppLlmConfigs");

            migrationBuilder.DropColumn(
                name: "IsSystem",
                table: "AppLlmConfigs");
        }
    }
}
