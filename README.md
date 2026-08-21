# Unswarm

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

```
┌──────────────┐       WebSocket        ┌──────────────┐       Docker SDK       ┌──────────────────┐
│              │ ◄──────────────────────►│              │ ◄─────────────────────►│  Docker Engine   │
│   Frontend   │        REST API        │   Backend    │                        │  (agent host)    │
│  (React/TS)  │ ◄────────────────────► │  (.NET 10)   │                        └──────────────────┘
│              │                        │              │       WebSocket        ┌──────────────────┐
└──────────────┘                        │  SQLite DB   │ ◄────────────────────►│   Agent (Go)     │
                                        │              │                        │  (remote host)   │
                                        └──────────────┘                        └──────────────────┘
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
- **Telemetry** — Agents stream host info (CPU, memory, GPU), container statuses, and script process info to the dashboard in real time.
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
| `GET /api/agents` | List connected agents |
| `GET /api/containers` | List registered containers/runtimes |
| `POST /api/containers/registered/{id}/start` | Start a registered runtime |
| `POST /api/containers/registered/{id}/stop` | Stop a registered runtime |
| `GET /api/models` | List discovered models |
| `POST /api/models` | Register a model |
| `POST /v1/chat/completions` | OpenAI-compatible inference proxy |
| `GET /api/queue` | Get inference queue status |
| `GET /api/benchmarks` | List benchmark history |
| `POST /api/benchmarks/run` | Run a benchmark |
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
| `ASPNETCORE_URLS` | Listening URLs | `http://localhost:5014` |

Settings in `appsettings.json`:

```json
{
  "Auth": {
    "ApiKey": "",
    "ProtectedPaths": ["/api/agents", "/ws/agent"]
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

## Project Structure

```
unswarm/
├── agent/                  # Go agent daemon
│   ├── cmd/agent/          #   Entry point
│   └── internal/           #   Client, docker, dispatch, protocol, scripts, telemetry
├── backend/                # .NET backend
│   ├── src/
│   │   ├── Unswarm.Api/    #   Controllers, middleware, background services
│   │   └── Unswarm.Core/   #   Domain models, services, persistence
│   ├── tests/              #   Unit tests
│   └── docs/               #   Protocol and config documentation
└── frontend/               # React SPA
    └── src/
        ├── components/     #   Layout and UI primitives
        ├── features/       #   Dashboard, models, fleet, benchmarks, queue, logs, settings
        ├── lib/            #   API client, query config, theme
        └── __tests__/      #   Component tests
```

## License

This project is licensed under the MIT License. See [LICENSE](LICENSE) for details.
