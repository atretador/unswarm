package client

import "errors"

// ErrUnsafeHTTP is returned when plaintext HTTP is used for non-loopback hosts
var ErrUnsafeHTTP = errors.New("refusing plaintext HTTP to non-loopback host")

// TransportError represents a transport-level error
type TransportError struct {
	Err error
}

func (e *TransportError) Error() string {
	return e.Err.Error()
}

func (e *TransportError) Unwrap() error {
	return e.Err
}
