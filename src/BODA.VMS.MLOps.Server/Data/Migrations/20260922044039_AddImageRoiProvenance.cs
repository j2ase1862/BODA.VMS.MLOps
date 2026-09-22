using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BODA.VMS.MLOps.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddImageRoiProvenance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "RoiJson",
                table: "Images",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SourceImageId",
                table: "Images",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RoiJson",
                table: "Images");

            migrationBuilder.DropColumn(
                name: "SourceImageId",
                table: "Images");
        }
    }
}
