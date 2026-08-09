using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aether.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddMemoryModeToAgentConfig : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "MaxHistoryMessages",
                table: "AppAgentConfigs",
                type: "int",
                nullable: false,
                defaultValue: 20);

            migrationBuilder.AddColumn<string>(
                name: "MemoryMode",
                table: "AppAgentConfigs",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "SlidingWindow");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MaxHistoryMessages",
                table: "AppAgentConfigs");

            migrationBuilder.DropColumn(
                name: "MemoryMode",
                table: "AppAgentConfigs");
        }
    }
}
