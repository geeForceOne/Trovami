using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Trovami.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class LabelSize : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "LabelSize",
                table: "UserSettings",
                type: "TEXT",
                maxLength: 8,
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LabelSize",
                table: "UserSettings");
        }
    }
}
