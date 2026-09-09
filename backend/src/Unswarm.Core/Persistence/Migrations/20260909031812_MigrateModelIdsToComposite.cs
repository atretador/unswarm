using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Unswarm.Core.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MigrateModelIdsToComposite : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // IMPORTANT: All Sql() calls use suppressTransaction: true because
            // SQLite PRAGMA changes inside a transaction don't take effect until
            // the next transaction (per SQLite spec). Running outside the
            // transaction ensures FK checks are actually disabled for the UPDATEs.

            migrationBuilder.Sql("PRAGMA foreign_keys = OFF;", suppressTransaction: true);

            // Transform ContainerModelMappingEntity.ModelId from bare to composite
            // Old: ModelId = 'Qwen_Q4.gguf'
            // New: ModelId = 'abc-123:Qwen_Q4.gguf' (where abc-123 is SourceRuntimeId)
            // Idempotent: only transforms rows where ModelId has no colon yet
            migrationBuilder.Sql(@"
                UPDATE ContainerModelMappings
                SET ModelId = (
                    SELECT m.SourceRuntimeId || ':' || m.Id
                    FROM Models m
                    WHERE m.Id = ContainerModelMappings.ModelId AND m.SourceRuntimeId IS NOT NULL
                )
                WHERE ModelId IN (
                    SELECT Id FROM Models WHERE SourceRuntimeId IS NOT NULL
                )
                AND ModelId NOT LIKE '%:%';
            ", suppressTransaction: true);

            // Transform Models.Id from bare to composite
            // Idempotent: only transforms rows where Id has no colon yet
            migrationBuilder.Sql(@"
                UPDATE Models
                SET Id = SourceRuntimeId || ':' || Id
                WHERE SourceRuntimeId IS NOT NULL
                AND Id NOT LIKE '%:%';
            ", suppressTransaction: true);

            migrationBuilder.Sql("PRAGMA foreign_keys = ON;", suppressTransaction: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("PRAGMA foreign_keys = OFF;", suppressTransaction: true);

            // Strip runtime prefix from Models.Id
            migrationBuilder.Sql(@"
                UPDATE Models
                SET Id = SUBSTR(Id, INSTR(Id, ':') + 1)
                WHERE Id LIKE '%:%';
            ", suppressTransaction: true);

            // Strip runtime prefix from ContainerModelMappings.ModelId
            migrationBuilder.Sql(@"
                UPDATE ContainerModelMappings
                SET ModelId = SUBSTR(ModelId, INSTR(ModelId, ':') + 1)
                WHERE ModelId LIKE '%:%';
            ", suppressTransaction: true);

            migrationBuilder.Sql("PRAGMA foreign_keys = ON;", suppressTransaction: true);
        }
    }
}
