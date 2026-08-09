using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aether.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddWorkflowFieldsToAgentConfig : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Kind",
                table: "AppAgentConfigs",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "MaxResets",
                table: "AppAgentConfigs",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MaxRounds",
                table: "AppAgentConfigs",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MaxStalls",
                table: "AppAgentConfigs",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "RequirePlanSignoff",
                table: "AppAgentConfigs",
                type: "bit",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SubAgentsJson",
                table: "AppAgentConfigs",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "WorkflowMode",
                table: "AppAgentConfigs",
                type: "int",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Kind",
                table: "AppAgentConfigs");

            migrationBuilder.DropColumn(
                name: "MaxResets",
                table: "AppAgentConfigs");

            migrationBuilder.DropColumn(
                name: "MaxRounds",
                table: "AppAgentConfigs");

            migrationBuilder.DropColumn(
                name: "MaxStalls",
                table: "AppAgentConfigs");

            migrationBuilder.DropColumn(
                name: "RequirePlanSignoff",
                table: "AppAgentConfigs");

            migrationBuilder.DropColumn(
                name: "SubAgentsJson",
                table: "AppAgentConfigs");

            migrationBuilder.DropColumn(
                name: "WorkflowMode",
                table: "AppAgentConfigs");
        }
    }
}
