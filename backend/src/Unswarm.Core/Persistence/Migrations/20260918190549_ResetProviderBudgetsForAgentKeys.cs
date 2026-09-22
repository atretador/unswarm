using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Unswarm.Core.Persistence.Migrations
{
    /// <inheritdoc />
    /// <summary>
    /// Data-only migration: clears persisted per-provider budgets exactly once,
    /// server-side, after provider budgets became keyed by agent (cost unit)
    /// rather than runtime display name. This replaces the frontend's
    /// localStorage-based one-time reset, which could wipe budgets re-set from
    /// another browser/device. No model/schema change.
    /// </summary>
    public partial class ResetProviderBudgetsForAgentKeys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Table/column verified against SettingsEntity.DbSet name (Settings)
            // and UnswarmDbContextModelSnapshot (ToTable("Settings")).
            migrationBuilder.Sql(
                """UPDATE "Settings" SET "ProviderBudgetsJson" = '{}';""");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // One-time destructive reset; intentionally not reversible.
        }
    }
}
