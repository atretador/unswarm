// Package client provides a WebSocket client with automatic reconnection.
package client

import (
	"bytes"
	"context"
	"crypto/sha256"
	"crypto/tls"
	"crypto/x509"
	"encoding/hex"
	"encoding/json"
	"errors"
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

// ErrFingerprintMismatch is returned by Connect when the backend's TLS
// certificate does not match the configured expected_server_fingerprint.
// Callers must treat this as fatal (fail closed): retrying cannot succeed
// against an impostor, so the reconnect loop should abort instead of leaking
// the API key to an unverified server on every attempt.
var ErrFingerprintMismatch = errors.New("server certificate fingerprint mismatch")

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
//
// Server identity: over wss:// with expected_server_fingerprint configured,
// the peer certificate is verified inside the TLS handshake (via
// VerifyPeerCertificate) — i.e. before the HTTP upgrade request carrying the
// API key is ever written to the wire. A mismatch aborts with
// ErrFingerprintMismatch. Over plaintext ws:// to a non-loopback host a
// prominent warning is logged on every attempt.
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

	u, err := url.Parse(wsURL)
	if err != nil {
		return fmt.Errorf("parse ws URL: %w", err)
	}
	switch strings.ToLower(u.Scheme) {
	case "wss":
		tlsCfg, err := c.tlsConfig()
		if err != nil {
			return err
		}
		if tlsCfg != nil {
			dialer.TLSClientConfig = tlsCfg
		} else {
			c.logger.Info(
				"connected over wss:// without expected_server_fingerprint: server identity is not pinned; " +
					"set expected_server_fingerprint (SHA-256 hex of the backend TLS certificate) so a MITM with a " +
					"valid-for-another-host certificate cannot intercept the API key",
			)
		}
	case "ws":
		if !config.IsLoopback(u.Hostname()) {
			c.logger.Warn(
				"INSECURE CONNECTION: API key travels UNENCRYPTED over plaintext ws:// to a non-loopback host "+
					"and can be intercepted by anyone on the path; use wss:// instead",
				"host", LoggableURL(wsURL),
			)
		}
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

// tlsConfig builds the TLS configuration enforcing certificate pinning when
// expected_server_fingerprint is configured. It returns (nil, nil) when no
// fingerprint is set (default system verification applies). The check lives in
// VerifyPeerCertificate, which crypto/tls runs during the handshake — before
// any application data (and therefore before the API key header) is sent.
func (c *WSClient) tlsConfig() (*tls.Config, error) {
	fp, err := config.NormalizeFingerprint(c.cfg.ExpectedServerFingerprint)
	if err != nil {
		return nil, err
	}
	if fp == "" {
		return nil, nil
	}
	expected, err := hex.DecodeString(fp)
	if err != nil {
		return nil, fmt.Errorf("decode expected_server_fingerprint: %w", err)
	}
	return &tls.Config{
		VerifyPeerCertificate: func(rawCerts [][]byte, _ [][]*x509.Certificate) error {
			if len(rawCerts) == 0 {
				return fmt.Errorf("%w: server presented no certificate", ErrFingerprintMismatch)
			}
			sum := sha256.Sum256(rawCerts[0])
			if !bytes.Equal(sum[:], expected) {
				return fmt.Errorf("%w: got %s, want %s", ErrFingerprintMismatch, hex.EncodeToString(sum[:]), fp)
			}
			return nil
		},
	}, nil
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
