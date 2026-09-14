package resolve

import (
	"context"
	"encoding/json"
	"strings"
	"testing"
)

// mockClient implements Client for testing.
type mockClient struct {
	responses []*Response // queued responses, consumed in order
	err       error
	calls     []string // records method+path calls
	callIdx   int
}

func (m *mockClient) Do(_ context.Context, method, path string, _ any) (*Response, error) {
	m.calls = append(m.calls, method+" "+path)
	if m.err != nil {
		return nil, m.err
	}
	if m.callIdx < len(m.responses) {
		resp := m.responses[m.callIdx]
		m.callIdx++
		return resp, nil
	}
	// Fall back to last response.
	if len(m.responses) > 0 {
		return m.responses[len(m.responses)-1], nil
	}
	return &Response{StatusCode: 200, Body: []byte("[]")}, nil
}

func jsonResponse(v any) *Response {
	b, _ := json.Marshal(v)
	return &Response{StatusCode: 200, Body: b}
}

func httpError(status int) *Response {
	return &Response{StatusCode: status, Body: []byte(`{"error":"not_found"}`)}
}

// ---------------------------------------------------------------------------
// Helper data
// ---------------------------------------------------------------------------

var testModels = []map[string]any{
	{"id": "aaa111aaa111aaa111aaa111aaa111aa1", "displayName": "gemma-4-12b-q8", "name": "gemma-4-12b-q8"},
	{"id": "bbb222bbb222bbb222bbb222bbb222bb2", "displayName": "llama-3-8b-instruct", "name": "llama-3-8b-instruct"},
	{"id": "ccc333ccc333ccc333ccc333ccc333cc3", "displayName": "mistral-7b-q4", "name": "mistral-7b-q4"},
	{"id": "ddd444ddd444ddd444ddd444ddd444dd4", "displayName": "llama-3-70b-q4", "name": "llama-3-70b-q4"},
}

var testRuntimes = []map[string]any{
	{"id": "eee555eee555eee555eee555eee555ee5", "displayName": "local-gpu-1", "image": "nvidia/cuda:12"},
	{"id": "fff666fff666fff666fff666fff666ff6", "displayName": "local-cpu-1", "image": "python:3.11"},
}

var testPrompts = []map[string]any{
	{"id": "77777777777777777777777777777777", "name": "summarize"},
	{"id": "88888888888888888888888888888888", "name": "translate"},
}

// ---------------------------------------------------------------------------
// Tests
// ---------------------------------------------------------------------------

func TestResolve_EmptyInput(t *testing.T) {
	mc := &mockClient{responses: []*Response{jsonResponse(testModels)}}
	r := New(mc, "/api/models", "displayName")

	_, err := r.Resolve(context.Background(), "")
	if err == nil {
		t.Fatal("expected error for empty input")
	}
	if err.Error() != "no resource specified" {
		t.Fatalf("unexpected error: %v", err)
	}
	if len(mc.calls) != 0 {
		t.Fatalf("expected no API calls, got %d", len(mc.calls))
	}
}

func TestResolve_WhitespaceInput(t *testing.T) {
	mc := &mockClient{responses: []*Response{jsonResponse(testModels)}}
	r := New(mc, "/api/models", "displayName")

	_, err := r.Resolve(context.Background(), "   ")
	if err == nil {
		t.Fatal("expected error for whitespace-only input")
	}
}

func TestResolve_HexIDPassthrough(t *testing.T) {
	mc := &mockClient{}
	r := New(mc, "/api/models", "displayName")

	id := "52b021aba5a241dca7728e8f5ca665c0"
	got, err := r.Resolve(context.Background(), id)
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if got != id {
		t.Fatalf("expected %q, got %q", id, got)
	}
	if len(mc.calls) != 0 {
		t.Fatalf("expected no API calls for ID passthrough, got %d", len(mc.calls))
	}
}

func TestResolve_HexIDWithDashes(t *testing.T) {
	mc := &mockClient{}
	r := New(mc, "/api/models", "displayName")

	id := "52b021ab-a5a2-41dc-a772-8e8f5ca665c0"
	got, err := r.Resolve(context.Background(), id)
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if got != id {
		t.Fatalf("expected %q, got %q", id, got)
	}
}

func TestResolve_HexIDTooShort(t *testing.T) {
	mc := &mockClient{responses: []*Response{jsonResponse(testModels)}}
	r := New(mc, "/api/models", "displayName")

	// "abc123" is only 6 hex chars — too short to be an ID, falls through to name resolution.
	// Since it doesn't match any model name, we expect "no resource named" error.
	_, err := r.Resolve(context.Background(), "abc123")
	if err == nil {
		t.Fatal("expected error for non-matching short hex string")
	}
	if !strings.Contains(err.Error(), "no resource named") {
		t.Fatalf("expected 'no resource named' in error, got: %v", err)
	}
}

func TestResolve_ExactMatch(t *testing.T) {
	mc := &mockClient{responses: []*Response{jsonResponse(testModels)}}
	r := New(mc, "/api/models", "displayName")

	got, err := r.Resolve(context.Background(), "gemma-4-12b-q8")
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if got != "aaa111aaa111aaa111aaa111aaa111aa1" {
		t.Fatalf("expected gemma ID, got %q", got)
	}
}

func TestResolve_ExactMatchCaseInsensitive(t *testing.T) {
	mc := &mockClient{responses: []*Response{jsonResponse(testModels)}}
	r := New(mc, "/api/models", "displayName")

	got, err := r.Resolve(context.Background(), "GEMMA-4-12B-Q8")
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if got != "aaa111aaa111aaa111aaa111aaa111aa1" {
		t.Fatalf("expected gemma ID, got %q", got)
	}
}

func TestResolve_SinglePartialMatch(t *testing.T) {
	mc := &mockClient{responses: []*Response{jsonResponse(testModels)}}
	r := New(mc, "/api/models", "displayName")

	got, err := r.Resolve(context.Background(), "mistral")
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if got != "ccc333ccc333ccc333ccc333ccc333cc3" {
		t.Fatalf("expected mistral ID, got %q", got)
	}
}

func TestResolve_MultiplePartialMatches(t *testing.T) {
	mc := &mockClient{responses: []*Response{jsonResponse(testModels)}}
	r := New(mc, "/api/models", "displayName")

	_, err := r.Resolve(context.Background(), "llama")
	if err == nil {
		t.Fatal("expected ambiguous error")
	}
	if !strings.Contains(err.Error(), "ambiguous") {
		t.Fatalf("expected 'ambiguous' in error, got: %v", err)
	}
	if !strings.Contains(err.Error(), "llama-3-8b-instruct") || !strings.Contains(err.Error(), "llama-3-70b-q4") {
		t.Fatalf("expected both model names in error, got: %v", err)
	}
}

func TestResolve_NoMatches(t *testing.T) {
	mc := &mockClient{responses: []*Response{jsonResponse(testModels)}}
	r := New(mc, "/api/models", "displayName")

	_, err := r.Resolve(context.Background(), "nonexistent-model")
	if err == nil {
		t.Fatal("expected error for no matches")
	}
	if !strings.Contains(err.Error(), "no resource named") {
		t.Fatalf("expected 'no resource named' in error, got: %v", err)
	}
}

func TestResolve_FallbackToNameField(t *testing.T) {
	data := []map[string]any{
		{"id": "aaa111aaa111aaa111aaa111aaa111aa1", "displayName": "", "name": "fallback-name"},
	}
	mc := &mockClient{responses: []*Response{jsonResponse(data)}}
	r := New(mc, "/api/models", "displayName")

	got, err := r.Resolve(context.Background(), "fallback-name")
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if got != "aaa111aaa111aaa111aaa111aaa111aa1" {
		t.Fatalf("expected ID, got %q", got)
	}
}

func TestResolveOptional_EmptyReturnsEmpty(t *testing.T) {
	mc := &mockClient{}
	r := New(mc, "/api/models", "displayName")

	got, err := r.ResolveOptional(context.Background(), "")
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if got != "" {
		t.Fatalf("expected empty string, got %q", got)
	}
	if len(mc.calls) != 0 {
		t.Fatalf("expected no API calls, got %d", len(mc.calls))
	}
}

func TestResolveOptional_WhitespaceReturnsEmpty(t *testing.T) {
	mc := &mockClient{}
	r := New(mc, "/api/models", "displayName")

	got, err := r.ResolveOptional(context.Background(), "  ")
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if got != "" {
		t.Fatalf("expected empty string, got %q", got)
	}
}

func TestResolveOptional_DelegatesToResolve(t *testing.T) {
	mc := &mockClient{responses: []*Response{jsonResponse(testModels)}}
	r := New(mc, "/api/models", "displayName")

	got, err := r.ResolveOptional(context.Background(), "gemma-4-12b-q8")
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if got != "aaa111aaa111aaa111aaa111aaa111aa1" {
		t.Fatalf("expected gemma ID, got %q", got)
	}
}

func TestResolve_CachesAfterFirstLoad(t *testing.T) {
	mc := &mockClient{responses: []*Response{jsonResponse(testModels)}}
	r := New(mc, "/api/models", "displayName")

	_, _ = r.Resolve(context.Background(), "gemma-4-12b-q8")
	_, _ = r.Resolve(context.Background(), "mistral")

	if len(mc.calls) != 1 {
		t.Fatalf("expected 1 API call (cached), got %d", len(mc.calls))
	}
}

func TestClearCache(t *testing.T) {
	// First response has model-a, second has model-b.
	mc := &mockClient{responses: []*Response{
		jsonResponse([]map[string]any{{"id": "id1", "displayName": "model-a"}}),
		jsonResponse([]map[string]any{{"id": "id2", "displayName": "model-b"}}),
	}}
	r := New(mc, "/api/models", "displayName")

	// Resolve from first load.
	got, err := r.Resolve(context.Background(), "model-a")
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if got != "id1" {
		t.Fatalf("expected id1, got %q", got)
	}

	// Clear cache so next call re-fetches.
	r.ClearCache()

	got, err = r.Resolve(context.Background(), "model-b")
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if got != "id2" {
		t.Fatalf("expected id2, got %q", got)
	}

	// Should have made 2 API calls.
	if len(mc.calls) != 2 {
		t.Fatalf("expected 2 API calls, got %d", len(mc.calls))
	}
}

func TestResolve_APIError(t *testing.T) {
	mc := &mockClient{responses: []*Response{httpError(500)}}
	r := New(mc, "/api/models", "displayName")

	_, err := r.Resolve(context.Background(), "some-model")
	if err == nil {
		t.Fatal("expected error on HTTP 500")
	}
	if !strings.Contains(err.Error(), "500") {
		t.Fatalf("expected HTTP 500 in error, got: %v", err)
	}
}

func TestResolve_ClientError(t *testing.T) {
	mc := &mockClient{err: context.DeadlineExceeded}
	r := New(mc, "/api/models", "displayName")

	_, err := r.Resolve(context.Background(), "some-model")
	if err == nil {
		t.Fatal("expected error on client failure")
	}
}

func TestResolve_RuntimesDisplayName(t *testing.T) {
	mc := &mockClient{responses: []*Response{jsonResponse(testRuntimes)}}
	r := New(mc, "/api/containers/registered", "displayName")

	got, err := r.Resolve(context.Background(), "local-gpu")
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if got != "eee555eee555eee555eee555eee555ee5" {
		t.Fatalf("expected runtime ID, got %q", got)
	}
}

func TestResolve_PromptsNameField(t *testing.T) {
	mc := &mockClient{responses: []*Response{jsonResponse(testPrompts)}}
	r := New(mc, "/api/prompts", "name")

	got, err := r.Resolve(context.Background(), "summarize")
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if got != "77777777777777777777777777777777" {
		t.Fatalf("expected prompt ID, got %q", got)
	}
}

func TestResolve_EmptyList(t *testing.T) {
	mc := &mockClient{responses: []*Response{jsonResponse([]map[string]any{})}}
	r := New(mc, "/api/models", "displayName")

	_, err := r.Resolve(context.Background(), "anything")
	if err == nil {
		t.Fatal("expected error on empty list")
	}
}

// ---------------------------------------------------------------------------
// isID unit tests
// ---------------------------------------------------------------------------

func TestIsID(t *testing.T) {
	tests := []struct {
		input string
		want  bool
	}{
		{"52b021aba5a241dca7728e8f5ca665c0", true},
		{"52b021ab-a5a2-41dc-a772-8e8f5ca665c0", true},
		{"ABCDEF0123456789ABCDEF0123456789", true},
		{"abc123", false},                        // too short
		{"", false},                              // empty
		{"hello-world", false},                   // non-hex
		{"zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz", false}, // 'z' is not hex
	}
	for _, tt := range tests {
		got := isID(tt.input)
		if got != tt.want {
			t.Errorf("isID(%q) = %v, want %v", tt.input, got, tt.want)
		}
	}
}

func TestResolve_UnicodeInName(t *testing.T) {
	data := []map[string]any{
		{"id": "aaa111aaa111aaa111aaa111aaa111aa1", "displayName": "model-üñîcödé"},
	}
	mc := &mockClient{responses: []*Response{jsonResponse(data)}}
	r := New(mc, "/api/models", "displayName")

	got, err := r.Resolve(context.Background(), "model-üñîcödé")
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if got != "aaa111aaa111aaa111aaa111aaa111aa1" {
		t.Fatalf("expected ID, got %q", got)
	}
}

func TestResolve_PartialMatchIsSubstring(t *testing.T) {
	mc := &mockClient{responses: []*Response{jsonResponse(testModels)}}
	r := New(mc, "/api/models", "displayName")

	got, err := r.Resolve(context.Background(), "8b")
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if got != "bbb222bbb222bbb222bbb222bbb222bb2" {
		t.Fatalf("expected llama-3-8b ID, got %q", got)
	}
}
