using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BODA.VMS.MLOps.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddImageQualityMetrics : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "ClippedBrightRatio",
                table: "Images",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "ClippedDarkRatio",
                table: "Images",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "MeanLuma",
                table: "Images",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "Sharpness",
                table: "Images",
                type: "REAL",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ClippedBrightRatio",
                table: "Images");

            migrationBuilder.DropColumn(
                name: "ClippedDarkRatio",
                table: "Images");

            migrationBuilder.DropColumn(
                name: "MeanLuma",
                table: "Images");

            migrationBuilder.DropColumn(
                name: "Sharpness",
                table: "Images");
        }
    }
}
