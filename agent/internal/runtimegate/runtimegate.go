// Package runtime maintains the agent's registered-runtime set (synced from
// the backend via sync_registrations) and gates container lifecycle commands
// against it. When enforcement is enabled, a lifecycle command whose target is
// not in the registered set is rejected with a command_result error BEFORE any
// Docker API call is made.
package runtimegate

import (
	"fmt"
	"strings"
	"sync"

	"unswarm/agent/internal/protocol"
)

// Registry holds the registered runtime mappings for this agent. Safe for
// concurrent use: the message loop applies sync_registrations snapshots while
// command goroutines look up targets.
type Registry struct {
	mu    sync.RWMutex
	byKey map[string]string // normalized container name or id -> registeredRuntimeId
}

// NewRegistry creates an empty Registry.
func NewRegistry() *Registry {
	return &Registry{byKey: make(map[string]string)}
}

// Replace atomically swaps the mapping set with a full snapshot from the
// backend. Empty/whitespace entries are skipped.
func (r *Registry) Replace(entries []protocol.RegistrationEntry) {
	next := make(map[string]string, len(entries)*2)
	for _, e := range entries {
		if strings.TrimSpace(e.RegisteredRuntimeID) == "" {
			continue
		}
		if name := strings.TrimSpace(e.ContainerName); name != "" {
			next[normalize(name)] = e.RegisteredRuntimeID
		}
		if id := strings.TrimSpace(e.ContainerID); id != "" {
			next[normalize(id)] = e.RegisteredRuntimeID
		}
	}

	r.mu.Lock()
	r.byKey = next
	r.mu.Unlock()
}

// Lookup resolves a container name or id to its registeredRuntimeId.
func (r *Registry) Lookup(nameOrID string) (string, bool) {
	key := normalize(nameOrID)
	if key == "" {
		return "", false
	}
	r.mu.RLock()
	defer r.mu.RUnlock()
	id, ok := r.byKey[key]
	return id, ok
}

// Contains reports whether the given container name or id is registered.
func (r *Registry) Contains(nameOrID string) bool {
	_, ok := r.Lookup(nameOrID)
	return ok
}

// Size returns the number of distinct registered keys (test/observability aid).
func (r *Registry) Size() int {
	r.mu.RLock()
	defer r.mu.RUnlock()
	return len(r.byKey)
}

// Gate enforces the registered-runtime set on container commands.
type Gate struct {
	registry *Registry
	enforce  bool
}

// NewGate creates a Gate. When enforce is false the gate allows everything
// (legacy behavior); registry may then be nil.
func NewGate(registry *Registry, enforce bool) *Gate {
	if registry == nil {
		registry = NewRegistry()
	}
	return &Gate{registry: registry, enforce: enforce}
}

// Enforce reports whether enforcement is enabled.
func (g *Gate) Enforce() bool { return g.enforce }

// Registry exposes the underlying registry (for sync handlers).
func (g *Gate) Registry() *Registry { return g.registry }

// Check decides whether a container lifecycle command may execute.
//
// Returns (result, true) when the command is BLOCKED — result is a failed
// CommandResultPayload carrying "container not registered". Returns
// (zero, false) when the command may proceed (enforcement off, non-container
// command, or registered target).
//
// Commands with no container target (list_containers, health_check,
// discover_models, chat_completion, script commands) always pass; list
// filtering is handled separately by FilterListResult.
func (g *Gate) Check(command, target string) (protocol.CommandResultPayload, bool) {
	if !isContainerLifecycleCommand(command) {
		return protocol.CommandResultPayload{}, false
	}
	if !g.enforce {
		return protocol.CommandResultPayload{}, false
	}
	if g.registry.Contains(target) {
		return protocol.CommandResultPayload{}, false
	}
	msg := fmt.Sprintf(
		"container not registered: %q is not in this agent's registered runtime set (%s rejected)",
		target, command)
	return protocol.CommandResultPayload{OK: false, Error: &msg}, true
}

// FilterListResult filters a list_containers result down to registered
// containers when enforcement is on. Non-list results and disabled
// enforcement pass through unchanged. With enforcement on but nothing synced
// yet, the list is emptied (fail closed).
func (g *Gate) FilterListResult(command string, result protocol.CommandResultPayload) protocol.CommandResultPayload {
	if command != protocol.CmdListContainers || !result.OK || !g.enforce {
		return result
	}
	items, ok := result.Data.([]map[string]interface{})
	if !ok {
		return result
	}
	filtered := make([]map[string]interface{}, 0, len(items))
	for _, item := range items {
		name, _ := item["name"].(string)
		id, _ := item["id"].(string)
		if g.registry.Contains(name) || g.registry.Contains(id) {
			filtered = append(filtered, item)
		}
	}
	result.Data = filtered
	return result
}

// isContainerLifecycleCommand reports whether the command operates on a
// specific container and must be gated.
func isContainerLifecycleCommand(command string) bool {
	switch command {
	case protocol.CmdStartContainer,
		protocol.CmdStopContainer,
		protocol.CmdRestartContainer,
		protocol.CmdRemoveContainer,
		protocol.CmdGetContainerLogs,
		protocol.CmdInspectContainer:
		return true
	default:
		return false
	}
}

// normalize lowercases and trims container keys so lookups are
// case-insensitive (docker names are case-sensitive, but backend/agent casing
// drift should not cause spurious rejections).
func normalize(s string) string {
	return strings.ToLower(strings.TrimSpace(s))
}
