using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Unswarm.Core.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddModelDisplayName : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DisplayName",
                table: "Models",
                type: "TEXT",
                maxLength: 500,
                nullable: true);

            // Backfill DisplayName from Name for existing records.
            // Models with '/' in their name get the path prefix stripped.
            migrationBuilder.Sql(@"
                UPDATE Models 
                SET DisplayName = SUBSTR(Name, INSTR(Name, '/') + 1)
                WHERE DisplayName IS NULL AND Name LIKE '%/%'
            ");
            migrationBuilder.Sql(@"
                UPDATE Models 
                SET DisplayName = Name
                WHERE DisplayName IS NULL AND Name NOT LIKE '%/%'
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DisplayName",
                table: "Models");
        }
    }
}
