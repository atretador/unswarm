// Package docker provides container management operations via the Docker SDK.
//
// The agent does NOT create containers: containers are pre-provisioned on the
// remote machine. All operations are performed on an existing container NAME.
package docker

import (
	"bytes"
	"context"
	"encoding/json"
	"fmt"
	"io"
	"strings"
	"time"

	"github.com/docker/docker/api/types"
	"github.com/docker/docker/api/types/container"
	"github.com/docker/docker/api/types/filters"
	"github.com/docker/docker/client"
	"github.com/docker/docker/errdefs"
	"github.com/docker/docker/pkg/stdcopy"
	"github.com/docker/go-connections/nat"

	"unswarm/agent/internal/protocol"
)

// unswarmLabel is the label used to mark containers managed by Unswarm.
const unswarmLabel = "app=unswarm"

// Handler manages Docker container operations on pre-provisioned containers.
type Handler struct {
	client *client.Client
	socket string
}

// New creates a Docker Handler connected to the given socket.
// If socket is empty, the Docker client's default (DOCKER_HOST env) is used.
func New(socket string) (*Handler, error) {
	opts := []client.Opt{client.WithAPIVersionNegotiation()}
	if socket != "" {
		opts = append(opts, client.WithHost(socket))
	}
	c, err := client.NewClientWithOpts(opts...)
	if err != nil {
		return nil, fmt.Errorf("create docker client: %w", err)
	}
	return &Handler{client: c, socket: socket}, nil
}

// Socket returns the docker socket this handler is attached to.
func (h *Handler) Socket() string { return h.socket }

// StartContainer starts a pre-provisioned container by name.
func (h *Handler) StartContainer(ctx context.Context, name string) protocol.CommandResultPayload {
	if err := h.client.ContainerStart(ctx, name, container.StartOptions{}); err != nil {
		return containerErrorResult("start", name, err)
	}
	return okResult(map[string]string{"status": "started", "name": name})
}

// StopContainer stops a running container by name.
func (h *Handler) StopContainer(ctx context.Context, name string) protocol.CommandResultPayload {
	timeout := 10 // seconds
	if err := h.client.ContainerStop(ctx, name, container.StopOptions{Timeout: &timeout}); err != nil {
		return containerErrorResult("stop", name, err)
	}
	return okResult(map[string]string{"status": "stopped", "name": name})
}

// RestartContainer restarts a container by name.
func (h *Handler) RestartContainer(ctx context.Context, name string) protocol.CommandResultPayload {
	timeout := 10 // seconds
	if err := h.client.ContainerRestart(ctx, name, container.StopOptions{Timeout: &timeout}); err != nil {
		return containerErrorResult("restart", name, err)
	}
	return okResult(map[string]string{"status": "restarted", "name": name})
}

// RemoveContainer removes a container by name.
func (h *Handler) RemoveContainer(ctx context.Context, name string) protocol.CommandResultPayload {
	if err := h.client.ContainerRemove(ctx, name, container.RemoveOptions{Force: false}); err != nil {
		return containerErrorResult("remove", name, err)
	}
	return okResult(map[string]string{"status": "removed", "name": name})
}

// InspectContainer returns detailed info about a pre-provisioned container.
func (h *Handler) InspectContainer(ctx context.Context, name string) protocol.CommandResultPayload {
	info, err := h.client.ContainerInspect(ctx, name)
	if err != nil {
		return containerErrorResult("inspect", name, err)
	}
	data := map[string]interface{}{
		"id":          info.ID,
		"name":        strings.TrimPrefix(info.Name, "/"),
		"status":      info.State.Status,
		"running":     info.State.Running,
		"startedAt":   info.State.StartedAt,
		"finishedAt":  info.State.FinishedAt,
		"image":       info.Config.Image,
	}
	if info.State.Health != nil {
		data["health"] = info.State.Health.Status
	}
	if info.NetworkSettings != nil {
		data["ports"] = formatInspectPorts(info.NetworkSettings.Ports)
	}
	return okResult(data)
}

// ListContainers lists containers, preferring unswarm-managed (labeled)
// containers and falling back to all containers when none are labeled.
func (h *Handler) ListContainers(ctx context.Context) protocol.CommandResultPayload {
	opts := container.ListOptions{
		All:     true,
		Filters: filters.NewArgs(filters.Arg("label", unswarmLabel)),
	}
	containers, err := h.client.ContainerList(ctx, opts)
	if err != nil {
		return errorResult(fmt.Sprintf("list containers: %v", err))
	}

	// Fall back to all containers when no unswarm-labeled containers exist.
	if len(containers) == 0 {
		opts.Filters = filters.NewArgs()
		containers, err = h.client.ContainerList(ctx, opts)
		if err != nil {
			return errorResult(fmt.Sprintf("list containers (all): %v", err))
		}
	}

	result := make([]map[string]interface{}, 0, len(containers))
	for _, c := range containers {
		result = append(result, map[string]interface{}{
			"id":     shortID(c.ID),
			"name":   firstContainerName(c.Names),
			"image":  c.Image,
			"status": c.Status,
			"state":  c.State,
			"ports":  formatPorts(c.Ports),
		})
	}
	return okResult(map[string]interface{}{"containers": result})
}

// maxTailLines caps the tailLines parameter of GetContainerLogs so an
// untrusted payload cannot ask the Docker daemon for an unbounded log read.
const maxTailLines = 10000

// GetContainerLogs returns the last N lines of container logs.
// The Docker log stream is multiplexed for non-TTY containers, so the
// stream is de-multiplexed with stdcopy to produce clean log output.
// TTY containers reject ShowStderr, so the container is inspected first and
// the options are built accordingly.
func (h *Handler) GetContainerLogs(ctx context.Context, name string, tailLines int) protocol.CommandResultPayload {
	if tailLines <= 0 {
		tailLines = 100
	}
	if tailLines > maxTailLines {
		tailLines = maxTailLines
	}

	// Inspect first: TTY containers reject ShowStderr, and the decode path
	// (raw vs stdcopy) depends on the TTY flag.
	tty := false
	if info, ierr := h.client.ContainerInspect(ctx, name); ierr == nil {
		tty = info.Config.Tty
	}

	opts := container.LogsOptions{
		ShowStdout: true,
		Tail:       fmt.Sprintf("%d", tailLines),
	}
	if !tty {
		opts.ShowStderr = true
	}

	reader, err := h.client.ContainerLogs(ctx, name, opts)
	if err != nil {
		return containerErrorResult("get logs for", name, err)
	}
	defer func() { _ = reader.Close() }()

	var buf bytes.Buffer
	if tty {
		_, err = io.Copy(&buf, reader)
	} else {
		_, err = stdcopy.StdCopy(&buf, &buf, reader)
	}
	if err != nil {
		return errorResult(fmt.Sprintf("read logs for %q: %v", name, err))
	}
	lines := strings.Split(buf.String(), "\n")
	// Trim trailing empty line from final newline
	if len(lines) > 0 && lines[len(lines)-1] == "" {
		lines = lines[:len(lines)-1]
	}
	return okResult(map[string]interface{}{
		"name":  name,
		"logs":  lines,
	})
}

// ListContainerStatuses returns lightweight status info for telemetry.
// Best-effort: per-container stats failures are skipped, not fatal.
//
// Telemetry is restricted to unswarm-labeled containers when any exist —
// each container costs a ContainerInspect + ContainerStatsOneShot round
// trip per tick, so unrelated host containers must not be polled. Hosts
// with no labeled containers fall back to listing everything (mirrors
// ListContainers) so unlabeled single-tenant setups keep telemetry.
func (h *Handler) ListContainerStatuses(ctx context.Context) []protocol.ContainerTelemetry {
	containers, err := h.client.ContainerList(ctx, container.ListOptions{
		All:     true,
		Filters: filters.NewArgs(filters.Arg("label", unswarmLabel)),
	})
	if err != nil {
		return nil
	}
	if len(containers) == 0 {
		containers, err = h.client.ContainerList(ctx, container.ListOptions{All: true})
		if err != nil {
			return nil
		}
	}
	result := make([]protocol.ContainerTelemetry, 0, len(containers))
	for _, c := range containers {
		ct := protocol.ContainerTelemetry{
			ID:     shortID(c.ID),
			Name:   firstContainerName(c.Names),
			Status: c.State,
			Port:   firstPublicPort(c.Ports),
		}
		if info, ierr := h.client.ContainerInspect(ctx, c.ID); ierr == nil {
			if started, terr := time.Parse(time.RFC3339, info.State.StartedAt); terr == nil && !started.IsZero() {
				ct.Uptime = time.Since(started).Round(time.Second).String()
			}
		}
		if memMB, ierr := h.containerMemoryMb(ctx, c.ID); ierr == nil {
			ct.Memory = fmt.Sprintf("%d MB", memMB)
		}
		result = append(result, ct)
	}
	return result
}

// containerMemoryMb returns the current memory usage of a container in MB.
func (h *Handler) containerMemoryMb(ctx context.Context, id string) (int64, error) {
	resp, err := h.client.ContainerStatsOneShot(ctx, id)
	if err != nil {
		return 0, err
	}
	defer func() { _ = resp.Body.Close() }()

	var stats container.StatsResponse
	dec := json.NewDecoder(resp.Body)
	if err := dec.Decode(&stats); err != nil {
		return 0, err
	}
	return int64(stats.MemoryStats.Usage / 1024 / 1024), nil
}

// containerErrorResult formats an error, giving a clear message when the
// container (or socket) was not found.
func containerErrorResult(op, name string, err error) protocol.CommandResultPayload {
	if errdefs.IsNotFound(err) {
		msg := fmt.Sprintf("container %q not found: %v", name, err)
		return errorResult(msg)
	}
	return errorResult(fmt.Sprintf("%s container %q: %v", op, name, err))
}

func okResult(data interface{}) protocol.CommandResultPayload {
	return protocol.CommandResultPayload{OK: true, Data: data}
}

func errorResult(msg string) protocol.CommandResultPayload {
	return protocol.CommandResultPayload{OK: false, Error: &msg}
}

func shortID(id string) string {
	if len(id) > 12 {
		return id[:12]
	}
	return id
}

func firstContainerName(names []string) string {
	if len(names) == 0 {
		return ""
	}
	return strings.TrimPrefix(names[0], "/")
}

func firstPublicPort(ports []types.Port) int {
	for _, p := range ports {
		if p.PublicPort != 0 {
			return int(p.PublicPort)
		}
	}
	if len(ports) > 0 {
		return int(ports[0].PrivatePort)
	}
	return 0
}

func formatPorts(ports []types.Port) []map[string]interface{} {
	result := make([]map[string]interface{}, 0, len(ports))
	for _, p := range ports {
		result = append(result, map[string]interface{}{
			"privatePort": p.PrivatePort,
			"publicPort":  p.PublicPort,
			"type":        p.Type,
		})
	}
	return result
}

func formatInspectPorts(ports nat.PortMap) []map[string]interface{} {
	result := make([]map[string]interface{}, 0, len(ports))
	for private, bindings := range ports {
		entry := map[string]interface{}{
			"privatePort": private.Port(),
			"protocol":    private.Proto(),
		}
		if len(bindings) > 0 {
			entry["publicPort"] = bindings[0].HostPort
			entry["hostIp"] = bindings[0].HostIP
		}
		result = append(result, entry)
	}
	return result
}
