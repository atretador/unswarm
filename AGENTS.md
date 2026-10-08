# AGENTS.md

Guidance for AI coding agents working in the **Unswarm** repository. Human
contributors should read [CONTRIBUTING.md](CONTRIBUTING.md); security-specific
guidance lives in [SECURITY.md](SECURITY.md).

## What this repo is

Unswarm is a self-hosted control plane for running and routing local and remote
LLM inference. It has four code components plus deployment assets:

| Component | Path | Stack | Notes |
|-----------|------|-------|-------|
| Backend API + core | `backend/` | .NET 10 / ASP.NET Core | 20 controllers, cookie + API-key auth, EF Core + SQLite |
| Frontend SPA | `frontend/` | React 19 + Vite + Tailwind v4 | Served as static files by the backend |
| Agent | `agent/` | Go | Runs on GPU hosts, Docker socket + launcher scripts |
| CLI | `cli/` | Go | 30+ subcommands |

Reference docs: [README.md](README.md), [backend/docs/](backend/docs)
(agent protocol, agent config, migrations), [cli/docs/CLI_FOR_AGENTS.md](cli/docs/CLI_FOR_AGENTS.md),
[deploy/README.md](deploy/README.md).

## Build, test, lint

Run the commands for every component you touch. CI
(`.github/workflows/ci.yml`) enforces exactly these.

### Backend (.NET) — `backend/`

```bash
dotnet build Unswarm.slnx
dotnet test --no-build
```

- Solution: `backend/Unswarm.slnx`.
- Domain logic goes in `src/Unswarm.Core`; HTTP surface in `src/Unswarm.Api`.
- Tests: `tests/Unswarm.Tests` (unit), `tests/Unswarm.E2ETests`.

### Frontend (React) — `frontend/`

```bash
pnpm install --frozen-lockfile
pnpm lint      # oxlint
pnpm test      # vitest
pnpm build     # tsc -b && vite build
pnpm e2e       # playwright (optional)
```

### Agent (Go) — `agent/`

```bash
go vet ./...
golangci-lint run     # config: agent/.golangci.yml
go test -race ./...
```

### CLI (Go) — `cli/`

```bash
go vet ./...
go test ./...
```

### Docker

```bash
docker build -f backend/Dockerfile .
docker build ./agent
```

## Conventions

- **Authorization is security-critical.** Backend controllers use explicit
  policies (`ControlPlaneAccess`, `AdminOnly`, `InferenceKey`, `AgentKey`, ...).
  Do **not** add bare `[Authorize]` — it means "any authenticated user". Any new
  endpoint needs a deliberate policy, and the frontend must never be treated as
  an authorization boundary.
- **Keep it fail-closed.** Permission checks deny unmapped paths/write methods by
  default. Preserve that behavior.
- **Agent and backend are host-equivalent** (Docker socket + script execution).
  Changes to container policy, egress, path handling, or agent commands are
  security-sensitive — treat them accordingly and add tests.
- **Frontend**: feature code lives under `src/features/<feature>/`; shared UI in
  `src/components`; API calls in `src/lib/api`; data fetching via React Query.
  All user-facing strings go through i18n (`src/i18n/locales`) — keep locale key
  parity or `pnpm lint`/tests will fail.
- **SQLite migrations**: EF Core migrations only (startup calls `MigrateAsync()`).
  There is no `EnsureCreated`. See [backend/docs/migrations.md](backend/docs/migrations.md).
- **Config precedence (agent)**: environment overrides YAML when set; conflicts
  log a warning naming the field but never the value.

## Things that must not happen

- **Never commit secrets.** `.env` and friends are git-ignored; shipped configs
  contain placeholders only.
- **Never commit built binaries.** `**/unswarm`, `**/agent`, `agent/agent`,
  `cli/unswarm` are git-ignored — keep them that way.
- **Never commit `SECURITY_AUDIT.md`.** It is an internal, pre-fix artifact with
  file:line exploit paths and is intentionally git-ignored. Do not re-add it, and
  do not paste its contents into public files, commits, or PRs.
- **Don't weaken transport/cookie hardening.** The API is expected to run behind
  a TLS-terminating reverse proxy; the auth cookie is `Secure` on purpose.

## Definition of done

1. The touched component builds.
2. Tests pass (`dotnet test`, `go test`, `pnpm test`) and lint passes
   (`golangci-lint`, `oxlint`).
3. No new secrets, binaries, or internal security docs are staged
   (`git status` is clean of those).
