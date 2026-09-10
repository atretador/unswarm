package telemetry

import (
	"testing"

	"unswarm/agent/internal/protocol"
)

func TestParseDockerStatsLine(t *testing.T) {
	tests := []struct {
		name    string
		line    string
		wantNil bool
		want    protocol.ContainerMetrics
	}{
		{
			name: "valid line",
			line: "abc123def456\t45.23%\t33.00%\t2112MiB / 6400MiB",
			want: protocol.ContainerMetrics{
				ContainerId: "abc123def456",
				CpuPercent:  45.23,
				RamPercent:  33.00,
				RamUsedMb:   2112,
				RamTotalMb:  6400,
			},
		},
		{
			name: "GiB units",
			line: "xyz789\t10.50%\t50.00%\t8GiB / 16GiB",
			want: protocol.ContainerMetrics{
				ContainerId: "xyz789",
				CpuPercent:  10.50,
				RamPercent:  50.00,
				RamUsedMb:   8192,
				RamTotalMb:  16384,
			},
		},
		{
			name: "kB units",
			line: "short\t1.00%\t2.00%\t512000kB / 1024000kB",
			want: protocol.ContainerMetrics{
				ContainerId: "short",
				CpuPercent:  1.00,
				RamPercent:  2.00,
				RamUsedMb:   500,
				RamTotalMb:  1000,
			},
		},
		{
			name: "zero usage",
			line: "container123\t0.00%\t0.00%\t0B / 6400MiB",
			want: protocol.ContainerMetrics{
				ContainerId: "container123",
				CpuPercent:  0,
				RamPercent:  0,
				RamUsedMb:   0,
				RamTotalMb:  6400,
			},
		},
		{
			name:    "too few fields",
			line:    "abc123\t45.23%\t33.00%",
			wantNil: true,
		},
		{
			name:    "empty container ID",
			line:    "\t45.23%\t33.00%\t2112MiB / 6400MiB",
			wantNil: true,
		},
		{
			name:    "empty line",
			line:    "",
			wantNil: true,
		},
	}

	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			got := parseDockerStatsLine(tt.line)
			if tt.wantNil {
				if got != nil {
					t.Fatalf("expected nil, got %+v", got)
				}
				return
			}
			if got == nil {
				t.Fatal("expected non-nil result")
			}
			if got.ContainerId != tt.want.ContainerId {
				t.Errorf("ContainerId = %q, want %q", got.ContainerId, tt.want.ContainerId)
			}
			if got.CpuPercent != tt.want.CpuPercent {
				t.Errorf("CpuPercent = %f, want %f", got.CpuPercent, tt.want.CpuPercent)
			}
			if got.RamPercent != tt.want.RamPercent {
				t.Errorf("RamPercent = %f, want %f", got.RamPercent, tt.want.RamPercent)
			}
			if got.RamUsedMb != tt.want.RamUsedMb {
				t.Errorf("RamUsedMb = %d, want %d", got.RamUsedMb, tt.want.RamUsedMb)
			}
			if got.RamTotalMb != tt.want.RamTotalMb {
				t.Errorf("RamTotalMb = %d, want %d", got.RamTotalMb, tt.want.RamTotalMb)
			}
		})
	}
}

func TestParseDockerStatsOutput(t *testing.T) {
	output := "abc123def456\t45.23%\t33.00%\t2112MiB / 6400MiB\nxyz789\t10.50%\t50.00%\t8GiB / 16GiB"
	result := parseDockerStatsOutput(output)
	if len(result) != 2 {
		t.Fatalf("expected 2 entries, got %d", len(result))
	}
	if _, ok := result["abc123def456"]; !ok {
		t.Error("missing container abc123def456")
	}
	if _, ok := result["xyz789"]; !ok {
		t.Error("missing container xyz789")
	}
}

func TestParseDockerStatsOutputEmpty(t *testing.T) {
	result := parseDockerStatsOutput("")
	if len(result) != 0 {
		t.Errorf("expected empty map, got %d entries", len(result))
	}
}

func TestParseDockerMemSize(t *testing.T) {
	tests := []struct {
		input string
		want  int64
	}{
		{"2112MiB", 2112},
		{"8GiB", 8192},
		{"512000kB", 500},
		{"1.5GiB", 1536},
		{"0B", 0},
		{"", 0},
		{"1024MB", 1024},
	}
	for _, tt := range tests {
		t.Run(tt.input, func(t *testing.T) {
			got := parseDockerMemSize(tt.input)
			if got != tt.want {
				t.Errorf("parseDockerMemSize(%q) = %d, want %d", tt.input, got, tt.want)
			}
		})
	}
}

func TestParseDockerPercent(t *testing.T) {
	tests := []struct {
		input string
		want  float64
	}{
		{"45.23%", 45.23},
		{"0%", 0},
		{"100%", 100},
		{"  50.5%  ", 50.5},
		{"", 0},
	}
	for _, tt := range tests {
		t.Run(tt.input, func(t *testing.T) {
			got := parseDockerPercent(tt.input)
			if got != tt.want {
				t.Errorf("parseDockerPercent(%q) = %f, want %f", tt.input, got, tt.want)
			}
		})
	}
}
