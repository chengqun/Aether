using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aether.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddTenantIdToAgentConfigAndLlmConfig : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                table: "AppLlmConfigs",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                table: "AppAgentConfigs",
                type: "uniqueidentifier",
                nullable: true);

            // 回填存量数据到 qifu 租户（开发数据归 qifu）。系统级 LLM 配置(IsSystem=1)留 null（host 共享）。
            migrationBuilder.Sql(@"
UPDATE AppAgentConfigs SET TenantId = (SELECT TOP 1 Id FROM AbpTenants WHERE Name = 'qifu') WHERE TenantId IS NULL;
UPDATE AppLlmConfigs SET TenantId = (SELECT TOP 1 Id FROM AbpTenants WHERE Name = 'qifu') WHERE TenantId IS NULL AND IsSystem = 0;
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "AppLlmConfigs");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "AppAgentConfigs");
        }
    }
}
