# Agent-Backend WebSocket Protocol

The Unswarm backend exposes a WebSocket endpoint at `/ws/agent` for agent connections.
Agents connect outbound (NAT-friendly) and the backend pushes commands down while the
agent streams telemetry up.

## Connection Flow

1. Agent opens a WebSocket to `ws://<backend>/ws/agent`.
2. The backend may require API-key auth (via `X-Api-Key` header or `Authorization: Bearer <key>`).
3. Agent sends a `hello` message as the **first** message.
4. Backend validates and responds with `hello` ack.
5. Bidirectional messaging begins.

## Message Envelope

Every message (both directions) uses this JSON envelope:

```json
{
  "type": "string",
  "id": "string|null",
  "agent": "string|null",
  "payload": {}
}
```

| Field     | Type     | Description                                          |
|-----------|----------|------------------------------------------------------|
| `type`    | string   | Message type (see below).                            |
| `id`      | string?  | Correlation ID. Echoed in `command_result` responses.|
| `agent`   | string?  | Agent name (set by server or agent as appropriate).  |
| `payload` | object?  | Type-specific data.                                  |

All field names are **camelCase**.

## Message Types

### `hello` (agent → backend)

First message after WebSocket connect. Registers the agent.

```json
{
  "type": "hello",
  "id": null,
  "agent": null,
  "payload": {
    "name": "machine-b",
    "dockerSocket": "unix:///var/run/docker.sock",
    "version": "0.1.0"
  }
}
```

| Payload Field  | Type   | Required | Description                     |
|----------------|--------|----------|---------------------------------|
| `name`         | string | yes      | Unique agent identifier.        |
| `dockerSocket` | string | no       | Docker socket path on agent.    |
| `version`      | string | no       | Agent software version.         |

Backend responds with:

```json
{
  "type": "hello",
  "id": null,
  "agent": null,
  "payload": { "ok": true }
}
```

On error the backend sends `type: "error"` and closes the socket.

### `command` (backend → agent)

Backend dispatches a command to the agent. Each command has a unique `id` that must
be echoed back in the corresponding `command_result`.

#### Command Types

##### `start_container`

Start a container on the agent machine.

> **Note:** The agent does **not** create containers. Containers are
> pre-provisioned on the remote machine, and the `image` field is repurposed
> as the **container name** to start. Image/GPU/memory creation semantics are
> ignored by the agent.

```json
{
  "type": "command",
  "id": "cmd-abc123",
  "payload": {
    "command": "start_container",
    "registeredContainerId": "rc-001",
    "image": "my-ollama",
    "containerPort": 11434,
    "gpuDevices": "0",
    "memoryLimitMb": 8192,
    "extraLabels": { "app": "unswarm" }
  }
}
```

##### `stop_container`

Stop a running container.

```json
{
  "type": "command",
  "id": "cmd-abc124",
  "payload": {
    "command": "stop_container",
    "containerId": "docker-abc123"
  }
}
```

##### `restart_container`

Restart a container.

```json
{
  "type": "command",
  "id": "cmd-abc125",
  "payload": {
    "command": "restart_container",
    "containerId": "docker-abc123"
  }
}
```

##### `inspect_container`

Get container details (status, resource usage, etc.).

```json
{
  "type": "command",
  "id": "cmd-abc126",
  "payload": {
    "command": "inspect_container",
    "containerId": "docker-abc123"
  }
}
```

##### `list_containers`

List all containers known to the agent.

```json
{
  "type": "command",
  "id": "cmd-abc127",
  "payload": {
    "command": "list_containers"
  }
}
```

##### `get_container_logs`

Retrieve container logs.

```json
{
  "type": "command",
  "id": "cmd-abc128",
  "payload": {
    "command": "get_container_logs",
    "containerId": "docker-abc123",
    "tailLines": 100
  }
}
```

##### `remove_container`

Remove a stopped container.

```json
{
  "type": "command",
  "id": "cmd-abc129",
  "payload": {
    "command": "remove_container",
    "containerId": "docker-abc123"
  }
}
```

##### `health_check`

Check if a local port is serving a model.

```json
{
  "type": "command",
  "id": "cmd-abc130",
  "payload": {
    "command": "health_check",
    "port": 11434
  }
}
```

##### `discover_models`

Query a local endpoint for available models.

```json
{
  "type": "command",
  "id": "cmd-abc131",
  "payload": {
    "command": "discover_models",
    "port": 11434
  }
}
```

### `command_result` (agent → backend)

Agent responds to a command. The `id` field must match the command's `id`.

```json
{
  "type": "command_result",
  "id": "cmd-abc123",
  "agent": "machine-b",
  "payload": {
    "ok": true,
    "error": null,
    "data": {}
  }
}
```

| Payload Field | Type     | Description                              |
|---------------|----------|------------------------------------------|
| `ok`          | bool     | `true` if the command succeeded.         |
| `error`       | string?  | Error message if `ok` is `false`.        |
| `data`        | object?  | Command-specific response data.          |

### `telemetry` (agent → backend)

Agent sends status updates, health info, or logs. Free-form payload.

```json
{
  "type": "telemetry",
  "agent": "machine-b",
  "payload": {
    "status": "healthy",
    "containers": 3,
    "uptime": 86400
  }
}
```

### `heartbeat` (bidirectional)

Keep-alive. Either side may send. Server acks heartbeats.

```json
{
  "type": "heartbeat",
  "id": "hbeat-001",
  "agent": "machine-b",
  "payload": {}
}
```

### `error` (backend → agent)

Sent when the backend rejects a message or encounters an error.

```json
{
  "type": "error",
  "id": "cmd-abc123",
  "payload": {
    "error": "Unknown message type: foo"
  }
}
```

## Notes

- All timestamps in UTC (`DateTimeOffset` serialized as ISO 8601).
- The backend uses `CamelCase` JSON serialization (`PropertyNamingPolicy = JsonNamingPolicy.CamelCase`).
- The WebSocket connection stays open; the agent should reconnect on disconnect with exponential backoff.
- Command dispatch (Phase 4) will route `command` messages to the appropriate agent based on container registration.
