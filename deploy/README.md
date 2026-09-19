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

Environment overrides win over the file: `UNSWARM_AGENT_BACKEND_URL` and
`UNSWARM_AGENT_API_KEY` take precedence. A conflict logs a warning naming the
field (never the value) and the agent keeps running.

Check status with `journalctl -u unswarm-agent -f`.

## Launcher scripts

Script support lets the backend start pre-provisioned `.sh` launcher scripts on
the host. Anything in `scripts_dir` is executed as bash, so treat that directory
as a trusted, root-owned location:

```bash
sudo mkdir -p /opt/unswarm/scripts
sudo chown root:root /opt/unswarm/scripts
sudo chmod 0755 /opt/unswarm/scripts
```

- `scripts_dir` should be **root-owned and not writable by the `unswarm`
  user**. A writable `scripts_dir` is remote code execution by design.
- `allow_script_start` defaults to `true`; `stop_script` is never gated.
- `allow_script_upload` defaults to `false` — the backend cannot write
  executables to the host. Enable it only if you accept that risk and have made
  `scripts_dir` agent-writable.
- `script_log_dir` MUST live under `/var/lib/unswarm` (the unit's only
  `ReadWritePaths` entry) or the agent refuses to start with the exact fix in
  the error.

> **Migration note:** the default layout (`scripts_dir: /opt/unswarm/scripts`,
> no `script_log_dir`) derives `/opt/unswarm/script-logs`, which is outside
> `ReadWritePaths=/var/lib/unswarm` — on upgrade the agent will refuse to
> start. Set `script_log_dir: /var/lib/unswarm/script-logs`, or add your
> existing log directory to `ReadWritePaths`.

## Container creation

`create_container` is disabled by default (`allow_container_creation: false`).
When enabled, the payload is validated against `container_creation`:

```yaml
allow_container_creation: true
container_creation:
  allowed_images: ["ghcr.io/ggml-org/llama.cpp:*"]
  allowed_host_path_prefixes: ["/srv/models"]
  allow_host_network: false
  allow_ipc_host: false
  allowed_device_prefixes: ["/dev/kfd", "/dev/dri/", "/dev/nvidia"]
  bind_address: "127.0.0.1"
```

Empty allowlists deny everything. Hard-denied bind sources: `/`, `/etc`,
`/proc`, `/sys`, `/dev`, `/root`, `/boot`, and any `docker.sock` /
`containerd.sock`. Mapped ports bind to `127.0.0.1` by default.

## Systemd Service Hardening

The included `unswarm-agent.service` applies several security hardening
options:

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
# Allow NVIDIA telemetry (`nvidia-smi`) despite PrivateDevices=true.
DeviceAllow=/dev/nvidiactl rw
DeviceAllow=/dev/nvidia* rw
ProtectKernelTunables=true
ProtectKernelModules=true
ProtectKernelLogs=true
ProtectControlGroups=true
ProtectClock=true
ProtectHostname=true
ProtectProc=invisible
RestrictSUIDSGID=true
RestrictNamespaces=true
RestrictRealtime=true
LockPersonality=true
MemoryDenyWriteExecute=true
SystemCallArchitectures=native
CapabilityBoundingSet=
AmbientCapabilities=
RestrictAddressFamilies=AF_UNIX AF_INET AF_INET6
RemoveIPC=true
KeyringMode=private
UMask=0077
# script_log_dir MUST live under a path listed here.
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
| `PrivateDevices=true` | Restricts access to pseudo-devices only. `DeviceAllow` re-admits `/dev/nvidia*` for GPU telemetry. |
| `ProtectKernelTunables=true` | Prevents writing to `/proc`, `/sys`. |
| `ProtectKernelModules=true` | Prevents loading/unloading kernel modules. |
| `ProtectKernelLogs=true` | Prevents access to the kernel log buffer. |
| `ProtectControlGroups=true` | Prevents writing to cgroup filesystem. |
| `ProtectClock=true` | Prevents changing the system clock. |
| `ProtectHostname=true` | Prevents changing the hostname. |
| `ProtectProc=invisible` | Hides other users' processes while keeping `/proc/meminfo` and `/proc/stat` readable (do NOT use `ProcSubset=pid` — it would break host CPU/RAM metrics). |
| `RestrictSUIDSGID=true` | Prevents creating setuid/setgid files. |
| `RestrictNamespaces=true` | Prevents creating new namespaces. |
| `RestrictRealtime=true` | Prevents realtime scheduling. |
| `LockPersonality=true` | Locks execution domain. |
| `MemoryDenyWriteExecute=true` | Prevents W+X memory mappings (may break some JIT runtimes). |
| `SystemCallArchitectures=native` | Restricts to native system call ABI. |
| `CapabilityBoundingSet=` / `AmbientCapabilities=` | Drops all capabilities; the agent does not need any. |
| `RestrictAddressFamilies=AF_UNIX AF_INET AF_INET6` | Restricts socket families. Note this also constrains child launcher scripts; add `AF_NETLINK` if a script needs it. |
| `RemoveIPC=true` | Removes IPC objects owned by the service on stop. |
| `KeyringMode=private` | Private kernel keyring. |
| `UMask=0077` | Files created by the agent are owner-only by default. |
| `DeviceAllow=/dev/nvidia* rw` | Re-admits NVIDIA devices hidden by `PrivateDevices=true`. |

`SystemCallFilter=@system-service` is intentionally **not** set yet (deferred
until smoke-tested).

### `ProtectHome` — do NOT enable

The shipped unit does **not** set `ProtectHome`. `ProtectHome=true` mounts an
empty `tmpfs` over `/home`, hiding all home directories, which breaks scripts
that reference files in `/home` (e.g., model binaries or llama.cpp builds).

**Do not add `ProtectHome=true`** to the service file.

### NVIDIA telemetry and `PrivateDevices`

`PrivateDevices=true` hides `/dev/nvidia*` from the service, which would break
`nvidia-smi` GPU telemetry. The unit re-admits the NVIDIA device nodes with the
two `DeviceAllow=` lines above. On an NVIDIA host without those lines you would
instead need `PrivateDevices=false`.

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
3. If you change `script_log_dir`, it MUST be under `ReadWritePaths` or the
   agent will refuse to start.

## TLS note

Plaintext `ws://`/`http://` is permitted to any host, but the agent logs a
prominent warning for non-loopback plaintext because the API key would travel
unencrypted. **Prefer `wss://`**: terminate TLS at your reverse proxy in front
of the backend and point `backend_url` at `wss://...`. The former
`allow_insecure_ws` key has been removed; optimize for encryption rather than
accepting plaintext.

The same reverse proxy should also terminate TLS for browser/dashboard traffic:
proxy all traffic (`/`, `/api`, `/v1`, `/ws`, `/health`) to the backend over
`https://`. The backend enables HSTS outside development, so once
a client has seen an HTTPS response it will refuse plain HTTP — put the TLS
layer in place before exposing the stack beyond loopback. Cookies are issued
with `Secure` policy regardless, so dashboard sign-in only works over HTTPS.
