# Security Policy

Unswarm is a self-hosted control plane for running and routing local and remote
inference workloads. This document explains how to report vulnerabilities, what
the security model and trust boundaries are, and how to deploy the stack safely.

## Supported versions

Security fixes are applied to the latest release and to the `master` branch.
Older tags are not maintained — if you are running a fork or a pinned older
commit, you own the backporting. Always prefer the most recent release.

## Reporting a vulnerability

**Please do not open a public GitHub issue for security vulnerabilities.**

Use GitHub's private vulnerability reporting:

1. Go to the repository's **Security** tab.
2. Click **Report a vulnerability**.
3. Describe the issue in as much detail as you can.

Please include:

- A description of the issue and its impact.
- The affected component (backend API, agent, frontend, CLI) and version/commit.
- Reproduction steps or a proof of concept.
- Any suggested remediation, if you have one.

If private reporting is unavailable to you, open a minimal public issue asking
for a private contact channel — without disclosing the vulnerability details.

### What to expect

- **Acknowledgement** within 3 business days.
- **Triage and severity assessment** after acknowledgement.
- **A fix** for confirmed issues in `master` and the next release.
- **Coordinated disclosure**: we ask that you give us a reasonable window
  (typically 90 days, negotiable for severe issues) before publishing details.
- Credit in the release notes for the fix, if you would like it.

## Security model and trust boundaries

Unswarm has several components with very different privilege levels. Understanding
them is essential to deploying it safely.

| Component | Runs as | Trust level |
|-----------|---------|-------------|
| Backend API + core | Service account, with Docker socket access | **Control plane — treat as host-equivalent** |
| Agent | On each workload host, with Docker socket access | **Host-equivalent on that machine** |
| Frontend SPA | Browser (same-origin cookie auth) | Untrusted |
| CLI | Operator machine | Untrusted by the backend |

Key implications:

- **The backend and every agent are effectively root-equivalent** on their hosts
  because they have Docker socket access and can create containers, mount paths,
  and execute launcher scripts. A control-plane compromise is a host compromise.
- **An agent API key is equivalent to host root** for that agent host. Treat it
  like an SSH root key.
- **Permissions are intended to be least-privilege.** Control-plane API keys
  carry a scoped permission set. Grant only the permissions a key actually needs
  — keys with runtime/script/user/key-management permissions are superuser-grade.
- **The launcher `scripts_dir` is code-execution surface.** Anything placed there
  is executed as bash. It must be root-owned and **not** writable by the agent
  user. Script upload is disabled by default; enable it only for a fully trusted
  backend.
- **Unswarm does not terminate TLS itself.** It must run behind a TLS-terminating
  reverse proxy. The raw API port must never be exposed directly to the internet.

## Deploying securely

Before exposing an Unswarm instance on a public URL:

1. **Never expose the API port directly.** Bind it to loopback and put an
   authenticated, TLS-terminating reverse proxy (Caddy/nginx/Traefik) in front.
   Make sure the proxy handles the WebSocket upgrade for `/ws/agent`.
2. **Restrict the Docker socket.** Prefer routing Docker operations through
   agents or a restricted socket proxy. If the raw socket must be mounted, run
   the container read-only, drop capabilities, and set `no-new-privileges`.
3. **Use least-privilege API keys.** Do not hand out runtime-, script-, user-,
   or key-management permissions unless the holder is fully trusted.
4. **Harden the agent host.** Keep the launcher scripts directory root-owned and
   read-only to the agent user; leave script upload disabled unless required.
   Constrain any loopback port allowlist and container-creation policy instead of
   relying on defaults.
5. **Harden the browser edge.** Set CSP, HSTS, `X-Frame-Options`, and
   `X-Content-Type-Options` at the proxy and self-host fonts rather than loading
   them from third-party CDNs.
6. **Keep dependencies current.** Run the scans below in CI and on release.
7. **Do not rely on the frontend for authorization.** Client-side route guards
   are cosmetic; the backend enforces authorization and must be correct.

## Security practices in this repository

- **No secrets in git.** Secrets and local config (`.env`, databases, build
  artifacts) are git-ignored. Shipped config files contain placeholders only.
- **Key material handling.** API keys are generated from a cryptographically
  secure random source; only a SHA-256 hash and a short non-secret prefix are
  persisted. Plaintext is shown once, at creation. Provider secrets are encrypted
  at rest.
- **Fail-closed authorization.** Permission checks deny unmapped paths and write
  methods by default; controllers use explicit authorization policies.
- **Session hardening.** The auth cookie is `HttpOnly`, `SameSite=Strict`, and
  `Secure`, with 401/403 responses rather than redirects.
- **Input handling.** No raw SQL string construction; external commands are
  invoked via argument arrays; host script paths are validated against traversal.
- **Dependency scanning** (run locally or in CI):

  ```bash
  # Backend (.NET, incl. transitive)
  dotnet list package --vulnerable --include-transitive

  # Frontend
  pnpm audit --prod

  # Agent / CLI (Go)
  govulncheck ./...
  ```

## Scope

**In scope:** vulnerabilities in the code and shipped configuration of the
backend, agent, frontend, and CLI — including authentication/authorization flaws,
injection, privilege escalation, unsafe container/agent policy defaults, SSRF,
and secret handling.

**Out of scope:** issues caused solely by operator misconfiguration (exposing the
API directly, weak proxy settings, over-privileged keys), third-party reverse
proxies and IdPs, social engineering, and denial of service by an authenticated
administrator who already holds host-equivalent access.
