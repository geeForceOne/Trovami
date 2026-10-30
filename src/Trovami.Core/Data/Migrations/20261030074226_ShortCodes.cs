using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Trovami.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class ShortCodes : Migration
    {
        private const string Alphabet = "23456789ABCDEFGHJKMNPQRSTUVWXYZ";

        private static readonly string RandomCode = string.Join(" || ",
            Enumerable.Repeat($"substr('{Alphabet}', abs(random()) % {Alphabet.Length} + 1, 1)", 6));

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ShortCode",
                table: "Locations",
                type: "TEXT",
                maxLength: 6,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "ShortCode",
                table: "Items",
                type: "TEXT",
                maxLength: 6,
                nullable: false,
                defaultValue: "");

            // Codes for what's already there, made by SQLite: six random characters from the
            // ShortCodes alphabet. Rows that drew a code already taken draw again, before the
            // unique indexes go on.
            foreach (var table in new[] { "Locations", "Items" })
            {
                migrationBuilder.Sql($"UPDATE \"{table}\" SET \"ShortCode\" = {RandomCode};");
                for (var i = 0; i < 5; i++)
                    migrationBuilder.Sql($"UPDATE \"{table}\" SET \"ShortCode\" = {RandomCode} " +
                        $"WHERE rowid NOT IN (SELECT MIN(rowid) FROM \"{table}\" GROUP BY \"ShortCode\");");
            }

            migrationBuilder.CreateIndex(
                name: "IX_Locations_ShortCode",
                table: "Locations",
                column: "ShortCode",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Items_ShortCode",
                table: "Items",
                column: "ShortCode",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Locations_ShortCode",
                table: "Locations");

            migrationBuilder.DropIndex(
                name: "IX_Items_ShortCode",
                table: "Items");

            migrationBuilder.DropColumn(
                name: "ShortCode",
                table: "Locations");

            migrationBuilder.DropColumn(
                name: "ShortCode",
                table: "Items");
        }
    }
}
