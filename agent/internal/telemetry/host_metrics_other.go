//go:build !linux

package telemetry

import (
	"context"
	"errors"
)

// readProcFile returns an error on non-Linux platforms.
func readProcFile(_ context.Context, _ string) ([]byte, error) {
	return nil, errors.New("/proc not available on this platform")
}
