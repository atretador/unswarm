# Agent Configuration

The Go agent reads a YAML configuration file at startup. This document describes each field and its defaults.

Environment overrides win over the YAML file (H5): `UNSWARM_AGENT_BACKEND_URL` and `UNSWARM_AGENT_API_KEY` take precedence, and a conflict logs a warning naming the field and sources (never the value). `applyEnvOverrides` never fails the agent on a conflict.

## Fields

### `backend_url`
- **Type:** `string`
- **Default:** `"ws://localhost:5014"`
- **Description:** Base URL of the Unswarm backend. Used for both WebSocket connections and REST API calls. Supports `http://`, `https://`, `ws://`, and `wss://` schemes. Plaintext `ws://`/`http://` is permitted to any host; the agent logs a prominent warning when a plaintext connection targets a non-loopback host, because the API key would travel unencrypted. Prefer `wss://` behind a TLS-terminating reverse proxy.

### `api_key`
- **Type:** `string`
- **Default:** `""` (empty — auth disabled)
- **Description:** API key used to authenticate with the backend. The agent sends this key in the `X-Api-Key` header (or `Authorization: Bearer <key>`). Create this key in the backend dashboard under **API Keys → Create Agent Key**, or via the `POST /api/api-keys/agent` endpoint.

  This is the **remote-agent channel** key: it authenticates to `/api/agents` and `/ws/agent`, the surface that talks to the Go agent. It is distinct from **inference keys** — those authenticate to the OpenAI-compatible proxy (`/v1`) and are created through the *API Keys* page in the web dashboard. The key is stored in the backend's managed key store with agent scope.

### `agent_name`
- **Type:** `string`
- **Default:** `"machine-b"`
- **Description:** Unique name identifying this agent. Used by the backend to distinguish agents and track which models are managed by which agent. Must be unique across all agents connecting to the same backend.

### `docker_socket`
- **Type:** `string`
- **Default:** `"unix:///var/run/docker.sock"`
- **Description:** Path to the local Docker socket. Used by the agent to manage containers on its host machine.

### `expected_server_fingerprint`
- **Type:** `string`
- **Default:** `""` (disabled)
- **Description:** Optional SHA-256 hex fingerprint of the backend's TLS certificate. When set and `backend_url` uses `wss://`, the agent verifies the peer certificate during the TLS handshake — before any API key material is sent — and refuses to connect on mismatch. Parsing is case/space-insensitive; colons are accepted.

### `allow_unrestricted_loopback`
- **Type:** `bool`
- **Default:** `true`
- **Description:** Only consulted when `allowed_loopback_ports` is empty. When the list is empty and this is `true`, the agent may dial any `127.0.0.1` port for `health_check` / `discover_models` / `chat_completion` (legacy default); when the list is empty and this is `false`, startup fails so the choice must be explicit. A non-empty `allowed_loopback_ports` always scopes the agent regardless of this flag. Set the list (and optionally `false`) to limit a compromised backend's SSRF pivot into local services.

### `allowed_loopback_ports`
- **Type:** `[]int`
- **Default:** `[]` (empty means any loopback port while `allow_unrestricted_loopback: true`; a non-empty list always scopes the agent)
- **Description:** The exact local ports the agent may dial. Port ranges are not supported yet, and auto-assigned container ports (`hostPort: 0`) cannot be predicted — scoping requires explicit host ports on the backend.

### `telemetry_interval_ms`
- **Type:** `int`
- **Default:** `30000`
- **Description:** How often telemetry (host + per-container status, including Docker inspect/stats calls) is collected and sent, in milliseconds. Values below `5000` are rejected so a typo cannot turn telemetry into a hot loop against the Docker daemon.

### `enforce_registered_runtime`
- **Type:** `bool`
- **Default:** `true`
- **Description:** Gates container lifecycle commands against the registered runtime set synced from the backend. When `true`, unregistered targets are rejected without touching Docker, and `list_containers` is filtered to registered containers. Set to `false` to restore legacy behavior (act on any container on the host).

### `scripts_dir`
- **Type:** `string`
- **Default:** `""` (script support disabled)
- **Description:** Directory containing `.sh` launcher scripts the backend may start. **Security:** `scripts_dir` should be owned by root and NOT writable by the agent user. Anything placed there is executed as bash by the agent.

### `script_log_dir`
- **Type:** `string`
- **Default:** `""` (derives `<parent of scripts_dir>/script-logs`)
- **Description:** Where script log and PID files are written. It MUST live under a path listed in the systemd unit's `ReadWritePaths`, or the agent refuses to start with an error naming the path and the fix (`script_log_dir: /var/lib/unswarm/script-logs` or add the path to `ReadWritePaths`).

### `allow_script_start`
- **Type:** `bool`
- **Default:** `true`
- **Description:** Enables `start_script` for pre-provisioned launcher scripts. `stop_script` is **never** gated, so a running script can always be stopped even after this is turned off.

### `allow_script_upload`
- **Type:** `bool`
- **Default:** `false`
- **Description:** Enables `upload_script`, `update_script`, `delete_script`, and reading a script's content (`get_script_content`). Only enable when `scripts_dir` is root-owned and read-only to the agent. Written scripts are mode `0700`.

### `allow_container_creation`
- **Type:** `bool`
- **Default:** `false`
- **Description:** Enables the `create_container` command. When `false` the command is rejected before any Docker API call. When `true`, the payload is additionally validated against the `container_creation` policy below.

### `container_creation`
Policy applied to `create_container` payloads. Empty allowlists mean **deny-all**.

#### `container_creation.allowed_images`
- **Type:** `[]string`
- **Default:** `[]` (deny all)
- **Description:** Image glob patterns; at least one must match. `*` matches any characters including `/` (e.g. `ghcr.io/ggml-org/llama.cpp:*`).

#### `container_creation.allowed_host_path_prefixes`
- **Type:** `[]string`
- **Default:** `[]` (deny all host mounts)
- **Description:** Host paths that may be bind-mounted, matched on a path-component boundary. Hard-denied regardless: `/`, `/etc`, `/proc`, `/sys`, `/dev`, `/root`, `/boot`, and any `docker.sock`/`containerd.sock`. Symlinks are resolved before the prefix test so a link cannot escape the allowlist.

#### `container_creation.allow_host_network`
- **Type:** `bool`
- **Default:** `false`
- **Description:** Permits `networkMode: host` and `networkMode: container:*`.

#### `container_creation.allow_ipc_host`
- **Type:** `bool`
- **Default:** `false`
- **Description:** Permits `ipcMode: host` and `ipcMode: container:*`.

#### `container_creation.allowed_device_prefixes`
- **Type:** `[]string`
- **Default:** `["/dev/kfd", "/dev/dri/", "/dev/nvidia"]`
- **Description:** Device paths that may be passed through (plain prefix match, so `/dev/nvidia` covers `/dev/nvidia0`). Empty = deny all devices.

#### `container_creation.bind_address`
- **Type:** `string`
- **Default:** `"127.0.0.1"`
- **Description:** Host IP mapped container ports bind to. Empty defaults to `127.0.0.1` — never `0.0.0.0`.

### `reconnect`
Connection retry settings for reconnecting to the backend after a disconnect.

#### `reconnect.initial_backoff_ms`
- **Type:** `int`
- **Default:** `1000`
- **Description:** Initial delay in milliseconds before the first reconnect attempt after a disconnection.

#### `reconnect.max_backoff_ms`
- **Type:** `int`
- **Default:** `30000`
- **Description:** Maximum delay in milliseconds between reconnect attempts. The backoff increases exponentially up to this cap.

#### `reconnect.max_retries`
- **Type:** `int`
- **Default:** `-1`
- **Description:** Maximum number of reconnect attempts. `-1` means infinite retries (keep trying forever).

### Removed keys

#### `allow_insecure_ws`
Removed as a control (H6). Plaintext `ws://` is allowed to any host with a warning for non-loopback targets. The key is still accepted as a deprecated no-op for one release so existing files load.

## Example

```yaml
backend_url: ""
api_key: ""                       # prefer UNSWARM_AGENT_API_KEY
agent_name: "machine-b"
docker_socket: "unix:///var/run/docker.sock"

allow_unrestricted_loopback: true # set false + allowed_loopback_ports to scope
telemetry_interval_ms: 30000
enforce_registered_runtime: true

# scripts_dir: "/opt/unswarm/scripts"   # root-owned, agent-read-only
allow_script_start: true
allow_script_upload: false

allow_container_creation: false
# container_creation:
#   allowed_images: ["ghcr.io/ggml-org/llama.cpp:*"]
#   allowed_host_path_prefixes: ["/srv/models"]
#   allow_host_network: false
#   allow_ipc_host: false
#   allowed_device_prefixes: ["/dev/kfd", "/dev/dri/", "/dev/nvidia"]
#   bind_address: "127.0.0.1"

reconnect:
  initial_backoff_ms: 1000
  max_backoff_ms: 30000
  max_retries: -1
```
