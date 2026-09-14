package runtimegate

import (
	"strings"
	"testing"

	"unswarm/agent/internal/protocol"
)

// ---------------------------------------------------------------------------
// normalize (unexported, tested indirectly + directly via reflection-free idiom)
// ---------------------------------------------------------------------------

func TestNormalize(t *testing.T) {
	tests := []struct {
		input, want string
	}{
		{"Hello", "hello"},
		{"  Foo  ", "foo"},
		{"BAR", "bar"},
		{"", ""},
		{"  ", ""},
		{"MiXeD CaSe  ", "mixed case"},
	}
	for _, tt := range tests {
		got := normalize(tt.input)
		if got != tt.want {
			t.Errorf("normalize(%q) = %q, want %q", tt.input, got, tt.want)
		}
	}
}

// ---------------------------------------------------------------------------
// Registry tests
// ---------------------------------------------------------------------------

func TestNewRegistry_Empty(t *testing.T) {
	r := NewRegistry()
	if r == nil {
		t.Fatal("NewRegistry returned nil")
	}
	if r.Size() != 0 {
		t.Errorf("new registry Size() = %d, want 0", r.Size())
	}
}

func TestRegistry_Replace_IndexesByNameAndID(t *testing.T) {
	r := NewRegistry()
	r.Replace([]protocol.RegistrationEntry{
		{
			RegisteredRuntimeID: "rt-1",
			ContainerName:       "myapp",
			ContainerID:         "abc123",
		},
	})

	if r.Size() != 2 {
		t.Errorf("Size() = %d, want 2", r.Size())
	}

	// Lookup by name
	if id, ok := r.Lookup("myapp"); !ok || id != "rt-1" {
		t.Errorf("Lookup(\"myapp\") = (%q, %v), want (\"rt-1\", true)", id, ok)
	}

	// Lookup by ID
	if id, ok := r.Lookup("abc123"); !ok || id != "rt-1" {
		t.Errorf("Lookup(\"abc123\") = (%q, %v), want (\"rt-1\", true)", id, ok)
	}
}

func TestRegistry_Replace_SkipsEmptyRuntimeID(t *testing.T) {
	r := NewRegistry()
	r.Replace([]protocol.RegistrationEntry{
		{RegisteredRuntimeID: "", ContainerName: "a", ContainerID: "b"},
		{RegisteredRuntimeID: "  ", ContainerName: "c", ContainerID: "d"},
	})
	if r.Size() != 0 {
		t.Errorf("Size() = %d, want 0 (empty runtime IDs should be skipped)", r.Size())
	}
}

func TestRegistry_Replace_SkipsEmptyNameAndID(t *testing.T) {
	r := NewRegistry()
	r.Replace([]protocol.RegistrationEntry{
		{RegisteredRuntimeID: "rt-1", ContainerName: "", ContainerID: ""},
		{RegisteredRuntimeID: "rt-2", ContainerName: "  ", ContainerID: "  "},
	})
	if r.Size() != 0 {
		t.Errorf("Size() = %d, want 0 (entries with no name/id should be skipped)", r.Size())
	}
}

func TestRegistry_Replace_PartialEmptyName(t *testing.T) {
	r := NewRegistry()
	// Only ContainerName set, no ContainerID
	r.Replace([]protocol.RegistrationEntry{
		{RegisteredRuntimeID: "rt-1", ContainerName: "onlyname"},
	})
	if r.Size() != 1 {
		t.Errorf("Size() = %d, want 1", r.Size())
	}
	if _, ok := r.Lookup("onlyname"); !ok {
		t.Error("expected to find by ContainerName")
	}
}

func TestRegistry_Replace_PartialEmptyID(t *testing.T) {
	r := NewRegistry()
	// Only ContainerID set, no ContainerName
	r.Replace([]protocol.RegistrationEntry{
		{RegisteredRuntimeID: "rt-1", ContainerID: "onlyid"},
	})
	if r.Size() != 1 {
		t.Errorf("Size() = %d, want 1", r.Size())
	}
	if _, ok := r.Lookup("onlyid"); !ok {
		t.Error("expected to find by ContainerID")
	}
}

func TestRegistry_Replace_NormalizesKeys(t *testing.T) {
	r := NewRegistry()
	r.Replace([]protocol.RegistrationEntry{
		{
			RegisteredRuntimeID: "rt-1",
			ContainerName:       "  MyContainer ",
			ContainerID:         " ABC123 ",
		},
	})

	// Should find with different casing and whitespace
	if _, ok := r.Lookup("mycontainer"); !ok {
		t.Error("expected case-insensitive lookup by name to succeed")
	}
	if _, ok := r.Lookup("MYCONTAINER"); !ok {
		t.Error("expected uppercase lookup by name to succeed")
	}
	if _, ok := r.Lookup("  MyContainer  "); !ok {
		t.Error("expected whitespace-padded lookup by name to succeed")
	}
	if _, ok := r.Lookup("abc123"); !ok {
		t.Error("expected lowercase lookup by ID to succeed")
	}
	if _, ok := r.Lookup("ABC123"); !ok {
		t.Error("expected uppercase lookup by ID to succeed")
	}
}

func TestRegistry_Lookup_FindsByName(t *testing.T) {
	r := NewRegistry()
	r.Replace([]protocol.RegistrationEntry{
		{RegisteredRuntimeID: "rt-1", ContainerName: "web"},
	})
	id, ok := r.Lookup("web")
	if !ok || id != "rt-1" {
		t.Errorf("Lookup(\"web\") = (%q, %v), want (\"rt-1\", true)", id, ok)
	}
}

func TestRegistry_Lookup_FindsByID(t *testing.T) {
	r := NewRegistry()
	r.Replace([]protocol.RegistrationEntry{
		{RegisteredRuntimeID: "rt-2", ContainerID: "deadbeef"},
	})
	id, ok := r.Lookup("deadbeef")
	if !ok || id != "rt-2" {
		t.Errorf("Lookup(\"deadbeef\") = (%q, %v), want (\"rt-2\", true)", id, ok)
	}
}

func TestRegistry_Lookup_UnknownReturnsFalse(t *testing.T) {
	r := NewRegistry()
	r.Replace([]protocol.RegistrationEntry{
		{RegisteredRuntimeID: "rt-1", ContainerName: "web"},
	})
	id, ok := r.Lookup("unknown")
	if ok {
		t.Errorf("Lookup(\"unknown\") = (%q, %v), want (\"\", false)", id, ok)
	}
}

func TestRegistry_Lookup_EmptyStringReturnsFalse(t *testing.T) {
	r := NewRegistry()
	r.Replace([]protocol.RegistrationEntry{
		{RegisteredRuntimeID: "rt-1", ContainerName: "web"},
	})
	id, ok := r.Lookup("")
	if ok {
		t.Errorf("Lookup(\"\") = (%q, %v), want (\"\", false)", id, ok)
	}
}

func TestRegistry_Lookup_WhitespaceOnlyReturnsFalse(t *testing.T) {
	r := NewRegistry()
	r.Replace([]protocol.RegistrationEntry{
		{RegisteredRuntimeID: "rt-1", ContainerName: "web"},
	})
	id, ok := r.Lookup("   ")
	if ok {
		t.Errorf("Lookup(\"   \") = (%q, %v), want (\"\", false)", id, ok)
	}
}

func TestRegistry_Contains_TrueAndFalse(t *testing.T) {
	r := NewRegistry()
	r.Replace([]protocol.RegistrationEntry{
		{RegisteredRuntimeID: "rt-1", ContainerName: "web", ContainerID: "abc"},
	})
	if !r.Contains("web") {
		t.Error("Contains(\"web\") = false, want true")
	}
	if !r.Contains("abc") {
		t.Error("Contains(\"abc\") = false, want true")
	}
	if r.Contains("missing") {
		t.Error("Contains(\"missing\") = true, want false")
	}
}

func TestRegistry_Contains_CaseInsensitive(t *testing.T) {
	r := NewRegistry()
	r.Replace([]protocol.RegistrationEntry{
		{RegisteredRuntimeID: "rt-1", ContainerName: "MyApp"},
	})
	if !r.Contains("myapp") {
		t.Error("Contains(\"myapp\") should be true (case-insensitive)")
	}
	if !r.Contains("MYAPP") {
		t.Error("Contains(\"MYAPP\") should be true (case-insensitive)")
	}
}

func TestRegistry_Replace_AtomicReplacesOldEntries(t *testing.T) {
	r := NewRegistry()

	// First set
	r.Replace([]protocol.RegistrationEntry{
		{RegisteredRuntimeID: "rt-1", ContainerName: "old"},
	})
	if !r.Contains("old") {
		t.Fatal("expected to find \"old\" after first Replace")
	}

	// Second set — does NOT include "old"
	r.Replace([]protocol.RegistrationEntry{
		{RegisteredRuntimeID: "rt-2", ContainerName: "new"},
	})

	if r.Contains("old") {
		t.Error("\"old\" should be gone after second Replace (atomic swap)")
	}
	if !r.Contains("new") {
		t.Error("\"new\" should be present after second Replace")
	}
}

func TestRegistry_Size_CorrectCount(t *testing.T) {
	r := NewRegistry()

	r.Replace([]protocol.RegistrationEntry{
		{RegisteredRuntimeID: "rt-1", ContainerName: "a", ContainerID: "id-a"},
		{RegisteredRuntimeID: "rt-2", ContainerName: "b", ContainerID: "id-b"},
	})
	// 4 distinct keys: a, id-a, b, id-b
	if r.Size() != 4 {
		t.Errorf("Size() = %d, want 4", r.Size())
	}

	// Replace with smaller set
	r.Replace([]protocol.RegistrationEntry{
		{RegisteredRuntimeID: "rt-3", ContainerName: "c"},
	})
	if r.Size() != 1 {
		t.Errorf("Size() = %d, want 1 after smaller Replace", r.Size())
	}
}

func TestRegistry_Size_SkippedEntriesNotCounted(t *testing.T) {
	r := NewRegistry()
	r.Replace([]protocol.RegistrationEntry{
		{RegisteredRuntimeID: "", ContainerName: "a", ContainerID: "b"},
		{RegisteredRuntimeID: "rt-1", ContainerName: "", ContainerID: ""},
		{RegisteredRuntimeID: "rt-2", ContainerName: "valid", ContainerID: "vid"},
	})
	// Only "valid" and "vid" should be counted
	if r.Size() != 2 {
		t.Errorf("Size() = %d, want 2", r.Size())
	}
}

func TestRegistry_Replace_EmptySlice(t *testing.T) {
	r := NewRegistry()
	r.Replace([]protocol.RegistrationEntry{
		{RegisteredRuntimeID: "rt-1", ContainerName: "a"},
	})
	r.Replace([]protocol.RegistrationEntry{})
	if r.Size() != 0 {
		t.Errorf("Size() = %d after Replace with empty slice, want 0", r.Size())
	}
}

func TestRegistry_DuplicateNameAndID(t *testing.T) {
	r := NewRegistry()
	// Same entry appears twice — both should resolve to same runtime ID
	r.Replace([]protocol.RegistrationEntry{
		{RegisteredRuntimeID: "rt-1", ContainerName: "web", ContainerID: "abc"},
		{RegisteredRuntimeID: "rt-1", ContainerName: "web", ContainerID: "abc"},
	})
	if r.Size() != 2 {
		t.Errorf("Size() = %d, want 2 (deduplicated keys)", r.Size())
	}
	id1, _ := r.Lookup("web")
	id2, _ := r.Lookup("abc")
	if id1 != "rt-1" || id2 != "rt-1" {
		t.Errorf("expected both to map to \"rt-1\", got %q and %q", id1, id2)
	}
}

func TestRegistry_NameAndIDDifferentRuntimes(t *testing.T) {
	r := NewRegistry()
	// Name maps to one runtime, ID to another
	r.Replace([]protocol.RegistrationEntry{
		{RegisteredRuntimeID: "rt-name", ContainerName: "web"},
		{RegisteredRuntimeID: "rt-id", ContainerID: "abc"},
	})
	idName, _ := r.Lookup("web")
	idID, _ := r.Lookup("abc")
	if idName != "rt-name" {
		t.Errorf("Lookup(\"web\") = %q, want \"rt-name\"", idName)
	}
	if idID != "rt-id" {
		t.Errorf("Lookup(\"abc\") = %q, want \"rt-id\"", idID)
	}
}

// ---------------------------------------------------------------------------
// Gate tests
// ---------------------------------------------------------------------------

func TestNewGate_NilRegistry(t *testing.T) {
	g := NewGate(nil, true)
	if g == nil {
		t.Fatal("NewGate returned nil")
	}
	if g.Registry() == nil {
		t.Error("expected non-nil registry from NewGate with nil input")
	}
	if g.Registry().Size() != 0 {
		t.Error("expected internal registry to be empty")
	}
}

func TestNewGate_WithRegistry(t *testing.T) {
	r := NewRegistry()
	r.Replace([]protocol.RegistrationEntry{
		{RegisteredRuntimeID: "rt-1", ContainerName: "web"},
	})
	g := NewGate(r, false)
	if g.Registry() != r {
		t.Error("expected Registry() to return the same registry passed to NewGate")
	}
}

func TestGate_Enforce(t *testing.T) {
	g1 := NewGate(nil, true)
	if !g1.Enforce() {
		t.Error("Enforce() = false, want true")
	}
	g2 := NewGate(nil, false)
	if g2.Enforce() {
		t.Error("Enforce() = true, want false")
	}
}

func TestGate_Check_EnforcementOff_AllowsAll(t *testing.T) {
	g := NewGate(nil, false)
	lifecycleCmds := []string{
		protocol.CmdStartContainer,
		protocol.CmdStopContainer,
		protocol.CmdRestartContainer,
		protocol.CmdRemoveContainer,
		protocol.CmdGetContainerLogs,
		protocol.CmdInspectContainer,
	}
	for _, cmd := range lifecycleCmds {
		result, blocked := g.Check(cmd, "anything")
		if blocked {
			t.Errorf("Check(%q, \"anything\") blocked with enforcement off: %v", cmd, result)
		}
	}
}

func TestGate_Check_NonLifecycleCommand_AlwaysAllowed(t *testing.T) {
	r := NewRegistry()
	g := NewGate(r, true) // enforcement ON

	nonLifecycleCmds := []string{
		protocol.CmdListContainers,
		protocol.CmdHealthCheck,
		protocol.CmdDiscoverModels,
		protocol.CmdChatCompletion,
		protocol.CmdChatCompletionStream,
		protocol.CmdListScripts,
		protocol.CmdStartScript,
		"some_unknown_command",
	}
	for _, cmd := range nonLifecycleCmds {
		result, blocked := g.Check(cmd, "unregistered-target")
		if blocked {
			t.Errorf("Check(%q, \"unregistered-target\") should pass non-lifecycle: %v", cmd, result)
		}
	}
}

func TestGate_Check_LifecycleCommand_RegisteredTarget_Allows(t *testing.T) {
	r := NewRegistry()
	r.Replace([]protocol.RegistrationEntry{
		{RegisteredRuntimeID: "rt-1", ContainerName: "web", ContainerID: "abc123"},
	})
	g := NewGate(r, true)

	cmds := []string{
		protocol.CmdStartContainer,
		protocol.CmdStopContainer,
		protocol.CmdRestartContainer,
		protocol.CmdInspectContainer,
		protocol.CmdGetContainerLogs,
		protocol.CmdRemoveContainer,
	}
	for _, cmd := range cmds {
		// Lookup by name
		result, blocked := g.Check(cmd, "web")
		if blocked {
			t.Errorf("Check(%q, \"web\") should allow registered target: %v", cmd, result)
		}
		// Lookup by ID
		result, blocked = g.Check(cmd, "abc123")
		if blocked {
			t.Errorf("Check(%q, \"abc123\") should allow registered target: %v", cmd, result)
		}
		// Lookup case-insensitive
		result, blocked = g.Check(cmd, "Web")
		if blocked {
			t.Errorf("Check(%q, \"Web\") should allow registered target (case-insensitive): %v", cmd, result)
		}
	}
}

func TestGate_Check_LifecycleCommand_UnregisteredTarget_Blocks(t *testing.T) {
	r := NewRegistry()
	r.Replace([]protocol.RegistrationEntry{
		{RegisteredRuntimeID: "rt-1", ContainerName: "web"},
	})
	g := NewGate(r, true)

	result, blocked := g.Check(protocol.CmdStartContainer, "unknown")
	if !blocked {
		t.Fatal("expected Check to block unregistered target")
	}
	if result.OK {
		t.Error("expected result.OK = false")
	}
	if result.Error == nil {
		t.Fatal("expected non-nil error message")
	}
	if !strings.Contains(*result.Error, protocol.CmdStartContainer) {
		t.Errorf("error message should contain command name, got: %q", *result.Error)
	}
	if !strings.Contains(*result.Error, "unknown") {
		t.Errorf("error message should contain target, got: %q", *result.Error)
	}
}

func TestGate_Check_AllLifecycleCommands_BlockUnregistered(t *testing.T) {
	r := NewRegistry()
	g := NewGate(r, true) // empty registry — nothing registered

	cmds := []string{
		protocol.CmdStartContainer,
		protocol.CmdStopContainer,
		protocol.CmdRestartContainer,
		protocol.CmdRemoveContainer,
		protocol.CmdGetContainerLogs,
		protocol.CmdInspectContainer,
	}
	for _, cmd := range cmds {
		result, blocked := g.Check(cmd, "mycontainer")
		if !blocked {
			t.Errorf("Check(%q, \"mycontainer\") should block with empty registry", cmd)
		}
		if result.Error == nil {
			t.Errorf("Check(%q) error message should not be nil", cmd)
		}
	}
}

func TestGate_Check_ErrorMessageContainsCommandAndTarget(t *testing.T) {
	r := NewRegistry()
	g := NewGate(r, true)

	result, _ := g.Check(protocol.CmdStopContainer, "my-service")
	if result.Error == nil {
		t.Fatal("expected error")
	}
	msg := *result.Error
	if !strings.Contains(msg, "stop_container") {
		t.Errorf("error should contain command, got: %q", msg)
	}
	if !strings.Contains(msg, "my-service") {
		t.Errorf("error should contain target, got: %q", msg)
	}
	if !strings.Contains(msg, "not registered") {
		t.Errorf("error should contain \"not registered\", got: %q", msg)
	}
}

func TestGate_Check_LifecycleCommand_RegisteredByID_Allows(t *testing.T) {
	r := NewRegistry()
	r.Replace([]protocol.RegistrationEntry{
		{RegisteredRuntimeID: "rt-1", ContainerID: "cafe1234"},
	})
	g := NewGate(r, true)

	_, blocked := g.Check(protocol.CmdInspectContainer, "cafe1234")
	if blocked {
		t.Error("expected lifecycle command with registered container ID to be allowed")
	}
}

func TestGate_Check_LifecycleCommand_UnregisteredByID_Blocks(t *testing.T) {
	r := NewRegistry()
	r.Replace([]protocol.RegistrationEntry{
		{RegisteredRuntimeID: "rt-1", ContainerName: "web"},
	})
	g := NewGate(r, true)

	_, blocked := g.Check(protocol.CmdStartContainer, "deadbeef")
	if !blocked {
		t.Error("expected lifecycle command with unregistered container ID to be blocked")
	}
}

// ---------------------------------------------------------------------------
// FilterListResult tests
// ---------------------------------------------------------------------------

func TestFilterListResult_NonListCommand_PassesThrough(t *testing.T) {
	r := NewRegistry()
	g := NewGate(r, true)

	input := protocol.CommandResultPayload{
		OK:   true,
		Data: "some data",
	}
	for _, cmd := range []string{
		protocol.CmdStartContainer,
		protocol.CmdHealthCheck,
		protocol.CmdDiscoverModels,
		"unknown_cmd",
	} {
		got := g.FilterListResult(cmd, input)
		if got != input {
			t.Errorf("FilterListResult(%q, ...) should pass through unchanged", cmd)
		}
	}
}

func TestFilterListResult_ListContainers_EnforcementOff_PassesThrough(t *testing.T) {
	g := NewGate(nil, false) // enforcement OFF

	input := protocol.CommandResultPayload{
		OK:   true,
		Data: []map[string]interface{}{{"name": "web"}},
	}
	got := g.FilterListResult(protocol.CmdListContainers, input)
	// Data should still be the original (no filtering)
	gotItems, _ := got.Data.([]map[string]interface{})
	if len(gotItems) != 1 {
		t.Errorf("expected 1 item with enforcement off, got %d", len(gotItems))
	}
}

func TestFilterListResult_ListContainers_EnforcementOn_FiltersToRegistered(t *testing.T) {
	r := NewRegistry()
	r.Replace([]protocol.RegistrationEntry{
		{RegisteredRuntimeID: "rt-1", ContainerName: "web"},
		{RegisteredRuntimeID: "rt-2", ContainerID: "abc123"},
	})
	g := NewGate(r, true)

	input := protocol.CommandResultPayload{
		OK: true,
		Data: []map[string]interface{}{
			{"name": "web", "id": "aaa"},        // registered by name
			{"name": "db", "id": "abc123"},       // registered by id
			{"name": "cache", "id": "zzz"},       // not registered
			{"name": "proxy", "id": "unknown"},    // not registered
		},
	}

	got := g.FilterListResult(protocol.CmdListContainers, input)
	items, ok := got.Data.([]map[string]interface{})
	if !ok {
		t.Fatal("expected Data to be []map[string]interface{}")
	}
	if len(items) != 2 {
		t.Fatalf("expected 2 filtered items, got %d", len(items))
	}
	// First should be "web"
	if items[0]["name"] != "web" {
		t.Errorf("first item name = %q, want \"web\"", items[0]["name"])
	}
	// Second should be "db" (matched by id abc123)
	if items[1]["name"] != "db" {
		t.Errorf("second item name = %q, want \"db\"", items[1]["name"])
	}
}

func TestFilterListResult_ListContainers_EmptyList(t *testing.T) {
	r := NewRegistry()
	r.Replace([]protocol.RegistrationEntry{
		{RegisteredRuntimeID: "rt-1", ContainerName: "web"},
	})
	g := NewGate(r, true)

	input := protocol.CommandResultPayload{
		OK:   true,
		Data: []map[string]interface{}{},
	}

	got := g.FilterListResult(protocol.CmdListContainers, input)
	items, ok := got.Data.([]map[string]interface{})
	if !ok {
		t.Fatal("expected Data to be []map[string]interface{}")
	}
	if len(items) != 0 {
		t.Errorf("expected 0 items from empty list, got %d", len(items))
	}
}

func TestFilterListResult_ListContainers_ResultNotOK_PassesThrough(t *testing.T) {
	r := NewRegistry()
	g := NewGate(r, true)

	input := protocol.CommandResultPayload{
		OK: false,
		Error: strPtr("docker error"),
		Data: []map[string]interface{}{
			{"name": "web"},
		},
	}

	got := g.FilterListResult(protocol.CmdListContainers, input)
	// Should pass through unchanged because OK=false
	items, ok := got.Data.([]map[string]interface{})
	if !ok {
		t.Fatal("expected Data to be []map[string]interface{}")
	}
	if len(items) != 1 {
		t.Errorf("expected 1 item (pass-through), got %d", len(items))
	}
}

func TestFilterListResult_ListContainers_DataNotSlice_PassesThrough(t *testing.T) {
	r := NewRegistry()
	g := NewGate(r, true)

	input := protocol.CommandResultPayload{
		OK:   true,
		Data: "unexpected type",
	}

	got := g.FilterListResult(protocol.CmdListContainers, input)
	// Should pass through unchanged because Data is not []map[string]interface{}
	if got.Data != "unexpected type" {
		t.Errorf("expected Data to pass through unchanged, got %v", got.Data)
	}
}

func TestFilterListResult_ListContainers_EnforcementOn_EmptyRegistry(t *testing.T) {
	r := NewRegistry() // empty — nothing registered
	g := NewGate(r, true)

	input := protocol.CommandResultPayload{
		OK: true,
		Data: []map[string]interface{}{
			{"name": "web", "id": "abc"},
		},
	}

	got := g.FilterListResult(protocol.CmdListContainers, input)
	items, ok := got.Data.([]map[string]interface{})
	if !ok {
		t.Fatal("expected Data to be []map[string]interface{}")
	}
	if len(items) != 0 {
		t.Errorf("expected 0 items with empty registry (fail closed), got %d", len(items))
	}
}

func TestFilterListResult_ListContainers_FiltersByNameAndID(t *testing.T) {
	r := NewRegistry()
	r.Replace([]protocol.RegistrationEntry{
		{RegisteredRuntimeID: "rt-1", ContainerName: "alpha"},
		{RegisteredRuntimeID: "rt-2", ContainerID: "deadbeef"},
	})
	g := NewGate(r, true)

	input := protocol.CommandResultPayload{
		OK: true,
		Data: []map[string]interface{}{
			{"name": "alpha", "id": "other1"},     // match by name
			{"name": "other2", "id": "deadbeef"},  // match by id
			{"name": "gamma", "id": "nope"},        // no match
		},
	}

	got := g.FilterListResult(protocol.CmdListContainers, input)
	items := got.Data.([]map[string]interface{})
	if len(items) != 2 {
		t.Fatalf("expected 2 items, got %d", len(items))
	}
	if items[0]["name"] != "alpha" {
		t.Errorf("first item name = %q, want \"alpha\"", items[0]["name"])
	}
	if items[1]["id"] != "deadbeef" {
		t.Errorf("second item id = %q, want \"deadbeef\"", items[1]["id"])
	}
}

func TestFilterListResult_ListContainers_AllRegistered(t *testing.T) {
	r := NewRegistry()
	r.Replace([]protocol.RegistrationEntry{
		{RegisteredRuntimeID: "rt-1", ContainerName: "a"},
		{RegisteredRuntimeID: "rt-2", ContainerName: "b"},
	})
	g := NewGate(r, true)

	input := protocol.CommandResultPayload{
		OK: true,
		Data: []map[string]interface{}{
			{"name": "a"},
			{"name": "b"},
		},
	}

	got := g.FilterListResult(protocol.CmdListContainers, input)
	items := got.Data.([]map[string]interface{})
	if len(items) != 2 {
		t.Errorf("expected 2 items (all registered), got %d", len(items))
	}
}

func TestFilterListResult_ListContainers_NoneRegistered(t *testing.T) {
	r := NewRegistry()
	r.Replace([]protocol.RegistrationEntry{
		{RegisteredRuntimeID: "rt-1", ContainerName: "x"},
	})
	g := NewGate(r, true)

	input := protocol.CommandResultPayload{
		OK: true,
		Data: []map[string]interface{}{
			{"name": "a"},
			{"name": "b"},
		},
	}

	got := g.FilterListResult(protocol.CmdListContainers, input)
	items := got.Data.([]map[string]interface{})
	if len(items) != 0 {
		t.Errorf("expected 0 items (none registered), got %d", len(items))
	}
}

func TestFilterListResult_ListContainers_NilData(t *testing.T) {
	r := NewRegistry()
	g := NewGate(r, true)

	input := protocol.CommandResultPayload{
		OK:   true,
		Data: nil,
	}

	got := g.FilterListResult(protocol.CmdListContainers, input)
	if got.Data != nil {
		t.Errorf("expected nil Data to pass through, got %v", got.Data)
	}
}

// ---------------------------------------------------------------------------
// Concurrency smoke test (Registry)
// ---------------------------------------------------------------------------

func TestRegistry_ConcurrentAccess(t *testing.T) {
	r := NewRegistry()
	// Seed with data
	r.Replace([]protocol.RegistrationEntry{
		{RegisteredRuntimeID: "rt-1", ContainerName: "web", ContainerID: "abc"},
	})

	// Concurrent reads and a concurrent replace — should not panic
	done := make(chan struct{})
	go func() {
		for i := 0; i < 100; i++ {
			r.Lookup("web")
			r.Contains("abc")
			r.Size()
		}
		close(done)
	}()

	go func() {
		for i := 0; i < 50; i++ {
			r.Replace([]protocol.RegistrationEntry{
				{RegisteredRuntimeID: "rt-new", ContainerName: "svc", ContainerID: "id-new"},
			})
		}
	}()

	<-done
}

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

func strPtr(s string) *string { return &s }
