using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aether.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddTenantIdToBusinessEntities : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Channel",
                table: "AppAgentConfigs");

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                table: "AppYushuTokens",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                table: "AppServiceAccounts",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                table: "AppServiceAccountPosts",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                table: "AppRelationships",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                table: "AppMoments",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                table: "AppGroups",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                table: "AppDevices",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                table: "AppConversations",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                table: "AppCloudDocsTokens",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                table: "AppChatMessages",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                table: "AppAgentUsageRecords",
                type: "uniqueidentifier",
                nullable: true);

            // 服务号数据归 stockapp 租户（「明日策略」服务号为 stockapp 专属，由 DailySync 定时任务持续发布）。
            // 其余历史业务数据清空，只保留往后按租户新增的数据。
            migrationBuilder.Sql(@"
UPDATE AppServiceAccountPosts SET TenantId = (SELECT TOP 1 Id FROM AbpTenants WHERE Name = 'stockapp') WHERE TenantId IS NULL;
UPDATE AppServiceAccounts     SET TenantId = (SELECT TOP 1 Id FROM AbpTenants WHERE Name = 'stockapp') WHERE TenantId IS NULL;
DELETE FROM AppChatMessages;
DELETE FROM AppRelationships;
DELETE FROM AppConversations;
DELETE FROM AppMoments;
DELETE FROM AppGroups;
DELETE FROM AppDevices;
DELETE FROM AppYushuTokens;
DELETE FROM AppCloudDocsTokens;
DELETE FROM AppAgentUsageRecords;
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "AppYushuTokens");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "AppServiceAccounts");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "AppServiceAccountPosts");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "AppRelationships");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "AppMoments");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "AppGroups");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "AppDevices");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "AppConversations");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "AppCloudDocsTokens");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "AppChatMessages");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "AppAgentUsageRecords");

            migrationBuilder.AddColumn<int>(
                name: "Channel",
                table: "AppAgentConfigs",
                type: "int",
                nullable: false,
                defaultValue: 0);
        }
    }
}
