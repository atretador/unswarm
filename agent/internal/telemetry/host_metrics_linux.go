//go:build linux

package telemetry

import (
	"context"
	"os"
)

// readProcFile reads a file from /proc with the given context for timeout.
func readProcFile(_ context.Context, path string) ([]byte, error) {
	return os.ReadFile(path)
}
