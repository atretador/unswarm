package telemetry

import (
	"os"
	"path/filepath"
	"testing"
)

func TestParseNvidiaSMILine(t *testing.T) {
	tests := []struct {
		name    string
		line    string
		wantNil bool
		wantIdx int
		wantGPU string
		wantVnd string
		wantCPU float64
		wantMU  int64
		wantMT  int64
	}{
		{
			name: "valid RTX 4090",
			line: "0, NVIDIA GeForce RTX 4090, 72, 6656, 24576",
			wantIdx: 0,
			wantGPU: "NVIDIA GeForce RTX 4090",
			wantVnd: "nvidia",
			wantCPU: 72,
			wantMU:  6656,
			wantMT:  24576,
		},
		{
			name: "GPU with N/A utilization",
			line: "1, Tesla T4, [Not Supported], 1024, 15360",
			wantIdx: 1,
			wantGPU: "Tesla T4",
			wantVnd: "nvidia",
			wantCPU: -1,
			wantMU:  1024,
			wantMT:  15360,
		},
		{
			name: "GPU with N/A memory",
			line: "0, A100, 50, N/A, N/A",
			wantIdx: 0,
			wantGPU: "A100",
			wantVnd: "nvidia",
			wantCPU: 50,
			wantMU:  -1,
			wantMT:  -1,
		},
		{
			name:    "too few fields",
			line:    "0, RTX 4090, 72",
			wantNil: true,
		},
		{
			name:    "empty line",
			line:    "",
			wantNil: true,
		},
		{
			name:    "invalid index",
			line:    "abc, RTX 4090, 50, 1024, 2048",
			wantNil: true,
		},
	}

	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			got := parseNvidiaSMILine(tt.line)
			if tt.wantNil {
				if got != nil {
					t.Fatalf("expected nil, got %+v", got)
				}
				return
			}
			if got == nil {
				t.Fatal("expected non-nil result")
			}
			if got.Index != tt.wantIdx {
				t.Errorf("Index = %d, want %d", got.Index, tt.wantIdx)
			}
			if got.Name != tt.wantGPU {
				t.Errorf("Name = %q, want %q", got.Name, tt.wantGPU)
			}
			if got.Vendor != tt.wantVnd {
				t.Errorf("Vendor = %q, want %q", got.Vendor, tt.wantVnd)
			}
			if got.CorePercent != tt.wantCPU {
				t.Errorf("CorePercent = %f, want %f", got.CorePercent, tt.wantCPU)
			}
			if got.MemoryUsedMb != tt.wantMU {
				t.Errorf("MemoryUsedMb = %d, want %d", got.MemoryUsedMb, tt.wantMU)
			}
			if got.MemoryTotalMb != tt.wantMT {
				t.Errorf("MemoryTotalMb = %d, want %d", got.MemoryTotalMb, tt.wantMT)
			}
		})
	}
}

func TestParseSmiPercent(t *testing.T) {
	tests := []struct {
		input string
		want  float64
	}{
		{"72", 72},
		{"0", 0},
		{"100.5", 100.5},
		{"N/A", -1},
		{"[Not Supported]", -1},
		{"", -1},
		{"72 %", 72},
	}
	for _, tt := range tests {
		t.Run(tt.input, func(t *testing.T) {
			got := parseSmiPercent(tt.input)
			if got != tt.want {
				t.Errorf("parseSmiPercent(%q) = %f, want %f", tt.input, got, tt.want)
			}
		})
	}
}

func TestParseSmiInt(t *testing.T) {
	tests := []struct {
		input string
		want  int64
	}{
		{"1024", 1024},
		{"0", 0},
		{"N/A", -1},
		{"[Not Supported]", -1},
		{"", -1},
		{"abc", -1},
	}
	for _, tt := range tests {
		t.Run(tt.input, func(t *testing.T) {
			got := parseSmiInt(tt.input)
			if got != tt.want {
				t.Errorf("parseSmiInt(%q) = %d, want %d", tt.input, got, tt.want)
			}
		})
	}
}

func TestParseAmdSmiJSON(t *testing.T) {
	input := `{"gpu": [{"gfx_activity": 85, "vram_used": 4096, "vram_total": 16384}]}`
	result := parseAmdSmiJSON(input)
	if len(result) != 1 {
		t.Fatalf("expected 1 GPU, got %d", len(result))
	}
	gpu := result[0]
	if gpu.Vendor != "amd" {
		t.Errorf("Vendor = %q, want amd", gpu.Vendor)
	}
	if gpu.CorePercent != 85 {
		t.Errorf("CorePercent = %f, want 85", gpu.CorePercent)
	}
	if gpu.MemoryUsedMb != 4096 {
		t.Errorf("MemoryUsedMb = %d, want 4096", gpu.MemoryUsedMb)
	}
	if gpu.MemoryTotalMb != 16384 {
		t.Errorf("MemoryTotalMb = %d, want 16384", gpu.MemoryTotalMb)
	}
}

func TestParseAmdSmiJSONEmpty(t *testing.T) {
	if got := parseAmdSmiJSON(""); len(got) != 0 {
		t.Errorf("expected empty, got %v", got)
	}
	if got := parseAmdSmiJSON("not json"); len(got) != 0 {
		t.Errorf("expected empty, got %v", got)
	}
}

func TestParseSysfsBytes(t *testing.T) {
	tests := []struct {
		input string
		want  int64
	}{
		{"16384000", 16384000},
		{"0", 0},
		{"123kB", 123},
		{"", 0},
	}
	for _, tt := range tests {
		t.Run(tt.input, func(t *testing.T) {
			got := parseSysfsBytes(tt.input)
			if got != tt.want {
				t.Errorf("parseSysfsBytes(%q) = %d, want %d", tt.input, got, tt.want)
			}
		})
	}
}

// TestCollectAMDSysfsMetrics tests sysfs-based AMD metrics collection
// by providing mock data through the package-level readFile.
func TestCollectAMDSysfsMetrics(t *testing.T) {
	// Create temp dir structure to mock sysfs
	tmpDir := t.TempDir()
	cardDir := filepath.Join(tmpDir, "card0", "device")
	if err := os.MkdirAll(cardDir, 0o755); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(filepath.Join(cardDir, "gpu_busy_percent"), []byte("75\n"), 0o644); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(filepath.Join(cardDir, "mem_info_vram_total"), []byte("16384000000\n"), 0o644); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(filepath.Join(cardDir, "mem_info_vram_used"), []byte("4096000000\n"), 0o644); err != nil {
		t.Fatal(err)
	}

	// Save and restore original vars
	origGlob := filepathGlob
	origReadFile := readFile
	defer func() {
		filepathGlob = origGlob
		readFile = origReadFile
	}()

	filepathGlob = func(pattern string) ([]string, error) {
		return []string{filepath.Join(tmpDir, "card0")}, nil
	}
	readFile = func(name string) ([]byte, error) {
		return os.ReadFile(name)
	}

	result := collectAMDSysfsMetrics(nil)
	if len(result) != 1 {
		t.Fatalf("expected 1 GPU, got %d", len(result))
	}
	gpu := result[0]
	if gpu.CorePercent != 75 {
		t.Errorf("CorePercent = %f, want 75", gpu.CorePercent)
	}
	// vram_total = 16384000000 bytes = 15625 MB, vram_used = 4096000000 bytes = 3906 MB (truncated)
	if gpu.MemoryTotalMb != 15625 {
		t.Errorf("MemoryTotalMb = %d, want 15625", gpu.MemoryTotalMb)
	}
	if gpu.MemoryUsedMb != 3906 {
		t.Errorf("MemoryUsedMb = %d, want 3906", gpu.MemoryUsedMb)
	}
}
