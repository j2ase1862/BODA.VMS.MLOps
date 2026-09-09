using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BODA.VMS.MLOps.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class LineClients : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "LineClients",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    LineId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    TokenHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Disabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    DisabledReason = table.Column<string>(type: "TEXT", nullable: true),
                    LastSeenAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LineClients", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LineClients_LineId",
                table: "LineClients",
                column: "LineId");

            migrationBuilder.CreateIndex(
                name: "IX_LineClients_TokenHash",
                table: "LineClients",
                column: "TokenHash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LineClients");
        }
    }
}
