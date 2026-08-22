# Database migrations

Unswarm uses EF Core migrations (SQLite). Startup calls `MigrateAsync()` — there is
no `EnsureCreated` and no PRAGMA schema-drift repair anymore. Pre-release, old dev
database files (`~/.config/unswarm/unswarm.db`) are disposable: delete them and let
migrations recreate the schema.

## Adding a migration

From `backend/`, after changing entities / `UnswarmDbContext`:

```sh
dotnet ef migrations add <Name> -p src/Unswarm.Core -s src/Unswarm.Api
```

- `-p src/Unswarm.Core` — project holding the DbContext, migrations, and the
  design-time factory (`UnswarmDbContextDesignFactory`).
- `-s src/Unswarm.Api` — startup project (provides the EF Design package).

Commit the generated `<Timestamp>_<Name>.cs`, its `.Designer.cs`, and the updated
`UnswarmDbContextModelSnapshot.cs`. On next API start, `MigrateAsync()` applies it
and records it in `__EFMigrationsHistory`.

## Seeding

Seeding (static API key into the managed key store, roles, admin user) runs in
`Program.cs` immediately after `MigrateAsync()` and is idempotent.
