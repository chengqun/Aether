using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aether.Server.Migrations
{
    /// <inheritdoc />
    public partial class Update_AppSecurities_UniqueIndex_Code_Type : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AppSecurities_Code",
                table: "AppSecurities");

            migrationBuilder.AlterColumn<string>(
                name: "Type",
                table: "AppSecurities",
                type: "nvarchar(450)",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(max)");

            migrationBuilder.CreateIndex(
                name: "IX_AppSecurities_Code_Type",
                table: "AppSecurities",
                columns: new[] { "Code", "Type" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AppSecurities_Code_Type",
                table: "AppSecurities");

            migrationBuilder.AlterColumn<string>(
                name: "Type",
                table: "AppSecurities",
                type: "nvarchar(max)",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(450)");

            migrationBuilder.CreateIndex(
                name: "IX_AppSecurities_Code",
                table: "AppSecurities",
                column: "Code");
        }
    }
}
