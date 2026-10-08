package telemetry

import (
	"context"
	"errors"
	"os"
	"path/filepath"
	"testing"
)

// ---------------------------------------------------------------------------
// AMD JSON parsing edge cases
// ---------------------------------------------------------------------------

func TestParseAmdSmiJSONActivityFallback(t *testing.T) {
	// No "gfx_activity" key: parser falls back to the generic "activity" key.
	got := parseAmdSmiJSON(`{"gpu":[{"activity": 42}]}`)
	if len(got) != 1 {
		t.Fatalf("got %d GPUs, want 1", len(got))
	}
	if got[0].CorePercent != 42 {
		t.Errorf("CorePercent = %f, want 42", got[0].CorePercent)
	}
	if got[0].MemoryPercent != -1 || got[0].MemoryTotalMb != -1 || got[0].MemoryUsedMb != -1 {
		t.Errorf("memory should be unavailable (-1), got %+v", got[0])
	}
}

func TestParseAmdSmiJSONMultipleGPUs(t *testing.T) {
	in := `[{"gfx_activity": 10, "vram_used": 1024, "vram_total": 2048},` +
		`{"gfx_activity": 90, "vram_used": 1024, "vram_total": 4096}]`
	got := parseAmdSmiJSON(in)
	if len(got) != 2 {
		t.Fatalf("got %d GPUs, want 2", len(got))
	}
	if got[0].MemoryPercent != 50 {
		t.Errorf("gpu0 mem percent = %f, want 50", got[0].MemoryPercent)
	}
}

func TestParseAmdSmiJSONNoColon(t *testing.T) {
	// A "gfx_activity" marker with no colon terminates parsing with no GPUs.
	if got := parseAmdSmiJSON(`{"gfx_activity" }`); len(got) != 0 {
		t.Errorf("expected no GPUs, got %v", got)
	}
}

func TestExtractAMDIntValue(t *testing.T) {
	if got := extractAMDIntValue(`"vram_total": 4096,`, 0); got != 4096 {
		t.Errorf("extractAMDIntValue = %d, want 4096", got)
	}
	if got := extractAMDIntValue(`no colon here`, 0); got != -1 {
		t.Errorf("no-colon = %d, want -1", got)
	}
	if got := extractAMDIntValue(`"vram_total": notanumber`, 0); got != -1 {
		t.Errorf("invalid number = %d, want -1", got)
	}
}

// ---------------------------------------------------------------------------
// Intel JSON parsing
// ---------------------------------------------------------------------------

func TestParseIntelGpuTopJSON(t *testing.T) {
	in := `{"engines":{"Render/3D":{"busy": 12.5},"Blitter":{"busy": 7.5}}}`
	got := parseIntelGpuTopJSON(in)
	if len(got) != 1 {
		t.Fatalf("got %d GPUs, want 1", len(got))
	}
	if got[0].Vendor != "intel" {
		t.Errorf("Vendor = %q, want intel", got[0].Vendor)
	}
	if got[0].CorePercent != 10 {
		t.Errorf("CorePercent = %f, want 10 (average of 12.5 and 7.5)", got[0].CorePercent)
	}
	if got[0].MemoryPercent != -1 || got[0].MemoryUsedMb != -1 || got[0].MemoryTotalMb != -1 {
		t.Errorf("intel memory should be unavailable (-1), got %+v", got[0])
	}
}

func TestParseIntelGpuTopJSONEmptyAndInvalid(t *testing.T) {
	for _, in := range []string{"", "[]", "not json", `{"busy": notanumber}`} {
		if got := parseIntelGpuTopJSON(in); got != nil {
			t.Errorf("parseIntelGpuTopJSON(%q) = %v, want nil", in, got)
		}
	}
}

// ---------------------------------------------------------------------------
// sysfs-backed AMD metrics error branches
// ---------------------------------------------------------------------------

func withFakeReadFile(t *testing.T, glob func(string) ([]string, error), read func(string) ([]byte, error)) {
	t.Helper()
	origGlob, origRead := filepathGlob, readFile
	filepathGlob, readFile = glob, read
	t.Cleanup(func() { filepathGlob, readFile = origGlob, origRead })
}

func TestCollectAMDSysfsMetricsGlobError(t *testing.T) {
	withFakeReadFile(t,
		func(string) ([]string, error) { return nil, errors.New("glob failed") },
		func(string) ([]byte, error) { return nil, errors.New("read failed") },
	)
	if got := collectAMDSysfsMetrics(nil); len(got) != 0 {
		t.Errorf("expected no GPUs on glob error, got %v", got)
	}
}

func TestCollectAMDSysfsMetricsBusyReadErrorSkipsCard(t *testing.T) {
	withFakeReadFile(t,
		func(string) ([]string, error) { return []string{"/sys/card0"}, nil },
		func(string) ([]byte, error) { return nil, errors.New("no busy file") },
	)
	if got := collectAMDSysfsMetrics(nil); len(got) != 0 {
		t.Errorf("expected card with unreadable busy file to be skipped, got %v", got)
	}
}

func TestCollectAMDSysfsMetricsMissingVramFiles(t *testing.T) {
	device := filepath.Join(t.TempDir(), "device")
	if err := os.MkdirAll(device, 0o755); err != nil {
		t.Fatal(err)
	}
	busy := filepath.Join(device, "gpu_busy_percent")
	if err := os.WriteFile(busy, []byte("50\n"), 0o644); err != nil {
		t.Fatal(err)
	}
	card := filepath.Dir(device)
	withFakeReadFile(t,
		func(string) ([]string, error) { return []string{card}, nil },
		func(name string) ([]byte, error) {
			if name == busy {
				return os.ReadFile(name)
			}
			return nil, os.ErrNotExist
		},
	)
	got := collectAMDSysfsMetrics(nil)
	if len(got) != 1 {
		t.Fatalf("got %d GPUs, want 1", len(got))
	}
	if got[0].CorePercent != 50 {
		t.Errorf("CorePercent = %f, want 50", got[0].CorePercent)
	}
	if got[0].MemoryPercent != -1 {
		t.Errorf("MemoryPercent = %f, want -1 when vram files missing", got[0].MemoryPercent)
	}
}

// ---------------------------------------------------------------------------
// readFileString
// ---------------------------------------------------------------------------

func TestReadFileStringError(t *testing.T) {
	withFakeReadFile(t,
		func(string) ([]string, error) { return nil, nil },
		func(string) ([]byte, error) { return nil, errors.New("boom") },
	)
	if _, err := readFileString("/whatever"); err == nil {
		t.Fatal("expected error")
	}
}

func TestReadFileStringTrims(t *testing.T) {
	withFakeReadFile(t,
		func(string) ([]string, error) { return nil, nil },
		func(string) ([]byte, error) { return []byte("  hello \n"), nil },
	)
	got, err := readFileString("/whatever")
	if err != nil {
		t.Fatalf("readFileString: %v", err)
	}
	if got != "hello" {
		t.Errorf("readFileString = %q, want %q", got, "hello")
	}
}

// ---------------------------------------------------------------------------
// Misc collectors that shell out (guarded: no GPU present in CI)
// ---------------------------------------------------------------------------

func TestNvidiaSmiFallbackAndVram(t *testing.T) {
	// No nvidia-smi in CI: both return their zero value without panicking.
	_ = nvidiaSmiFallback(context.Background())
	_ = getLinuxGpuVram(context.Background(), 0)
}
