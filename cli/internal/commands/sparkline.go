package commands

import (
	"math"
	"strings"
)

// sparkChars are the 8 Unicode block characters used for sparkline rendering,
// from lowest (▁ U+2581) to highest (█ U+2588).
const sparkChars = "▁▂▃▄▅▆▇█"

// SparkLine renders a Unicode sparkline from the given float64 values.
// Width controls the desired output width; 0 defaults to 20.
func SparkLine(values []float64, width int) string {
	if width <= 0 {
		width = 20
	}

	n := len(values)
	if n == 0 {
		return ""
	}

	// Single value → full block
	if n == 1 {
		return strings.Repeat("█", width)
	}

	// Find min and max
	min := values[0]
	max := values[0]
	for _, v := range values[1:] {
		if v < min {
			min = v
		}
		if v > max {
			max = v
		}
	}

	// All zeros (or all same value) → ▁ repeated
	if max-min < 1e-12 {
		var b strings.Builder
		b.Grow(width * 3)
		for i := 0; i < width; i++ {
			b.WriteString(sparkChars[0:3]) // ▁ is 3 bytes
		}
		return b.String()
	}

	// Map values to the width using linear interpolation
	var b strings.Builder
	b.Grow(width)
	range_ := max - min
	for i := 0; i < width; i++ {
		// Map position to the values array
		idx := float64(i) * float64(n-1) / float64(width-1)
		// Linear interpolation between adjacent values
		lo := int(idx)
		hi := lo + 1
		if hi >= n {
			hi = n - 1
		}
		frac := idx - float64(lo)
		v := values[lo]*(1-frac) + values[hi]*frac

		// Map to character index 0..7
		norm := (v - min) / range_
		charIdx := int(math.Round(norm * 7))
		if charIdx > 7 {
			charIdx = 7
		}
		if charIdx < 0 {
			charIdx = 0
		}
		b.WriteString(sparkChars[charIdx*3 : charIdx*3+3])
	}
	return b.String()
}

// SparkLineInt is a convenience wrapper that converts int64 values to float64
// and renders a sparkline.
func SparkLineInt(values []int64, width int) string {
	fv := make([]float64, len(values))
	for i, v := range values {
		fv[i] = float64(v)
	}
	return SparkLine(fv, width)
}
