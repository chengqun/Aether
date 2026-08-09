using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aether.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddMarketplaceFieldsToAgentConfig : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Category",
                table: "AppAgentConfigs",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Description",
                table: "AppAgentConfigs",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "EnabledTools",
                table: "AppAgentConfigs",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsPublished",
                table: "AppAgentConfigs",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "MinAppVersion",
                table: "AppAgentConfigs",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_AppAgentConfigs_IsPublished",
                table: "AppAgentConfigs",
                column: "IsPublished");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AppAgentConfigs_IsPublished",
                table: "AppAgentConfigs");

            migrationBuilder.DropColumn(
                name: "Category",
                table: "AppAgentConfigs");

            migrationBuilder.DropColumn(
                name: "Description",
                table: "AppAgentConfigs");

            migrationBuilder.DropColumn(
                name: "EnabledTools",
                table: "AppAgentConfigs");

            migrationBuilder.DropColumn(
                name: "IsPublished",
                table: "AppAgentConfigs");

            migrationBuilder.DropColumn(
                name: "MinAppVersion",
                table: "AppAgentConfigs");
        }
    }
}
