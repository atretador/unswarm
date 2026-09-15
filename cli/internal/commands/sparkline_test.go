package commands

import (
	"strings"
	"testing"
)

func TestSparkLine(t *testing.T) {
	tests := []struct {
		name     string
		values   []float64
		width    int
		expected int // expected string length (in runes)
	}{
		{
			name:     "basic",
			values:   []float64{1, 2, 3, 4, 5, 4, 3, 2, 1},
			width:    10,
			expected: 10,
		},
		{
			name:     "default width",
			values:   []float64{1, 2, 3},
			width:    0,
			expected: 20,
		},
		{
			name:     "two values",
			values:   []float64{0, 100},
			width:    5,
			expected: 5,
		},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			result := SparkLine(tt.values, tt.width)
			// Count runes
			runeCount := 0
			for range result {
				runeCount++
			}
			if runeCount != tt.expected {
				t.Errorf("expected %d runes, got %d: %q", tt.expected, runeCount, result)
			}
			// Verify all characters are valid spark chars
			for _, r := range result {
				if !strings.ContainsRune(sparkChars, r) {
					t.Errorf("unexpected character %c in sparkline", r)
				}
			}
		})
	}
}

func TestSparkLineAllZeros(t *testing.T) {
	result := SparkLine([]float64{0, 0, 0, 0, 0}, 5)
	runeCount := 0
	for range result {
		runeCount++
	}
	if runeCount != 5 {
		t.Errorf("expected 5 runes, got %d: %q", runeCount, result)
	}
	// All zeros should produce ▁ (lowest char) repeated
	for _, r := range result {
		if r != '▁' {
			t.Errorf("expected ▁ for zero values, got %c", r)
		}
	}
}

func TestSparkLineSingleValue(t *testing.T) {
	result := SparkLine([]float64{42.0}, 5)
	runeCount := 0
	for range result {
		runeCount++
	}
	if runeCount != 5 {
		t.Errorf("expected 5 runes, got %d: %q", runeCount, result)
	}
	// Single value should produce █ (highest char) repeated
	for _, r := range result {
		if r != '█' {
			t.Errorf("expected █ for single value, got %c", r)
		}
	}
}

func TestSparkLineEmpty(t *testing.T) {
	result := SparkLine(nil, 10)
	if result != "" {
		t.Errorf("expected empty string for nil values, got %q", result)
	}
}

func TestSparkLineIncreasing(t *testing.T) {
	result := SparkLine([]float64{1, 2, 3, 4, 5, 6, 7, 8}, 8)
	runeCount := 0
	for range result {
		runeCount++
	}
	if runeCount != 8 {
		t.Errorf("expected 8 runes, got %d", runeCount)
	}
	// First char should be ▁, last should be █
	runes := []rune(result)
	if runes[0] != '▁' {
		t.Errorf("expected first char ▁, got %c", runes[0])
	}
	if runes[7] != '█' {
		t.Errorf("expected last char █, got %c", runes[7])
	}
}

func TestSparkLineInt(t *testing.T) {
	result := SparkLineInt([]int64{10, 20, 30, 20, 10}, 5)
	runeCount := 0
	for range result {
		runeCount++
	}
	if runeCount != 5 {
		t.Errorf("expected 5 runes, got %d", runeCount)
	}
	for _, r := range result {
		if !strings.ContainsRune(sparkChars, r) {
			t.Errorf("unexpected character %c", r)
		}
	}
}
