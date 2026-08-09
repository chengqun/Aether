using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aether.Server.Migrations
{
    /// <inheritdoc />
    public partial class SplitLlmConfigFromAgentConfig : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 1. 先创建 AppLlmConfigs 表
            migrationBuilder.CreateTable(
                name: "AppLlmConfigs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ModelName = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    ApiKey = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Endpoint = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Temperature = table.Column<float>(type: "real", nullable: false),
                    IsDefault = table.Column<bool>(type: "bit", nullable: false),
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
                    table.PrimaryKey("PK_AppLlmConfigs", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AppLlmConfigs_CreatorId",
                table: "AppLlmConfigs",
                column: "CreatorId");

            // 2. 先添加 LlmConfigId 列（允许 NULL）
            migrationBuilder.AddColumn<Guid>(
                name: "LlmConfigId",
                table: "AppAgentConfigs",
                type: "uniqueidentifier",
                nullable: true);

            // 3. 数据迁移：从 AppAgentConfigs 提取每个用户的 LLM 配置到 AppLlmConfigs
            //    对每个 CreatorId，取第一条有非空 ApiKey 的记录作为默认 LLM 配置
            migrationBuilder.Sql(@"
-- 为每个用户提取 LLM 配置（优先选有非空 ApiKey 的活跃配置）
INSERT INTO AppLlmConfigs (Id, Name, ModelName, ApiKey, [Endpoint], Temperature, IsDefault, ExtraProperties, ConcurrencyStamp, CreationTime, CreatorId, IsDeleted)
SELECT
    NEWID(),
    ISNULL(a.AgentName, '默认配置'),
    ISNULL(a.ModelName, 'deepseek-chat'),
    ISNULL(a.ApiKey, ''),
    ISNULL(a.[Endpoint], 'https://api.deepseek.com/v1'),
    ISNULL(a.Temperature, 0.5),
    1,  -- 第一条为默认
    '{}',
    LEFT(REPLACE(NEWID(), '-', ''), 40),
    GETUTCDATE(),
    a.CreatorId,
    0
FROM AppAgentConfigs a
WHERE a.IsDeleted = 0
  AND a.CreatorId IS NOT NULL
  AND a.Id = (
    SELECT TOP 1 a2.Id FROM AppAgentConfigs a2
    WHERE a2.CreatorId = a.CreatorId
      AND a2.IsDeleted = 0
    ORDER BY
      CASE WHEN a2.ApiKey <> '' THEN 0 ELSE 1 END,
      a2.IsActive DESC,
      a2.CreationTime ASC
  );

-- 对于没有任何记录的用户（理论上不会发生，但做防护），创建一条默认空配置
-- 此步骤通常不需要，因为上面已经覆盖了所有有记录的用户
            ");

            // 4. 更新 AppAgentConfigs.LlmConfigId 指向对应的 LLM 配置
            //    每个用户的 Agent 配置都指向该用户的默认 LLM 配置
            migrationBuilder.Sql(@"
UPDATE a
SET a.LlmConfigId = l.Id
FROM AppAgentConfigs a
INNER JOIN AppLlmConfigs l ON l.CreatorId = a.CreatorId AND l.IsDefault = 1
WHERE a.IsDeleted = 0
  AND a.CreatorId IS NOT NULL;
            ");

            // 5. 删除旧列（数据已迁移到 AppLlmConfigs）
            migrationBuilder.DropColumn(
                name: "ApiKey",
                table: "AppAgentConfigs");

            migrationBuilder.DropColumn(
                name: "Endpoint",
                table: "AppAgentConfigs");

            migrationBuilder.DropColumn(
                name: "ModelName",
                table: "AppAgentConfigs");

            migrationBuilder.DropColumn(
                name: "Temperature",
                table: "AppAgentConfigs");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AppLlmConfigs");

            migrationBuilder.DropColumn(
                name: "LlmConfigId",
                table: "AppAgentConfigs");

            migrationBuilder.AddColumn<string>(
                name: "ApiKey",
                table: "AppAgentConfigs",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Endpoint",
                table: "AppAgentConfigs",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "ModelName",
                table: "AppAgentConfigs",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<float>(
                name: "Temperature",
                table: "AppAgentConfigs",
                type: "real",
                nullable: false,
                defaultValue: 0f);
        }
    }
}
