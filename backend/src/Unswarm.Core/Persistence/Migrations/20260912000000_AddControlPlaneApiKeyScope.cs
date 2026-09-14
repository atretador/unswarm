using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Unswarm.Core.Persistence.Migrations;

public partial class AddControlPlaneApiKeyScope : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "PermissionsJson",
            table: "ApiKeys",
            type: "TEXT",
            maxLength: 4096,
            nullable: false,
            defaultValue: "{}");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "PermissionsJson", table: "ApiKeys");
    }
}
