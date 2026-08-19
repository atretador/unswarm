// Package dispatch routes incoming commands to the appropriate handler function.
package dispatch

import (
	"fmt"
	"unswarm/agent/internal/protocol"
)

// Handler is a function that processes a command and returns a result.
type Handler func(payload protocol.CommandPayload) protocol.CommandResultPayload

// Dispatcher maps command names to handler functions.
type Dispatcher struct {
	handlers map[string]Handler
}

// New creates a Dispatcher.
func New() *Dispatcher {
	return &Dispatcher{
		handlers: make(map[string]Handler),
	}
}

// Register maps a command name to a handler. Panics on duplicate.
func (d *Dispatcher) Register(command string, handler Handler) {
	if _, exists := d.handlers[command]; exists {
		panic(fmt.Sprintf("duplicate handler registration for command: %s", command))
	}
	d.handlers[command] = handler
}

// Dispatch routes a command payload to its registered handler.
// Returns an error result if the command is unknown.
func (d *Dispatcher) Dispatch(payload protocol.CommandPayload) protocol.CommandResultPayload {
	handler, ok := d.handlers[payload.Command]
	if !ok {
		errMsg := fmt.Sprintf("unknown command: %s", payload.Command)
		return protocol.CommandResultPayload{
			OK:    false,
			Error: &errMsg,
		}
	}
	return handler(payload)
}

// HasCommand returns true if a handler is registered for the given command.
func (d *Dispatcher) HasCommand(command string) bool {
	_, ok := d.handlers[command]
	return ok
}

// Commands returns the list of registered command names.
func (d *Dispatcher) Commands() []string {
	cmds := make([]string, 0, len(d.handlers))
	for name := range d.handlers {
		cmds = append(cmds, name)
	}
	return cmds
}
