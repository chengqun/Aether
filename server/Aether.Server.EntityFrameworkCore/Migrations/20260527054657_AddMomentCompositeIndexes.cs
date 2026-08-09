using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aether.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddMomentCompositeIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_AppMoments_Type_CreatedAt",
                table: "AppMoments",
                columns: new[] { "Type", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AppMoments_UserId_Type",
                table: "AppMoments",
                columns: new[] { "UserId", "Type" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AppMoments_Type_CreatedAt",
                table: "AppMoments");

            migrationBuilder.DropIndex(
                name: "IX_AppMoments_UserId_Type",
                table: "AppMoments");
        }
    }
}
