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
api_key: "<UNSWARM_API_KEY from the backend>"
agent_name: "machine-b"
docker_socket: "unix:///var/run/docker.sock"
```

Check status with `journalctl -u unswarm-agent -f`.

## TLS note

`wss://` is **required** for any non-loopback backend — the agent refuses plain
`ws://` to remote hosts unless `allow_insecure_ws: true` is set in the config
(only do this on an isolated network you trust). Terminate TLS at your reverse
proxy in front of the backend and point `backend_url` at `wss://...`.
