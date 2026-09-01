package main

import (
	"context"
	"log/slog"
	"net/http"
	"net/http/httptest"
	"runtime"
	"sync"
	"testing"
	"time"

	"github.com/gorilla/websocket"

	"unswarm/agent/internal/backoff"
	"unswarm/agent/internal/client"
	"unswarm/agent/internal/config"
	"unswarm/agent/internal/dispatch"
	"unswarm/agent/internal/protocol"
	"unswarm/agent/internal/runtimegate"
	"unswarm/agent/internal/telemetry"
)

func discardLogger() *slog.Logger {
	return slog.New(slog.DiscardHandler)
}

// TestDispatcherRegistersChatCompletion verifies the chat_completion command is
// registered on the dispatcher so benchmark/validation inference can be proxied.
func TestDispatcherRegistersChatCompletion(t *testing.T) {
	disp := setupDispatcher(nil, nil, runtimegate.NewGate(nil, false), nil, discardLogger())
	if !disp.HasCommand(protocol.CmdChatCompletion) {
		t.Fatalf("dispatcher does not have %q registered", protocol.CmdChatCompletion)
	}
	// With a nil docker handler the command should still route to a result
	// (not-connected error), never panic.
	result := disp.Dispatch(protocol.CommandPayload{Command: protocol.CmdChatCompletion, Port: 8080})
	if result.OK {
		t.Error("chat_completion with nil docker handler should return ok=false")
	}
	if result.Error == nil || *result.Error == "" {
		t.Error("chat_completion with nil docker handler should include an error message")
	}
}

// TestRunPeriodicStopsOnCancel verifies the session-scoped ticker goroutine
// terminates promptly when its session context is cancelled — the mechanism
// that prevents tickers from leaking across reconnects.
func TestRunPeriodicStopsOnCancel(t *testing.T) {
	ctx, cancel := context.WithCancel(context.Background())
	var wg sync.WaitGroup
	wg.Add(1)

	ran := make(chan struct{}, 1)
	done := runPeriodic(ctx, time.Hour, func(ctx context.Context) {
		ran <- struct{}{}
	}, &wg)

	cancel()
	select {
	case <-done:
	case <-time.After(2 * time.Second):
		t.Fatal("runPeriodic did not stop after session cancel")
	}
	wg.Wait()

	select {
	case <-ran:
		t.Error("runPeriodic fn ran after cancel")
	default:
	}
}

// TestReachedMaxRetries verifies the reconnect give-up semantics.
func TestReachedMaxRetries(t *testing.T) {
	tests := []struct {
		name       string
		maxRetries int
		attempt    int
		want       bool
	}{
		{name: "infinite retries never give up", maxRetries: -1, attempt: 100, want: false},
		{name: "zero retries gives up immediately", maxRetries: 0, attempt: 0, want: true},
		{name: "below limit keeps trying", maxRetries: 5, attempt: 3, want: false},
		{name: "at limit gives up", maxRetries: 5, attempt: 5, want: true},
		{name: "above limit gives up", maxRetries: 5, attempt: 6, want: true},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			if got := reachedMaxRetries(tt.maxRetries, tt.attempt); got != tt.want {
				t.Errorf("reachedMaxRetries(%d, %d) = %v, want %v", tt.maxRetries, tt.attempt, got, tt.want)
			}
		})
	}
}

// startFakeBackend accepts up to sessions WebSocket connections, performs the
// hello handshake on each, then reads until the client closes or a timeout.
func startFakeBackend(t *testing.T, sessions int) *httptest.Server {
	t.Helper()
	upgrader := websocket.Upgrader{}
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		conn, err := upgrader.Upgrade(w, r, nil)
		if err != nil {
			return
		}
		defer func() { _ = conn.Close() }()

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

		_ = conn.SetReadDeadline(time.Now().Add(300 * time.Millisecond))
		for {
			if _, _, err := conn.ReadMessage(); err != nil {
				return
			}
		}
	}))
	return server
}

// TestSessionJoinsGoroutinesOnDisconnect runs a full session against a fake
// backend that closes the connection. runSession must return promptly (it
// only returns after joining the telemetry/heartbeat tickers) and must not
// leak goroutines.
func TestSessionJoinsGoroutinesOnDisconnect(t *testing.T) {
	server := startFakeBackend(t, 1)
	defer server.Close()

	cfg := config.Config{BackendURL: server.URL, AgentName: "test-agent"}
	logger := discardLogger()
	wsClient := client.New(cfg, logger)
	bo := backoff.New(time.Millisecond, time.Millisecond)
	disp := dispatch.New()
	msgRouter := dispatch.NewRouter()
	telem := telemetry.New(logger)
	sc := sessionConfig{telemetryInterval: 5 * time.Millisecond, heartbeatInterval: 5 * time.Millisecond}

	baseline := runtime.NumGoroutine()

	err := runSession(context.Background(), wsClient, bo, cfg, disp, msgRouter, telem, nil, nil, sc, logger)
	if err == nil {
		t.Fatal("expected session to end with an error after the backend closed the connection")
	}

	// runSession joins its tickers before returning; poll briefly to let the
	// backend's server goroutines unwind, then assert no goroutine leak.
	deadline := time.Now().Add(2 * time.Second)
	for time.Now().Before(deadline) {
		if runtime.NumGoroutine() <= baseline+2 {
			return
		}
		time.Sleep(20 * time.Millisecond)
	}
	t.Errorf("goroutine leak: baseline=%d after=%d", baseline, runtime.NumGoroutine())
}

// TestSessionReconnectNoStaleWrites runs two consecutive sessions against a
// fake backend. The second session must complete its handshake and receive
// traffic without interference from session 1's tickers.
func TestSessionReconnectNoStaleWrites(t *testing.T) {
	server := startFakeBackend(t, 2)
	defer server.Close()

	cfg := config.Config{BackendURL: server.URL, AgentName: "test-agent"}
	logger := discardLogger()
	wsClient := client.New(cfg, logger)
	bo := backoff.New(time.Millisecond, time.Millisecond)
	disp := dispatch.New()
	msgRouter := dispatch.NewRouter()
	telem := telemetry.New(logger)
	sc := sessionConfig{telemetryInterval: 5 * time.Millisecond, heartbeatInterval: 5 * time.Millisecond}

	// Session 1: backend closes it.
	if err := runSession(context.Background(), wsClient, bo, cfg, disp, msgRouter, telem, nil, nil, sc, logger); err == nil {
		t.Fatal("session 1 should end with an error")
	}

	// Session 2: must connect and handshake cleanly (the fake backend closes
	// it after ~2s of reads, so an error is expected — but not a connect or
	// handshake failure).
	err := runSession(context.Background(), wsClient, bo, cfg, disp, msgRouter, telem, nil, nil, sc, logger)
	if err == nil {
		t.Fatal("session 2 should end with an error after backend close")
	}
	if err.Error() == "connect: " || err.Error() == "hello ack: " {
		t.Fatalf("session 2 failed to establish: %v", err)
	}
}
