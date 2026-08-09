using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aether.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddUpdatedAtToAppSecurity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "UpdatedAt",
                table: "AppSecurities",
                type: "datetime2",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.CreateIndex(
                name: "IX_AppSecurities_UpdatedAt",
                table: "AppSecurities",
                column: "UpdatedAt");

            // 回填现有数据的 UpdatedAt
            migrationBuilder.Sql("UPDATE AppSecurities SET UpdatedAt = GETUTCDATE() WHERE UpdatedAt = '0001-01-01T00:00:00.0000000'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AppSecurities_UpdatedAt",
                table: "AppSecurities");

            migrationBuilder.DropColumn(
                name: "UpdatedAt",
                table: "AppSecurities");
        }
    }
}
