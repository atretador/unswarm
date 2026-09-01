package client

import (
	"context"
	"log/slog"
	"net/http"
	"net/http/httptest"
	"strings"
	"sync"
	"testing"
	"time"

	"github.com/gorilla/websocket"

	"unswarm/agent/internal/config"
	"unswarm/agent/internal/protocol"
)

func discardLogger() *slog.Logger {
	return slog.New(slog.DiscardHandler)
}

// startTestServer runs a fake backend that performs the hello handshake and
// forwards every subsequent message to onMessage (if non-nil).
func startTestServer(t *testing.T, apiKey string, onMessage func(protocol.Envelope)) *httptest.Server {
	t.Helper()
	upgrader := websocket.Upgrader{}
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if apiKey != "" {
			if r.Header.Get("X-Api-Key") != apiKey || r.Header.Get("Authorization") != "Bearer "+apiKey {
				http.Error(w, "unauthorized", http.StatusUnauthorized)
				return
			}
		}
		conn, err := upgrader.Upgrade(w, r, nil)
		if err != nil {
			return
		}
		defer func() { _ = conn.Close() }()

		// Hello handshake: read hello, reply with hello ack.
		_, data, err := conn.ReadMessage()
		if err != nil {
			return
		}
		env, err := protocol.DecodeEnvelope(data)
		if err != nil || env.Type != protocol.TypeHello {
			return
		}
		ack := protocol.MustEnvelope(protocol.TypeHello, nil, nil, protocol.HelloAckPayload{OK: true})
		ackData, _ := ack.Encode()
		if err := conn.WriteMessage(websocket.TextMessage, ackData); err != nil {
			return
		}

		// Read until the client closes.
		for {
			_, data, err := conn.ReadMessage()
			if err != nil {
				return
			}
			if onMessage != nil {
				if env, derr := protocol.DecodeEnvelope(data); derr == nil {
					onMessage(env)
				}
			}
		}
	}))
	return server
}

func TestBuildURL(t *testing.T) {
	tests := []struct {
		name    string
		backend string
		want    string
		wantErr bool
	}{
		{name: "ws default", backend: "ws://localhost:5014", want: "ws://localhost:5014/ws/agent"},
		{name: "http converts to ws", backend: "http://backend.example.com:8080", want: "ws://backend.example.com:8080/ws/agent"},
		{name: "https converts to wss", backend: "https://backend.example.com", want: "wss://backend.example.com/ws/agent"},
		{name: "wss passthrough", backend: "wss://backend.example.com:8443", want: "wss://backend.example.com:8443/ws/agent"},
		{name: "existing path unchanged", backend: "ws://localhost:5014/ws/agent", want: "ws://localhost:5014/ws/agent"},
		{name: "trailing slash trimmed", backend: "ws://localhost:5014/ws/agent/", want: "ws://localhost:5014/ws/agent"},
		{name: "custom path appended", backend: "ws://localhost:5014/custom", want: "ws://localhost:5014/custom/ws/agent"},
		{name: "unsupported scheme", backend: "ftp://example.com", wantErr: true},
		{name: "unparseable url", backend: "://bad", wantErr: true},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			c := New(config.Config{BackendURL: tt.backend}, discardLogger())
			got, err := c.buildURL()
			if tt.wantErr {
				if err == nil {
					t.Fatalf("buildURL() = %q, want error", got)
				}
				return
			}
			if err != nil {
				t.Fatalf("buildURL() error: %v", err)
			}
			if got != tt.want {
				t.Errorf("buildURL() = %q, want %q", got, tt.want)
			}
		})
	}
}

func TestLoggableURL(t *testing.T) {
	tests := []struct {
		in   string
		want string
	}{
		{in: "ws://localhost:5014", want: "localhost:5014"},
		{in: "wss://user:pass@backend.example.com:8443/ws/agent", want: "backend.example.com:8443"},
		{in: "not a url", want: "not a url"},
	}
	for _, tt := range tests {
		if got := LoggableURL(tt.in); got != tt.want {
			t.Errorf("LoggableURL(%q) = %q, want %q", tt.in, got, tt.want)
		}
	}
}

func TestConnectSendCloseLifecycle(t *testing.T) {
	got := make(chan protocol.Envelope, 8)
	server := startTestServer(t, "", func(env protocol.Envelope) { got <- env })
	defer server.Close()

	cfg := config.Config{
		BackendURL:   server.URL,
		AgentName:    "test-agent",
		DockerSocket: "unix:///var/run/docker.sock",
	}
	c := New(cfg, discardLogger())
	ctx := context.Background()

	if err := c.Connect(ctx); err != nil {
		t.Fatalf("Connect: %v", err)
	}
	if !c.IsConnected() {
		t.Error("IsConnected() = false after Connect")
	}

	if err := c.SendHello(ctx, "0.1.0"); err != nil {
		t.Fatalf("SendHello: %v", err)
	}
	if err := c.WaitForHelloAck(ctx); err != nil {
		t.Fatalf("WaitForHelloAck: %v", err)
	}

	hb := protocol.MustEnvelope(protocol.TypeHeartbeat, nil, nil, nil)
	if err := c.Send(ctx, hb); err != nil {
		t.Fatalf("Send heartbeat: %v", err)
	}

	// The server should have received the heartbeat after the handshake.
	select {
	case env := <-got:
		if env.Type != protocol.TypeHeartbeat {
			t.Errorf("server received type %q, want heartbeat", env.Type)
		}
	case <-time.After(2 * time.Second):
		t.Fatal("server did not receive heartbeat")
	}

	c.Close()
	if c.IsConnected() {
		t.Error("IsConnected() = true after Close")
	}
}

func TestConnectSendsAPIKeyHeaders(t *testing.T) {
	server := startTestServer(t, "secret-key", nil)
	defer server.Close()

	cfg := config.Config{BackendURL: server.URL, APIKey: "secret-key", AgentName: "test-agent"}
	c := New(cfg, discardLogger())
	ctx := context.Background()

	if err := c.Connect(ctx); err != nil {
		t.Fatalf("Connect with API key: %v", err)
	}
	c.Close()
}

func TestSendWithCancelledContext(t *testing.T) {
	server := startTestServer(t, "", nil)
	defer server.Close()

	c := New(config.Config{BackendURL: server.URL}, discardLogger())
	ctx, cancel := context.WithCancel(context.Background())
	if err := c.Connect(ctx); err != nil {
		t.Fatalf("Connect: %v", err)
	}
	cancel()

	env := protocol.MustEnvelope(protocol.TypeHeartbeat, nil, nil, nil)
	if err := c.Send(ctx, env); err == nil {
		t.Error("Send() with cancelled context should fail")
	}
	c.Close()
}

func TestSendNotConnected(t *testing.T) {
	c := New(config.Config{BackendURL: "ws://localhost:1"}, discardLogger())
	env := protocol.MustEnvelope(protocol.TypeHeartbeat, nil, nil, nil)
	if err := c.Send(context.Background(), env); err == nil {
		t.Error("Send() before Connect should fail")
	}
}

// TestSendConcurrentCloseNoPanic stresses Send against a concurrent Close.
// Run under -race this also verifies the conn field is properly synchronized.
func TestSendConcurrentCloseNoPanic(t *testing.T) {
	server := startTestServer(t, "", nil)
	defer server.Close()

	c := New(config.Config{BackendURL: server.URL}, discardLogger())
	ctx := context.Background()
	if err := c.Connect(ctx); err != nil {
		t.Fatalf("Connect: %v", err)
	}

	var wg sync.WaitGroup
	stop := make(chan struct{})
	for i := 0; i < 4; i++ {
		wg.Add(1)
		go func() {
			defer wg.Done()
			for {
				select {
				case <-stop:
					return
				default:
					env := protocol.MustEnvelope(protocol.TypeHeartbeat, nil, nil, nil)
					// Errors are expected after Close; panics are not.
					_ = c.Send(ctx, env)
				}
			}
		}()
	}

	time.Sleep(20 * time.Millisecond)
	c.Close()
	close(stop)
	wg.Wait()
}

func TestConnectRejectsUnsupportedScheme(t *testing.T) {
	c := New(config.Config{BackendURL: "ftp://example.com"}, discardLogger())
	if err := c.Connect(context.Background()); err == nil {
		t.Fatal("Connect() with unsupported scheme should fail")
	} else if !strings.Contains(err.Error(), "unsupported scheme") {
		t.Errorf("unexpected error: %v", err)
	}
}
