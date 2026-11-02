using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Trovami.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class LocationKeywords : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Keywords",
                table: "Locations",
                type: "TEXT",
                maxLength: 840,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "KeywordsFinal",
                table: "Items",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            // Until now, any keywords at all meant the AI or a person had set them.
            migrationBuilder.Sql("UPDATE Items SET KeywordsFinal = 1 WHERE Keywords IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Keywords",
                table: "Locations");

            migrationBuilder.DropColumn(
                name: "KeywordsFinal",
                table: "Items");
        }
    }
}
