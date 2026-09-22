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

// ContainsID reports whether any registered key matches id as a (possibly
// truncated) container ID: exact match, or one is a >=12-char prefix of the
// other. This bridges the backend syncing the full 64-hex RuntimeContainerId
// with the agent emitting a 12-char short ID in list_containers.
func (r *Registry) ContainsID(id string) bool {
	id = normalize(id)
	if id == "" {
		return false
	}
	r.mu.RLock()
	defer r.mu.RUnlock()
	for key := range r.byKey {
		if idMatch(key, id) {
			return true
		}
	}
	return false
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
// yet, the list is emptied (fail closed). An unrecognized Data shape is also
// treated as empty (fail closed) so an unexpected payload cannot bypass the
// filter and leak unregistered containers.
//
// The agent's list_containers result shape is
// {"containers": []map[string]interface{}} (see docker.Handler.ListContainers);
// a bare []map[string]interface{} is also accepted for older callers/tests.
func (g *Gate) FilterListResult(command string, result protocol.CommandResultPayload) protocol.CommandResultPayload {
	if command != protocol.CmdListContainers || !result.OK || !g.enforce {
		return result
	}
	switch data := result.Data.(type) {
	case []map[string]interface{}:
		result.Data = g.filterItems(data)
	case map[string]interface{}:
		items, ok := data["containers"].([]map[string]interface{})
		if !ok {
			// Unrecognized container list shape: fail closed by filtering out
			// every item rather than passing the raw, unfiltered payload on.
			items = nil
		}
		filtered := g.filterItems(items)
		// Rebuild with a fresh map so we never mutate a shared payload.
		next := make(map[string]interface{}, len(data))
		for k, v := range data {
			next[k] = v
		}
		next["containers"] = filtered
		result.Data = next
	default:
		// Unexpected Data shape: return an empty, filtered result (fail closed)
		// instead of passing an unfiltered payload through.
		result.Data = []map[string]interface{}{}
	}
	return result
}

// filterItems keeps only items whose name, image/model name, or (possibly
// truncated) container id is in the registered set.
func (g *Gate) filterItems(items []map[string]interface{}) []map[string]interface{} {
	filtered := make([]map[string]interface{}, 0, len(items))
	for _, item := range items {
		if g.matchesItem(item) {
			filtered = append(filtered, item)
		}
	}
	return filtered
}

// matchesItem reports whether a list_containers item maps to a registered
// runtime. The agent emits the short container id (12 chars) while the backend
// syncs the full ID, and ContainerName may carry the image rather than the
// container name, so every candidate field is checked by exact name/id match
// and by truncated-ID prefix match.
func (g *Gate) matchesItem(item map[string]interface{}) bool {
	for _, field := range []string{"name", "modelName", "image", "id", "modelId"} {
		value, _ := item[field].(string)
		if value == "" {
			continue
		}
		if g.registry.Contains(value) || g.registry.ContainsID(value) {
			return true
		}
	}
	return false
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

// minIDPrefixLen is the shortest container-id prefix considered a match.
// Docker short IDs are 12 hex chars; both sides must reach this length before
// a prefix comparison is trusted, so two short/name-like values are not
// spuriously matched.
const minIDPrefixLen = 12

// idMatch reports whether a and b identify the same container ID: equal, or
// one is a >=minIDPrefixLen prefix of the other (either direction).
func idMatch(a, b string) bool {
	a = normalize(a)
	b = normalize(b)
	if a == "" || b == "" {
		return false
	}
	if a == b {
		return true
	}
	if len(a) < minIDPrefixLen || len(b) < minIDPrefixLen {
		return false
	}
	return strings.HasPrefix(a, b) || strings.HasPrefix(b, a)
}
