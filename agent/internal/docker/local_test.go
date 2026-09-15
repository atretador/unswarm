package docker

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
)

// ---------------------------------------------------------------------------
// PortAllowlist.Check
// ---------------------------------------------------------------------------

func TestPortAllowlist_Check_EmptyAllowsAll(t *testing.T) {
	var allow PortAllowlist
	if err := allow.Check(12345); err != nil {
		t.Errorf("empty allowlist should allow any port, got error: %v", err)
	}
}

func TestPortAllowlist_Check_NilAllowsAll(t *testing.T) {
	var allow PortAllowlist = nil
	if err := allow.Check(9999); err != nil {
		t.Errorf("nil allowlist should allow any port, got error: %v", err)
	}
}

func TestPortAllowlist_Check_Allowed(t *testing.T) {
	allow := PortAllowlist{8080, 9090}
	if err := allow.Check(8080); err != nil {
		t.Errorf("port 8080 should be allowed, got: %v", err)
	}
	if err := allow.Check(9090); err != nil {
		t.Errorf("port 9090 should be allowed, got: %v", err)
	}
}

func TestPortAllowlist_Check_Denied(t *testing.T) {
	allow := PortAllowlist{8080, 9090}
	err := allow.Check(3000)
	if err == nil {
		t.Fatal("port 3000 should be denied")
	}
	if !strings.Contains(err.Error(), "3000") {
		t.Errorf("error should mention port 3000, got: %v", err)
	}
	if !strings.Contains(err.Error(), "not in allowed_loopback_ports") {
		t.Errorf("error should mention allowed_loopback_ports, got: %v", err)
	}
}

func TestPortAllowlist_Check_SingleElement(t *testing.T) {
	allow := PortAllowlist{42}
	if err := allow.Check(42); err != nil {
		t.Errorf("port 42 should be allowed, got: %v", err)
	}
	if err := allow.Check(43); err == nil {
		t.Error("port 43 should be denied")
	}
}

// ---------------------------------------------------------------------------
// HealthCheck
// ---------------------------------------------------------------------------

func TestHealthCheck_InvalidPort(t *testing.T) {
	ctx := context.Background()
	r := HealthCheck(ctx, nil, 0)
	if r.OK {
		t.Error("port 0 should fail")
	}
	if r.Error == nil || !strings.Contains(*r.Error, "invalid port") {
		t.Errorf("expected 'invalid port' error, got: %v", r.Error)
	}
}

func TestHealthCheck_NegativePort(t *testing.T) {
	ctx := context.Background()
	r := HealthCheck(ctx, nil, -1)
	if r.OK {
		t.Error("negative port should fail")
	}
}

func TestHealthCheck_PortBlockedByAllowlist(t *testing.T) {
	ctx := context.Background()
	allow := PortAllowlist{9090}
	r := HealthCheck(ctx, allow, 8080)
	if r.OK {
		t.Error("blocked port should fail")
	}
	if r.Error == nil || !strings.Contains(*r.Error, "not in allowed_loopback_ports") {
		t.Errorf("expected allowlist error, got: %v", r.Error)
	}
}

func TestHealthCheck_UnreachablePort(t *testing.T) {
	ctx := context.Background()
	// Use a high port that nothing is listening on.
	r := HealthCheck(ctx, nil, 19999)
	if !r.OK {
		t.Fatalf("HealthCheck should succeed (ok=true) even when unhealthy, got: %v", r.Error)
	}
	data, ok := r.Data.(map[string]interface{})
	if !ok {
		t.Fatalf("expected map data, got %T", r.Data)
	}
	if data["healthy"] != false {
		t.Errorf("expected healthy=false, got %v", data["healthy"])
	}
	if data["port"] != 19999 {
		t.Errorf("expected port=19999, got %v", data["port"])
	}
}

func TestHealthCheck_HealthyServer(t *testing.T) {
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.WriteHeader(http.StatusOK)
		_, _ = fmt.Fprintln(w, "ok")
	}))
	defer srv.Close()

	// Extract port from srv.URL (http://127.0.0.1:PORT)
	var port int
	_, _ = fmt.Sscanf(srv.URL, "http://127.0.0.1:%d", &port)

	ctx := context.Background()
	r := HealthCheck(ctx, nil, port)
	if !r.OK {
		t.Fatalf("HealthCheck failed: %v", r.Error)
	}
	data := r.Data.(map[string]interface{})
	if data["healthy"] != true {
		t.Errorf("expected healthy=true, got %v", data["healthy"])
	}
	if data["port"] != port {
		t.Errorf("expected port=%d, got %v", port, data["port"])
	}
	statusCode, ok := data["statusCode"].(int)
	if !ok {
		t.Fatalf("expected statusCode to be int, got %T", data["statusCode"])
	}
	if statusCode != 200 {
		t.Errorf("expected statusCode=200, got %d", statusCode)
	}
}

// ---------------------------------------------------------------------------
// DiscoverModels
// ---------------------------------------------------------------------------

func TestDiscoverModels_InvalidPort(t *testing.T) {
	ctx := context.Background()
	r := DiscoverModels(ctx, nil, 0)
	if r.OK {
		t.Error("port 0 should fail")
	}
	if r.Error == nil || !strings.Contains(*r.Error, "invalid port") {
		t.Errorf("expected 'invalid port' error, got: %v", r.Error)
	}
}

func TestDiscoverModels_PortBlocked(t *testing.T) {
	ctx := context.Background()
	allow := PortAllowlist{9090}
	r := DiscoverModels(ctx, allow, 8080)
	if r.OK {
		t.Error("blocked port should fail")
	}
}

func TestDiscoverModels_Success(t *testing.T) {
	expected := map[string]interface{}{
		"object": "list",
		"data": []interface{}{
			map[string]interface{}{
				"id":       "gpt-4",
				"object":   "model",
				"owned_by": "openai",
			},
			map[string]interface{}{
				"id":       "gpt-3.5-turbo",
				"object":   "model",
				"owned_by": "openai",
			},
		},
	}

	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.URL.Path != "/v1/models" {
			t.Errorf("unexpected path: %s", r.URL.Path)
			w.WriteHeader(http.StatusNotFound)
			return
		}
		w.Header().Set("Content-Type", "application/json")
		_ = json.NewEncoder(w).Encode(expected)
	}))
	defer srv.Close()

	var port int
	_, _ = fmt.Sscanf(srv.URL, "http://127.0.0.1:%d", &port)

	ctx := context.Background()
	r := DiscoverModels(ctx, nil, port)
	if !r.OK {
		t.Fatalf("DiscoverModels failed: %v", r.Error)
	}
	data, ok := r.Data.(map[string]interface{})
	if !ok {
		t.Fatalf("expected map data, got %T", r.Data)
	}
	if data["object"] != "list" {
		t.Errorf("expected object=list, got %v", data["object"])
	}
}

func TestDiscoverModels_HTTPError(t *testing.T) {
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.WriteHeader(http.StatusInternalServerError)
		_, _ = fmt.Fprintln(w, "internal error")
	}))
	defer srv.Close()

	var port int
	_, _ = fmt.Sscanf(srv.URL, "http://127.0.0.1:%d", &port)

	ctx := context.Background()
	r := DiscoverModels(ctx, nil, port)
	// Server returns 500 but JSON decode of the body may succeed or fail.
	// Either way, the function doesn't return an HTTP-status error for
	// DiscoverModels — it tries to decode the body. If decoding fails we
	// get an error result.
	if r.OK {
		t.Log("DiscoverModels returned ok=true — server response was decodable")
	} else {
		t.Logf("DiscoverModels returned error (expected for non-JSON 500): %v", r.Error)
	}
}

func TestDiscoverModels_UnreachablePort(t *testing.T) {
	ctx := context.Background()
	r := DiscoverModels(ctx, nil, 19998)
	if r.OK {
		t.Fatalf("expected error for unreachable port, got ok=true")
	}
	if r.Error == nil || !strings.Contains(*r.Error, "discover models") {
		t.Errorf("expected discover models error, got: %v", r.Error)
	}
}

// ---------------------------------------------------------------------------
// ChatCompletion
// ---------------------------------------------------------------------------

func TestChatCompletion_InvalidPort(t *testing.T) {
	ctx := context.Background()
	r := ChatCompletion(ctx, nil, 0, json.RawMessage(`{}`))
	if r.OK {
		t.Error("port 0 should fail")
	}
}

func TestChatCompletion_PortBlocked(t *testing.T) {
	ctx := context.Background()
	allow := PortAllowlist{9090}
	r := ChatCompletion(ctx, allow, 8080, json.RawMessage(`{}`))
	if r.OK {
		t.Error("blocked port should fail")
	}
}

func TestChatCompletion_EmptyBody(t *testing.T) {
	ctx := context.Background()
	r := ChatCompletion(ctx, nil, 8080, nil)
	if r.OK {
		t.Error("empty body should fail")
	}
	if r.Error == nil || !strings.Contains(*r.Error, "empty chat completion body") {
		t.Errorf("expected 'empty body' error, got: %v", r.Error)
	}
}

func TestChatCompletion_EmptyBody_ZeroLength(t *testing.T) {
	ctx := context.Background()
	r := ChatCompletion(ctx, nil, 8080, json.RawMessage{})
	if r.OK {
		t.Error("zero-length body should fail")
	}
}

func TestChatCompletion_Success(t *testing.T) {
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.Method != http.MethodPost {
			t.Errorf("expected POST, got %s", r.Method)
		}
		if r.URL.Path != "/v1/chat/completions" {
			t.Errorf("unexpected path: %s", r.URL.Path)
		}

		// Read and verify body
		body, err := io.ReadAll(r.Body)
		if err != nil {
			t.Errorf("read body: %v", err)
		}

		w.Header().Set("Content-Type", "application/json")
		w.WriteHeader(http.StatusOK)
		// Echo the request body back as response
		_, _ = w.Write(body)
	}))
	defer srv.Close()

	var port int
	_, _ = fmt.Sscanf(srv.URL, "http://127.0.0.1:%d", &port)

	ctx := context.Background()
	reqBody := json.RawMessage(`{"model":"gpt-4","messages":[{"role":"user","content":"hi"}]}`)
	r := ChatCompletion(ctx, nil, port, reqBody)
	if !r.OK {
		t.Fatalf("ChatCompletion failed: %v", r.Error)
	}
	raw, ok := r.Data.(string)
	if !ok {
		t.Fatalf("expected string data, got %T", r.Data)
	}
	if !strings.Contains(raw, "gpt-4") {
		t.Errorf("response should echo request, got: %s", raw)
	}
}

func TestChatCompletion_Server500(t *testing.T) {
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.WriteHeader(http.StatusInternalServerError)
		_, _ = fmt.Fprintln(w, `{"error":"internal server error"}`)
	}))
	defer srv.Close()

	var port int
	_, _ = fmt.Sscanf(srv.URL, "http://127.0.0.1:%d", &port)

	ctx := context.Background()
	r := ChatCompletion(ctx, nil, port, json.RawMessage(`{"model":"gpt-4"}`))
	if r.OK {
		t.Error("HTTP 500 should return error result")
	}
	if r.Error == nil || !strings.Contains(*r.Error, "500") {
		t.Errorf("error should mention status 500, got: %v", r.Error)
	}
}

func TestChatCompletion_ContextCancelled(t *testing.T) {
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		// Block until context is cancelled
		<-r.Context().Done()
	}))
	defer srv.Close()

	var port int
	_, _ = fmt.Sscanf(srv.URL, "http://127.0.0.1:%d", &port)

	ctx, cancel := context.WithCancel(context.Background())
	cancel() // cancel immediately

	r := ChatCompletion(ctx, nil, port, json.RawMessage(`{"model":"gpt-4"}`))
	if r.OK {
		t.Error("cancelled context should return error")
	}
	if r.Error == nil || !strings.Contains(*r.Error, "cancelled") {
		t.Errorf("error should mention 'cancelled', got: %v", r.Error)
	}
}

func TestChatCompletion_UnreachablePort(t *testing.T) {
	ctx := context.Background()
	r := ChatCompletion(ctx, nil, 19997, json.RawMessage(`{"model":"gpt-4"}`))
	if r.OK {
		t.Error("unreachable port should fail")
	}
}

// ---------------------------------------------------------------------------
// ChatCompletionStream
// ---------------------------------------------------------------------------

func TestChatCompletionStream_InvalidPort(t *testing.T) {
	ctx := context.Background()
	err := ChatCompletionStream(ctx, nil, 0, "{}", func(chunk []byte) error { return nil })
	if err == nil {
		t.Error("port 0 should fail")
	}
	if !strings.Contains(err.Error(), "invalid port") {
		t.Errorf("expected 'invalid port' error, got: %v", err)
	}
}

func TestChatCompletionStream_PortBlocked(t *testing.T) {
	ctx := context.Background()
	allow := PortAllowlist{9090}
	err := ChatCompletionStream(ctx, allow, 8080, "{}", func(chunk []byte) error { return nil })
	if err == nil {
		t.Error("blocked port should fail")
	}
}

func TestChatCompletionStream_EmptyBody(t *testing.T) {
	ctx := context.Background()
	err := ChatCompletionStream(ctx, nil, 8080, "", func(chunk []byte) error { return nil })
	if err == nil {
		t.Error("empty body should fail")
	}
	if !strings.Contains(err.Error(), "empty chat completion body") {
		t.Errorf("expected 'empty body' error, got: %v", err)
	}
}

func TestChatCompletionStream_Success(t *testing.T) {
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.Method != http.MethodPost {
			t.Errorf("expected POST, got %s", r.Method)
		}
		w.Header().Set("Content-Type", "text/event-stream")
		w.WriteHeader(http.StatusOK)
		// Write several chunks to simulate streaming
		for i := 0; i < 3; i++ {
			_, _ = fmt.Fprintf(w, "chunk-%d\n", i)
			if f, ok := w.(http.Flusher); ok {
				f.Flush()
			}
		}
	}))
	defer srv.Close()

	var port int
	_, _ = fmt.Sscanf(srv.URL, "http://127.0.0.1:%d", &port)

	ctx := context.Background()
	var chunks []string
	err := ChatCompletionStream(ctx, nil, port, `{"model":"gpt-4"}`, func(chunk []byte) error {
		chunks = append(chunks, string(chunk))
		return nil
	})
	if err != nil {
		t.Fatalf("ChatCompletionStream failed: %v", err)
	}
	if len(chunks) == 0 {
		t.Fatal("expected at least one chunk")
	}
	// Verify the combined output contains our chunks
	combined := strings.Join(chunks, "")
	for i := 0; i < 3; i++ {
		expected := fmt.Sprintf("chunk-%d", i)
		if !strings.Contains(combined, expected) {
			t.Errorf("combined output should contain %q, got: %s", expected, combined)
		}
	}
}

func TestChatCompletionStream_ServerError(t *testing.T) {
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.WriteHeader(http.StatusBadRequest)
		_, _ = fmt.Fprintln(w, `{"error":"bad request"}`)
	}))
	defer srv.Close()

	var port int
	_, _ = fmt.Sscanf(srv.URL, "http://127.0.0.1:%d", &port)

	ctx := context.Background()
	err := ChatCompletionStream(ctx, nil, port, `{"model":"gpt-4"}`, func(chunk []byte) error { return nil })
	if err == nil {
		t.Error("HTTP 400 should return error")
	}
	if !strings.Contains(err.Error(), "400") {
		t.Errorf("error should mention status 400, got: %v", err)
	}
}

func TestChatCompletionStream_ContextCancelled(t *testing.T) {
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		<-r.Context().Done()
	}))
	defer srv.Close()

	var port int
	_, _ = fmt.Sscanf(srv.URL, "http://127.0.0.1:%d", &port)

	ctx, cancel := context.WithCancel(context.Background())
	cancel()

	err := ChatCompletionStream(ctx, nil, port, `{"model":"gpt-4"}`, func(chunk []byte) error { return nil })
	if err == nil {
		t.Error("cancelled context should return error")
	}
	if !strings.Contains(err.Error(), "cancelled") {
		t.Errorf("error should mention 'cancelled', got: %v", err)
	}
}

func TestChatCompletionStream_EmitError(t *testing.T) {
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.WriteHeader(http.StatusOK)
		_, _ = fmt.Fprintln(w, "data")
	}))
	defer srv.Close()

	var port int
	_, _ = fmt.Sscanf(srv.URL, "http://127.0.0.1:%d", &port)

	ctx := context.Background()
	emitErr := errors.New("emit failed")
	err := ChatCompletionStream(ctx, nil, port, `{"model":"gpt-4"}`, func(chunk []byte) error {
		return emitErr
	})
	if err == nil {
		t.Error("emit error should propagate")
	}
	if !strings.Contains(err.Error(), "emit chat completion stream chunk") {
		t.Errorf("error should mention emit, got: %v", err)
	}
}

func TestChatCompletionStream_UnreachablePort(t *testing.T) {
	ctx := context.Background()
	err := ChatCompletionStream(ctx, nil, 19996, `{"model":"gpt-4"}`, func(chunk []byte) error { return nil })
	if err == nil {
		t.Error("unreachable port should fail")
	}
}
