# Cloud Providers Page + Unqueued Proxy — Design & Plan

Status: DRAFT (planning only — no code changes yet). Oracle implementation-readiness
review incorporated (2026-08-23): recon claims verified against codebase; blockers and
major gaps resolved below.
Session: `.slim/deepwork/cloud-providers-proxy-progress.md`

## Goal (user intent)

A standalone **Cloud Providers** page where users save all their cloud LLM providers once
(model name, base URL, API key) so they never manage per-harness API keys again. All
provider traffic routes through unswarm's OpenAI-compatible endpoint: requests targeting a
cloud provider **bypass the local queue/scheduler entirely** ("unqueued") and are proxied
straight through with full SSE stream support. Provider models appear in `GET v1/models`
alongside fleet-hosted models, so any OpenAI-compatible harness pointed at unswarm sees and
uses them transparently.

UI requirements (from user):
- Add Provider button; form fields: provider model name, URL, API key; Fetch Models button.
- List of cloud providers with Edit buttons.

## Current state (verified recon + Gate 1 spot-checks)

- Models are fleet-shaped: `ModelEntity` (`backend/src/Unswarm.Core/Persistence/UnswarmDbContext.cs:8-25`)
  → FK `SourceRuntimeId` → `RegisteredRuntimeEntity` (:101-132) whose `Agent` field ("host"/agent name)
  drives `ModelTargetResolver.ResolveTargetAsync` (`backend/src/Unswarm.Core/Services/ModelTargetResolver.cs:20-35`).
- Queue path: `OpenAIController.HandleInferenceAsync` (`backend/src/Unswarm.Api/Controllers/OpenAIController.cs`)
  builds `InferenceRequest` → `SchedulerQueue.EnqueueAsync` (bounded global channel) →
  `SchedulerWorker.DispatchAsync` → per-target slot → container switch → `InferenceProxy.InvokeAsync`;
  controller pipes response bytes flush-per-chunk for SSE (:130-164).
- `GET v1/models` (:32-53) lists fleet models only, `OwnedBy="unswarm"`.
- Persistence uses EF migrations (`Program.cs:331`) → additive migration is clean.
- Auth: fail-closed `ApiKeyAuthMiddleware`, scoped keys (`InferenceKey` policy). Existing
  `ApiKeyEntity` is hash-only — NOT reusable for provider keys (see Security).
- Frontend: React 19 + TanStack Query v5 + Tailwind v4 CSS-var tokens; pages in
  `frontend/src/features/<name>/index.tsx`; nav `lib/nav-items.ts`; routes `App.tsx`;
  hand-written API layer (`types.ts`, `client.ts`, `httpClient.ts`, `mock.ts`).

## Architecture decisions

1. **Separate entity, not ModelEntity.** New `CloudProviderEntity` table. Cloud models never
   become `ModelEntity` rows (that type carries ContainerImage / SourceRuntimeId / container
   Status / benchmarks — none apply to hosted APIs). Scheduler and `ModelTargetResolver`
   stay untouched.
2. **Namespace prefix is the only routing seam.** Provider models surface as
   `cloud/<providerName>/<model>`. In `HandleInferenceAsync`, if the requested model starts
   with `cloud/`, branch to the unqueued forwarding service BEFORE any queue interaction;
   otherwise the existing queued flow runs unchanged.
3. **Same public endpoints.** No new `/v1` route: harnesses keep pointing at
   `POST v1/chat/completions` (+ `v1/completions`). Routing by model name keeps client setup
   identical for cloud vs local models.
4. **Keys are reversible but encrypted at rest.** ASP.NET DataProtection, machine-scoped,
   purpose string `"Unswarm.CloudProviderApiKey"`, plus `SetApplicationName("Unswarm")` for
   stability across content-root moves. Key-ring persistence: in the current docker-compose
   deployment (`HOME=/data` on a persistent volume) the Linux default location
   (`~/.aspnet/DataProtection-Keys`) already persists — so this is hardening, not fixing a
   live failure mode. Still configure it explicitly:
   `PersistKeysToFileSystem(<app-data>/dp-keys)` mirroring the DB path convention
   (`Program.cs:46-48`), because an ephemeral/app-lifetime ring would make every stored
   ciphertext undecryptable after restart/redeploy (silent loss of all saved keys;
   recovery = re-entering provider keys), and any future bare-metal systemd unit with
   `ProtectHome=true` (cf. `unswarm-agent.service:17`) would break HOME-derived defaults.
   Keys are never returned by any API — list/read DTOs carry only a masked hint stored at
   create time (`sk-…3f9a`; see `ApiKeyHint` column below). Plaintext exists only in memory
   of the forwarding service. (Documented exposure: SQLite file + DP key ring on same machine.)
5. **Backend-side Fetch Models.** Server calls `<baseUrl>/v1/models` with the stored key;
   keys never reach the browser. Result saved as the provider's model list (JSON column on
   the entity), user-editable.
6. **Base URL normalization.** Store origin only (e.g. `https://api.openai.com`);
   append `/v1/models`, `/v1/chat/completions` server-side. Validate/normalize on save so
   Fetch Models doesn't silently 404 for providers whose docs include `/v1`.

## Backend design

### Entity + migration
```
CloudProviderEntity: Id (string, "cp_<rand>"), Name (unique), BaseUrl (normalized origin),
    ApiKeyCiphertext (DataProtected blob), ApiKeyHint (plain string, e.g. "sk-…3f9a" —
    captured at create/update time; a masked hint cannot be derived from the ciphertext,
    precedent: ApiKeyEntity.KeyPrefix, UnswarmDbContext.cs:183),
    ModelsJson (plain string column, list of model ids —
    same pattern as existing ExtraLabelsJson, NOT EF JSON-column mapping),
    CreatedAt, UpdatedAt
```
One clean additive EF migration; cover it in `MigrationTests.cs`.

ModelsJson hygiene (validated on save/fetch): entries are non-empty strings, must not
start with `cloud/`, cap count and total serialized size.

### Management API (admin-scoped, under `/api/cloudproviders`)

Auth mechanism: `[Authorize(Roles = "Admin")]`, same precedent as `ApiKeyController.cs:17`.
Note `/api/cloudproviders` sits outside `ApiKeyAuthMiddleware`'s protected prefixes, so
cookie auth + role is the whole story — consistent with other management controllers.

| Endpoint | Behavior |
|---|---|
| `GET /api/cloudproviders` | List; key masked hint only |
| `POST /api/cloudproviders` | Create; validates name uniqueness + URL normalization; **Name charset restricted to `[a-zA-Z0-9-_]`** (it becomes part of the public model id) |
| `PUT /api/cloudproviders/{id}` | Edit; empty apiKey field = keep existing; **Name is immutable after create** (renaming would silently invalidate every client using `cloud/<oldName>/...`) |
| `DELETE /api/cloudproviders/{id}` | Delete; cascades model list (JSON column dies with row) |
| `POST /api/cloudproviders/{id}/fetch-models` | Server-side fetch of upstream `/v1/models`; returns ids, saves to ModelsJson |

Delete semantics (explicit): deleting a provider removes its models from `v1/models`
immediately; in-flight proxied requests complete or fail naturally (nothing provisioned,
no orphan cleanup). Subsequent `cloud/<deleted>/...` requests return a clean 404-style
error body — never a scheduler miss.

### Forwarding service (`CloudForwardingService`)
- Dedicated named `HttpClient` via `IHttpClientFactory`: `Timeout.InfiniteTimeSpan` (default
  100 s would kill long generations mid-stream); cancellation driven by the request's CT so
  client disconnect cancels the upstream call (stops token spend).
- **Model-id parsing:** split the requested id on the *second* `/` only —
  `cloud/<providerName>/<rest-of-model>` where `<rest-of-model>` may itself contain slashes
  (OpenRouter-style ids like `meta-llama/Llama-3-70B`). This is unambiguous because provider
  names are charset-restricted to `[a-zA-Z0-9-_]` (no `/`). Strip only the
  `cloud/<provider>/` prefix when rewriting the upstream body's `model` field.
- Copies request body verbatim, rewrites only `model`,
  sets `Authorization: Bearer <decrypted key>`, and forwards to the **same path the client
  called** (`Request.Path`, e.g. `/v1/chat/completions` or `/v1/completions`) on the provider
  origin — never a hardcoded chat path, so legacy-completions clients aren't silently misrouted.
- **Header strategy** (the queued-path piping hardcodes `text/event-stream` / stored
  ContentType — a proxy cannot): outbound request strips hop-by-hop headers (`Connection`,
  `Transfer-Encoding`, `Host`), replaces `Authorization`, and **strips `Accept-Encoding`**
  (simplest coherent choice: upstream responds uncompressed, byte-piping stays correct).
  Response side: relay upstream status + `Content-Type` verbatim; do not forward
  hop-by-hop response headers.
- Streams upstream response via `ResponseHeadersRead`; byte-pipe passthrough modeled on
  `OpenAIController.cs:130-164` but with relayed Content-Type per above. Flush-per-chunk
  for SSE. No server-side SSE parsing.
- Upstream non-2xx: relay status + body through unchanged (provider error shapes stay intact).
- **Upstream transport failures** (`HttpRequestException`, TLS errors, timeout/CT-cancelled):
  map explicitly to OpenAI-style `{ "error": { ... } }` bodies — 502 for connect/TLS
  failures, 504 for timeouts; client-disconnect CT cancellations just end the response.
  Decryption failure of a stored key (lost key ring after redeploy) must be a loud,
  distinct 500 — never an upstream-shaped error that misleads debugging. Add all of these
  to P2 tests.
- **Concurrency cap:** global `SemaphoreSlim` (configurable setting, default e.g. 8) around
  the forward call, using `WaitAsync(timeout)` → clean 503/429 error body when saturated
  (a plain `WaitAsync` would leave request N+1 hanging with no feedback until its CT
  fires). Release in `finally`, including client-disconnect paths. The existing per-key
  rate limit (`Program.cs:300-307`, 600 req/min) is throughput, not concurrency — N
  held-open SSE streams need an explicit bound.
- Log-scrub: never log Authorization header or decrypted key.

### v1/models merge
`GET v1/models` returns fleet models (`OwnedBy="unswarm"`) plus one entry per provider
model: `id = "cloud/<providerName>/<model>"`, `OwnedBy = providerName`. Cloud rows need
nullable/placeholder `Unswarm` DTO fields (`OpenAiModelData.Unswarm` is currently
non-nullable with fleet-only fields — fixer-level detail in P2). Collision safety:
`cloud/` is a reserved prefix. **Enforcement point:** there is currently no model-name
validation anywhere — fleet model names flow from container discovery →
`ContainerRegistrationService.cs:987` → `ModelRegistry.CreateAsync`
(`ModelRegistry.cs:45-67`) unchecked. Add the reservation guard in
`ModelRegistry.CreateAsync`/`UpdateAsync` (reject model ids starting with `cloud/`),
otherwise a container exposing a model literally named `cloud/x` would silently shadow a
provider.

## Frontend design

- New page `frontend/src/features/providers/index.tsx`; route `/providers` in `App.tsx`;
  sidebar nav item (Cloud icon) in `lib/nav-items.ts`. Standalone page per user decision —
  not a settings tab.
- Layout follows UsersTab pattern (`features/settings/index.tsx:522-633`): Card with header +
  "Add Provider" button; provider rows (name, base URL, masked key hint, model count) with
  Edit/Delete actions; Skeleton loading; EmptyState when none; ConfirmDialog for delete.
- Add/Edit in a `Dialog` (`components/ui/Dialog.tsx`): fields Name, Base URL, API Key
  (password input; on edit shows masked hint, blank = unchanged) + **Fetch Models** button
  (enabled once URL+key present; shows fetched model count; errors render inline using the
  Input error prop / error-string precedent). Manual validation, no schema lib.
- API layer additions across all four files: `types.ts` (`CloudProvider`,
  `CloudProviderInput`), `client.ts` signatures, `httpClient.ts` impl, `mock.ts`.
- TanStack Query mutations with invalidate-on-success and onError rendering (fleet
  rediscover precedent `features/fleet/index.tsx:1319-1327`).

## Implementation phases (for future execution)

| # | Phase | Scope | Owner | Gate |
|---|---|---|---|---|
| P1 | Backend storage + CRUD + fetch-models | Entity (incl. `ApiKeyHint`), migration (+ `MigrationTests.cs` coverage), DTOs, controller (`[Authorize(Roles = "Admin")]`), DataProtection wiring incl. persisted key ring + `SetApplicationName`, ModelsJson hygiene validation, tests | @fixer | Oracle (security of key handling) |
| P2 | Forwarding + routing + v1/models merge | CloudForwardingService (header strategy, second-slash parsing, transport-failure → 502/504 mapping, decryption-failure 500, semaphore with `WaitAsync(timeout)`), HandleInferenceAsync branch, models merge (cloud rows need nullable/placeholder Unswarm-info DTO fields — fixer-level detail), `cloud/` reservation guard in `ModelRegistry.CreateAsync/UpdateAsync`, HttpClient config via `IHttpClientFactory`, tests (use a stubbed upstream message handler — E2E `WebApplicationFactory` tests cannot hit real providers) | @fixer | Oracle (streaming/correctness/risk) |
| P3 | Providers page UI | Page, nav, dialog forms, fetch-models UX, api client ×4 files | @designer | Oracle (UI/UX review) |
| P4 | Validation | dotnet build/test, vitest, tsc, live smoke: add provider → fetch models → streaming chat via `cloud/...` model | orchestrator | final |

## Risks & mitigations (Gate 1 ranked, updated post-review)

1. Reversible keys at rest → DataProtection machine-scope encryption with explicit
   persisted key-ring path + `SetApplicationName`; reads restricted to forwarding service;
   never serialized to any DTO (masked hint stored separately in `ApiKeyHint`);
   log-scrubbed.
2. Unbounded proxy concurrency → global SemaphoreSlim cap (configurable) with
   `WaitAsync(timeout)` → clean 503/429 instead of silent hangs; release in `finally`.
3. Long-stream HTTP timeouts → infinite-timeout dedicated `IHttpClientFactory` client +
   request-CT propagation.
4. Model-name collisions → reserved `cloud/` prefix enforced in `ModelRegistry`
   Create/Update; second-slash-only id parsing keeps slash-containing upstream model ids
   unambiguous; deleted provider → explicit 404-style error.
5. Delete lifecycle → cascade model list; in-flight completes/fails naturally (documented).
6. Base URL variance → store normalized origin; append `/v1/*` server-side; validate on save.
7. Proxy header/streaming correctness → strip hop-by-hop headers, strip outbound
   `Accept-Encoding`, relay upstream status + Content-Type verbatim.
8. Upstream transport failures → explicit OpenAI-style 502/504 error mapping; decryption
   failure = loud distinct 500 (never upstream-shaped).

## Resolved open questions

- Proxy auth: reuse existing `InferenceKey` policy — no new auth surface.
- Unqueued semantics: full scheduler bypass for `cloud/` targets (no slots, no container
  switching); bounded only by the SemaphoreSlim cap + existing rate limit.
- Fetch Models: backend-side (keys stay server-side).
- Key storage: encrypted-at-rest reversible (not hash-only like ApiKeyEntity).
