using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aether.Server.Migrations
{
    /// <inheritdoc />
    public partial class ReplaceAppAgentConfigWithAppNoteNode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AppAgentConfigs");

            migrationBuilder.CreateTable(
                name: "AppNoteNodes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    NodeType = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Title = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Content = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    IsPublished = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                    Description = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Category = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    MinAppVersion = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    ExtraProperties = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ConcurrencyStamp = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    CreationTime = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatorId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    LastModificationTime = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastModifierId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                    DeleterId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DeletionTime = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppNoteNodes", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AppNoteNodes_CreatorId",
                table: "AppNoteNodes",
                column: "CreatorId");

            migrationBuilder.CreateIndex(
                name: "IX_AppNoteNodes_CreatorId_Title_NodeType",
                table: "AppNoteNodes",
                columns: new[] { "CreatorId", "Title", "NodeType" });

            migrationBuilder.CreateIndex(
                name: "IX_AppNoteNodes_IsPublished",
                table: "AppNoteNodes",
                column: "IsPublished");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AppNoteNodes");

            migrationBuilder.CreateTable(
                name: "AppAgentConfigs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AgentName = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Category = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ConcurrencyStamp = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    CreationTime = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatorId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DefaultSystemPrompt = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    DeleterId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DeletionTime = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Description = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    EnabledTools = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ExtraProperties = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                    IsPublished = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                    Kind = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    LastModificationTime = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastModifierId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    MaxHistoryMessages = table.Column<int>(type: "int", nullable: false),
                    MaxResets = table.Column<int>(type: "int", nullable: true),
                    MaxRounds = table.Column<int>(type: "int", nullable: true),
                    MaxStalls = table.Column<int>(type: "int", nullable: true),
                    MemoryMode = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    MinAppVersion = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    RequirePlanSignoff = table.Column<bool>(type: "bit", nullable: true),
                    SubAgentsJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    WorkflowMode = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppAgentConfigs", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AppAgentConfigs_CreatorId",
                table: "AppAgentConfigs",
                column: "CreatorId");

            migrationBuilder.CreateIndex(
                name: "IX_AppAgentConfigs_CreatorId_AgentName_IsActive",
                table: "AppAgentConfigs",
                columns: new[] { "CreatorId", "AgentName", "IsActive" });

            migrationBuilder.CreateIndex(
                name: "IX_AppAgentConfigs_IsPublished",
                table: "AppAgentConfigs",
                column: "IsPublished");
        }
    }
}
