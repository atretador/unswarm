// Package client provides a WebSocket client with automatic reconnection.
package client

import (
	"context"
	"encoding/json"
	"fmt"
	"log/slog"
	"net/http"
	"net/url"
	"strings"
	"sync"
	"time"

	"github.com/gorilla/websocket"

	"unswarm/agent/internal/config"
	"unswarm/agent/internal/protocol"
)

// readTimeout is how long the connection may stay silent before it is
// considered dead and the reconnect loop takes over.
const readTimeout = 60 * time.Second

// writeTimeout bounds how long a single send may block on a half-open
// connection where the peer stopped reading.
const writeTimeout = 30 * time.Second

// maxMessageSize caps the size of a single inbound WebSocket message (4MB).
// Without a read limit a malicious or broken peer could exhaust memory with
// one oversized frame.
const maxMessageSize = 4 << 20 // 4MB

// WSClient manages a WebSocket connection to the backend.
type WSClient struct {
	cfg    config.Config
	logger *slog.Logger

	// mu guards conn: Connect/Close write it from the session goroutine while
	// ticker/command goroutines read it via Send/IsConnected/Read.
	mu   sync.Mutex
	conn *websocket.Conn

	// writeMu serializes WriteMessage calls: gorilla/websocket panics on
	// concurrent writes from multiple goroutines (telemetry ticker, heartbeat
	// ticker, command handlers, heartbeat acks).
	writeMu sync.Mutex
}

// New creates a new WSClient.
func New(cfg config.Config, logger *slog.Logger) *WSClient {
	return &WSClient{
		cfg:    cfg,
		logger: logger,
	}
}

// Connect establishes a WebSocket connection to the backend.
// It sets the API key header if configured and handles URL scheme conversion.
func (c *WSClient) Connect(ctx context.Context) error {
	wsURL, err := c.buildURL()
	if err != nil {
		return fmt.Errorf("build URL: %w", err)
	}

	// Log host only: the URL may embed credentials that must not leak to logs.
	c.logger.Info("connecting to backend", "host", LoggableURL(wsURL))

	header := http.Header{}
	if c.cfg.APIKey != "" {
		// The backend accepts the key via either header.
		header.Set("X-Api-Key", c.cfg.APIKey)
		header.Set("Authorization", "Bearer "+c.cfg.APIKey)
	}

	dialer := websocket.Dialer{
		HandshakeTimeout: 10 * time.Second,
	}

	conn, _, err := dialer.DialContext(ctx, wsURL, header)
	if err != nil {
		return fmt.Errorf("websocket dial %s: %w", LoggableURL(wsURL), err)
	}

	c.mu.Lock()
	c.conn = conn
	c.mu.Unlock()
	c.configureConn(conn)
	c.logger.Info("connected to backend")
	return nil
}

// configureConn sets up keep-alive handling: respond to protocol pings and
// expire the connection when no data arrives within the read deadline.
func (c *WSClient) configureConn(conn *websocket.Conn) {
	conn.SetReadLimit(maxMessageSize)
	conn.SetPongHandler(func(string) error {
		return conn.SetReadDeadline(time.Now().Add(readTimeout))
	})
	conn.SetReadDeadline(time.Now().Add(readTimeout))
}

// SendHello sends the initial hello message to the backend.
func (c *WSClient) SendHello(ctx context.Context, version string) error {
	env := protocol.MustEnvelope(protocol.TypeHello, nil, nil, protocol.HelloPayload{
		Name:         c.cfg.AgentName,
		DockerSocket: c.cfg.DockerSocket,
		Version:      version,
	})
	return c.Send(ctx, env)
}

// WaitForHelloAck blocks until a hello ack is received or timeout.
func (c *WSClient) WaitForHelloAck(ctx context.Context) error {
	conn := c.currentConn()
	if conn == nil {
		return fmt.Errorf("not connected")
	}

	conn.SetReadDeadline(time.Now().Add(10 * time.Second))
	defer conn.SetReadDeadline(time.Time{})

	_, data, err := conn.ReadMessage()
	if err != nil {
		return fmt.Errorf("read hello ack: %w", err)
	}

	env, err := protocol.DecodeEnvelope(data)
	if err != nil {
		return fmt.Errorf("decode hello ack: %w", err)
	}

	if env.Type == protocol.TypeError {
		var errPayload protocol.ErrorPayload
		if env.Payload != nil {
			json.Unmarshal(env.Payload, &errPayload)
		}
		return fmt.Errorf("backend rejected hello: %s", errPayload.Error)
	}

	if env.Type != protocol.TypeHello {
		return fmt.Errorf("expected hello ack, got %s", env.Type)
	}

	c.logger.Info("hello acknowledged")
	return nil
}

// Send encodes and sends an envelope over the WebSocket.
// Safe to call concurrently with Close: it snapshots the connection under the
// mutex and fails cleanly (with "not connected" or a write error) if the
// connection was closed or replaced in the meantime.
func (c *WSClient) Send(ctx context.Context, env protocol.Envelope) error {
	// Honor a cancelled session context so stale handlers from a previous
	// session cannot write to a newer connection.
	if err := ctx.Err(); err != nil {
		return fmt.Errorf("send %s: %w", env.Type, err)
	}

	conn := c.currentConn()
	if conn == nil {
		return fmt.Errorf("send %s: not connected", env.Type)
	}

	data, err := env.Encode()
	if err != nil {
		return fmt.Errorf("encode envelope: %w", err)
	}

	// Serialize writes (gorilla panics on concurrent WriteMessage) and bound
	// the write so a peer that stopped reading cannot wedge the agent.
	c.writeMu.Lock()
	defer c.writeMu.Unlock()
	conn.SetWriteDeadline(time.Now().Add(writeTimeout))
	return conn.WriteMessage(websocket.TextMessage, data)
}

// Read blocks until the next message is received.
func (c *WSClient) Read() (protocol.Envelope, error) {
	conn := c.currentConn()
	if conn == nil {
		return protocol.Envelope{}, fmt.Errorf("not connected")
	}
	// Refresh the deadline so any traffic keeps the connection alive.
	conn.SetReadDeadline(time.Now().Add(readTimeout))
	_, data, err := conn.ReadMessage()
	if err != nil {
		return protocol.Envelope{}, err
	}
	return protocol.DecodeEnvelope(data)
}

// Close closes the WebSocket connection and marks the client as disconnected.
// Concurrent Send calls either finish on the closing connection (getting a
// write error) or observe "not connected"; neither path panics.
func (c *WSClient) Close() {
	c.mu.Lock()
	conn := c.conn
	c.conn = nil
	c.mu.Unlock()
	if conn != nil {
		conn.Close()
	}
}

// IsConnected returns true if the WebSocket is open.
func (c *WSClient) IsConnected() bool {
	return c.currentConn() != nil
}

// currentConn returns the current connection, or nil if not connected.
func (c *WSClient) currentConn() *websocket.Conn {
	c.mu.Lock()
	defer c.mu.Unlock()
	return c.conn
}

// LoggableURL returns the host portion of a URL for logging. If the URL cannot
// be parsed (or has no host), the raw string is returned so callers still see
// useful context.
func LoggableURL(raw string) string {
	u, err := url.Parse(raw)
	if err != nil || u.Host == "" {
		return raw
	}
	return u.Host
}

// buildURL converts the backend URL to a WebSocket URL.
// Converts http:// -> ws://, https:// -> wss://, and appends /ws/agent path.
func (c *WSClient) buildURL() (string, error) {
	u, err := url.Parse(c.cfg.BackendURL)
	if err != nil {
		return "", fmt.Errorf("parse backend URL: %w", err)
	}

	// Convert scheme
	switch u.Scheme {
	case "http":
		u.Scheme = "ws"
	case "https":
		u.Scheme = "wss"
	case "ws", "wss":
		// already correct
	default:
		return "", fmt.Errorf("unsupported scheme: %s", u.Scheme)
	}

	// Ensure path ends with /ws/agent
	path := strings.TrimRight(u.Path, "/")
	if path == "" {
		path = "/ws/agent"
	} else if !strings.HasSuffix(path, "/ws/agent") {
		path += "/ws/agent"
	}
	u.Path = path

	return u.String(), nil
}
