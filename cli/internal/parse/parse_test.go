package parse

import (
	"testing"
	"time"
)

// ---------------------------------------------------------------------------
// ParseDuration
// ---------------------------------------------------------------------------

func TestParseDuration(t *testing.T) {
	tests := []struct {
		input string
		want  time.Duration
	}{
		{"5s", 5 * time.Second},
		{"5m", 5 * time.Minute},
		{"30m", 30 * time.Minute},
		{"2h", 2 * time.Hour},
		{"1h30m", 90 * time.Minute},
		{"2h30m", 150 * time.Minute},
		{"3d", 72 * time.Hour},
		{"1w", 168 * time.Hour},
		{"1w2d", 168*time.Hour + 48*time.Hour},
		{"1d12h", 36*time.Hour},
		{"1w1d1h1m1s", 168*time.Hour + 24*time.Hour + time.Hour + time.Minute + time.Second},
		{"10s", 10 * time.Second},
		{"0m", 0},
	}

	for _, tt := range tests {
		t.Run(tt.input, func(t *testing.T) {
			got, err := ParseDuration(tt.input)
			if err != nil {
				t.Fatalf("ParseDuration(%q) returned error: %v", tt.input, err)
			}
			if got != tt.want {
				t.Errorf("ParseDuration(%q) = %v, want %v", tt.input, got, tt.want)
			}
		})
	}
}

func TestParseDurationErrors(t *testing.T) {
	bad := []string{"", " ", "abc", "5", "m", "5x", "hh", "5mm", "2h3m4"}
	for _, s := range bad {
		t.Run(s, func(t *testing.T) {
			_, err := ParseDuration(s)
			if err == nil {
				t.Errorf("ParseDuration(%q) expected error, got nil", s)
			}
		})
	}
}

// ---------------------------------------------------------------------------
// RelativeTime
// ---------------------------------------------------------------------------

func TestRelativeTimeNow(t *testing.T) {
	before := time.Now()
	got, err := RelativeTime("now")
	after := time.Now()
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if got.Before(before.Add(-time.Second)) || got.After(after.Add(time.Second)) {
		t.Errorf("RelativeTime(\"now\") = %v, want ~%v", got, before)
	}
}

func TestRelativeTimeToday(t *testing.T) {
	now := time.Now()
	got, err := RelativeTime("today")
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	want := time.Date(now.Year(), now.Month(), now.Day(), 0, 0, 0, 0, now.Location())
	if !got.Equal(want) {
		t.Errorf("RelativeTime(\"today\") = %v, want %v", got, want)
	}
}

func TestRelativeTimeYesterday(t *testing.T) {
	now := time.Now()
	got, err := RelativeTime("yesterday")
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	want := time.Date(now.Year(), now.Month(), now.Day()-1, 0, 0, 0, 0, now.Location())
	if !got.Equal(want) {
		t.Errorf("RelativeTime(\"yesterday\") = %v, want %v", got, want)
	}
}

func TestRelativeTimeDurations(t *testing.T) {
	now := time.Now()
	tests := []struct {
		input   string
		approx  time.Duration
		tol     time.Duration
	}{
		{"5m", 5 * time.Minute, time.Second},
		{"30m", 30 * time.Minute, time.Second},
		{"2h", 2 * time.Hour, time.Second},
		{"1h30m", 90 * time.Minute, time.Second},
		{"3d", 72 * time.Hour, time.Second},
		{"1w", 168 * time.Hour, time.Second},
	}

	for _, tt := range tests {
		t.Run(tt.input, func(t *testing.T) {
			got, err := RelativeTime(tt.input)
			if err != nil {
				t.Fatalf("RelativeTime(%q) error: %v", tt.input, err)
			}
			diff := now.Sub(got)
			if diff < tt.approx-tt.tol || diff > tt.approx+tt.tol {
				t.Errorf("RelativeTime(%q) = %v, want ~%v ago (diff=%v)", tt.input, got, now.Add(-tt.approx), diff)
			}
		})
	}
}

func TestRelativeTimeISO8601(t *testing.T) {
	tests := []struct {
		input string
		want  time.Time
	}{
		{"2026-01-15", time.Date(2026, 1, 15, 0, 0, 0, 0, time.UTC)},
		{"2026-01-15T10:30:00Z", time.Date(2026, 1, 15, 10, 30, 0, 0, time.UTC)},
	}

	for _, tt := range tests {
		t.Run(tt.input, func(t *testing.T) {
			got, err := RelativeTime(tt.input)
			if err != nil {
				t.Fatalf("RelativeTime(%q) error: %v", tt.input, err)
			}
			if !got.Equal(tt.want) {
				t.Errorf("RelativeTime(%q) = %v, want %v", tt.input, got, tt.want)
			}
		})
	}
}

func TestRelativeTimeISO8601WithOffset(t *testing.T) {
	got, err := RelativeTime("2026-01-15T10:30:00+11:00")
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	// 10:30+11:00 == 23:30 UTC (previous day)
	wantUTC := time.Date(2026, 1, 14, 23, 30, 0, 0, time.UTC)
	if !got.UTC().Equal(wantUTC) {
		t.Errorf("got %v (UTC: %v), want %v", got, got.UTC(), wantUTC)
	}
}

func TestRelativeTimeEmpty(t *testing.T) {
	_, err := RelativeTime("")
	if err == nil {
		t.Error("RelativeTime(\"\") expected error")
	}
	_, err = RelativeTime("  ")
	if err == nil {
		t.Error("RelativeTime(\"  \") expected error")
	}
}

func TestRelativeTimeInvalid(t *testing.T) {
	bad := []string{"abc", "5x", "foo2d"}
	for _, s := range bad {
		_, err := RelativeTime(s)
		if err == nil {
			t.Errorf("RelativeTime(%q) expected error", s)
		}
	}
}

// ---------------------------------------------------------------------------
// FormatDuration
// ---------------------------------------------------------------------------

func TestFormatDuration(t *testing.T) {
	tests := []struct {
		input time.Duration
		want  string
	}{
		{0, "just now"},
		{30 * time.Second, "just now"},
		{59 * time.Second, "just now"},
		{time.Minute, "1m ago"},
		{5 * time.Minute, "5m ago"},
		{59 * time.Minute, "59m ago"},
		{time.Hour, "1h ago"},
		{2 * time.Hour, "2h ago"},
		{90 * time.Minute, "1h30m ago"},
		{23*time.Hour + 59*time.Minute, "23h59m ago"},
		{24 * time.Hour, "yesterday"},
		{47 * time.Hour, "yesterday"},
		{48 * time.Hour, "2d ago"},
		{72 * time.Hour, "3d ago"},
		{13 * 24 * time.Hour, "13d ago"},
	}

	for _, tt := range tests {
		t.Run(tt.want, func(t *testing.T) {
			got := FormatDuration(tt.input)
			if got != tt.want {
				t.Errorf("FormatDuration(%v) = %q, want %q", tt.input, got, tt.want)
			}
		})
	}
}

func TestFormatDurationNegative(t *testing.T) {
	// Negative durations should be treated as positive (absolute).
	got := FormatDuration(-5 * time.Minute)
	if got != "5m ago" {
		t.Errorf("FormatDuration(-5m) = %q, want %q", got, "5m ago")
	}
}

// ---------------------------------------------------------------------------
// FormatRelative
// ---------------------------------------------------------------------------

func TestFormatRelative(t *testing.T) {
	now := time.Now()

	tests := []struct {
		input time.Time
		want  string
	}{
		{now, "just now"},
		{now.Add(-30 * time.Second), "just now"},
		{now.Add(-5 * time.Minute), "5m ago"},
		{now.Add(-2 * time.Hour), "2h ago"},
		{now.Add(-1 * time.Hour - 30*time.Minute), "1h30m ago"},
		{now.Add(-25 * time.Hour), "yesterday"},
		{now.Add(-3 * 24 * time.Hour), "3d ago"},
	}

	for _, tt := range tests {
		t.Run(tt.want, func(t *testing.T) {
			got := FormatRelative(tt.input)
			if got != tt.want {
				t.Errorf("FormatRelative(%v) = %q, want %q", tt.input, got, tt.want)
			}
		})
	}
}

// ---------------------------------------------------------------------------
// Round-trip: parse then format
// ---------------------------------------------------------------------------

func TestRoundTrip(t *testing.T) {
	inputs := []struct {
		parse  string
		format string
	}{
		{"5m", "5m ago"},
		{"2h", "2h ago"},
		{"1h30m", "1h30m ago"},
		{"3d", "3d ago"},
		{"1w", "7d ago"},
		{"now", "just now"},
	}

	for _, tt := range inputs {
		t.Run(tt.parse, func(t *testing.T) {
			parsed, err := RelativeTime(tt.parse)
			if err != nil {
				t.Fatalf("RelativeTime(%q) error: %v", tt.parse, err)
			}
			got := FormatRelative(parsed)
			if got != tt.format {
				t.Errorf("round-trip: RelativeTime(%q) → FormatRelative → %q, want %q", tt.parse, got, tt.format)
			}
		})
	}
}

// ---------------------------------------------------------------------------
// Edge cases
// ---------------------------------------------------------------------------

func TestParseDurationZero(t *testing.T) {
	d, err := ParseDuration("0m")
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if d != 0 {
		t.Errorf("ParseDuration(\"0m\") = %v, want 0", d)
	}
}

func TestParseDurationLeadingZeros(t *testing.T) {
	d, err := ParseDuration("005m")
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if d != 5*time.Minute {
		t.Errorf("ParseDuration(\"005m\") = %v, want 5m", d)
	}
}

func TestFormatDurationJustNowBoundary(t *testing.T) {
	// Exactly 1 minute should be "1m ago", not "just now".
	got := FormatDuration(time.Minute)
	if got != "1m ago" {
		t.Errorf("FormatDuration(1m) = %q, want %q", got, "1m ago")
	}
}
