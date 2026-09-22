# Unswarm — Security Audit for Public Deployment

**Target:** self-hosted Unswarm stack (ASP.NET Core API + React SPA + Go agent) exposed on a public URL
**Repo state:** working tree at `75744fb` (`dev`)
**Method:** read-only static review of application, infrastructure, and client code, plus dependency vulnerability scans
**Auditor:** automated specialist review (backend, infra/agent, frontend lanes) + orchestrator reconciliation

> This is a static audit of the repository. It does **not** include live penetration testing,
> runtime configuration of your actual host, or your reverse-proxy/IdP settings. Findings are
> grounded in file:line evidence and should be re-validated against the deployed topology.

---

## 1. Executive summary

The application's auth *model* is thoughtfully designed: API keys are stored as SHA-256 hashes,
the session cookie is `HttpOnly` + `SameSite=Strict` + `Secure`, permissions are fail-closed,
there is no SQL/shell injection from user input, and no plaintext secrets are committed.

The public-deployment risk is **not** anonymous authentication bypass. It is a combination of:

1. **Cleartext, unauthenticated-by-default network exposure** — the compose file publishes the
   plain-HTTP API on `0.0.0.0:22301` with no TLS and no loopback binding, so API keys and agent
   keys travel in the clear and Swagger enumerates the whole API.
2. **Extreme blast radius of authenticated control-plane access** — the backend and agents hold
   the Docker socket and execute backend-supplied bash. Any authorization flaw, leaked key, or
   compromised control-plane user becomes **host root** on the backend and every agent host.
3. **Authorization gaps for the default `User` role** — several controllers use bare `[Authorize]`
   with no policy, and the permission filter deliberately skips non-control-plane principals, so
   any logged-in dashboard user can read/overwrite launcher scripts and reach operational data.

**Bottom line:** do not expose port `22301` directly. Terminate TLS at an authenticated reverse
proxy bound to a restricted interface, fix the authorization gaps in §4, and treat every API key
with `runtimes`/`scripts`/`users`/`apikeys` permission as host-root-equivalent.

### Risk summary

| # | Severity | Finding | Component |
|---|----------|---------|-----------|
| C1 | Critical | API published on `0.0.0.0:22301` over plain HTTP, no TLS/proxy config | deploy |
| C2 | Critical | Backend container is root-equivalent on host via `/var/run/docker.sock` | deploy |
| C3 | Critical | Any authenticated `User` can read/overwrite launcher scripts → host RCE | backend |
| H1 | High | `apikeys:rw` is a de-facto superuser (can mint any permission set) | backend |
| H2 | High | `users:rw` → admin account takeover (self-check bypass for API-key identities) | backend |
| H3 | High | `runtimes:rw` → host compromise via container create with host mounts/devices | backend |
| H4 | High | SSRF via cloud-provider base URL (metadata/internal services) | backend |
| H5 | High | Shipped agent template enables plaintext `ws://` (`allow_insecure_ws: true`) | agent |
| H6 | Accepted risk | Plaintext (`ws://`/`http://`) to remote hosts is intentional; `allow_insecure_ws` enforcement to be removed in favor of a warning | agent |
| H7 | High | Agent `create_container` bypasses runtime gate; arbitrary binds/ports/devices | agent |
| H8 | High | Contradictory `scripts_dir` guidance enables backend→host RCE | agent/deploy |
| H9 | High | systemd unit contradicts docs and lacks modern hardening | deploy |
| H10 | High | `allowed_loopback_ports` unrestricted by default (SSRF pivot) | agent |
| M1–M15 | Medium | Operational-data exposure, proxy-header gaps, SSRF hardening, image/CI hygiene, CSP, agent runtime-gate bug, known Go vulns | all |
| L1–L18 | Low | Defense-in-depth, info disclosure, hardening nits | all |

---

## 2. Scope & assets

| Component | Path | Notes |
|-----------|------|-------|
| Backend API | `backend/src/Unswarm.Api` | ASP.NET Core (.NET 10), 20 controllers, cookie + API-key auth |
| Core domain | `backend/src/Unswarm.Core` | Docker controller, inference proxy, cloud forwarding, stores |
| Frontend SPA | `frontend/` | React 19 + Vite, same-origin cookie auth |
| Agent | `agent/` | Go, remote host, Docker socket + script execution |
| CLI | `cli/` | Go client |
| Deployment | `docker-compose.yml`, `deploy/`, `Dockerfile`s, `.github/workflows/` | |

No live testing was performed. Local `.env` (per operator) is a development-only file and is
**not** treated as a finding; the repository confirms it contains no secrets and is git-ignored.

---

## 3. Critical findings

### C1 — API published on `0.0.0.0:22301` over plain HTTP; no TLS or proxy config shipped

- **Evidence:** `docker-compose.yml:9-10` (`- "22301:5014"`, no `host_ip`), `backend/Dockerfile:29`
  (`ASPNETCORE_URLS=http://0.0.0.0:5014`), `README.md:765-771`.
- **Impact:** `docker compose up` on a public host exposes the API, `/v1`, and `/ws/agent` in
  cleartext. Agent and inference API keys (`Authorization`/`X-Api-Key`) are sniffable. Swagger is
  served anonymously (`Program.cs:558-565`), disclosing the full API surface. The cookie being
  `Secure` (`Program.cs:132`) creates a false sense of safety — API-key auth is unaffected and
  still sent in the clear.
- **Exploit:** passive network attacker captures agent/inference keys; unauthenticated attacker
  enumerates endpoints via `/swagger/v1/swagger.json`.
- **Remediation:** bind to loopback by default (`127.0.0.1:22301:5014`); require a TLS-terminating
  reverse proxy for external access; ship a reference Caddy/nginx/Traefik config incl. WebSocket
  upgrade for `/ws/agent`; add a prominent "do not expose 22301 directly" warning.

### C2 — Backend container is root-equivalent on the host

- **Evidence:** `docker-compose.yml:17-18` (`/var/run/docker.sock` mount), `:27-28`
  (`group_add: DOCKER_GID`), `:25-26` (host `/proc`, `/sys/fs/cgroup` read-only).
- **Impact:** the `app` user has full Docker API access. Any backend RCE, SSRF-to-Docker, or
  malicious/compromised control-plane user can run
  `docker run --rm -v /:/host --pid=host --network=host alpine chroot /host sh`, yielding host
  root and access to the SQLite DB, the DataProtection key ring, every agent key, `.env`, and all
  SSH keys — then pivoting to every agent host.
- **Exploit:** chained with C3/H1–H3, a single authorization bypass crosses the container boundary.
- **Remediation:** avoid the raw socket — route Docker ops through agents or a restricted
  socket-proxy (`tecnativa/docker-socket-proxy`) exposing only required read endpoints on a private
  network. If unavoidable: `read_only: true`, `cap_drop: ["ALL"]`,
  `security_opt: ["no-new-privileges:true"]`, dedicated non-root UID, and drop the host `/proc` mount.

### C3 — Any authenticated `User` can read/overwrite launcher scripts → host RCE

- **Evidence:** `ScriptsController.cs:28` (class-level bare `[Authorize]`, no policy); actions
  `:70` upload, `:100` update, `:134` content read, `:159` delete, and the remote-agent variants.
  `ControlPlanePermissionFilter.cs:35` skips the permission matrix for any principal that is not a
  ControlPlane API key, and `UsersController.cs:47` grants every API-created user the `User` role.
  There is no global fallback authorization policy, so bare `[Authorize]` = "any authenticated user".
- **Impact / exploit:** a normal dashboard user can call the API directly to read scripts that
  commonly embed tokens/paths, then overwrite an admin launcher. On next start (including
  on-demand inference start via `InferenceProxy.StartOnDemandAsync`),
  `HostScriptRuntimeController.cs:80-92` runs `/bin/bash --login <launcherPath>` → **code execution
  as the service account**, which (per C2) reaches host root. Host endpoints are gated in Docker
  mode (`HostEnvironment.IsRunningInDocker`), but the remote-agent endpoints
  (`/api/scripts/agent/{agentName}/...`) remain reachable and forward an unvalidated `file.FileName`.
- **Remediation:** require `ControlPlaneAccess`/Admin on `ScriptsController` (especially mutating and
  content-read actions); define a global `FallbackPolicy` (deny-by-default) and give every
  `[Authorize]`-only endpoint an explicit policy; validate remote `fileName` with
  `HostScriptDirectoryService.ValidateFileName` before sending to agents.

---

## 4. High findings

### H1 — `apikeys:rw` is a de-facto superuser

- **Evidence:** `ApiKeyController.cs:29,95-110,136-151,212-242`; `ApiKeyStore.cs:58-73,312-323`.
- **Impact:** a key scoped only to `apikeys:rw` can mint a new ControlPlane key with **any** valid
  domain permission (no subset/ceiling check, `ValidDomains` at `:53`) and rotate/revoke any other
  key (only self is blocked), with rotate returning the new plaintext secret.
- **Exploit:** leaked CI key with `apikeys:rw` → mint `users:rw` + `cloudproviders:rw` → admin
  takeover (H2) / SSRF provider.
- **Remediation:** refuse to mint keys whose permissions are not a subset of the caller's, or add a
  separate `apikeys:delegate` capability; never rotate another principal's key without Admin.

### H2 — `users:rw` → admin account takeover via password reset

- **Evidence:** `UsersController.cs:55-77` (`ResetPassword`), guard `:61-63`; `:79-94` (`Delete`),
  guard `:85-87`; `ApiKeyAuthMiddleware.cs:270-294` (`WithKeyIdentity` adds no `NameIdentifier`).
- **Impact:** for an API-key principal `GetUserId(User)` returns `null`, so the "cannot modify
  yourself" checks never match. A key with `users:rw` can reset **any** password, including the
  seeded `admin`, then log in with a full cookie session; it can also delete the admin (DoS).
- **Remediation:** resolve the acting identity explicitly for API-key callers; require the `Admin`
  role (not merely `users:rw`) for password resets and deletions.

### H3 — `runtimes:rw` → host compromise via container creation

- **Evidence:** `ContainersController.cs:104-147`; `Dtos/ContainerDtos.cs:170-190` (`IpcMode`
  defaults to `"host"`); `DockerController.cs:459-593` (esp. `:505-543`: arbitrary binds, devices
  `rwm`, host networking).
- **Impact:** `runtimes:rw` can create a container with `Volumes: [{Host:"/",Container:"/host"}]`
  or the Docker socket, `Devices`, `NetworkMode: "host"` — full host access. Effectively root.
- **Remediation:** block host-path binds and `/dev` mapping unless caller is an Admin cookie;
  allowlist images/volumes; drop host `NetworkMode`/`IpcMode` defaults.

### H4 — SSRF via cloud-provider base URL

- **Evidence:** `CloudProviderController.cs:564-582` (`NormalizeBaseUrl` — no private-IP/link-local
  block), `:207-224` (`test-and-fetch`), `:142-201` (`fetch-models`);
  `CloudForwardingService.cs:186` (stored base URL used for `/v1` forwarding).
- **Impact:** `cloudproviders:rw`/Admin can point a provider at `http://169.254.169.254/...` or
  internal services; later `/v1` requests POST attacker-influenced JSON to the internal target.
- **Remediation:** after DNS resolution reject RFC1918/loopback/link-local/IPv6 ULA (reuse the
  address-resolving `ConnectCallback` pattern at `Program.cs:235-269`); require HTTPS except
  allow-listed hosts; rate-limit `test-and-fetch`.

### H5 — Shipped agent template enables plaintext WebSocket

- **Evidence:** `agent/agent.yaml:9` (`backend_url: "ws://backend:url"`), `:15`
  (`api_key: "ak_replace_me_api_key"`), `:27` (`allow_insecure_ws: true`), and precedence logic in
  `agent/internal/config/config.go:99-131`; compose wiring `docker-compose.yml:65,71-72`.
- **Impact:** operators who copy the template connect over `ws://` and send the agent API key in
  cleartext. Additionally, a non-empty YAML value always wins over env vars, so the compose
  profile's `UNSWARM_AGENT_BACKEND_URL`/`UNSWARM_AGENT_API_KEY` are silently ignored (and
  `ws://backend:url` fails URL validation with `invalid port ":url"`).
- **Remediation:** ship a `wss://` placeholder URL and blank `api_key`; make env override YAML in
  container contexts; fail fast on invalid URLs rather than silently misconnecting. (The
  `allow_insecure_ws: true` line is dropped along with the enforcement — see H6.)

### H6 — Plaintext `http://` is allowed while `ws://` is blocked (accepted risk, consistency fix)

- **Evidence:** `agent/internal/config/config.go:206-227` (guard only when scheme is `ws`),
  `agent/internal/client/client.go:319-348` (`http`→`ws` conversion), `:308-317`.
- **Description:** `backend_url: "http://remote.example.com:5014"` passes validation because the
  scheme is not `ws`; the client then connects `ws://` and sends the key in cleartext. Meanwhile
  the equivalent `ws://remote.example.com:5014` is hard-blocked unless `allow_insecure_ws: true`.
  The two spellings therefore disagree.
- **Decision (operator):** plaintext to a remote backend is an intentional operator choice; the
  hoster is not required to run TLS. The fix is therefore to stop *blocking* plaintext, not to
  tighten `http://`.
- **Remediation:** remove the `allow_insecure_ws` hard block in `validateInsecureWs` (and do not
  add the planned `Connect` hard error); allow both `ws://` and `http://` to any host; log a
  warning (host only, never the key) when the link is plaintext + non-loopback. Drop / deprecate
  the `allow_insecure_ws` key. The shipped template still shows `wss://` as the recommended value.

### H7 — Agent `create_container` bypasses the runtime gate

- **Evidence:** `agent/cmd/agent/main.go:594-617` (`create_container`, ungated), `:660-669`
  (`start_script`), `:695-710` (`upload_script`/`update_script`); `runtimegate.go:148-160` (gate
  covers start/stop/restart/remove/logs/inspect but **not** `create_container`);
  `internal/docker/handler.go:91-203` (arbitrary image, `Binds`, `Devices`, `NetworkMode`,
  `IpcMode`, env, `HostIP: "0.0.0.0"`); `internal/scripts/manager.go:150,324` (write then `bash`).
- **Impact:** a leaked/misused agent key or a compromised backend sends one command to bind the
  agent host root (or the socket) into a container and escape to root on every GPU host; 0.0.0.0
  port publishes can also expose model servers to the internet. `enforce_registered_runtime` does
  not help because `create_container` is not gated.
- **Remediation:** gate `create_container` through the registry/allowlist; disallow sensitive binds
  (`/`, `/etc`, `/var/run/docker.sock`, `/proc`), `networkMode: host`, and undeclared devices;
  default `HostIP` to `127.0.0.1`; make script upload/start off by default; document that an agent
  key is equivalent to host root.

### H8 — Contradictory `scripts_dir` guidance enables backend→host RCE

- **Evidence:** `agent/agent.yaml:48-53` ("MUST be owned by root and NOT writable by the agent
  user") vs `README.md:292-293,304-307` (instructs `chown unswarm:unswarm
  /opt/unswarm/scripts`, i.e. writable) and `deploy/unswarm-agent.service:26`
  (`ReadWritePaths=/var/lib/unswarm`, which conflicts with the README's `/opt/unswarm/scripts`).
- **Impact:** following the README makes the scripts dir agent-writable and executes dropped files
  as bash → RCE-by-design via the backend/agent key, escalating to host root through the socket.
- **Remediation:** choose one model and document it consistently — (a) root-owned read-only scripts
  dir with upload/update disabled, or (b) writable dir with script start/upload disabled by default
  and a loud warning. Align systemd `ReadWritePaths` with the documented path.

### H9 — systemd unit contradicts docs and lacks hardening

- **Evidence:** `deploy/unswarm-agent.service:17` (`ProtectHome=true`) vs `deploy/README.md:95-101`
  ("**Do not add `ProtectHome=true`**"); unit lacks `CapabilityBoundingSet`,
  `RestrictAddressFamilies`, `SystemCallFilter`, `RestrictNamespaces`, `ProtectProc`, `ProcSubset`,
  `ProtectHostname`, `ProtectClock`, `RemoveIPC`, `UMask`.
- **Impact:** the docs/unit mismatch invites operators to weaken hardening; missing syscall/address
  restrictions enlarges post-exploitation capability for a process that already has Docker access.
- **Remediation:** align with docs; add the missing restrictions (see `systemd-analyze security`).

### H10 — `allowed_loopback_ports` unrestricted by default

- **Evidence:** `agent/agent.yaml:41-46` (commented out), `internal/config/config.go:41-45`,
  `internal/docker/local.go:23-33` (empty ⇒ allow all), `:38-70,100-140` (GET `/`, GET
  `/v1/models`, POST `/v1/chat/completions` to any `127.0.0.1:<port>`).
- **Impact:** a compromised backend can use the agent as an SSRF pivot to probe and POST arbitrary
  JSON to every localhost service on a GPU host (admin UIs, metrics, Jupyter, caches).
- **Remediation:** ship a conservative allowlist of model/runtime ports, or make empty mean
  "deny all" with an explicit `unrestricted_loopback: true` opt-in.

---

## 5. Medium findings

| # | Finding | Evidence | Remediation |
|---|---------|----------|-------------|
| M1 | Unclamped log `limit` → data dump / DoS | `LogsController.cs:39,43`; `LogStore.cs:124-126` | `limit = Math.Clamp(limit, 1, 1000)` |
| M2 | Operational data exposed to any authenticated user via bare `[Authorize]` (metrics incl. key names, settings, containers, queue, prompts, agents) | `MetricsController.cs:38,73,550,608`; `SettingsController.cs:17,25`; `ContainersController.cs:33,63,209,223`; `StatsController.cs:9`; `RuntimeStatusController.cs:13`; `QueueController.cs:18,26`; `PromptsController.cs:26`; `BenchmarksController.cs:24,403`; `AgentsController.cs:28,58,76,98` | Assign explicit policies; default-deny for non-admin cookie principals |
| M3 | `AllowedHosts: "*"` and no HTTPS redirect | `appsettings.json:8`; `Program.cs` (no `UseHttpsRedirection`) | Set real hostnames; redirect to HTTPS behind proxy |
| M4 | Rate limiter and loopback gates unreliable behind reverse proxy (no `ForwardedHeaders`) | `Program.cs:406-450,598-604` | Configure `UseForwardedHeaders` with explicit known proxies **before** rate limiting/auth |
| M5 | Remote script upload filename not validated by backend | `ScriptsController.cs:193-254`; `RemoteAgentDockerController.UploadScriptAsync` | Apply `HostScriptDirectoryService.ValidateFileName`; reject `..`/absolute paths |
| M6 | Error detail leakage (`ex.Message`) in API responses | `ContainersController.cs:145,205,289`; `ModelsController.cs:443,509`; `CloudProviderController.cs:99,128,255,319`; `AgentsController.cs:128,133,159,164`; `RouterProfileController.cs:177` | Generic message + correlation id; log details server-side |
| M7 | Agent-scoped keys can enumerate all agents and their scripts | `ApiKeyAuthMiddleware.cs:45-51`; `AgentsController.cs:58,76,98` | Restrict agent scope to its bound agent, or require ControlPlane access |
| M8 | Agent `list_containers` gate is dead code — full host inventory leaks | `runtimegate.go:122-144` (no call site); `cmd/agent/main.go:567-575`; `docker/handler.go:243-279` | Call `FilterListResult` in `handleCommand`; add integration test |
| M9 | No container hardening in compose (no `read_only`, `cap_drop`, `no-new-privileges`, limits) | `docker-compose.yml:4-49,59-72` | Add `security_opt`, `cap_drop: ["ALL"]`, `read_only` + tmpfs, mem/pids/CPU limits |
| M10 | Base images use mutable, unpinned tags | `backend/Dockerfile:2,11,25`; `agent/Dockerfile:2,10` | Pin by `@sha256:` digest; add Dependabot/Renovate + SBOM/image scan |
| M11 | Swagger/OpenAPI exposed anonymously in all environments | `Program.cs:453-493,558-565` | Gate to Development or admin auth; disable in production image |
| M12 | Docker build context leaks local files (`.env`, `.opencode`, DB, binaries) | root `.dockerignore` missing `.env`/`*.db`; no `agent/.dockerignore`; `agent/Dockerfile:6` (`COPY . .`) | Extend `.dockerignore`; add `agent/.dockerignore`; COPY only source |
| M13 | Committed Go binaries in git (stale, symbol leakage, supply-chain vector) | `agent/agent` (tracked, 14 MB), `cli/unswarm`; `.gitignore:14,23` misses `agent/agent` | `git rm --cached`; gitignore; distribute signed releases |
| M14 | Host `/proc` + cgroup mounts expose host metadata | `docker-compose.yml:25-26` | Prefer agent-collected metrics; mount only specific `/proc` files |
| M15 | Frontend has no CSP/security headers; Google Fonts loaded without SRI | `frontend/index.html:9-14,15-30`; `frontend/vite.config.ts:5-16` | Set CSP/HSTS/XFO/nosniff/Referrer-Policy at proxy; self-host fonts or add SRI |
| M16 | Known vulnerabilities in Go Docker client dependency | `govulncheck`: GO-2026-4887, GO-2026-4883 in `github.com/docker/docker@v27.5.1+incompatible`, **no fix available** | Track upstream; assess Moby AuthZ-plugin-relevant exposure; consider pinning newer fork/version when fixed |

---

## 6. Low findings

**Backend**
- L1 Swagger anonymous (also M11).
- L2 Account-lockout DoS: 5 failed attempts lock `admin` for 15 min (`Program.cs:108-110`,
  `AuthController.cs:44`). Consider IP throttling/CAPTCHA.
- L3 `Cookie` header forwarded upstream (`OpenAIController.cs:820-846` excludes `Authorization`/`Host`
  but not `Cookie`); currently not reachable with a valid session, but exclude it (defense in depth).
- L4 `AuthController` actions use manual `IsAuthenticated` checks instead of `[Authorize]`
  (`AuthController.cs:55-94`); add attributes.
- L5 Password policy is length-only (`Program.cs:99-105`).
- L6 DataProtection key ring unencrypted at rest (`Program.cs:84-91`); consider
  `ProtectKeysWithCertificate`.
- L7 OAuth token-exchange failure body logged (`ChatGptOAuthService.cs:94`); redact.
- L8 WebSocket `AllowedOrigins` derived from CORS config (`Program.cs:535-538`); an empty array
  allows all origins. Validate explicitly.
- L9 No body-size limit on `/v1` beyond Kestrel default (`OpenAIController.HandleInferenceAsync`
  reads whole body); add `[RequestSizeLimit]`.

**Agent / deploy**
- L10 `HOST_SCRIPTS_DIR` `~` expansion depends on invoking user (`docker-compose.yml:21`,
  `.env.example:17`); use an absolute path or named volume.
- L11 CI actions pinned to mutable major tags; no dependency/SBOM/secret scanning;
  `.golangci.yml` lacks `gosec` (`.github/workflows/ci.yml`). Pin by SHA; add scanning.
- L12 Runtime image installs `curl` only for healthcheck (`backend/Dockerfile:26`).
- L13 `AllowedHosts:"*"` and empty Prometheus `ScrapeToken` default (loopback fallback is safe).
- L14 Script files written world-readable `0o644` (`agent/internal/scripts/manager.go:150`); use
  `0o700`/`0o750`.
- L15 `LoggableURL` can echo a malformed URL containing credentials (`client.go:311-317`); return a
  fixed placeholder on parse failure.
- L16 `.env` included in build context (see M12).

**Frontend**
- L17 Server-provided OAuth `verificationUrl` used as raw `href`
  (`frontend/src/features/providers/index.tsx:203,240,305-308`); allowlist schemes.
- L18 Login `from` redirect is internally sourced but unvalidated
  (`frontend/src/features/login/index.tsx:226,242`); enforce root-relative.
- L19 CSRF posture relies solely on cookie flags (no token); safe with `SameSite=Strict` same-origin,
  but if `VITE_API_URL` cross-origin forces `SameSite=None`, state-changing requests need an
  additional control.
- L20 Client-side route guards are cosmetic (`ProtectedRoute.tsx:17-19`); acceptable only while the
  backend enforces authorization (M2/C3 show it must be fixed).
- L21 Real-time channel inconsistencies: `use-live-tail.ts:19-24` can emit `ws://` from an `http://`
  base; `use-runtime-status.ts:70` omits `withCredentials`.

---

## 7. Verified controls (checked, working as intended)

- API keys: 256-bit random, only SHA-256 hash + 8-char prefix persisted, raw secret shown once
  (`ApiKeyStore.cs:292-310`).
- Cookie hardening: `HttpOnly`, `SameSite=Strict`, `SecurePolicy.Always`, 401/403 instead of
  redirects (`Program.cs:127-147`); JSON-only binding + same-origin CORS mitigates CSRF.
- Secrets at rest encrypted via DataProtection; DTOs expose masked hints only
  (`DataProtectionEncryptor.cs`, `CloudProviderController.cs:588-593`).
- No SQL injection: no `FromSqlRaw`/`ExecuteSqlRaw`/string-built SQL.
- No shell injection from user input: Docker.DotNet API, `ProcessStartInfo.ArgumentList`, argument
  arrays (`DockerController.cs`, `HostScriptRuntimeController.cs:80-92`,
  `HostScriptDirectoryService.cs:137`).
- Path-traversal protection for host scripts (`HostScriptDirectoryService.cs:65-104`).
- API-key path scoping strict and fail-closed (`ApiKeyAuthMiddleware.TryResolveScope:227-248`).
- Per-key model access control for inference and model listing (`OpenAIController.cs:157-164,362-370`,
  `ApiKeyAccessService.cs:85-121`).
- Control-plane permission matrix fail-closed for unmapped paths and write methods
  (`ControlPlanePermissionFilter.cs:45-55`, `PermissionCheck.cs:17`).
- Agent WebSocket: origin allowlist, `AgentKey` policy, per-agent key binding with atomic first-use
  (`Program.cs:535-538`, `AgentController.cs:111-122`), 1 MB message cap.
- Prometheus endpoint: constant-time token compare or loopback-only (`Program.cs:574-609`).
- Account enumeration mitigated: uniform 401 for unknown/password/locked (`AuthController.cs:41-50`).
- Response security headers: CSP (`object-src 'none'`, `frame-ancestors 'none'`, `connect-src
  'self'`), `X-Frame-Options: DENY`, `X-Content-Type-Options: nosniff`, Referrer-Policy
  (`Program.cs:704-717`).
- Frontend: no `dangerouslySetInnerHTML`/`innerHTML`/`eval`/`new Function`/markdown renderer; no
  tokens/keys in `localStorage`/`sessionStorage`/`document.cookie`; all dynamic data rendered as
  React text nodes; no committed secrets (frontend lane).
- Agent TLS fingerprint pinning verified correctly before the handshake and fail-closed
  (`agent/internal/client/client.go:135-171`); CLI config `0600`, symlink rejection, no persisted
  key, cross-host redirect protection.
- No default admin password: seeding requires `--admin-setup`/`UNSWARM_ADMIN_PASSWORD`
  (`Program.cs:31-45,645-692`); compose line commented.
- CI has `permissions: contents: read`, no `pull_request_target`, no untrusted-event interpolation.
- No secrets committed: `.env` git-ignored and secret-free; `agent/agent.yaml:15` is a placeholder;
  `backend/unswarm.db` untracked.

---

## 8. Dependency vulnerability scans

| Ecosystem | Tool | Result |
|-----------|------|--------|
| .NET (NuGet, incl. transitive) | `dotnet list package --vulnerable --include-transitive` | **No vulnerable packages** |
| Frontend (npm ecosystem via pnpm lock) | `pnpm audit --prod` | **No known vulnerabilities** |
| Go (agent) | `govulncheck ./...` | **2 reachable vulnerabilities** in `github.com/docker/docker@v27.5.1+incompatible` (see M16): GO-2026-4887 (AuthZ plugin bypass on oversized bodies), GO-2026-4883 (off-by-one in plugin privilege validation). No fixed version available. |
| CLI (Go) | — | Not separately scanned; shares the Go module family. Recommend `govulncheck` on CI. |

---

## 9. Prioritized remediation plan (for a public URL)

**Do before exposing the service (blockers):**
1. Bind the API to loopback and put it behind a TLS-terminating reverse proxy (C1); ship the proxy
   config and disable/anonymous-gate Swagger (M11).
2. Fix the authorization gap: global deny-by-default fallback policy + explicit policies on
   `ScriptsController` and all bare `[Authorize]` controllers (C3, M2).
3. Constrain privileged permissions and API-key delegation: permission-subset enforcement (H1),
   Admin-only user reset/delete (H2), host-mount/device/network restrictions (H3), SSRF egress
   filtering (H4).
4. Harden the agent trust boundary: gate `create_container`, restrict binds/ports/devices, disable
   script upload/start by default, fix env-vs-YAML precedence and blank the shipped URL template
   (plaintext itself is an accepted operator choice — warn only), set a conservative
   `allowed_loopback_ports`, align scripts_dir docs and systemd unit (H5, H7–H10, M8).

**Do right after:**
5. Remove/replace the raw Docker socket, or front it with a restricted socket-proxy; add compose
   container hardening (C2, M9).
6. Configure `UseForwardedHeaders` with known proxies before auth/rate limiting (M4); set
   `AllowedHosts` precisely (M3).
7. Clamp inputs, stop leaking `ex.Message`, validate remote filenames, constrain agent-scope reads
   (M1, M5, M6, M7).
8. Add CSP / security headers at the proxy and self-host fonts (M15); fix low-risk frontend items.

**Hygiene:**
9. Pin base images/CI actions by digest/SHA; add SBOM + dependency/secret/image scanning (M10, L11).
10. Fix build-context hygiene and remove committed binaries (M12, M13).
11. Track the Moby advisory and upgrade when a fix is released (M16).

---

## 10. Caveats & uncertainty

- `ControlPlanePermissionFilter`/bare-`[Authorize]` behavior (C3, M2) was **confirmed by direct code
  read**; exploitability in your deployment depends on whether non-Admin `User` accounts are issued
  (the code path exists and login has no role gate).
- M1's negative-`limit` → unlimited behavior is SQLite-provider-dependent and was not runtime-tested;
  the large-positive-value case is certain.
- Exception→ProblemDetails behavior in Production was not runtime-verified, but controllers'
  explicit `ex.Message` returns (M6) are confirmed.
- Agent-side sanitization of remote `fileName` (M5) is in the Go agent; the backend-side gap is
  confirmed and cross-boundary exploitability was not runtime-tested.
- No live penetration test or reverse-proxy/runtime configuration review was performed.
