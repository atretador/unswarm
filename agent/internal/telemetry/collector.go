// Package telemetry collects host and container status information.
package telemetry

import (
	"context"
	"log/slog"
	"os"
	"os/exec"
	"path/filepath"
	"runtime"
	"strconv"
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
// It is vendor-agnostic: supports NVIDIA, AMD, and Intel on Linux, Windows, and macOS.
func detectGPU() string {
	ctx, cancel := context.WithTimeout(context.Background(), 3*time.Second)
	defer cancel()

	var entries []string
	switch runtime.GOOS {
	case "linux":
		entries = detectGPULinux(ctx)
	case "windows":
		entries = detectGPUWindows(ctx)
	case "darwin":
		entries = detectGPUMacOS(ctx)
	}

	if len(entries) == 0 {
		return ""
	}
	return strings.Join(entries, ", ")
}

// ── Linux: lspci for names, sysfs / nvidia-smi for VRAM ────────

func detectGPULinux(ctx context.Context) []string {
	var results []string

	// GPU names via lspci (works for all vendors)
	out, err := runTool(ctx, "lspci", "-nn")
	if err != nil || out == "" {
		return nvidiaSmiFallback(ctx)
	}

	for _, line := range strings.Split(out, "\n") {
		if !strings.Contains(strings.ToUpper(line), "VGA") &&
			!strings.Contains(strings.ToUpper(line), "3D") {
			continue
		}

		name := extractLinuxGpuName(line)
		if name == "" {
			continue
		}

		vramMB := getLinuxGpuVram(ctx, len(results))
		if vramMB > 0 {
			results = append(results, name+" ("+formatGB(vramMB)+")")
		} else {
			results = append(results, name)
		}
	}

	if len(results) == 0 {
		return nvidiaSmiFallback(ctx)
	}
	return results
}

func extractLinuxGpuName(lspciLine string) string {
	// "05:00.0 VGA compatible controller [0300]: Advanced Micro Devices, Inc. [AMD/ATI] Vega 20 [Radeon Pro VII/Radeon Instinct MI50] [1002:66a1] (rev 06)"
	// → "Radeon Pro VII"
	//
	// "01:00.0 VGA compatible controller: NVIDIA Corporation GeForce RTX 4090 (rev a1)"
	// → "GeForce RTX 4090"

	name := lspciLine

	// Strip leading address (everything up to first space after the address)
	if idx := strings.IndexByte(name, ' '); idx >= 0 {
		name = strings.TrimSpace(name[idx+1:])
	}

	// Strip controller type keywords
	for _, kw := range []string{
		"VGA compatible controller:", "VGA compatible controller",
		"3D controller:", "3D controller",
		"Display controller:", "Display controller",
	} {
		name = strings.ReplaceAll(name, kw, "")
	}
	name = strings.TrimSpace(name)

	// Strip trailing "(rev xx)"
	if idx := strings.LastIndex(name, "(rev "); idx >= 0 {
		name = strings.TrimSpace(name[:idx])
	}

	// Iterate bracket contents from last to first — find the first non-PCI-ID bracket
	allBrackets := regexpAllBrackets(name)
	for i := len(allBrackets) - 1; i >= 0; i-- {
		content := strings.TrimSpace(allBrackets[i])

		// Skip PCI device ID brackets like "[1002:66a1]" or "[0300]"
		if isPCIIDBracket(content) {
			continue
		}

		// "Radeon Pro VII/Radeon Instinct MI50" → "Radeon Pro VII"
		if slashIdx := strings.Index(content, "/"); slashIdx > 0 {
			content = strings.TrimSpace(content[:slashIdx])
		}
		return content
	}

	// No useful brackets — strip known vendor prefixes
	vendorPrefixes := []string{
		"NVIDIA Corporation ",
		"Advanced Micro Devices, Inc. ",
		"Intel Corporation ",
		"Qualcomm Technologies, Inc. ",
		"Broadcom Inc. ",
	}
	for _, prefix := range vendorPrefixes {
		if strings.HasPrefix(name, prefix) {
			name = strings.TrimPrefix(name, prefix)
			break
		}
	}

	name = strings.TrimSpace(name)
	if name == "" {
		return ""
	}
	return name
}

func regexpAllBrackets(s string) []string {
	// Extract all content inside [...] brackets
	var results []string
	for {
		open := strings.Index(s, "[")
		close := strings.Index(s, "]")
		if open < 0 || close <= open {
			break
		}
		results = append(results, s[open+1:close])
		s = s[close+1:]
	}
	return results
}

func isPCIIDBracket(s string) bool {
	// Matches PCI device IDs like "1002:66a1" or "0300"
	for _, ch := range s {
		if (ch < '0' || ch > '9') && (ch < 'a' || ch > 'f') && (ch < 'A' || ch > 'F') && ch != ':' {
			return false
		}
	}
	return len(s) >= 4
}

func getLinuxGpuVram(ctx context.Context, gpuIndex int) int64 {
	cards, _ := filepath.Glob("/sys/class/drm/card*")
	idx := 0
	for _, card := range cards {
		vramPath := filepath.Join(card, "device", "mem_info_vram_total")
		data, err := os.ReadFile(vramPath)
		if err != nil {
			continue
		}
		if idx == gpuIndex {
			var bytes int64
			for _, ch := range strings.TrimSpace(string(data)) {
				if ch >= '0' && ch <= '9' {
					bytes = bytes*10 + int64(ch-'0')
				} else {
					break
				}
			}
			if bytes > 0 {
				return bytes / (1024 * 1024) // bytes → MB
			}
			break
		}
		idx++
	}

	// Fallback: nvidia-smi for NVIDIA VRAM
	out, err := runTool(ctx, "nvidia-smi", "--query-gpu=memory.total --format=csv,noheader,nounits")
	if err != nil || out == "" {
		return 0
	}
	lines := strings.Split(strings.TrimSpace(out), "\n")
	if gpuIndex < len(lines) {
		var mb int64
		for _, ch := range strings.TrimSpace(lines[gpuIndex]) {
			if ch >= '0' && ch <= '9' {
				mb = mb*10 + int64(ch-'0')
			} else {
				break
			}
		}
		return mb
	}
	return 0
}

func nvidiaSmiFallback(ctx context.Context) []string {
	out, err := runTool(ctx, "nvidia-smi",
		"--query-gpu=name,memory.total --format=csv,noheader,nounits")
	if err != nil || out == "" {
		return nil
	}
	var results []string
	for _, line := range strings.Split(out, "\n") {
		trimmed := strings.TrimSpace(line)
		if trimmed == "" {
			continue
		}
		parts := strings.SplitN(trimmed, ",", 2)
		if len(parts) == 2 {
			var mb int64
			for _, ch := range strings.TrimSpace(parts[1]) {
				if ch >= '0' && ch <= '9' {
					mb = mb*10 + int64(ch-'0')
				} else {
					break
				}
			}
			results = append(results, strings.TrimSpace(parts[0])+" ("+formatGB(mb)+")")
		} else {
			results = append(results, trimmed)
		}
	}
	return results
}

// ── Windows: WMI via PowerShell ──────────────────────────────

func detectGPUWindows(ctx context.Context) []string {
	out, err := runTool(ctx, "powershell",
		`-NoProfile -Command "Get-CimInstance Win32_VideoController | Select-Object Name | ConvertTo-Json -Compress"`)
	if err != nil || out == "" {
		return nil
	}

	// Parse JSON: could be object or array
	out = strings.TrimSpace(out)
	var results []string

	// Simple JSON parsing without importing encoding/json
	if strings.HasPrefix(out, "[") {
		// Array of objects with "Name" field
		for _, item := range splitJSONObjects(out) {
			if name := extractJSONString(item, "Name"); name != "" {
				results = append(results, name)
			}
		}
	} else if strings.HasPrefix(out, "{") {
		if name := extractJSONString(out, "Name"); name != "" {
			results = append(results, name)
		}
	}

	return results
}

// ── macOS: system_profiler ────────────────────────────────────

func detectGPUMacOS(ctx context.Context) []string {
	out, err := runTool(ctx, "system_profiler", "SPDisplaysDataType")
	if err != nil || out == "" {
		return nil
	}

	var results []string
	var currentName string

	for _, line := range strings.Split(out, "\n") {
		trimmed := strings.TrimSpace(line)

		if strings.HasPrefix(strings.ToLower(trimmed), "chipset model:") {
			currentName = strings.TrimSpace(trimmed[len("Chipset Model:"):])
		} else if currentName != "" && (strings.HasPrefix(strings.ToLower(trimmed), "vram (total):") ||
			strings.HasPrefix(strings.ToLower(trimmed), "memory:")) {
			vramStr := ""
			if idx := strings.Index(trimmed, ":"); idx >= 0 {
				vramStr = strings.TrimSpace(trimmed[idx+1:])
			}
			if vramStr != "" {
				results = append(results, currentName+" ("+vramStr+")")
			} else {
				results = append(results, currentName)
			}
			currentName = ""
		}
	}

	if currentName != "" {
		results = append(results, currentName)
	}

	return results
}

// ── Helpers ───────────────────────────────────────────────────

func runTool(ctx context.Context, name string, args ...string) (string, error) {
	out, err := exec.CommandContext(ctx, name, args...).Output() //nolint:gosec
	if err != nil {
		return "", err
	}
	return strings.TrimSpace(string(out)), nil
}

func formatGB(mb int64) string {
	gbFloat := float64(mb) / 1024.0
	if gbFloat >= 1.0 {
		gb := mb / 1024
		remainder := (mb % 1024) * 10 / 1024
		if remainder == 0 {
			return strconv.FormatInt(gb, 10) + " GB"
		}
		return strconv.FormatInt(gb, 10) + "." + strconv.FormatInt(remainder, 10) + " GB"
	}
	return strconv.FormatInt(mb, 10) + " MB"
}

// splitJSONObjects splits a JSON array string into individual objects (naive parser)
func splitJSONObjects(s string) []string {
	var results []string
	depth := 0
	start := -1
	for i, ch := range s {
		switch ch {
		case '{':
			if depth == 0 {
				start = i
			}
			depth++
		case '}':
			depth--
			if depth == 0 && start >= 0 {
				results = append(results, s[start:i+1])
				start = -1
			}
		}
	}
	return results
}

// extractJSONString extracts a string value for a given key from a JSON object (naive)
func extractJSONString(obj, key string) string {
	search := `"` + key + `":`
	idx := strings.Index(obj, search)
	if idx < 0 {
		return ""
	}
	rest := obj[idx+len(search):]
	rest = strings.TrimSpace(rest)
	if !strings.HasPrefix(rest, `"`) {
		return ""
	}
	rest = rest[1:]
	end := strings.Index(rest, `"`)
	if end < 0 {
		return ""
	}
	return rest[:end]
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
