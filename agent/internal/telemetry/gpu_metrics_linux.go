//go:build linux

package telemetry

import (
	"os"
	"path/filepath"
)

// filepathGlob wraps filepath.Glob for testability.
var filepathGlob = filepath.Glob

// readFile wraps os.ReadFile for testability.
var readFile = os.ReadFile
