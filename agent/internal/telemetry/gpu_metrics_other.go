//go:build !linux

package telemetry

import (
	"errors"
)

// filepathGlob returns empty on non-Linux (sysfs not available).
var filepathGlob = func(pattern string) ([]string, error) {
	return nil, errors.New("sysfs not available on this platform")
}

// readFile returns empty on non-Linux.
var readFile = func(name string) ([]byte, error) {
	return nil, errors.New("sysfs not available on this platform")
}
