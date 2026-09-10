package telemetry

import (
	"testing"
)

func TestParseMemInfoContent(t *testing.T) {
	content := `MemTotal:       16384000 kB
MemFree:         8192000 kB
MemAvailable:   12288000 kB
Buffers:          512000 kB
Cached:          2048000 kB`

	totalMB, usedMB, percent := parseMemInfoFromContent(content)

	if totalMB != 16000 { // 16384000 / 1024
		t.Errorf("totalMB = %d, want 16000", totalMB)
	}
	// Used = (16384000 - 12288000) / 1024 = 4096000 / 1024 = 4000
	if usedMB != 4000 {
		t.Errorf("usedMB = %d, want 4000", usedMB)
	}
	// percent = (16384000 - 12288000) / 16384000 * 100 = 25.0
	if percent != 25.0 {
		t.Errorf("percent = %f, want 25.0", percent)
	}
}

func TestParseCPUCalculate(t *testing.T) {
	// Format: "cpu  user nice system idle iowait irq softirq steal"
	line := "cpu  1000 100 500 8000 200 50 30 10"
	stat, err := parseCPUCalculate(line)
	if err != nil {
		t.Fatal(err)
	}
	if stat.idle != 8000 {
		t.Errorf("idle = %d, want 8000", stat.idle)
	}
	// total = 1000 + 100 + 500 + 8000 + 200 + 50 + 30 + 10 = 9890
	if stat.total != 9890 {
		t.Errorf("total = %d, want 9890", stat.total)
	}
}

func TestParseCPUCalculateTooFewFields(t *testing.T) {
	line := "cpu  1000 100"
	_, err := parseCPUCalculate(line)
	if err == nil {
		t.Error("expected error for too few fields")
	}
}

func TestParseCPUStatLine(t *testing.T) {
	content := `cpu  1000 100 500 8000 200 50 30 10
cpu0  500 50 250 4000 100 25 15 5
cpu1  500 50 250 4000 100 25 15 5`
	stat, err := parseCPUStatLine([]byte(content))
	if err != nil {
		t.Fatal(err)
	}
	if stat.idle != 8000 {
		t.Errorf("idle = %d, want 8000", stat.idle)
	}
}

func TestParseCPUStatLineNoAggregate(t *testing.T) {
	content := `cpu0  500 50 250 4000 100 25 15 5`
	_, err := parseCPUStatLine([]byte(content))
	if err == nil {
		t.Error("expected error when no aggregate cpu line found")
	}
}

func TestParseKBField(t *testing.T) {
	tests := []struct {
		input string
		want  int64
	}{
		{"   16384000 kB", 16384000},
		{"0 kB", 0},
		{"  1234  ", 1234},
	}
	for _, tt := range tests {
		t.Run(tt.input, func(t *testing.T) {
			got := parseKBField(tt.input)
			if got != tt.want {
				t.Errorf("parseKBField(%q) = %d, want %d", tt.input, got, tt.want)
			}
		})
	}
}

// parseMemInfoFromContent is a test helper that parses meminfo content directly.
func parseMemInfoFromContent(content string) (totalMB, usedMB int64, percent float64) {
	var memTotal, memAvailable int64
	for _, line := range splitLines(content) {
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
