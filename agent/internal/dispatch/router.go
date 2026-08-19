package dispatch

import (
	"fmt"

	"unswarm/agent/internal/protocol"
)

// MessageHandler processes a non-command envelope (e.g. a future inference
// request proxied over the WebSocket) and returns a response envelope, or
// nil if no response is required.
type MessageHandler func(env protocol.Envelope) *protocol.Envelope

// Router is the pluggable extension point for message types that are not
// commands. Phase 4+ will register inference message types here (e.g.
// "inference_request") so the agent can proxy them to a local model server.
type Router struct {
	handlers map[string]MessageHandler
}

// NewRouter creates an empty message Router.
func NewRouter() *Router {
	return &Router{handlers: make(map[string]MessageHandler)}
}

// RegisterMessage maps a message type to a handler. Panics on duplicates.
func (r *Router) RegisterMessage(msgType string, handler MessageHandler) {
	if _, exists := r.handlers[msgType]; exists {
		panic(fmt.Sprintf("duplicate message handler registration for type: %s", msgType))
	}
	r.handlers[msgType] = handler
}

// Route dispatches a non-command envelope to its registered handler.
// Returns (nil, false) when no handler is registered for the type.
func (r *Router) Route(env protocol.Envelope) (*protocol.Envelope, bool) {
	handler, ok := r.handlers[env.Type]
	if !ok {
		return nil, false
	}
	return handler(env), true
}

// HasMessage returns true if a handler is registered for the message type.
func (r *Router) HasMessage(msgType string) bool {
	_, ok := r.handlers[msgType]
	return ok
}
