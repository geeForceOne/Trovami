using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Trovami.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class ItemKeywords : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Keywords",
                table: "Items",
                type: "TEXT",
                maxLength: 840,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Keywords",
                table: "Items");
        }
    }
}
