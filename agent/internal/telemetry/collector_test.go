package telemetry

import (
	"context"
	"log/slog"
	"runtime"
	"testing"

	"unswarm/agent/internal/protocol"
)

func discardLogger() *slog.Logger { return slog.New(slog.DiscardHandler) }

// ---------------------------------------------------------------------------
// Pure helpers (collector.go)
// ---------------------------------------------------------------------------

func TestExtractLinuxGpuName(t *testing.T) {
	tests := []struct {
		name string
		line string
		want string
	}{
		{
			name: "nvidia bracket-free",
			line: "01:00.0 VGA compatible controller: NVIDIA Corporation GeForce RTX 4090 (rev a1)",
			want: "GeForce RTX 4090",
		},
		{
			name: "amd with pci ids and slash",
			line: "05:00.0 VGA compatible controller [0300]: Advanced Micro Devices, Inc. [AMD/ATI] Vega 20 [Radeon Pro VII/Radeon Instinct MI50] [1002:66a1] (rev 06)",
			want: "Radeon Pro VII",
		},
		{
			name: "intel vendor prefix",
			line: "00:02.0 VGA compatible controller: Intel Corporation UHD Graphics 630 (rev 02)",
			want: "UHD Graphics 630",
		},
		{
			name: "3d controller",
			line: "01:00.0 3D controller: NVIDIA Corporation Tesla T4",
			want: "Tesla T4",
		},
		{
			name: "display controller",
			line: "00:01.0 Display controller: VMware SVGA II Adapter",
			want: "VMware SVGA II Adapter",
		},
		{
			name: "empty after keywords",
			line: "00:00.0 VGA compatible controller:",
			want: "",
		},
		{
			name: "no brackets keeps pure name",
			line: "01:00.0 VGA compatible controller: Acme Graphics",
			want: "Acme Graphics",
		},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			if got := extractLinuxGpuName(tt.line); got != tt.want {
				t.Errorf("extractLinuxGpuName(%q) = %q, want %q", tt.line, got, tt.want)
			}
		})
	}
}

func TestRegexpAllBrackets(t *testing.T) {
	tests := []struct {
		in   string
		want []string
	}{
		{"a [x] b [y]", []string{"x", "y"}},
		{"no brackets", nil},
		{"[only-open", nil},
		{"[]", []string{""}},
		{"nested [a [b]]", []string{"a [b"}},
	}
	for _, tt := range tests {
		got := regexpAllBrackets(tt.in)
		if len(got) != len(tt.want) {
			t.Errorf("regexpAllBrackets(%q) = %v, want %v", tt.in, got, tt.want)
			continue
		}
		for i := range got {
			if got[i] != tt.want[i] {
				t.Errorf("regexpAllBrackets(%q)[%d] = %q, want %q", tt.in, i, got[i], tt.want[i])
			}
		}
	}
}

func TestIsPCIIDBracket(t *testing.T) {
	tests := []struct {
		in   string
		want bool
	}{
		{"1002:66a1", true},
		{"0300", true},
		{"abcd", true},
		{"abc", false},        // too short
		{"Radeon Pro", false}, // spaces
		{"NVIDIA", false},     // non-hex letters
		{"vega", false},       // g is not hex
		{"", false},           // empty
		{"12345", true},       // all digits
	}
	for _, tt := range tests {
		if got := isPCIIDBracket(tt.in); got != tt.want {
			t.Errorf("isPCIIDBracket(%q) = %v, want %v", tt.in, got, tt.want)
		}
	}
}

func TestFormatGB(t *testing.T) {
	tests := []struct {
		mb   int64
		want string
	}{
		{0, "0 MB"},
		{512, "512 MB"},
		{1024, "1 GB"},
		{1536, "1.5 GB"},
		{2048, "2 GB"},
		{1025, "1 GB"},
		{24576, "24 GB"},
	}
	for _, tt := range tests {
		if got := formatGB(tt.mb); got != tt.want {
			t.Errorf("formatGB(%d) = %q, want %q", tt.mb, got, tt.want)
		}
	}
}

func TestSplitJSONObjects(t *testing.T) {
	got := splitJSONObjects(`[{"Name":"a"},{"Name":"b"}]`)
	if len(got) != 2 {
		t.Fatalf("splitJSONObjects returned %d objects, want 2: %v", len(got), got)
	}
	if got[0] != `{"Name":"a"}` || got[1] != `{"Name":"b"}` {
		t.Errorf("unexpected objects: %v", got)
	}
	if out := splitJSONObjects("no objects here"); len(out) != 0 {
		t.Errorf("expected no objects, got %v", out)
	}
}

func TestExtractJSONString(t *testing.T) {
	tests := []struct {
		obj  string
		key  string
		want string
	}{
		{`{"Name":"RTX 4090"}`, "Name", "RTX 4090"},
		{`{"Other":"x"}`, "Name", ""},
		{`{"Name":123}`, "Name", ""},
		{`{"Name":"unterminated`, "Name", ""},
		{`{"Name": "spaced"}`, "Name", "spaced"},
	}
	for _, tt := range tests {
		if got := extractJSONString(tt.obj, tt.key); got != tt.want {
			t.Errorf("extractJSONString(%q, %q) = %q, want %q", tt.obj, tt.key, got, tt.want)
		}
	}
}

func TestRunTool(t *testing.T) {
	ctx := context.Background()
	out, err := runTool(ctx, "echo", "  hello  ")
	if err != nil {
		t.Fatalf("runTool(echo) error: %v", err)
	}
	if out != "hello" {
		t.Errorf("runTool(echo) = %q, want %q (trimmed)", out, "hello")
	}
	if _, err := runTool(ctx, "unswarm-definitely-not-a-real-binary"); err == nil {
		t.Error("expected error for missing binary")
	}
}

func TestGetMemoryMb(t *testing.T) {
	if runtime.GOOS != "linux" {
		t.Skip("reads /proc/meminfo (linux only)")
	}
	// Regression guard: real /proc/meminfo right-aligns the value with spaces
	// ("MemTotal:       65662428 kB"); the parser must skip that whitespace and
	// return a nonzero MB value rather than 0.
	if got := getMemoryMb(); got <= 0 {
		t.Errorf("getMemoryMb() = %d, want > 0 on linux", got)
	}
}

func TestParseMemTotalMb(t *testing.T) {
	tests := []struct {
		name    string
		content string
		want    int64
	}{
		{
			name: "real-world multiple spaces",
			content: `MemTotal:       16384000 kB
MemFree:         8192000 kB
MemAvailable:   12288000 kB`,
			want: 16000, // 16384000 / 1024
		},
		{
			name:    "single space",
			content: "MemTotal: 2048 kB\n",
			want:    2,
		},
		{
			name:    "tab separated",
			content: "MemTotal:\t4096 kB\n",
			want:    4,
		},
		{
			name:    "missing field",
			content: "MemFree: 4096 kB\n",
			want:    0,
		},
		{
			name:    "empty content",
			content: "",
			want:    0,
		},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			if got := parseMemTotalMb(tt.content); got != tt.want {
				t.Errorf("parseMemTotalMb() = %d, want %d", got, tt.want)
			}
		})
	}
}

// ---------------------------------------------------------------------------
// Collector lifecycle
// ---------------------------------------------------------------------------

func TestNewCollectorAndHostname(t *testing.T) {
	c := New(discardLogger())
	if c == nil {
		t.Fatal("New returned nil")
	}
	if c.Hostname() == "" {
		t.Error("Hostname() should not be empty")
	}
}

func TestCollectorCollect(t *testing.T) {
	t.Setenv("DOCKER_HOST", "tcp://127.0.0.1:1")
	c := New(discardLogger())
	called := false
	payload := c.Collect(func(ctx context.Context) []protocol.ContainerTelemetry {
		called = true
		return []protocol.ContainerTelemetry{{ID: "abc", Name: "svc", Status: "running"}}
	})
	if !called {
		t.Error("containerStatusesFn was not invoked")
	}
	if payload.Hostname != c.Hostname() {
		t.Errorf("payload hostname %q != collector hostname %q", payload.Hostname, c.Hostname())
	}
	if payload.OsPlatform != runtime.GOOS {
		t.Errorf("OsPlatform = %q, want %q", payload.OsPlatform, runtime.GOOS)
	}
	if payload.CPUCores <= 0 {
		t.Errorf("CPUCores = %d, want > 0", payload.CPUCores)
	}
	if len(payload.Containers) != 1 {
		t.Fatalf("Containers len = %d, want 1", len(payload.Containers))
	}
}

// ---------------------------------------------------------------------------
// Host metrics
// ---------------------------------------------------------------------------

func TestCollectHostMetricsPlatformSwitch(t *testing.T) {
	if runtime.GOOS == "linux" {
		if got := collectHostMetrics(); got == nil {
			t.Error("collectHostMetrics() returned nil on linux")
		}
	}
	// The stubs are callable on every platform.
	if got := collectHostMetricsWindows(); got != nil {
		t.Errorf("collectHostMetricsWindows() = %v, want nil stub", got)
	}
	if got := collectHostMetricsDarwin(); got != nil {
		t.Errorf("collectHostMetricsDarwin() = %v, want nil stub", got)
	}
}

func TestParseMemInfoAndCPUPercent(t *testing.T) {
	if runtime.GOOS != "linux" {
		t.Skip("reads /proc (linux only)")
	}
	total, used, percent := parseMemInfo(context.Background())
	if total <= 0 {
		t.Errorf("parseMemInfo total = %d, want > 0", total)
	}
	if used < 0 {
		t.Errorf("parseMemInfo used = %d, want >= 0", used)
	}
	if percent < 0 || percent > 100 {
		t.Errorf("parseMemInfo percent = %f, want 0..100", percent)
	}

	stat, err := readCPUStat(context.Background())
	if err != nil {
		t.Fatalf("readCPUStat: %v", err)
	}
	if stat.total == 0 {
		t.Error("readCPUStat total = 0")
	}

	if got := parseCPUPercent(context.Background()); got < 0 || got > 100 {
		t.Errorf("parseCPUPercent = %f, want 0..100", got)
	}
}

func TestParseCPUCalculateInvalidFieldSkipped(t *testing.T) {
	// A non-numeric field is skipped rather than failing the whole line.
	stat, err := parseCPUCalculate("cpu  100 xyz 500 8000 200 50 30 10")
	if err != nil {
		t.Fatalf("parseCPUCalculate error: %v", err)
	}
	if stat.idle != 8000 {
		t.Errorf("idle = %d, want 8000", stat.idle)
	}
}

func TestCPUErrorStrings(t *testing.T) {
	if errCPUNotFound.Error() == "" || errCPUInvalid.Error() == "" {
		t.Error("sentinel errors should have messages")
	}
}

// ---------------------------------------------------------------------------
// Container metrics
// ---------------------------------------------------------------------------

func TestParseDockerMemUsageError(t *testing.T) {
	used, total := parseDockerMemUsage("no-slash-here")
	if used != 0 || total != 0 {
		t.Errorf("parseDockerMemUsage(no-slash) = (%d, %d), want (0, 0)", used, total)
	}
}

func TestParseDockerMemSizeMoreUnits(t *testing.T) {
	tests := []struct {
		in   string
		want int64
	}{
		{"2048KiB", 2},
		{"5TiB", 5}, // unknown unit falls back to raw value
		{"512B", 0}, // bytes → MB truncates to 0
		{" 4096MiB ", 4096},
		{"abcMiB", 0}, // unparsable numeric prefix
	}
	for _, tt := range tests {
		if got := parseDockerMemSize(tt.in); got != tt.want {
			t.Errorf("parseDockerMemSize(%q) = %d, want %d", tt.in, got, tt.want)
		}
	}
}

func TestCollectContainerMetricsNoDocker(t *testing.T) {
	// Point the Docker CLI at an unreachable endpoint so it fails fast and
	// deterministically instead of waiting on a real daemon socket.
	t.Setenv("DOCKER_HOST", "tcp://127.0.0.1:1")
	// Best-effort: must not panic when docker is absent; result is nil or empty.
	_ = collectContainerMetrics(context.Background())
	_, _ = dockerStatsOutput(context.Background())
}

// ---------------------------------------------------------------------------
// GPU metrics collection entry points
// ---------------------------------------------------------------------------

func TestCollectGPUMetricsNoGPU(t *testing.T) {
	// No GPU tooling in CI: all collectors fail gracefully and the aggregate
	// returns nil. "No panic and a well-formed result" is the contract.
	_ = collectGPUMetrics(discardLogger())
	_ = collectNvidiaMetrics(context.Background())
	_ = collectAMDSmiMetrics(context.Background())
	_ = collectIntelMetrics(context.Background(), discardLogger())
	_ = collectAMDMetrics(context.Background(), discardLogger())
}

func TestDetectGPUEntryPoints(t *testing.T) {
	// detectGPU is platform-dispatched; on hosts without GPU tooling it returns
	// "". Just exercise the dispatch and per-platform entry points.
	_ = detectGPU()
	_ = detectGPULinux(context.Background())
	_ = detectGPUWindows(context.Background())
	_ = detectGPUMacOS(context.Background())
}
