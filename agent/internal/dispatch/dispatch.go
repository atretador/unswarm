// Package dispatch routes incoming commands to the appropriate handler function.
package dispatch

import (
	"context"
	"fmt"
	"unswarm/agent/internal/protocol"
)

// Handler is a function that processes a command and returns a result.
type Handler func(payload protocol.CommandPayload) protocol.CommandResultPayload

// ContextHandler is a handler that receives the session/connection context so
// long-running commands (e.g. chat_completion) can abort when the backend
// disconnects or cancels.
type ContextHandler func(ctx context.Context, payload protocol.CommandPayload) protocol.CommandResultPayload

// StreamContextHandler is a handler that streams its response incrementally.
// It receives the session context (so backend disconnects/cancels abort
// in-flight work) and an emit callback: each call delivers one raw chunk of
// the response body. The caller turns each emitted chunk into a command_chunk
// envelope and, after the handler returns, sends exactly one final
// command_result (ok:true on nil error, ok:false with the error otherwise).
type StreamContextHandler func(ctx context.Context, payload protocol.CommandPayload, emit func(chunk []byte) error) error

// Dispatcher maps command names to handler functions.
type Dispatcher struct {
	handlers        map[string]Handler
	contextHandlers map[string]ContextHandler
	streamHandlers  map[string]StreamContextHandler
}

// New creates a Dispatcher.
func New() *Dispatcher {
	return &Dispatcher{
		handlers:        make(map[string]Handler),
		contextHandlers: make(map[string]ContextHandler),
		streamHandlers:  make(map[string]StreamContextHandler),
	}
}

// Register maps a command name to a handler. Panics on duplicate.
func (d *Dispatcher) Register(command string, handler Handler) {
	if _, exists := d.handlers[command]; exists {
		panic(fmt.Sprintf("duplicate handler registration for command: %s", command))
	}
	d.handlers[command] = handler
}

// RegisterContext registers a command that receives the connection context. The
// handler can select on ctx.Done() so backend disconnects/cancels abort in-flight
// work (HTTP calls, long inference). DispatchContext routes the real context here.
func (d *Dispatcher) RegisterContext(command string, handler ContextHandler) {
	if _, exists := d.handlers[command]; exists {
		panic(fmt.Sprintf("duplicate handler registration for command: %s", command))
	}
	if _, exists := d.streamHandlers[command]; exists {
		panic(fmt.Sprintf("duplicate handler registration for command: %s", command))
	}
	d.contextHandlers[command] = handler
}

// RegisterStream registers a streaming command handler. The handler emits raw
// response chunks via the emit callback; the caller wraps each chunk in a
// command_chunk envelope and sends exactly one final command_result after the
// handler returns.
func (d *Dispatcher) RegisterStream(command string, handler StreamContextHandler) {
	if _, exists := d.handlers[command]; exists {
		panic(fmt.Sprintf("duplicate handler registration for command: %s", command))
	}
	if _, exists := d.contextHandlers[command]; exists {
		panic(fmt.Sprintf("duplicate handler registration for command: %s", command))
	}
	d.streamHandlers[command] = handler
}

// Dispatch routes a command payload to its registered handler.
// Returns an error result if the command is unknown.
func (d *Dispatcher) Dispatch(payload protocol.CommandPayload) protocol.CommandResultPayload {
	// Non-context dispatch: context-aware handlers get a detached context.
	if handler, ok := d.contextHandlers[payload.Command]; ok {
		return handler(context.Background(), payload)
	}
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

// DispatchContext routes a command payload with the connection context so
// context-aware handlers can cancel in-flight work. Handlers registered without
// context ignore it.
func (d *Dispatcher) DispatchContext(ctx context.Context, payload protocol.CommandPayload) protocol.CommandResultPayload {
	if handler, ok := d.contextHandlers[payload.Command]; ok {
		return handler(ctx, payload)
	}
	return d.Dispatch(payload)
}

// HasStream reports whether a streaming handler is registered for the command.
func (d *Dispatcher) HasStream(command string) bool {
	_, ok := d.streamHandlers[command]
	return ok
}

// DispatchStream routes a command payload to its registered streaming handler,
// passing the connection context and the emit callback. It returns false if no
// stream handler is registered for the command (the caller should fall back to
// the regular dispatch path). The returned error is the handler's error; the
// caller is responsible for sending exactly one final command_result.
func (d *Dispatcher) DispatchStream(ctx context.Context, payload protocol.CommandPayload, emit func(chunk []byte) error) (bool, error) {
	handler, ok := d.streamHandlers[payload.Command]
	if !ok {
		return false, nil
	}
	return true, handler(ctx, payload, emit)
}

// HasCommand returns true if a handler is registered for the given command.
func (d *Dispatcher) HasCommand(command string) bool {
	if _, ok := d.handlers[command]; ok {
		return true
	}
	if _, ok := d.contextHandlers[command]; ok {
		return true
	}
	_, ok := d.streamHandlers[command]
	return ok
}

// Commands returns the list of registered command names.
func (d *Dispatcher) Commands() []string {
	cmds := make([]string, 0, len(d.handlers)+len(d.contextHandlers)+len(d.streamHandlers))
	for name := range d.handlers {
		cmds = append(cmds, name)
	}
	for name := range d.contextHandlers {
		cmds = append(cmds, name)
	}
	for name := range d.streamHandlers {
		cmds = append(cmds, name)
	}
	return cmds
}
