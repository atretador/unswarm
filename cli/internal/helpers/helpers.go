package helpers

import (
	"fmt"
	"math"
)

// StrOrDash returns the string value of a key from a map, or "-" if missing/empty.
func StrOrDash(m map[string]any, key string) string {
	v, ok := m[key]
	if !ok || v == nil {
		return "-"
	}
	s, ok := v.(string)
	if !ok {
		return fmt.Sprintf("%v", v)
	}
	if s == "" {
		return "-"
	}
	return s
}

// FloatOrDash returns a formatted float from a map, or "-" if missing.
// Format: 2 decimal places for normal numbers, scientific notation for large/small.
func FloatOrDash(m map[string]any, key string) string {
	v, ok := m[key]
	if !ok || v == nil {
		return "-"
	}
	switch val := v.(type) {
	case float64:
		if math.IsNaN(val) || math.IsInf(val, 0) {
			return "-"
		}
		abs := math.Abs(val)
		if abs != 0 && (abs >= 1e6 || abs < 1e-4) {
			return fmt.Sprintf("%.2e", val)
		}
		return fmt.Sprintf("%.2f", val)
	case int:
		return fmt.Sprintf("%d", val)
	default:
		return fmt.Sprintf("%v", val)
	}
}

// IntOrDash returns an integer from a map, or "-" if missing.
func IntOrDash(m map[string]any, key string) string {
	v, ok := m[key]
	if !ok || v == nil {
		return "-"
	}
	switch val := v.(type) {
	case float64:
		return fmt.Sprintf("%d", int(val))
	case int:
		return fmt.Sprintf("%d", val)
	default:
		return fmt.Sprintf("%v", val)
	}
}

// IntStr converts various numeric types (float64, int, int64) to string.
// Returns "-" for nil or zero values.
func IntStr(v any) string {
	if v == nil {
		return "-"
	}
	switch val := v.(type) {
	case float64:
		if val == 0 {
			return "-"
		}
		return fmt.Sprintf("%g", val)
	case int:
		if val == 0 {
			return "-"
		}
		return fmt.Sprintf("%d", val)
	case int64:
		if val == 0 {
			return "-"
		}
		return fmt.Sprintf("%d", val)
	default:
		return fmt.Sprintf("%v", val)
	}
}

// BoolStr converts a boolean to "yes"/"no" string.
func BoolStr(v any) string {
	switch val := v.(type) {
	case bool:
		if val {
			return "yes"
		}
		return "no"
	default:
		return fmt.Sprintf("%v", v)
	}
}

// FirstN returns the first n characters of a string, with "..." suffix if truncated.
func FirstN(s string, n int) string {
	if len(s) <= n {
		return s
	}
	return s[:n] + "..."
}

// Contains checks if a string is in a slice.
func Contains(slice []string, item string) bool {
	for _, s := range slice {
		if s == item {
			return true
		}
	}
	return false
}
