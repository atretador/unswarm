package telemetry

import (
	"context"
	"log/slog"
	"strconv"
	"strings"
	"time"

	"unswarm/agent/internal/protocol"
)

// collectGPUMetrics returns live GPU utilization for all detected GPUs.
// Best-effort: returns nil on any failure.
func collectGPUMetrics(logger *slog.Logger) []protocol.GPUMetrics {
	ctx, cancel := context.WithTimeout(context.Background(), 3*time.Second)
	defer cancel()

	if m := collectNvidiaMetrics(ctx); len(m) > 0 {
		return m
	}
	if m := collectAMDMetrics(ctx, logger); len(m) > 0 {
		return m
	}
	if m := collectIntelMetrics(ctx, logger); len(m) > 0 {
		return m
	}
	return nil
}

// ── NVIDIA ────────────────────────────────────────────────────

func collectNvidiaMetrics(ctx context.Context) []protocol.GPUMetrics {
	out, err := runTool(ctx, "nvidia-smi",
		"--query-gpu=index,name,utilization.gpu,memory.used,memory.total",
		"--format=csv,noheader,nounits")
	if err != nil || out == "" {
		return nil
	}

	var result []protocol.GPUMetrics
	for _, line := range strings.Split(out, "\n") {
		line = strings.TrimSpace(line)
		if line == "" {
			continue
		}
		gpu := parseNvidiaSMILine(line)
		if gpu != nil {
			result = append(result, *gpu)
		}
	}
	return result
}

// parseNvidiaSMILine parses one CSV line from nvidia-smi.
// Example: "0, NVIDIA GeForce RTX 4090, 72, 6656, 24576"
func parseNvidiaSMILine(line string) *protocol.GPUMetrics {
	parts := strings.Split(line, ",")
	if len(parts) < 5 {
		return nil
	}

	idx, err := strconv.Atoi(strings.TrimSpace(parts[0]))
	if err != nil {
		return nil
	}

	name := strings.TrimSpace(parts[1])
	corePercent := parseSmiPercent(strings.TrimSpace(parts[2]))
	memUsed := parseSmiInt(strings.TrimSpace(parts[3]))
	memTotal := parseSmiInt(strings.TrimSpace(parts[4]))

	var memPercent float64
	if memTotal > 0 {
		memPercent = float64(memUsed) / float64(memTotal) * 100
	} else {
		memPercent = -1
	}

	return &protocol.GPUMetrics{
		Index:         idx,
		Name:          name,
		Vendor:        "nvidia",
		CorePercent:   corePercent,
		MemoryPercent: memPercent,
		MemoryUsedMb:  memUsed,
		MemoryTotalMb: memTotal,
	}
}

func parseSmiPercent(s string) float64 {
	s = strings.TrimSuffix(s, "%")
	s = strings.TrimSpace(s)
	if s == "N/A" || s == "[Not Supported]" || s == "" {
		return -1
	}
	v, err := strconv.ParseFloat(s, 64)
	if err != nil {
		return -1
	}
	return v
}

func parseSmiInt(s string) int64 {
	s = strings.TrimSpace(s)
	if s == "N/A" || s == "[Not Supported]" || s == "" {
		return -1
	}
	v, err := strconv.ParseInt(s, 10, 64)
	if err != nil {
		return -1
	}
	return v
}

// ── AMD ───────────────────────────────────────────────────────

func collectAMDMetrics(ctx context.Context, logger *slog.Logger) []protocol.GPUMetrics {
	if m := collectAMDSmiMetrics(ctx); len(m) > 0 {
		return m
	}
	return collectAMDSysfsMetrics(logger)
}

func collectAMDSmiMetrics(ctx context.Context) []protocol.GPUMetrics {
	out, err := runTool(ctx, "amd-smi", "monitor", "--gfx", "--mem", "--json")
	if err != nil || out == "" {
		return nil
	}
	return parseAmdSmiJSON(out)
}

//nolint:cyclop // intentionally linear JSON parsing
func parseAmdSmiJSON(s string) []protocol.GPUMetrics {
	s = strings.TrimSpace(s)
	if s == "" || (s[0] != '{' && s[0] != '[') {
		return nil
	}

	var result []protocol.GPUMetrics
	for i := 0; ; i++ {
		marker := `"gfx_activity"`
		idx := strings.Index(s, marker)
		if idx < 0 {
			marker = `"activity"`
			idx = strings.Index(s, marker)
		}
		if idx < 0 {
			break
		}
		rest := s[idx+len(marker):]
		colonIdx := strings.Index(rest, ":")
		if colonIdx < 0 {
			break
		}
		rest = strings.TrimSpace(rest[colonIdx+1:])
		core := parseSmiPercent(rest[:strings.IndexAny(rest, ",}\n")])

		memTotal := int64(-1)
		memUsed := int64(-1)
		if ti := strings.Index(s, `"vram_total"`); ti >= 0 {
			memTotal = extractAMDIntValue(s, ti+len("vram_total"))
		}
		if ui := strings.Index(s, `"vram_used"`); ui >= 0 {
			memUsed = extractAMDIntValue(s, ui+len("vram_used"))
		}

		var memPercent float64
		if memTotal > 0 && memUsed >= 0 {
			memPercent = float64(memUsed) / float64(memTotal) * 100
		} else {
			memPercent = -1
		}

		result = append(result, protocol.GPUMetrics{
			Index:         i,
			Name:          "AMD GPU",
			Vendor:        "amd",
			CorePercent:   core,
			MemoryPercent: memPercent,
			MemoryUsedMb:  memUsed,
			MemoryTotalMb: memTotal,
		})
		s = s[idx+1:]
	}
	return result
}

func extractAMDIntValue(s string, offset int) int64 {
	rest := s[offset:]
	colonIdx := strings.Index(rest, ":")
	if colonIdx < 0 {
		return -1
	}
	rest = strings.TrimSpace(rest[colonIdx+1:])
	endIdx := strings.IndexAny(rest, ",}\n")
	if endIdx < 0 {
		endIdx = len(rest)
	}
	v, err := strconv.ParseInt(strings.TrimSpace(rest[:endIdx]), 10, 64)
	if err != nil {
		return -1
	}
	return v
}

func collectAMDSysfsMetrics(logger *slog.Logger) []protocol.GPUMetrics {
	_ = logger

	cardDirs, _ := filepathGlob("/sys/class/drm/card*")
	var result []protocol.GPUMetrics
	for i, card := range cardDirs {
		gpuBusyData, err := readFileString(card + "/device/gpu_busy_percent")
		if err != nil {
			continue
		}
		corePercent := parseSmiPercent(gpuBusyData)

		var memTotal, memUsed int64
		if d, err := readFileString(card + "/device/mem_info_vram_total"); err == nil {
			memTotal = parseSysfsBytes(d) / (1024 * 1024)
		}
		if d, err := readFileString(card + "/device/mem_info_vram_used"); err == nil {
			memUsed = parseSysfsBytes(d) / (1024 * 1024)
		}

		var memPercent float64
		if memTotal > 0 && memUsed >= 0 {
			memPercent = float64(memUsed) / float64(memTotal) * 100
		} else {
			memPercent = -1
		}

		result = append(result, protocol.GPUMetrics{
			Index:         i,
			Name:          "AMD GPU",
			Vendor:        "amd",
			CorePercent:   corePercent,
			MemoryPercent: memPercent,
			MemoryUsedMb:  memUsed,
			MemoryTotalMb: memTotal,
		})
	}
	return result
}

// ── Intel ─────────────────────────────────────────────────────

func collectIntelMetrics(ctx context.Context, logger *slog.Logger) []protocol.GPUMetrics {
	_ = logger
	out, _ := runTool(ctx, "intel_gpu_top", "-J", "-s", "1000")
	if out == "" {
		return nil
	}
	return parseIntelGpuTopJSON(out)
}

func parseIntelGpuTopJSON(s string) []protocol.GPUMetrics {
	s = strings.TrimSpace(s)
	if s == "" || s[0] != '{' {
		return nil
	}

	var busyValues []float64
	search := `"busy"`
	rest := s
	for {
		idx := strings.Index(rest, search)
		if idx < 0 {
			break
		}
		after := rest[idx+len(search):]
		colonIdx := strings.Index(after, ":")
		if colonIdx < 0 {
			break
		}
		valStr := strings.TrimSpace(after[colonIdx+1:])
		endIdx := strings.IndexAny(valStr, ",}\n")
		if endIdx < 0 {
			endIdx = len(valStr)
		}
		v, err := strconv.ParseFloat(strings.TrimSpace(valStr[:endIdx]), 64)
		if err == nil {
			busyValues = append(busyValues, v)
		}
		rest = rest[idx+len(search):]
	}

	if len(busyValues) == 0 {
		return nil
	}

	var sum float64
	for _, v := range busyValues {
		sum += v
	}
	corePercent := sum / float64(len(busyValues))

	return []protocol.GPUMetrics{{
		Index:         0,
		Name:          "Intel GPU",
		Vendor:        "intel",
		CorePercent:   corePercent,
		MemoryPercent: -1,
		MemoryUsedMb:  -1,
		MemoryTotalMb: -1,
	}}
}

// ── Helpers ────────────────────────────────────────────────────

func readFileString(path string) (string, error) {
	data, err := readFile(path)
	if err != nil {
		return "", err
	}
	return strings.TrimSpace(string(data)), nil
}

func parseSysfsBytes(s string) int64 {
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
