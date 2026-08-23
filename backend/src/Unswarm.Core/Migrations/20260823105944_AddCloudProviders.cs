using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Unswarm.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddCloudProviders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CloudProviders",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    BaseUrl = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    ApiKeyCiphertext = table.Column<string>(type: "TEXT", nullable: false),
                    ApiKeyHint = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ModelsJson = table.Column<string>(type: "TEXT", maxLength: 8192, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CloudProviders", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CloudProviders_Name",
                table: "CloudProviders",
                column: "Name",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CloudProviders");
        }
    }
}
