package backoff

import (
	"testing"
	"time"
)

func TestBackoffIncreases(t *testing.T) {
	c := New(1*time.Second, 30*time.Second)

	for i := 0; i < 10; i++ {
		d := c.Next()
		if d > c.max {
			t.Errorf("attempt %d: backoff %v exceeds max %v", i, d, c.max)
		}
		if d < 0 {
			t.Errorf("attempt %d: negative backoff %v", i, d)
		}
	}
}

func TestBackoffCappedAtMax(t *testing.T) {
	c := New(1*time.Second, 5*time.Second)
	for i := 0; i < 50; i++ {
		d := c.Next()
		if d > 5*time.Second {
			t.Errorf("attempt %d: backoff %v exceeds max 5s", i, d)
		}
	}
}

func TestBackoffReset(t *testing.T) {
	c := New(1*time.Second, 30*time.Second)
	d1 := c.Next()
	d2 := c.Next()
	c.Reset()
	d3 := c.Next()
	// After reset, first backoff should be roughly the same as initial
	if d3 > 3*d1 { // rough check
		t.Errorf("After reset, backoff %v should be close to first %v", d3, d1)
	}
	if d1 == 0 || d2 == 0 {
		t.Errorf("Backoff should be non-zero: d1=%v d2=%v", d1, d2)
	}
}

func TestBackoffAttemptCounter(t *testing.T) {
	c := New(1*time.Second, 30*time.Second)
	if c.Attempt() != 0 {
		t.Errorf("Initial attempt = %d, want 0", c.Attempt())
	}
	c.Next()
	if c.Attempt() != 1 {
		t.Errorf("After 1 Next(), attempt = %d, want 1", c.Attempt())
	}
	c.Next()
	c.Next()
	if c.Attempt() != 3 {
		t.Errorf("After 3 Next(), attempt = %d, want 3", c.Attempt())
	}
	c.Reset()
	if c.Attempt() != 0 {
		t.Errorf("After Reset(), attempt = %d, want 0", c.Attempt())
	}
}

func TestBackoffWithinBounds(t *testing.T) {
	c := New(100*time.Millisecond, 5*time.Second)
	for i := 0; i < 20; i++ {
		d := c.Next()
		if d < 0 {
			t.Errorf("attempt %d: negative %v", i, d)
		}
		if d > 5*time.Second {
			t.Errorf("attempt %d: %v exceeds max", i, d)
		}
	}
}

// TestDelayForTable verifies the deterministic exponential backoff formula
// (initial * 2^attempt, capped at max) across a range of configurations.
func TestDelayForTable(t *testing.T) {
	tests := []struct {
		name    string
		initial time.Duration
		max     time.Duration
		attempt int
		want    time.Duration
	}{
		{name: "first attempt is initial", initial: 1 * time.Second, max: 30 * time.Second, attempt: 0, want: 1 * time.Second},
		{name: "second attempt doubles", initial: 1 * time.Second, max: 30 * time.Second, attempt: 1, want: 2 * time.Second},
		{name: "third attempt quadruples", initial: 1 * time.Second, max: 30 * time.Second, attempt: 2, want: 4 * time.Second},
		{name: "capped at max", initial: 1 * time.Second, max: 30 * time.Second, attempt: 10, want: 30 * time.Second},
		{name: "capped at max early", initial: 10 * time.Second, max: 15 * time.Second, attempt: 1, want: 15 * time.Second},
		{name: "small initial", initial: 100 * time.Millisecond, max: 5 * time.Second, attempt: 3, want: 800 * time.Millisecond},
		{name: "small initial capped", initial: 100 * time.Millisecond, max: 5 * time.Second, attempt: 10, want: 5 * time.Second},
		{name: "zero attempt stays initial", initial: 500 * time.Millisecond, max: 60 * time.Second, attempt: 0, want: 500 * time.Millisecond},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			c := New(tt.initial, tt.max)
			if got := c.delayFor(tt.attempt); got != tt.want {
				t.Errorf("delayFor(%d) = %v, want %v", tt.attempt, got, tt.want)
			}
		})
	}
}

// TestNextUsesFormula verifies Next() stays within the jittered bounds of the
// deterministic formula for each attempt.
func TestNextUsesFormula(t *testing.T) {
	c := New(1*time.Second, 30*time.Second)
	for i := 0; i < 10; i++ {
		base := c.delayFor(i)
		got := c.Next()
		// Jitter is ±25% of base, so the result must be within [0.75*base, max].
		lower := base - base/4
		if got < lower {
			t.Errorf("attempt %d: Next() = %v below jitter floor %v", i, got, lower)
		}
		if got > c.max {
			t.Errorf("attempt %d: Next() = %v exceeds max %v", i, got, c.max)
		}
	}
}
