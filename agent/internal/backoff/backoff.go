// Package backoff implements exponential backoff with jitter for reconnection.
package backoff

import (
	"math"
	"math/rand"
	"time"
)

// Calculator computes exponential backoff durations.
type Calculator struct {
	initial time.Duration
	max     time.Duration
	attempt int
}

// New creates a Calculator with the given bounds.
func New(initial, max time.Duration) *Calculator {
	return &Calculator{
		initial: initial,
		max:     max,
	}
}

// Next returns the next backoff duration and increments the attempt counter.
func (c *Calculator) Next() time.Duration {
	base := c.delayFor(c.attempt)
	// Add jitter: ±25%
	jitter := float64(base) * 0.25 * (rand.Float64()*2 - 1) //nolint:gosec
	d := base + time.Duration(jitter)
	if d < 0 {
		d = 0
	}
	if d > c.max {
		d = c.max
	}
	c.attempt++
	return d
}

// delayFor returns the deterministic exponential backoff for a given attempt
// number: initial * 2^attempt, capped at max. Exposed for testing.
func (c *Calculator) delayFor(attempt int) time.Duration {
	backoff := float64(c.initial) * math.Pow(2, float64(attempt))
	if backoff > float64(c.max) {
		backoff = float64(c.max)
	}
	return time.Duration(backoff)
}

// Reset resets the attempt counter (call after successful connection).
func (c *Calculator) Reset() {
	c.attempt = 0
}

// Attempt returns the current attempt number (0-based).
func (c *Calculator) Attempt() int {
	return c.attempt
}
