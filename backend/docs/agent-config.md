# Agent Configuration

The Go agent reads a YAML configuration file at startup. This document describes each field and its defaults.

## Fields

### `backend_url`
- **Type:** `string`
- **Default:** `"ws://localhost:5014"`
- **Description:** Base URL of the Unswarm backend. Used for both WebSocket connections and REST API calls. Supports `http://`, `https://`, `ws://`, and `wss://` schemes.

### `api_key`
- **Type:** `string`
- **Default:** `""` (empty — auth disabled)
- **Description:** API key used to authenticate with the backend. The agent sends this key in the `X-Api-Key` header (or `Authorization: Bearer <key>`). Must match the backend's `Auth.ApiKey` setting (or the `UNSWARM_API_KEY` environment variable).

  This is the **remote-agent channel** key: it authenticates to `/api/agents` and `/ws/agent`, the surface that talks to the Go agent. It is distinct from **inference keys** — those authenticate to the OpenAI-compatible proxy (`/v1`) and are created through the *API Keys* page in the web dashboard. The single configured key above is provisioned into the backend's managed key store (with agent scope) at startup, so it is managed like every other key.

### `agent_name`
- **Type:** `string`
- **Default:** `"machine-b"`
- **Description:** Unique name identifying this agent. Used by the backend to distinguish agents and track which models are managed by which agent. Must be unique across all agents connecting to the same backend.

### `docker_socket`
- **Type:** `string`
- **Default:** `"unix:///var/run/docker.sock"`
- **Description:** Path to the local Docker socket. Used by the agent to manage containers on its host machine.

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

## Example

```yaml
backend_url: "ws://localhost:5014"
api_key: ""
agent_name: "machine-b"
docker_socket: "unix:///var/run/docker.sock"
reconnect:
  initial_backoff_ms: 1000
  max_backoff_ms: 30000
  max_retries: -1
```
