package telemetry

import (
	"context"
	"runtime"
	"strconv"
	"strings"
	"time"

	"unswarm/agent/internal/protocol"
)

// collectHostMetrics returns live host CPU and RAM utilization.
// Best-effort: returns nil on failure.
func collectHostMetrics() *protocol.HostMetrics {
	switch runtime.GOOS {
	case "linux":
		return collectHostMetricsLinux()
	case "windows":
		return collectHostMetricsWindows()
	case "darwin":
		return collectHostMetricsDarwin()
	default:
		return nil
	}
}

// ── Linux ─────────────────────────────────────────────────────

func collectHostMetricsLinux() *protocol.HostMetrics {
	ctx, cancel := context.WithTimeout(context.Background(), 3*time.Second)
	defer cancel()

	ramTotal, ramUsed, ramPercent := parseMemInfo(ctx)
	cpuPercent := parseCPUPercent(ctx)

	return &protocol.HostMetrics{
		CpuPercent: cpuPercent,
		RamPercent: ramPercent,
		RamUsedMb:  ramUsed,
		RamTotalMb: ramTotal,
	}
}

// parseMemInfo reads /proc/meminfo and returns total MB, used MB, and usage %.
func parseMemInfo(ctx context.Context) (totalMB, usedMB int64, percent float64) {
	data, err := readProcFile(ctx, "/proc/meminfo")
	if err != nil {
		return 0, 0, 0
	}

	var memTotal, memAvailable int64
	for _, line := range splitLines(string(data)) {
		if len(line) > 9 && line[:9] == "MemTotal:" {
			memTotal = parseKBField(line[9:])
		} else if len(line) > 13 && line[:13] == "MemAvailable:" {
			memAvailable = parseKBField(line[13:])
		}
	}

	if memTotal <= 0 {
		return 0, 0, 0
	}
	totalMB = memTotal / 1024
	usedMB = (memTotal - memAvailable) / 1024
	if memTotal > 0 {
		percent = float64(memTotal-memAvailable) / float64(memTotal) * 100
	}
	return totalMB, usedMB, percent
}

// parseKBField extracts an integer (in kB) from a /proc/meminfo field value.
func parseKBField(s string) int64 {
	s = strings.TrimSpace(s)
	var v int64
	for _, ch := range s {
		if ch >= '0' && ch <= '9' {
			v = v*10 + int64(ch-'0')
		} else {
			break
		}
	}
	return v
}

// parseCPUPercent reads /proc/stat twice with a 100ms gap and computes idle%.
func parseCPUPercent(ctx context.Context) float64 {
	t1, err := readCPUStat(ctx)
	if err != nil {
		return 0
	}
	time.Sleep(100 * time.Millisecond)
	t2, err := readCPUStat(ctx)
	if err != nil {
		return 0
	}

	dTotal := t2.total - t1.total
	dIdle := t2.idle - t1.idle
	if dTotal == 0 {
		return 0
	}
	return float64(dTotal-dIdle) / float64(dTotal) * 100
}

type cpuStat struct {
	idle  uint64
	total uint64
}

// readCPUStat reads the first "cpu " aggregate line from /proc/stat.
func readCPUStat(ctx context.Context) (cpuStat, error) {
	data, err := readProcFile(ctx, "/proc/stat")
	if err != nil {
		return cpuStat{}, err
	}
	return parseCPUStatLine(data)
}

// parseCPUStatLine extracts idle and total from /proc/stat content.
func parseCPUStatLine(data []byte) (cpuStat, error) {
	for _, line := range splitLines(string(data)) {
		if strings.HasPrefix(line, "cpu ") {
			return parseCPUCalculate(line)
		}
	}
	return cpuStat{}, errCPUNotFound
}

// parseCPUCalculate parses a "cpu " aggregate line and sums all fields.
// Format: "cpu  user nice system idle iowait irq softirq steal"
func parseCPUCalculate(line string) (cpuStat, error) {
	fields := strings.Fields(line)
	if len(fields) < 5 {
		return cpuStat{}, errCPUInvalid
	}
	var total, idle uint64
	for i := 1; i < len(fields); i++ {
		v, err := strconv.ParseUint(fields[i], 10, 64)
		if err != nil {
			continue
		}
		total += v
		if i == 4 { // idle is the 5th field (index 4)
			idle = v
		}
	}
	return cpuStat{idle: idle, total: total}, nil
}

// Sentinel errors for CPU parsing.
var (
	errCPUNotFound = errString("cpu line not found")
	errCPUInvalid  = errString("cpu line too short")
)

type errString string

func (e errString) Error() string { return string(e) }

// ── Windows (stub) ───────────────────────────────────────────

func collectHostMetricsWindows() *protocol.HostMetrics {
	// TODO: implement via wmic or PowerShell
	return nil
}

// ── macOS (stub) ─────────────────────────────────────────────

func collectHostMetricsDarwin() *protocol.HostMetrics {
	// TODO: implement via sysctl or vm_stat
	return nil
}
