using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Trovami.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class KeywordSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "NewKeywordDays",
                table: "Households",
                type: "INTEGER",
                nullable: false,
                defaultValue: 30);

            migrationBuilder.CreateTable(
                name: "KeywordSettings",
                columns: table => new
                {
                    HouseholdId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Word = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    Color = table.Column<string>(type: "TEXT", maxLength: 16, nullable: true),
                    Pinned = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_KeywordSettings", x => new { x.HouseholdId, x.Word });
                    table.ForeignKey(
                        name: "FK_KeywordSettings_Households_HouseholdId",
                        column: x => x.HouseholdId,
                        principalTable: "Households",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "KeywordSettings");

            migrationBuilder.DropColumn(
                name: "NewKeywordDays",
                table: "Households");
        }
    }
}
