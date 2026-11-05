using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Trovami.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class HouseholdAiLanguage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AiLanguage",
                table: "Households",
                type: "TEXT",
                maxLength: 8,
                nullable: false,
                defaultValue: "en");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AiLanguage",
                table: "Households");
        }
    }
}
