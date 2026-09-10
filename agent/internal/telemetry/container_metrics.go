package telemetry

import (
	"context"
	"os/exec"
	"strconv"
	"strings"
	"time"

	"unswarm/agent/internal/protocol"
)

// collectContainerMetrics gathers live container metrics via `docker stats --no-stream`.
// Best-effort: returns nil on failure.
func collectContainerMetrics(ctx context.Context) map[string]protocol.ContainerMetrics {
	statsCtx, cancel := context.WithTimeout(ctx, 3*time.Second)
	defer cancel()

	out, err := dockerStatsOutput(statsCtx)
	if err != nil || out == "" {
		return nil
	}

	return parseDockerStatsOutput(out)
}

// dockerStatsOutput runs docker stats --no-stream and returns raw output.
func dockerStatsOutput(ctx context.Context) (string, error) {
	cmd := exec.CommandContext(ctx, "docker",
		"stats", "--no-stream", "--no-trunc",
		"--format", "{{.ID}}\t{{.CPUPerc}}\t{{.MemPerc}}\t{{.MemUsage}}")
	out, err := cmd.Output()
	if err != nil {
		return "", err
	}
	return strings.TrimSpace(string(out)), nil
}

// parseDockerStatsOutput parses the tab-separated output of docker stats.
func parseDockerStatsOutput(output string) map[string]protocol.ContainerMetrics {
	result := make(map[string]protocol.ContainerMetrics)
	for _, line := range strings.Split(output, "\n") {
		line = strings.TrimSpace(line)
		if line == "" {
			continue
		}
		cm := parseDockerStatsLine(line)
		if cm != nil {
			result[cm.ContainerId] = *cm
		}
	}
	return result
}

// parseDockerStatsLine parses one tab-separated line from docker stats.
//
// Format: "container_id\t45.23%\t33.00%\t2112MiB / 6400MiB"
func parseDockerStatsLine(line string) *protocol.ContainerMetrics {
	parts := strings.Split(line, "\t")
	if len(parts) < 4 {
		return nil
	}

	containerID := strings.TrimSpace(parts[0])
	if containerID == "" {
		return nil
	}

	cpuPercent := parseDockerPercent(parts[1])
	ramPercent := parseDockerPercent(parts[2])
	ramUsed, ramTotal := parseDockerMemUsage(parts[3])

	return &protocol.ContainerMetrics{
		ContainerId: containerID,
		CpuPercent:  cpuPercent,
		RamPercent:  ramPercent,
		RamUsedMb:   ramUsed,
		RamTotalMb:  ramTotal,
	}
}

// parseDockerPercent strips "%" and parses a float64.
func parseDockerPercent(s string) float64 {
	s = strings.TrimSpace(s)
	s = strings.TrimSuffix(s, "%")
	s = strings.TrimSpace(s)
	if s == "" {
		return 0
	}
	v, err := strconv.ParseFloat(s, 64)
	if err != nil {
		return 0
	}
	return v
}

// parseDockerMemUsage parses "2112MiB / 6400MiB" into (usedMB, totalMB).
func parseDockerMemUsage(s string) (usedMB, totalMB int64) {
	s = strings.TrimSpace(s)
	parts := strings.SplitN(s, "/", 2)
	if len(parts) != 2 {
		return 0, 0
	}
	usedMB = parseDockerMemSize(parts[0])
	totalMB = parseDockerMemSize(parts[1])
	return usedMB, totalMB
}

// parseDockerMemSize parses a memory size string like "2112MiB", "1.5GiB", "256.0kB".
// Returns the value in MB.
func parseDockerMemSize(s string) int64 {
	s = strings.TrimSpace(s)
	if s == "" || s == "0B" {
		return 0
	}

	// Split numeric prefix from unit
	numStr := s
	unit := ""
	for i, ch := range s {
		if (ch < '0' || ch > '9') && ch != '.' {
			numStr = s[:i]
			unit = strings.ToLower(s[i:])
			break
		}
	}

	if numStr == "" {
		return 0
	}

	val, err := strconv.ParseFloat(numStr, 64)
	if err != nil {
		return 0
	}

	switch unit {
	case "mib", "mb":
		return int64(val)
	case "gib", "gb":
		return int64(val * 1024)
	case "kib", "kb":
		return int64(val / 1024)
	case "b", "":
		return int64(val / (1024 * 1024))
	default:
		return int64(val)
	}
}
