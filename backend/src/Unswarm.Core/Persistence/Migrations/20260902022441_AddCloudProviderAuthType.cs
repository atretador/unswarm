using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Unswarm.Core.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCloudProviderAuthType : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AccessTokenCiphertext",
                table: "CloudProviders",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "AuthType",
                table: "CloudProviders",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "ChatgptAccountId",
                table: "CloudProviders",
                type: "TEXT",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RefreshTokenCiphertext",
                table: "CloudProviders",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "TokenExpiresAt",
                table: "CloudProviders",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AccessTokenCiphertext",
                table: "CloudProviders");

            migrationBuilder.DropColumn(
                name: "AuthType",
                table: "CloudProviders");

            migrationBuilder.DropColumn(
                name: "ChatgptAccountId",
                table: "CloudProviders");

            migrationBuilder.DropColumn(
                name: "RefreshTokenCiphertext",
                table: "CloudProviders");

            migrationBuilder.DropColumn(
                name: "TokenExpiresAt",
                table: "CloudProviders");
        }
    }
}
