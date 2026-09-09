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
            // Disable FK checks for SQLite to allow PK changes
            migrationBuilder.Sql("PRAGMA foreign_keys = OFF;");

            // Transform ContainerModelMappingEntity.ModelId from bare to composite
            // Old: ModelId = 'Qwen_Q4.gguf'
            // New: ModelId = 'abc-123:Qwen_Q4.gguf' (where abc-123 is SourceRuntimeId)
            migrationBuilder.Sql(@"
                UPDATE ContainerModelMappings
                SET ModelId = (
                    SELECT m.SourceRuntimeId || ':' || m.Id
                    FROM Models m
                    WHERE m.Id = ContainerModelMappings.ModelId AND m.SourceRuntimeId IS NOT NULL
                )
                WHERE ModelId IN (
                    SELECT Id FROM Models WHERE SourceRuntimeId IS NOT NULL
                );
            ");

            // Transform Models.Id from bare to composite
            migrationBuilder.Sql(@"
                UPDATE Models
                SET Id = SourceRuntimeId || ':' || Id
                WHERE SourceRuntimeId IS NOT NULL;
            ");

            // Re-enable FK checks
            migrationBuilder.Sql("PRAGMA foreign_keys = ON;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("PRAGMA foreign_keys = OFF;");

            // Strip runtime prefix from Models.Id
            migrationBuilder.Sql(@"
                UPDATE Models
                SET Id = SUBSTR(Id, INSTR(Id, ':') + 1)
                WHERE Id LIKE '%:%';
            ");

            // Strip runtime prefix from ContainerModelMappings.ModelId
            migrationBuilder.Sql(@"
                UPDATE ContainerModelMappings
                SET ModelId = SUBSTR(ModelId, INSTR(ModelId, ':') + 1)
                WHERE ModelId LIKE '%:%';
            ");

            migrationBuilder.Sql("PRAGMA foreign_keys = ON;");
        }
    }
}
