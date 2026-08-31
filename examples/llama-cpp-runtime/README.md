# llama.cpp Script Runtime for Unswarm

This example shows how to run [llama.cpp](https://github.com/ggerganov/llama.cpp) as a **Script runtime** in Unswarm. The agent manages the process lifecycle (start/stop via PID control), and the backend discovers models by hitting the OpenAI-compatible `/v1/models` endpoint.

## How It Works

Unswarm's Script runtime system lets you register any bash script as a managed runtime. Unlike Docker containers, script runtimes are plain processes on the host (or agent machine). The agent:

1. Spawns the script via `bash <path>` in a new process group
2. Tracks the PID and monitors health via the port
3. Sends `SIGTERM` to the process group on stop, with a 5-second grace period before `SIGKILL`

The launcher script (`run_llama.sh`) is responsible for:
- Starting `llama-server` on a self-contained PORT (must match the `containerPort` used at registration)
- Staying alive until Unswarm kills it
- Cleaning up child processes on shutdown

## Prerequisites

- **llama.cpp** built with `llama-server` support ([build instructions](https://github.com/ggerganov/llama.cpp#build))
- **Unswarm** backend + agent running
- A **GGUF model file** (e.g., `llama-3.1-70b-Q4_K_M.gguf`)

## Quick Start

### 1. Install the script

Copy `run_llama.sh` to your agent's configured `scripts_dir`:

```bash
# Check your agent config for the scripts_dir path
cp run_llama.sh ~/.config/unswarm/scripts/
```

### 2. Configure the script

Edit the configuration block at the top of `run_llama.sh`:

```bash
# Model path
MODEL="/data/models/llama-3.1-70b-Q4_K_M.gguf"

# Port — must match the containerPort used at registration
PORT=8080

# GPU offload (99 = all layers, 0 = CPU only)
NGL=99

# Context size (scales KV linearly — keep small on VRAM-constrained setups)
CTX=4096
```

### 3. Register via API

```bash
curl -X POST http://localhost:5000/api/containers/register \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer <your-api-key>" \
  -d '{
    "displayName": "llama-3.1-70b",
    "image": "local/llama-3.1-70b",
    "containerPort": 8080,
    "agent": "host",
    "runtimeKind": "script",
    "launcherPath": "/home/user/.config/unswarm/scripts/run_llama.sh"
  }'
```

Or register via the **Fleet UI**:
1. Open Fleet → Manage Runtimes
2. Select the **Scripts** tab
3. Pick `run_llama.sh` from the agent's script list
4. Set a display name and port
5. Click Register

### 4. Start the runtime

From the Fleet UI, click **Start** on the script runtime card. Or via API:

```bash
curl -X POST http://localhost:5000/api/containers/registered/<runtime-id>/start \
  -H "Authorization: Bearer <your-api-key>"
```

### 5. Discover models

Once the script is running and healthy, Unswarm automatically discovers models via `/v1/models`. You can also trigger manual rediscovery:

```bash
curl -X GET http://localhost:5000/api/containers/registered/<runtime-id>/rediscover \
  -H "Authorization: Bearer <your-api-key>"
```

## Environment Variables

All configuration is self-contained in the script. Edit the values directly — Unswarm does not inject environment variables.

### Configuration (edit in script)

| Variable | Default | Description |
|----------|---------|-------------|
| `PORT` | `8080` | Port to listen on (must match registration's `containerPort`) |
| `MODEL` | `/models/llama-3.1-70b-Q4_K_M.gguf` | Path to GGUF model file |
| `LLAMA_SERVER` | auto-detect | Path to llama-server binary |
| `CTX` | `4096` | Context size (scales KV linearly) |
| `NGL` | `99` | GPU offload layers (99=all, 0=CPU) |
| `BATCH` | `512` | Batch size for prompt eval |
| `THREADS` | `$(nproc)` | CPU threads |
| `HOST` | `0.0.0.0` | Bind address |
| `EXTRA_FLAGS` | `--jinja --fit off` | Additional llama-server flags |

## Script Requirements

A valid Unswarm launcher script must:

1. **Set PORT** — configure the port (must match registration's `containerPort`)
2. **Stay alive** — block until the process exits or a signal is received
3. **Handle SIGTERM** — shut down child processes gracefully
4. **Exit cleanly** — return a non-zero exit code on failure

## Customization

### GPU layers

Edit `NGL` in the script:
```bash
NGL=99     # All layers to GPU (default)
NGL=0      # CPU only (slow, no VRAM pressure)
NGL=35     # Partial offload (e.g., MoE hybrid)
```

### Context size

Edit `CTX` in the script:
```bash
CTX=4096   # Small — safe for 16GB VRAM
CTX=8192   # Medium — needs ~1GB KV at f16
CTX=32768  # Large — needs quantized KV (-ctk q8_0 -ctv q8_0)
```

### Quantized KV cache

Edit `EXTRA_FLAGS` in the script:
```bash
EXTRA_FLAGS="--jinja --fit off -ctk q8_0 -ctv q8_0 -fa on"
```

### Custom llama-server path

Edit `LLAMA_SERVER` in the script, or set the `BIN_VULKAN` / `BIN_FALLBACK` paths:
```bash
LLAMA_SERVER="/usr/local/bin/llama-server"
```

### Multiple models on one host

Register multiple script runtimes with different `MODEL` values and distinct ports. Use the `canRunAlongWith` field to control which runtimes can run concurrently:

```json
{
  "displayName": "llama-3.1-70b",
  "runtimeKind": "script",
  "launcherPath": "/path/to/run_llama.sh",
  "containerPort": 8080,
  "canRunAlongWith": ["llama-3.1-8b"]
}
```

## Troubleshooting

### Script doesn't start

- Check the agent's `scripts_dir` config — the script must be inside it
- Verify the script is readable: `bash -n run_llama.sh` (syntax check)
- Check agent logs for whitelist errors

### Health check fails

- Ensure the model file exists and is readable
- Check that the port isn't already in use: `lsof -i :8080`
- Look at the script's log output in Fleet UI (click the runtime card → Logs)

### Process not stopping

The agent sends `SIGTERM` to the process group. If your script spawns children that ignore SIGTERM, they'll be force-killed after 5 seconds. Make sure child processes forward signals.

### Binary not found

The script checks these locations in order:
1. `$LLAMA_SERVER` (explicit override)
2. `/home/user/llama.cpp-vulkan/build/bin/llama-server`
3. `/home/user/llama.cpp/build/bin/llama-server`
4. `llama-server` in PATH

Set `LLAMA_SERVER` explicitly if your binary is elsewhere.

## Architecture

```
┌─────────────────────────────────────────────┐
│  Unswarm Backend                            │
│  ┌─────────────────────────────────────┐    │
│  │  RegisteredRuntime (Script kind)    │    │
│  │  - launcherPath: /path/to/run.sh   │    │
│  │  - containerPort: 8080              │    │
│  │  - runtimeProcessId: <tracked PID>  │    │
│  └──────────────┬──────────────────────┘    │
│                 │  WebSocket commands        │
├─────────────────┼───────────────────────────┤
│  Unswarm Agent  │                           │
│  ┌──────────────▼──────────────────────┐    │
│  │  scripts.Manager                    │    │
│  │  - StartScript(path, port)          │    │
│  │  - StopScript(pid)                  │    │
│  │  - PID-reuse guard                  │    │
│  │  - Process group kill               │    │
│  └──────────────┬──────────────────────┘    │
│                 │  spawn bash                │
│  ┌──────────────▼──────────────────────┐    │
│  │  run_llama.sh                       │    │
│  │  ┌──────────────────────────┐       │    │
│  │  │ llama-server             │       │    │
│  │  │ --host 0.0.0.0           │       │    │
│  │  │ --port $PORT              │       │    │
│  │  │ --model $MODEL           │       │    │
│  │  └──────────────────────────┘       │    │
│  └─────────────────────────────────────┘    │
│                 │  HTTP                     │
│  ┌──────────────▼──────────────────────┐    │
│  │  OpenAI-compatible API              │    │
│  │  /v1/models   (discovery)           │    │
│  │  /v1/chat/completions (inference)   │    │
│  └─────────────────────────────────────┘    │
└─────────────────────────────────────────────┘
```
