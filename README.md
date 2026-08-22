<div align="center">
  <img src="assets/unswarm-icon.svg" alt="Unswarm" width="128" />
  <h1>Unswarm</h1>
  <p><a href="LICENSE"><img src="https://img.shields.io/badge/license-MIT-blue" alt="License: MIT"></a></p>
</div>

A self-hosted control plane for managing LLM inference infrastructure across multiple machines. Unswarm lets you register remote agents, orchestrate Docker containers running model servers (llama.cpp, vLLM, etc.), route OpenAI-compatible inference requests, benchmark models, and monitor your fleet — all from a single dashboard.

demo video:
[![Unswarm demo](https://img.youtube.com/vi/7wesHD9aXlo/maxresdefault.jpg)](https://youtu.be/7wesHD9aXlo)

## Why Unswarm?

Running multiple LLM models on a single machine is expensive — most machines can only keep one or two models loaded in VRAM at a time. Unswarm solves this by turning your machine into a **model switcher**: you pre-provision one container per model (each running an inference server like llama.cpp), and Unswarm's scheduler loads and unloads them on demand.

**Example:** A machine with 24 GB VRAM has 10 containers, each configured with a different model (Qwen, North Mini, Gemma, etc.). Only one model fits in memory at a time. When a request comes in for a model that isn't loaded, Unswarm automatically:

1. Stops the currently running container/runtime
2. Starts the container/runtime serving the requested model
3. Proxies the inference request to the freshly started server

From the caller's perspective, it looks like all 10 models are available — the `/v1/chat/completions` endpoint handles the switching transparently. This enables **multi-agentic workflows** where different agents or tools can request different models through a single API endpoint, and Unswarm handles the lifecycle.

### Model Grouping

Not all models need to be exclusive. Unswarm supports **model groups** that control co-location:

- **Exclusive groups** — Models that must be the only one running (e.g., large 27B models competing for VRAM). Only one model from the group can be active at a time.
- **Co-located groups** — Models that can run simultaneously (e.g., a small 9B models).

This lets you define which models compete for resources and which can coexist, matching your actual hardware constraints.

### Trade-offs

Model switching isn't free. There are inherent costs to this approach:

- **Model loading time** — Starting an inference server and loading model weights into VRAM takes seconds to minutes depending on model size and storage speed. First-token latency for a cold model will include this startup cost.
- **Cold-start prefill** — Inference servers hold computed prompt state (KV cache) only in memory, so the first request after a switch pays prefill cost again. This is a latency cost only: the Chat Completions API is stateless, so clients send the full message history on every request regardless, and no conversation data is lost across switches. Weight-loading time dominates cold starts.
- **Hit rate** — Under concurrent workloads requesting different models, frequent switching reduces effective throughput. The scheduler mitigates this with an inference queue, but high model churn means more time spent loading and less time generating.

These trade-offs are inherent to shared-VRAM scheduling. Unswarm is designed for workloads where you have many models but low concurrent demand per model — not for serving high-throughput traffic to a single model.

## Architecture

Unswarm separates **control plane** traffic (managing the fleet) from the **inference data plane** (serving model requests). Both flow through the backend — agents and containers are never exposed directly.

### Control Plane

The dashboard talks to the backend over REST only (polling). Agents dial *out* to the backend over a persistent WebSocket; the backend never connects to agents directly. Container management uses the Docker SDK on the backend's own host, and WebSocket commands relayed through the agent for remote hosts.

```
┌──────────────┐   REST API    ┌──────────────┐  Docker SDK   ┌────────────────┐
│   Frontend   │◄─────────────►│   Backend    │──────────────►│  Docker Engine │
│  (React/TS)  │   (polling)   │  (.NET 10)   │               │ (backend host) │
└──────────────┘               │              │               └────────────────┘
                               │  SQLite DB   │
                               └──────▲───────┘
                                      │ WebSocket /ws/agent (agent-initiated)
                               ┌──────┴───────┐  Docker SDK   ┌────────────────┐
                               │  Agent (Go)  │──────────────►│  Docker Engine │
                               │ (remote host)│               │  (agent host)  │
                               └──────────────┘               └────────────────┘
```

### Inference Data Plane

API clients send OpenAI-compatible requests to the backend. The scheduler queue decides where the target model runs and proxies accordingly — callers never talk to agents or containers directly.

```
┌──────────────┐ POST /v1/chat/completions ┌──────────────┐
│ API client / │──────────────────────────►│   Backend    │
│ OpenAI SDK   │                           │   scheduler  │
└──────────────┘                           │    queue     │
                                           └──┬────────▲──┘
                     model runs on backend    │        │ response
                     host?                    ▼        │
                         ┌─────────────────────────┐    │
                         │  HTTP proxy to          │────┘
                         │  127.0.0.1:<port>       │
                         └─────────────────────────┘
                                              │ else: tunnel request over
                                              │ agent WebSocket
                                              ▼
                                     ┌──────────────┐   HTTP    ┌──────────────┐
                                     │  Agent (Go)  │──────────►│  Container   │
                                     │ calls local  │           │ (llama.cpp,  │
                                     │   server     │           │  vLLM, …)    │
                                     └──────────────┘           └──────────────┘
```

| Component | Stack | Description |
|-----------|-------|-------------|
| **Backend** | C# / .NET 10, EF Core, SQLite | REST API + WebSocket server. Manages models, containers, scheduling, benchmarks, and proxies inference requests to agents. |
| **Agent** | Go, Docker SDK, Gorilla WebSocket | Lightweight daemon that runs on each remote machine. Connects outbound to the backend, manages local Docker containers, streams telemetry, and serves inference requests. |
| **Frontend** | React 19, TypeScript, Vite, Tailwind CSS v4 | Single-page dashboard for fleet management, model registry, benchmarking, queue monitoring, and settings. |

## Features

- **Fleet Management** — Register and manage Docker containers and script runtimes across multiple agent machines. Start, stop, restart, and inspect containers remotely.
- **Model Registry** — Auto-discover models from running inference servers. Track model status, associate models with containers, and manage model-to-runtime mappings.
- **OpenAI-Compatible Proxy** — Backend exposes `/v1/chat/completions` that routes requests to the correct agent and container, enabling a unified API endpoint for all your models.
- **Automatic Model Switching** — Scheduler loads and unloads models on demand, letting you serve many models from limited VRAM with a single API endpoint.
- **Model Groups** — Define exclusive groups (one model at a time) and co-located groups (models that share VRAM) to match your hardware constraints.
- **Inference Queue** — Bounded request queue with scheduler for managing concurrent inference workloads across the fleet.
- **Benchmarks** — Run benchmark prompts against models and track performance history (latency, tokens generated).
- **Telemetry** — Agents stream host info (CPU, memory, GPU), container statuses, and script process info to the backend over WebSocket; the dashboard picks it up via polling.
- **Saved Prompts** — Prompt library for reusing benchmark and inference prompts.
- **Settings** — Configurable idle shutdown, health check intervals, log retention, and auth.

## Quick Start

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download) (backend)
- [Go 1.26+](https://go.dev/dl/) (agent)
- [Node.js 20+](https://nodejs.org/) and [pnpm](https://pnpm.io/) (frontend)
- [Docker](https://docs.docker.com/get-docker/) (on agent machines)

### 1. Start the Backend

```bash
cd backend
dotnet run --project src/Unswarm.Api
```

The API starts on `http://localhost:5014` by default. The SQLite database is created automatically at `~/.config/unswarm/unswarm.db`.

On first run, you'll need to create an admin user:

```bash
dotnet run --project src/Unswarm.Api -- --admin-setup 'your-password'
```

The `--admin-setup` flag also works to reset the admin password at any time.

### 2. Start the Frontend

```bash
cd frontend
pnpm install
pnpm dev
```

The dashboard opens at `http://localhost:5173`.

### 3. Run an Agent (on a remote machine)

```bash
cd agent
go build -o unswarm ./cmd/agent
./unswarm -config agent.yaml
```

Edit `agent.yaml` to point to your backend:

```yaml
backend_url: "ws://your-backend-ip:5014"
agent_name: "machine-b"
docker_socket: "unix:///var/run/docker.sock"
```

See [agent configuration](backend/docs/agent-config.md) for all options.

## API Reference

### REST Endpoints

| Endpoint | Description |
|----------|-------------|
| **Agents** | |
| `GET /api/agents` | List connected agents |
| `GET /api/agents/{name}/containers` | List containers on a specific agent |
| `GET /api/agents/{name}/scripts` | List scripts on a specific agent |
| `GET /api/agents/{name}/scripts/available` | List available scripts on a specific agent |
| **Containers** | |
| `GET /api/containers` | List registered containers/runtimes |
| `POST /api/containers/register` | Register a new container/runtime |
| `GET /api/containers/registered` | List all registered runtimes |
| `GET /api/containers/registered/{id}` | Get a specific registered runtime |
| `DELETE /api/containers/registered/{id}` | Delete a registered runtime |
| `POST /api/containers/registered/{id}/start` | Start a registered runtime |
| `POST /api/containers/registered/{id}/stop` | Stop a registered runtime |
| `POST /api/containers/registered/{id}/restart` | Restart a registered runtime |
| `POST /api/containers/registered/{id}/rediscover` | Rediscover models from a runtime |
| `PUT /api/containers/registered/{id}/concurrency` | Update co-location settings for a runtime |
| `POST /api/containers/concurrency` | Atomically toggle co-location between two runtimes |
| `POST /api/containers/start` | Start a container |
| `POST /api/containers/{id}/stop` | Stop a container |
| `POST /api/containers/{id}/restart` | Restart a container |
| **Models** | |
| `GET /api/models` | List discovered models |
| `POST /api/models` | Register a model |
| `DELETE /api/models/{id}` | Delete a model |
| **Inference** | |
| `GET /v1/models` | List models (OpenAI-compatible format) |
| `POST /v1/chat/completions` | OpenAI-compatible chat completions proxy |
| `POST /v1/completions` | OpenAI-compatible completions proxy |
| `GET /api/queue/snapshot` | Get inference queue status |
| **Benchmarks & Prompts** | |
| `GET /api/benchmarks` | List benchmark history |
| `POST /api/benchmarks` | Run a benchmark |
| `GET /api/prompts` | List saved prompts |
| `POST /api/prompts` | Create a prompt |
| `GET /api/prompts/{id}` | Get a prompt |
| `PUT /api/prompts/{id}` | Update a prompt (creates new version) |
| `DELETE /api/prompts/{id}` | Delete a prompt |
| `POST /api/prompts/{id}/default` | Set a prompt as the default benchmark prompt |
| `GET /api/prompts/{id}/versions` | List prompt version history |
| `POST /api/prompts/{id}/rollback` | Rollback to a previous prompt version |
| **Auth & Users** | |
| `POST /api/auth/login` | Log in |
| `POST /api/auth/logout` | Log out |
| `GET /api/auth/me` | Get current user |
| `POST /api/auth/change-password` | Change password |
| `GET /api/users` | List users |
| `POST /api/users` | Create a user |
| `DELETE /api/users/{id}` | Delete a user |
| `POST /api/users/{id}/reset-password` | Reset a user's password |
| **API Keys** | |
| `GET /api/api-keys` | List API keys |
| `POST /api/api-keys` | Create an API key |
| `POST /api/api-keys/agent` | Create an agent API key |
| `DELETE /api/api-keys/{id}` | Delete an API key |
| `POST /api/api-keys/{id}/rotate` | Rotate an API key |
| **System** | |
| `GET /api/logs` | Query logs |
| `GET /api/settings` | Get settings |
| `PUT /api/settings` | Update settings |
| `GET /api/stats` | Get fleet stats |

### WebSocket Protocol

Agents connect to `ws://<backend>/ws/agent` using a JSON envelope protocol. See [agent protocol documentation](backend/docs/agent-protocol.md) for message types, command formats, and the connection handshake.

## Configuration

### Backend

Environment variables:

| Variable | Description | Default |
|----------|-------------|---------|
| `UNSWARM_API_KEY` | API key for authentication | (empty — auth disabled) |
| `UNSWARM_ADMIN_PASSWORD` | Bootstrap/reset the admin password on first run (alternative to the `--admin-setup` flag) | (unset) |
| `ASPNETCORE_URLS` | Listening URLs | `http://localhost:5014` |

Settings in `appsettings.json`:

```json
{
  "Auth": {
    "ApiKey": "",
    "ProtectedPaths": ["/api/agents", "/ws/agent"]
  },
  "Cors": {
    "AllowedOrigins": ["http://localhost:3000", "http://localhost:5173"]
  }
}
```

### Agent

See [agent config reference](backend/docs/agent-config.md). Key fields:

| Field | Default | Description |
|-------|---------|-------------|
| `backend_url` | `ws://localhost:5014` | Backend WebSocket URL |
| `agent_name` | `machine-b` | Unique agent identifier |
| `docker_socket` | `unix:///var/run/docker.sock` | Docker socket path |
| `api_key` | `""` | API key (must match backend) |
| `reconnect.initial_backoff_ms` | `1000` | Initial reconnect delay |
| `reconnect.max_backoff_ms` | `30000` | Max reconnect delay |
| `reconnect.max_retries` | `-1` | Max retries (-1 = infinite) |

## Development

### Backend

```bash
cd backend
dotnet build
dotnet test
```

### Frontend

```bash
cd frontend
pnpm dev          # dev server
pnpm build        # production build
pnpm test         # unit tests
pnpm lint         # linting
pnpm e2e          # end-to-end tests
```

### Agent

```bash
cd agent
go build ./cmd/agent
go test ./...
```

## Production Deployment

### Docker Compose Quickstart

```bash
export UNSWARM_API_KEY="$(openssl rand -hex 32)"
docker compose up -d --build
```

The dashboard is served at `http://localhost:8080`, with `/api`, `/v1`, `/ws`,
and `/health` proxied to the backend by nginx. SQLite persists in the
`unswarm-data` named volume.

To also run an agent inside the compose network (local testing only — agents
normally run on remote hosts):

```bash
docker compose --profile agent up -d
```

See the commented `agent` service in [docker-compose.yml](docker-compose.yml)
for the host Docker socket mount and `docker.socket` group setup.

### Reverse Proxy / TLS

Terminate TLS at your reverse proxy (Caddy, Traefik, nginx) in front of the
frontend container and proxy WebSocket upgrades through. HTTPS is required:
the auth cookie is issued with `SecurePolicy.Always`, so logins only work over
HTTPS. Set `Cors__AllowedOrigins` on the backend if you serve the SPA from a
different origin than the API; same-origin via the bundled proxy needs no CORS.

### Backups

All state lives in one SQLite file (`/data/.config/unswarm/unswarm.db` inside
the backend container). To back up safely:

```bash
# Preferred: consistent online backup
docker compose exec backend sqlite3 /data/.config/unswarm/unswarm.db ".backup '/data/.config/unswarm/backup.db'"

# Or stop the backend first for a plain file copy
docker compose stop backend && docker cp unswarm-backend-1:/data/.config/unswarm/unswarm.db ./unswarm.db.bak
```

### Bare-Metal Agents

Agents typically run directly on GPU hosts with access to the local Docker
daemon. See [deploy/README.md](deploy/README.md) for the systemd install
(hardened unit, dedicated user, config at `/etc/unswarm/agent.yaml`).

## Project Structure

```
unswarm/
├── agent/                  # Go agent daemon
│   ├── cmd/agent/          #   Entry point
│   └── internal/           #   Backoff, client, config, docker, dispatch, protocol, runtimegate, scripts, telemetry
├── backend/                # .NET backend
│   ├── src/
│   │   ├── Unswarm.Api/    #   Controllers, middleware, background services
│   │   └── Unswarm.Core/   #   Domain models, services, persistence
│   ├── tests/              #   Unit tests
│   └── docs/               #   Protocol and config documentation
└── frontend/               # React SPA
    └── src/
        ├── components/     #   Layout and UI primitives
        ├── features/       #   Dashboard, models, fleet, benchmarks, queue, logs, settings, auth, api-keys, profile
        ├── lib/            #   API client, auth context, nav items, query config, theme
        └── __tests__/      #   Component tests
```

## License

This project is licensed under the MIT License. See [LICENSE](LICENSE) for details.
