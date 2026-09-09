# Bare-Metal Agent Install

The Unswarm agent runs on each machine that hosts model containers. It connects
**outbound** to the backend over WebSocket — no inbound ports required.

## Install

```bash
# 1. Create a dedicated system user
sudo useradd --system --home /var/lib/unswarm --create-home --shell /usr/sbin/nologin unswarm

# 2. Build and install the binary (on a build box, then copy, or build in place)
cd agent
go build -o unswarm ./cmd/agent
sudo install -m 0755 unswarm /usr/local/bin/unswarm

# 3. Install config
sudo mkdir -p /etc/unswarm
sudo cp agent.yaml /etc/unswarm/agent.yaml
sudo chmod 600 /etc/unswarm/agent.yaml && sudo chown unswarm:unswarm /etc/unswarm/agent.yaml

# 4. Grant Docker access
sudo usermod -aG docker unswarm

# 5. Install the systemd unit
sudo cp deploy/unswarm-agent.service /etc/systemd/system/
sudo systemctl daemon-reload
sudo systemctl enable --now unswarm-agent
```

## Configuration

Edit `/etc/unswarm/agent.yaml`:

```yaml
backend_url: "wss://unswarm.example.com"
api_key: "<key created via dashboard or POST /api/api-keys/agent>"
agent_name: "machine-b"
docker_socket: "unix:///var/run/docker.sock"
```

Check status with `journalctl -u unswarm-agent -f`.

## Systemd Service Hardening

The included `unswarm-agent.service` applies several security hardening options:

```ini
[Unit]
Description=Unswarm Agent
After=network-online.target docker.service
Wants=network-online.target

[Service]
Type=simple
User=unswarm
Group=unswarm
ExecStart=/usr/local/bin/unswarm -config /etc/unswarm/agent.yaml
Restart=always
RestartSec=5

# ── Hardening ─────────────────────────────────────────────────────────────
NoNewPrivileges=true
ProtectSystem=strict
PrivateTmp=true
PrivateDevices=true
ProtectKernelTunables=true
ProtectKernelModules=true
ProtectControlGroups=true
RestrictSUIDSGID=true
LockPersonality=true
MemoryDenyWriteExecute=true
ReadWritePaths=/var/lib/unswarm
# ReadOnlyPaths=/path/to/models  (see below)

[Install]
WantedBy=multi-user.target
```

### Hardening options explained

| Option | Effect |
|--------|--------|
| `NoNewPrivileges=true` | Prevents the agent from gaining new privileges (setuid, capabilities). Required because the agent runs scripts. |
| `ProtectSystem=strict` | Mounts `/` as read-only. Only paths listed in `ReadWritePaths` are writable. |
| `PrivateTmp=true` | Gives the agent an isolated `/tmp` (avoids temp-file races with other services). |
| `PrivateDevices=true` | Restricts access to pseudo-devices only. |
| `ProtectKernelTunables=true` | Prevents writing to `/proc`, `/sys`. |
| `ProtectKernelModules=true` | Prevents loading/unloading kernel modules. |
| `ProtectControlGroups=true` | Prevents writing to cgroup filesystem. |
| `RestrictSUIDSGID=true` | Prevents creating setuid/setgid files. |
| `LockPersonality=true` | Locks execution domain. |
| `MemoryDenyWriteExecute=true` | Prevents W+X memory mappings (may break some JIT runtimes). |

### `ProtectHome` — do NOT enable

`ProtectHome=true` mounts an empty `tmpfs` over `/home`, hiding all home directories.
This breaks scripts that reference files in `/home` (e.g., model binaries or llama.cpp builds).

**Do not add `ProtectHome=true`** to the service file. If your agent scripts access
files under `/home`, this setting will cause "not found" errors even with `ReadOnlyPaths`.

### `ReadWritePaths` vs `ReadOnlyPaths`

| Directive | Purpose | Example |
|-----------|---------|---------|
| `ReadWritePaths` | Paths the agent can write to (logs, state, scripts). | `/var/lib/unswarm` |
| `ReadOnlyPaths` | Paths the agent can read but not write (models, binaries). | `/opt/models`, `/home/user/llm` |

If your model files or llama.cpp binaries live outside `/var/lib/unswarm`, add them
as `ReadOnlyPaths`. For example:

```ini
ReadWritePaths=/var/lib/unswarm
ReadOnlyPaths=/home/user/models /opt/llama.cpp
```

Without `ReadOnlyPaths` for model/binary directories, the agent can still reach them
(the default is to not restrict paths beyond `ProtectSystem=strict`), but adding them
explicitly documents the expected access and prevents future breakage if defaults tighten.

### Customizing for your setup

1. If you store models in `/opt/models`, add `ReadOnlyPaths=/opt/models`.
2. If you use Docker containers exclusively (no script runtimes), you can omit
   `ReadOnlyPaths` entirely — the agent only needs Docker socket access.
3. If your scripts need to write temporary files (e.g., logs), ensure the log
   directory is under `ReadWritePaths` or under the scripts directory itself.

## TLS note

`wss://` is **required** for any non-loopback backend — the agent refuses plain
`ws://` to remote hosts unless `allow_insecure_ws: true` is set in the config
(only do this on an isolated network you trust). Terminate TLS at your reverse
proxy in front of the backend and point `backend_url` at `wss://...`.

The same reverse proxy should also terminate TLS for browser/dashboard traffic:
proxy all traffic (`/`, `/api`, `/v1`, `/ws`, `/health`) to the backend over
`https://`. The backend enables HSTS outside development, so once
a client has seen an HTTPS response it will refuse plain HTTP — put the TLS
layer in place before exposing the stack beyond loopback. Cookies are issued
with `Secure` policy regardless, so dashboard sign-in only works over HTTPS.
