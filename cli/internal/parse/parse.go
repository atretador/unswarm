// Package parse provides relative time parsing utilities for the unswarm CLI.
package parse

import (
	"fmt"
	"regexp"
	"strconv"
	"strings"
	"time"
)

var (
	// durationRe matches a single compact duration component like "5m", "2h".
	// No $ anchor — we consume one component at a time in ParseDuration.
	durationRe = regexp.MustCompile(`^(\d+)([smhdw])`)

	// iso8601Re matches ISO 8601 date and datetime strings.
	iso8601Re = regexp.MustCompile(
		`^\d{4}-\d{2}-\d{2}` +
			`(T\d{2}:\d{2}:\d{2}` +
			`(\.\d+)?` +
			`(Z|[+-]\d{2}:\d{2})?)?$`,
	)
)

// ParseDuration parses a compact duration string.
// Supported units: s (seconds), m (minutes), h (hours), d (days=24h), w (weeks=168h).
// Multiple components can be combined, e.g. "2h30m".
func ParseDuration(s string) (time.Duration, error) {
	s = strings.TrimSpace(s)
	if s == "" {
		return 0, fmt.Errorf("empty duration string")
	}

	// Walk the string consuming <number><unit> pairs.
	remaining := s
	var total time.Duration

	for remaining != "" {
		m := durationRe.FindStringSubmatch(remaining)
		if m == nil {
			return 0, fmt.Errorf("invalid duration component in %q", s)
		}

		n, err := strconv.Atoi(m[1])
		if err != nil {
			return 0, fmt.Errorf("invalid number in %q: %w", s, err)
		}

		switch m[2] {
		case "s":
			total += time.Duration(n) * time.Second
		case "m":
			total += time.Duration(n) * time.Minute
		case "h":
			total += time.Duration(n) * time.Hour
		case "d":
			total += time.Duration(n) * 24 * time.Hour
		case "w":
			total += time.Duration(n) * 168 * time.Hour
		}

		remaining = remaining[len(m[0]):]
	}

	return total, nil
}

// RelativeTime parses a relative time string and returns the corresponding time.
//
// Supported formats:
//   - Relative durations: "5m", "2h30m", "3d", "1w" — time ago
//   - Keywords: "now", "today", "yesterday"
//   - ISO 8601 passthrough: "2026-01-15", "2026-01-15T10:30:00Z", "2026-01-15T10:30:00+11:00"
//   - Empty string returns an error
func RelativeTime(s string) (time.Time, error) {
	s = strings.TrimSpace(s)
	if s == "" {
		return time.Time{}, fmt.Errorf("empty time string")
	}

	now := time.Now()

	switch strings.ToLower(s) {
	case "now":
		return now, nil
	case "today":
		return time.Date(now.Year(), now.Month(), now.Day(), 0, 0, 0, 0, now.Location()), nil
	case "yesterday":
		return time.Date(now.Year(), now.Month(), now.Day()-1, 0, 0, 0, 0, now.Location()), nil
	}

	// Try ISO 8601 passthrough.
	if iso8601Re.MatchString(s) {
		formats := []string{
			time.RFC3339Nano,
			time.RFC3339,
			"2006-01-02T15:04:05.999",
			"2006-01-02T15:04:05",
			"2006-01-02",
		}
		for _, f := range formats {
			if t, err := time.Parse(f, s); err == nil {
				return t, nil
			}
		}
		return time.Time{}, fmt.Errorf("invalid ISO 8601 time %q", s)
	}

	// Try relative duration.
	d, err := ParseDuration(s)
	if err != nil {
		return time.Time{}, fmt.Errorf("invalid time %q: %w", s, err)
	}
	return now.Add(-d), nil
}

// FormatDuration formats a duration into a human-readable relative string.
//
//   - < 1m  → "just now"
//   - < 1h  → "5m ago"
//   - < 24h → "2h30m ago"
//   - < 48h → "yesterday"
//   - >= 48h → "3d ago"
func FormatDuration(d time.Duration) string {
	if d < 0 {
		d = -d
	}

	switch {
	case d < time.Minute:
		return "just now"
	case d < time.Hour:
		return fmt.Sprintf("%dm ago", int(d.Minutes()))
	case d < 24*time.Hour:
		h := int(d.Hours())
		m := int(d.Minutes()) % 60
		if m == 0 {
			return fmt.Sprintf("%dh ago", h)
		}
		return fmt.Sprintf("%dh%dm ago", h, m)
	case d < 48*time.Hour:
		return "yesterday"
	default:
		days := int(d.Hours() / 24)
		return fmt.Sprintf("%dd ago", days)
	}
}

// FormatRelative formats a time.Time as a human-readable relative string.
// It calls FormatDuration with time.Since(t).
func FormatRelative(t time.Time) string {
	return FormatDuration(time.Since(t))
}
