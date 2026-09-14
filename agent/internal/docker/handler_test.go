package docker

import (
	"encoding/json"
	"errors"
	"strings"
	"testing"

	"github.com/docker/docker/api/types"
	"github.com/docker/docker/errdefs"
	"github.com/docker/go-connections/nat"

	"unswarm/agent/internal/protocol"
)

// ---------------------------------------------------------------------------
// shortID
// ---------------------------------------------------------------------------

func TestShortID_LongerThan12(t *testing.T) {
	id := "abcdef1234567890"
	got := shortID(id)
	if got != "abcdef123456" {
		t.Errorf("shortID(%q) = %q, want %q", id, got, "abcdef123456")
	}
}

func TestShortID_Exactly12(t *testing.T) {
	id := "abcdef123456"
	got := shortID(id)
	if got != id {
		t.Errorf("shortID(%q) = %q, want %q", id, got, id)
	}
}

func TestShortID_ShorterThan12(t *testing.T) {
	id := "abc"
	got := shortID(id)
	if got != id {
		t.Errorf("shortID(%q) = %q, want %q", id, got, id)
	}
}

func TestShortID_Empty(t *testing.T) {
	got := shortID("")
	if got != "" {
		t.Errorf("shortID(\"\") = %q, want \"\"", got)
	}
}

// ---------------------------------------------------------------------------
// firstContainerName
// ---------------------------------------------------------------------------

func TestFirstContainerName_StripsSlash(t *testing.T) {
	names := []string{"/my-container", "/other"}
	got := firstContainerName(names)
	if got != "my-container" {
		t.Errorf("firstContainerName(%v) = %q, want %q", names, got, "my-container")
	}
}

func TestFirstContainerName_NoSlash(t *testing.T) {
	names := []string{"bare-name"}
	got := firstContainerName(names)
	if got != "bare-name" {
		t.Errorf("firstContainerName(%v) = %q, want %q", names, got, "bare-name")
	}
}

func TestFirstContainerName_EmptySlice(t *testing.T) {
	got := firstContainerName([]string{})
	if got != "" {
		t.Errorf("firstContainerName([]) = %q, want \"\"", got)
	}
}

func TestFirstContainerName_Nil(t *testing.T) {
	got := firstContainerName(nil)
	if got != "" {
		t.Errorf("firstContainerName(nil) = %q, want \"\"", got)
	}
}

// ---------------------------------------------------------------------------
// firstPublicPort
// ---------------------------------------------------------------------------

func TestFirstPublicPort_HasPublic(t *testing.T) {
	ports := []types.Port{
		{PrivatePort: 8080, PublicPort: 3000, Type: "tcp"},
		{PrivatePort: 9090, PublicPort: 4000, Type: "tcp"},
	}
	got := firstPublicPort(ports)
	if got != 3000 {
		t.Errorf("firstPublicPort = %d, want 3000", got)
	}
}

func TestFirstPublicPort_NoPublic_FallsBackToPrivate(t *testing.T) {
	ports := []types.Port{
		{PrivatePort: 8080, PublicPort: 0, Type: "tcp"},
	}
	got := firstPublicPort(ports)
	if got != 8080 {
		t.Errorf("firstPublicPort = %d, want 8080", got)
	}
}

func TestFirstPublicPort_Empty(t *testing.T) {
	got := firstPublicPort(nil)
	if got != 0 {
		t.Errorf("firstPublicPort(nil) = %d, want 0", got)
	}
}

func TestFirstPublicPort_AllPublicZero_MultipleEntries(t *testing.T) {
	ports := []types.Port{
		{PrivatePort: 1111, PublicPort: 0},
		{PrivatePort: 2222, PublicPort: 0},
	}
	got := firstPublicPort(ports)
	if got != 1111 {
		t.Errorf("firstPublicPort = %d, want 1111", got)
	}
}

// ---------------------------------------------------------------------------
// firstPort
// ---------------------------------------------------------------------------

func TestFirstPort_HasPublic(t *testing.T) {
	ports := []types.Port{
		{PrivatePort: 80, PublicPort: 8080},
		{PrivatePort: 443, PublicPort: 8443},
	}
	got := firstPort(ports)
	if got != 8080 {
		t.Errorf("firstPort = %d, want 8080", got)
	}
}

func TestFirstPort_NoPublic(t *testing.T) {
	ports := []types.Port{
		{PrivatePort: 80, PublicPort: 0},
	}
	got := firstPort(ports)
	if got != 0 {
		t.Errorf("firstPort = %d, want 0", got)
	}
}

func TestFirstPort_Empty(t *testing.T) {
	got := firstPort(nil)
	if got != 0 {
		t.Errorf("firstPort(nil) = %d, want 0", got)
	}
}

// ---------------------------------------------------------------------------
// formatPorts
// ---------------------------------------------------------------------------

func TestFormatPorts(t *testing.T) {
	ports := []types.Port{
		{PrivatePort: 80, PublicPort: 8080, Type: "tcp"},
		{PrivatePort: 443, PublicPort: 0, Type: "udp"},
	}
	result := formatPorts(ports)
	if len(result) != 2 {
		t.Fatalf("formatPorts returned %d entries, want 2", len(result))
	}

	// First entry — values are uint16 from types.Port, compare with typed constants.
	if result[0]["privatePort"] != uint16(80) {
		t.Errorf("entry[0].privatePort = %v, want 80", result[0]["privatePort"])
	}
	if result[0]["publicPort"] != uint16(8080) {
		t.Errorf("entry[0].publicPort = %v, want 8080", result[0]["publicPort"])
	}
	if result[0]["type"] != "tcp" {
		t.Errorf("entry[0].type = %v, want tcp", result[0]["type"])
	}

	// Second entry
	if result[1]["privatePort"] != uint16(443) {
		t.Errorf("entry[1].privatePort = %v, want 443", result[1]["privatePort"])
	}
}

func TestFormatPorts_Empty(t *testing.T) {
	result := formatPorts(nil)
	if len(result) != 0 {
		t.Errorf("formatPorts(nil) returned %d entries, want 0", len(result))
	}
}

// ---------------------------------------------------------------------------
// formatInspectPorts
// ---------------------------------------------------------------------------

func TestFormatInspectPorts_WithBindings(t *testing.T) {
	ports := nat.PortMap{
		"80/tcp": []nat.PortBinding{
			{HostIP: "0.0.0.0", HostPort: "8080"},
		},
	}
	result := formatInspectPorts(ports)
	if len(result) != 1 {
		t.Fatalf("formatInspectPorts returned %d entries, want 1", len(result))
	}
	entry := result[0]
	if entry["privatePort"] != "80" {
		t.Errorf("privatePort = %v, want \"80\"", entry["privatePort"])
	}
	if entry["protocol"] != "tcp" {
		t.Errorf("protocol = %v, want tcp", entry["protocol"])
	}
	if entry["publicPort"] != "8080" {
		t.Errorf("publicPort = %v, want \"8080\"", entry["publicPort"])
	}
	if entry["hostIp"] != "0.0.0.0" {
		t.Errorf("hostIp = %v, want \"0.0.0.0\"", entry["hostIp"])
	}
}

func TestFormatInspectPorts_NoBindings(t *testing.T) {
	ports := nat.PortMap{
		"443/tcp": nil,
	}
	result := formatInspectPorts(ports)
	if len(result) != 1 {
		t.Fatalf("formatInspectPorts returned %d entries, want 1", len(result))
	}
	entry := result[0]
	if entry["privatePort"] != "443" {
		t.Errorf("privatePort = %v, want \"443\"", entry["privatePort"])
	}
	// No publicPort/hostIp when no bindings
	if _, ok := entry["publicPort"]; ok {
		t.Error("expected no publicPort key when bindings are empty")
	}
}

func TestFormatInspectPorts_Empty(t *testing.T) {
	result := formatInspectPorts(nat.PortMap{})
	if len(result) != 0 {
		t.Errorf("formatInspectPorts(empty) returned %d entries, want 0", len(result))
	}
}

// ---------------------------------------------------------------------------
// okResult / errorResult
// ---------------------------------------------------------------------------

func TestOkResult(t *testing.T) {
	data := map[string]string{"status": "started"}
	r := okResult(data)
	if !r.OK {
		t.Error("okResult: OK should be true")
	}
	if r.Error != nil {
		t.Errorf("okResult: Error should be nil, got %q", *r.Error)
	}
	if r.Data == nil {
		t.Error("okResult: Data should not be nil")
	}
}

func TestErrorResult(t *testing.T) {
	r := errorResult("something broke")
	if r.OK {
		t.Error("errorResult: OK should be false")
	}
	if r.Error == nil {
		t.Fatal("errorResult: Error should not be nil")
	}
	if *r.Error != "something broke" {
		t.Errorf("errorResult: Error = %q, want %q", *r.Error, "something broke")
	}
	if r.Data != nil {
		t.Error("errorResult: Data should be nil")
	}
}

// ---------------------------------------------------------------------------
// containerErrorResult
// ---------------------------------------------------------------------------

func TestContainerErrorResult_NotFound(t *testing.T) {
	err := errdefs.NotFound(errors.New("container abc not found"))
	r := containerErrorResult("start", "abc", err)
	if r.OK {
		t.Error("expected OK=false")
	}
	if r.Error == nil {
		t.Fatal("expected non-nil Error")
	}
	if got := *r.Error; !strings.Contains(got, "not found") {
		t.Errorf("error message should mention 'not found', got %q", got)
	}
}

func TestContainerErrorResult_GenericError(t *testing.T) {
	err := errors.New("permission denied")
	r := containerErrorResult("stop", "mycontainer", err)
	if r.OK {
		t.Error("expected OK=false")
	}
	if r.Error == nil {
		t.Fatal("expected non-nil Error")
	}
	got := *r.Error
	if !strings.Contains(got, "stop") {
		t.Errorf("error should contain op 'stop', got %q", got)
	}
	if !strings.Contains(got, "mycontainer") {
		t.Errorf("error should contain container name, got %q", got)
	}
	if !strings.Contains(got, "permission denied") {
		t.Errorf("error should contain original error, got %q", got)
	}
}

// Verify that okResult and errorResult satisfy the expected JSON structure.
func TestCommandResultPayload_OkStructure(t *testing.T) {
	r := okResult("hello")
	b, err := json.Marshal(r)
	if err != nil {
		t.Fatalf("marshal: %v", err)
	}
	s := string(b)
	if !strings.Contains(s, `"ok":true`) {
		t.Errorf("JSON should contain ok:true, got %s", s)
	}
	if !strings.Contains(s, `"data":"hello"`) {
		t.Errorf("JSON should contain data, got %s", s)
	}
}

func TestCommandResultPayload_ErrorStructure(t *testing.T) {
	r := errorResult("boom")
	b, err := json.Marshal(r)
	if err != nil {
		t.Fatalf("marshal: %v", err)
	}
	s := string(b)
	if !strings.Contains(s, `"ok":false`) {
		t.Errorf("JSON should contain ok:false, got %s", s)
	}
	if !strings.Contains(s, `"error":"boom"`) {
		t.Errorf("JSON should contain error, got %s", s)
	}
}

// Roundtrip: marshal → unmarshal preserves fields.
func TestCommandResultPayload_Roundtrip(t *testing.T) {
	orig := okResult(map[string]string{"k": "v"})
	b, err := json.Marshal(orig)
	if err != nil {
		t.Fatalf("marshal: %v", err)
	}
	var decoded protocol.CommandResultPayload
	if err := json.Unmarshal(b, &decoded); err != nil {
		t.Fatalf("unmarshal: %v", err)
	}
	if !decoded.OK {
		t.Error("roundtrip OK should be true")
	}
	if decoded.Error != nil {
		t.Errorf("roundtrip Error should be nil, got %v", decoded.Error)
	}
}
