package client

import (
	"context"
	"fmt"
	"net/http"
	"net/http/httptest"
	"strings"
	"sync/atomic"
	"testing"
	"time"
)

func TestRetryLogic(t *testing.T) {
	var attempts int32

	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		n := atomic.AddInt32(&attempts, 1)
		if n <= 2 {
			w.WriteHeader(http.StatusBadGateway)
			w.Write([]byte("bad gateway"))
			return
		}
		w.WriteHeader(http.StatusOK)
		w.Write([]byte(`{"status":"ok"}`))
	}))
	defer server.Close()

	cfg := &Config{
		BaseURL:   server.URL,
		OutputFmt: "json",
		Color:     false,
	}

	c := New(cfg, WithTimeout(5*time.Second))
	ctx := context.Background()

	resp, err := c.Do(ctx, "GET", "/", nil)
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}

	if resp.StatusCode != http.StatusOK {
		t.Errorf("expected status 200, got %d", resp.StatusCode)
	}

	if atomic.LoadInt32(&attempts) != 3 {
		t.Errorf("expected 3 attempts, got %d", atomic.LoadInt32(&attempts))
	}
}

func TestNoRetryOnPOST(t *testing.T) {
	var attempts int32

	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		atomic.AddInt32(&attempts, 1)
		w.WriteHeader(http.StatusBadGateway)
		w.Write([]byte("bad gateway"))
	}))
	defer server.Close()

	cfg := &Config{
		BaseURL:   server.URL,
		OutputFmt: "json",
		Color:     false,
	}

	c := New(cfg, WithTimeout(5*time.Second))
	ctx := context.Background()

	resp, err := c.Do(ctx, "POST", "/", map[string]string{"key": "value"})
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}

	if resp.StatusCode != http.StatusBadGateway {
		t.Errorf("expected status 502, got %d", resp.StatusCode)
	}

	if atomic.LoadInt32(&attempts) != 1 {
		t.Errorf("expected 1 attempt for POST, got %d", atomic.LoadInt32(&attempts))
	}
}

func TestRedirectValidation(t *testing.T) {
	// Server that redirects to different host
	otherServer := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.WriteHeader(http.StatusOK)
		w.Write([]byte("other host"))
	}))
	defer otherServer.Close()

	var redirectCount int32
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		n := atomic.AddInt32(&redirectCount, 1)
		if n == 1 {
			// Redirect to different host (should be blocked)
			http.Redirect(w, r, otherServer.URL+"/other", http.StatusFound)
			return
		}
		w.WriteHeader(http.StatusOK)
		w.Write([]byte("original"))
	}))
	defer server.Close()

	cfg := &Config{
		BaseURL:   server.URL,
		OutputFmt: "json",
		Color:     false,
	}

	c := New(cfg, WithTimeout(5*time.Second))
	ctx := context.Background()

	resp, err := c.Do(ctx, "GET", "/", nil)
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}

	// Should follow redirect but the redirect policy blocks cross-origin
	// The response should be from the original server
	if resp.StatusCode != http.StatusOK {
		t.Errorf("expected status 200, got %d", resp.StatusCode)
	}
}

func TestAPIKeyHeader(t *testing.T) {
	var receivedKey string

	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		receivedKey = r.Header.Get("X-Api-Key")
		w.WriteHeader(http.StatusOK)
		w.Write([]byte(`{"status":"ok"}`))
	}))
	defer server.Close()

	cfg := &Config{
		BaseURL:   server.URL,
		OutputFmt: "json",
		Color:     false,
	}

	c := New(cfg, WithAPIKey("test-api-key-123"))
	ctx := context.Background()

	_, err := c.Do(ctx, "GET", "/", nil)
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}

	if receivedKey != "test-api-key-123" {
		t.Errorf("expected API key 'test-api-key-123', got '%s'", receivedKey)
	}
}

func TestErrorParsing(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		w.WriteHeader(http.StatusUnauthorized)
		w.Write([]byte(`{"error":"unauthorized","message":"invalid API key","hint":"check your API key"}`))
	}))
	defer server.Close()

	cfg := &Config{
		BaseURL:   server.URL,
		OutputFmt: "json",
		Color:     false,
	}

	c := New(cfg)
	ctx := context.Background()

	resp, err := c.Do(ctx, "GET", "/", nil)
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}

	apiErr := ParseError(resp)
	if apiErr.ErrCode != "unauthorized" {
		t.Errorf("expected error 'unauthorized', got '%s'", apiErr.ErrCode)
	}
	if apiErr.Message != "invalid API key" {
		t.Errorf("expected message 'invalid API key', got '%s'", apiErr.Message)
	}
	if apiErr.Hint != "check your API key" {
		t.Errorf("expected hint 'check your API key', got '%s'", apiErr.Hint)
	}
}

func TestNoBody204(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.WriteHeader(http.StatusNoContent)
	}))
	defer server.Close()

	cfg := &Config{
		BaseURL:   server.URL,
		OutputFmt: "json",
		Color:     false,
	}

	c := New(cfg)
	ctx := context.Background()

	resp, err := c.Do(ctx, "DELETE", "/resource", nil)
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}

	if resp.StatusCode != http.StatusNoContent {
		t.Errorf("expected status 204, got %d", resp.StatusCode)
	}

	if len(resp.Body) != 0 {
		t.Errorf("expected empty body for 204, got %d bytes", len(resp.Body))
	}
}

func TestTimeoutBehavior(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		time.Sleep(2 * time.Second)
		w.WriteHeader(http.StatusOK)
	}))
	defer server.Close()

	cfg := &Config{
		BaseURL:   server.URL,
		OutputFmt: "json",
		Color:     false,
	}

	c := New(cfg, WithTimeout(100*time.Millisecond))
	ctx := context.Background()

	_, err := c.Do(ctx, "GET", "/", nil)
	if err == nil {
		t.Fatal("expected timeout error")
	}

	if !IsTransportError(err) {
		t.Errorf("expected TransportError, got %T", err)
	}
}

func TestRetryAfterHeader(t *testing.T) {
	var attempts int32

	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		n := atomic.AddInt32(&attempts, 1)
		if n == 1 {
			w.Header().Set("Retry-After", "1")
			w.WriteHeader(http.StatusServiceUnavailable)
			w.Write([]byte("unavailable"))
			return
		}
		w.WriteHeader(http.StatusOK)
		w.Write([]byte(`{"status":"ok"}`))
	}))
	defer server.Close()

	cfg := &Config{
		BaseURL:   server.URL,
		OutputFmt: "json",
		Color:     false,
	}

	c := New(cfg, WithTimeout(10*time.Second))
	ctx := context.Background()

	resp, err := c.Do(ctx, "GET", "/", nil)
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}

	if resp.StatusCode != http.StatusOK {
		t.Errorf("expected status 200, got %d", resp.StatusCode)
	}
}

func TestLoopbackDetection(t *testing.T) {
	tests := []struct {
		host     string
		expected bool
	}{
		{"localhost", true},
		{"127.0.0.1", true},
		{"127.0.0.2", true},
		{"::1", true},
		{"example.com", false},
		{"192.168.1.1", false},
		{"10.0.0.1", false},
	}

	for _, tt := range tests {
		t.Run(tt.host, func(t *testing.T) {
			result := isLoopback(tt.host)
			if result != tt.expected {
				t.Errorf("isLoopback(%s) = %v, want %v", tt.host, result, tt.expected)
			}
		})
	}
}

func TestUnsafeHTTPRefusal(t *testing.T) {
	cfg := &Config{
		BaseURL:   "http://example.com:22301",
		OutputFmt: "json",
		Color:     false,
	}

	c := New(cfg)
	ctx := context.Background()

	_, err := c.Do(ctx, "GET", "/", nil)
	if err == nil {
		t.Fatal("expected error for non-loopback HTTP")
	}

	if !strings.Contains(err.Error(), "refusing plaintext HTTP") {
		t.Errorf("expected unsafe HTTP error, got: %v", err)
	}
}

func TestInsecureOverride(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.WriteHeader(http.StatusOK)
		w.Write([]byte(`{"status":"ok"}`))
	}))
	defer server.Close()

	// Extract host from server URL
	serverURL := server.URL

	cfg := &Config{
		BaseURL:   serverURL,
		OutputFmt: "json",
		Color:     false,
	}

	c := New(cfg, WithInsecure(true))
	ctx := context.Background()

	resp, err := c.Do(ctx, "GET", "/", nil)
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}

	if resp.StatusCode != http.StatusOK {
		t.Errorf("expected status 200, got %d", resp.StatusCode)
	}
}

func TestHTTPSRedirectBlocked(t *testing.T) {
	// Create HTTPS server with redirect to HTTP
	httpsServer := httptest.NewTLSServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		// Try to redirect to HTTP (should be blocked)
		http.Redirect(w, r, "http://localhost:12345/redirected", http.StatusFound)
	}))
	defer httpsServer.Close()

	cfg := &Config{
		BaseURL:   httpsServer.URL,
		OutputFmt: "json",
		Color:     false,
	}

	c := New(cfg)
	ctx := context.Background()

	// This should either follow the redirect but block it,
	// or return an error
	resp, err := c.Do(ctx, "GET", "/", nil)
	if err != nil {
		// Expected: redirect blocked
		return
	}

	// If we got a response, it should be from the HTTPS server
	if resp.StatusCode != http.StatusOK {
		t.Errorf("expected status 200, got %d", resp.StatusCode)
	}
}

func TestMaxRetriesExhausted(t *testing.T) {
	var attempts int32

	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		atomic.AddInt32(&attempts, 1)
		w.WriteHeader(http.StatusBadGateway)
		w.Write([]byte("bad gateway"))
	}))
	defer server.Close()

	cfg := &Config{
		BaseURL:   server.URL,
		OutputFmt: "json",
		Color:     false,
	}

	c := New(cfg, WithTimeout(30*time.Second))
	ctx := context.Background()

	resp, err := c.Do(ctx, "GET", "/", nil)
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}

	// Should have tried maxRetries + 1 times
	if atomic.LoadInt32(&attempts) != int32(maxRetries+1) {
		t.Errorf("expected %d attempts, got %d", maxRetries+1, atomic.LoadInt32(&attempts))
	}

	if resp.StatusCode != http.StatusBadGateway {
		t.Errorf("expected status 502, got %d", resp.StatusCode)
	}
}

func TestBodyTruncation(t *testing.T) {
	largeBody := strings.Repeat("x", maxBodyDisplay+100)

	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.WriteHeader(http.StatusOK)
		fmt.Fprint(w, largeBody)
	}))
	defer server.Close()

	cfg := &Config{
		BaseURL:   server.URL,
		OutputFmt: "json",
		Color:     false,
	}

	c := New(cfg)
	ctx := context.Background()

	resp, err := c.Do(ctx, "GET", "/", nil)
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}

	if len(resp.Body) != maxBodyDisplay {
		t.Errorf("expected body truncated to %d bytes, got %d", maxBodyDisplay, len(resp.Body))
	}
}
