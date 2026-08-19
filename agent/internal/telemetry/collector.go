// Package telemetry collects host and container status information.
package telemetry

import (
	"context"
	"log/slog"
	"os"
	"os/exec"
	"runtime"
	"strings"
	"time"

	"unswarm/agent/internal/protocol"
)

// Collector gathers host metrics and container statuses for telemetry messages.
type Collector struct {
	hostname string
	logger   *slog.Logger
	// gpuInfo is detected once at startup and reused: nvidia-smi is expensive
	// and GPU presence does not change during the agent's lifetime.
	gpuInfo string
}

// New creates a Collector.
func New(logger *slog.Logger) *Collector {
	h, _ := os.Hostname()
	if h == "" {
		h = "unknown"
	}
	return &Collector{
		hostname: h,
		logger:   logger,
		gpuInfo:  detectGPU(),
	}
}

// Collect gathers the current telemetry payload.
// containerStatusesFn is a callback that returns the current container list.
func (c *Collector) Collect(containerStatusesFn func(ctx context.Context) []protocol.ContainerTelemetry) protocol.TelemetryPayload {
	ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
	defer cancel()

	return protocol.TelemetryPayload{
		Hostname:      c.hostname,
		OsPlatform:    runtime.GOOS,
		GPUInfo:       c.gpuInfo,
		TotalMemoryMb: getMemoryMb(),
		CPUCores:      runtime.NumCPU(),
		Containers:    containerStatusesFn(ctx),
	}
}

// Hostname returns the collector's hostname.
func (c *Collector) Hostname() string {
	return c.hostname
}

// detectGPU returns a short GPU description, or "" when no GPU is detectable.
// It shells out to nvidia-smi (best-effort, with a timeout).
func detectGPU() string {
	ctx, cancel := context.WithTimeout(context.Background(), 3*time.Second)
	defer cancel()

	out, err := exec.CommandContext(ctx, "nvidia-smi",
		"--query-gpu=name,memory.total",
		"--format=csv,noheader,nounits").Output() //nolint:gosec
	if err != nil {
		return ""
	}
	line := strings.TrimSpace(string(out))
	if line == "" {
		return ""
	}
	// "NVIDIA GeForce RTX 4090, 24564" -> "NVIDIA GeForce RTX 4090 (24564 MB)"
	parts := strings.SplitN(line, ",", 2)
	if len(parts) == 2 {
		return strings.TrimSpace(parts[0]) + " (" + strings.TrimSpace(parts[1]) + " MB)"
	}
	return line
}

func getMemoryMb() int64 {
	// Read /proc/meminfo for Linux
	data, err := os.ReadFile("/proc/meminfo")
	if err != nil {
		return 0
	}
	var total int64
	for _, line := range splitLines(string(data)) {
		if len(line) > 10 && line[:10] == "MemTotal: " {
			// Parse "MemTotal:   16384000 kB"
			var kb int64
			for _, ch := range line[10:] {
				if ch >= '0' && ch <= '9' {
					kb = kb*10 + int64(ch-'0')
				} else {
					break
				}
			}
			total = kb / 1024 // Convert to MB
			break
		}
	}
	return total
}

func splitLines(s string) []string {
	var lines []string
	start := 0
	for i := 0; i < len(s); i++ {
		if s[i] == '\n' {
			lines = append(lines, s[start:i])
			start = i + 1
		}
	}
	if start < len(s) {
		lines = append(lines, s[start:])
	}
	return lines
}
