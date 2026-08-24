// Package config handles YAML configuration parsing with sensible defaults.
package config

import (
	"encoding/hex"
	"fmt"
	"net"
	"net/url"
	"os"
	"strings"
	"time"
	"unicode"

	"gopkg.in/yaml.v3"
)

// ReconnectConfig holds WebSocket reconnection settings.
type ReconnectConfig struct {
	InitialBackoffMs int `yaml:"initial_backoff_ms"`
	MaxBackoffMs     int `yaml:"max_backoff_ms"`
	MaxRetries       int `yaml:"max_retries"`
}

// Config is the top-level agent configuration.
type Config struct {
	BackendURL      string          `yaml:"backend_url"`
	APIKey          string          `yaml:"api_key"`
	AgentName       string          `yaml:"agent_name"`
	DockerSocket    string          `yaml:"docker_socket"`
	ScriptsDir      string          `yaml:"scripts_dir"`
	AllowInsecureWs bool            `yaml:"allow_insecure_ws"`
	Reconnect       ReconnectConfig `yaml:"reconnect"`

	// ExpectedServerFingerprint is an optional SHA-256 hex fingerprint of the
	// backend's TLS certificate. When set and the backend URL uses wss://, the
	// agent verifies the peer certificate during the TLS handshake (before any
	// API key material is sent) and refuses to connect on mismatch.
	// Parsing is case/space-insensitive; colons are accepted and stripped.
	ExpectedServerFingerprint string `yaml:"expected_server_fingerprint"`

	// AllowedLoopbackPorts restricts which 127.0.0.1 ports the agent will dial
	// for health_check / discover_models / chat_completion commands. Empty or
	// nil = unrestricted (any loopback port). When set, ports not on the list
	// are rejected before any connection attempt.
	AllowedLoopbackPorts []int `yaml:"allowed_loopback_ports"`

	// TelemetryIntervalMs is how often telemetry (host + per-container status,
	// including Docker inspect/stats calls) is collected and sent, in
	// milliseconds. Default 30000 (30s); values below 5000 are rejected so a
	// typo cannot turn telemetry into a hot loop against the Docker daemon.
	TelemetryIntervalMs int `yaml:"telemetry_interval_ms"`

	// APIKeyFromYAML reports whether api_key was set in the YAML config file
	// itself (as opposed to the UNSWARM_AGENT_API_KEY environment fallback).
	// Used to decide whether a plaintext-key-on-disk permission warning applies.
	APIKeyFromYAML bool `yaml:"-"`

	// EnforceRegisteredRuntime gates container lifecycle commands against the
	// registered runtime set synced from the backend (sync_registrations).
	// Default true: unregistered targets are rejected without touching Docker.
	// Set false to restore legacy behavior (act on any container on the host).
	EnforceRegisteredRuntime bool `yaml:"enforce_registered_runtime"`
}

// DefaultConfig returns the default configuration.
func DefaultConfig() Config {
	return Config{
		BackendURL:   "ws://localhost:5014",
		APIKey:       "",
		AgentName:    "machine-b",
		DockerSocket: "unix:///var/run/docker.sock",
		Reconnect: ReconnectConfig{
			InitialBackoffMs: 1000,
			MaxBackoffMs:     30000,
			MaxRetries:       -1,
		},
		// Registered-runtime enforcement is ON by default; an explicit
		// enforce_registered_runtime: false in agent.yaml opts out.
		EnforceRegisteredRuntime: true,
		// Telemetry every 30s by default.
		TelemetryIntervalMs: 30000,
	}
}

// Load reads a YAML config file, applies defaults, applies environment
// overrides, and returns the config.
// If path is empty, returns defaults.
func Load(path string) (Config, error) {
	cfg := DefaultConfig()
	if path == "" {
		return cfg, nil
	}

	data, err := os.ReadFile(path)
	if err != nil {
		return Config{}, fmt.Errorf("read config %s: %w", path, err)
	}

	if err := yaml.Unmarshal(data, &cfg); err != nil {
		return Config{}, fmt.Errorf("parse config %s: %w", path, err)
	}

	// Record whether the API key came from the YAML file before environment
	// overrides can fill it in from UNSWARM_AGENT_API_KEY.
	cfg.APIKeyFromYAML = strings.TrimSpace(cfg.APIKey) != ""

	cfg.ApplyEnvOverrides()

	if err := cfg.Validate(); err != nil {
		return Config{}, fmt.Errorf("validate config %s: %w", path, err)
	}

	return cfg, nil
}

// ApplyEnvOverrides fills in BackendURL and APIKey from the environment when
// the corresponding YAML field is empty. Precedence: a non-empty YAML value
// wins; the environment (UNSWARM_AGENT_BACKEND_URL, UNSWARM_AGENT_API_KEY) is
// used only as a fallback for empty fields.
func (c *Config) ApplyEnvOverrides() {
	if c.BackendURL == "" {
		if v := os.Getenv("UNSWARM_AGENT_BACKEND_URL"); v != "" {
			c.BackendURL = v
		}
	}
	if c.APIKey == "" {
		if v := os.Getenv("UNSWARM_AGENT_API_KEY"); v != "" {
			c.APIKey = v
		}
	}
}

// InitialBackoff returns the initial backoff as a time.Duration.
func (c Config) InitialBackoff() time.Duration {
	return time.Duration(c.Reconnect.InitialBackoffMs) * time.Millisecond
}

// MaxBackoff returns the max backoff as a time.Duration.
func (c Config) MaxBackoff() time.Duration {
	return time.Duration(c.Reconnect.MaxBackoffMs) * time.Millisecond
}

// minTelemetryIntervalMs is the lower bound for telemetry_interval_ms:
// telemetry fans out to per-container Docker inspect/stats calls, so a
// smaller interval would hammer the Docker daemon.
const minTelemetryIntervalMs = 5000

// Validate checks the config for security issues and missing required fields,
// returning an error if any are found. Called automatically by Load.
func (c Config) Validate() error {
	if strings.TrimSpace(c.BackendURL) == "" {
		return fmt.Errorf("backend_url is required")
	}
	if strings.TrimSpace(c.AgentName) == "" {
		return fmt.Errorf("agent_name is required")
	}
	if c.Reconnect.MaxBackoffMs < c.Reconnect.InitialBackoffMs {
		return fmt.Errorf(
			"reconnect.max_backoff_ms (%d) must be >= reconnect.initial_backoff_ms (%d)",
			c.Reconnect.MaxBackoffMs, c.Reconnect.InitialBackoffMs,
		)
	}
	if err := validateInsecureWs(c.BackendURL, c.AllowInsecureWs); err != nil {
		return err
	}
	if _, err := NormalizeFingerprint(c.ExpectedServerFingerprint); err != nil {
		return err
	}
	// DefaultConfig seeds 30000, so an unset field passes; only explicit
	// misconfigurations (0 or a hot-loop value) land here.
	if c.TelemetryIntervalMs < minTelemetryIntervalMs {
		return fmt.Errorf(
			"telemetry_interval_ms (%d) must be >= %d — telemetry fans out to per-container Docker inspect/stats calls and must not become a hot loop",
			c.TelemetryIntervalMs, minTelemetryIntervalMs,
		)
	}
	return nil
}

// NormalizeFingerprint parses a SHA-256 certificate fingerprint. Parsing is
// case/space-insensitive: whitespace and colons are stripped and the result is
// lowercased hex. Empty input yields an empty string (feature disabled).
func NormalizeFingerprint(s string) (string, error) {
	stripped := strings.Map(func(r rune) rune {
		switch r {
		case ':', ' ', '\t', '\n', '\r':
			return -1
		}
		return unicode.ToLower(r)
	}, s)
	if stripped == "" {
		return "", nil
	}
	if len(stripped) != 64 {
		return "", fmt.Errorf(
			"expected_server_fingerprint must be a SHA-256 hex digest (64 hex chars, colons/spaces allowed), got %d chars",
			len(stripped),
		)
	}
	if _, err := hex.DecodeString(stripped); err != nil {
		return "", fmt.Errorf("expected_server_fingerprint is not valid hex: %w", err)
	}
	return stripped, nil
}

// validateInsecureWs rejects ws:// connections to non-loopback hosts unless
// allowInsecureWs is true, because the API key would be sent in plaintext.
func validateInsecureWs(backendURL string, allowInsecureWs bool) error {
	u, err := url.Parse(backendURL)
	if err != nil {
		return nil // unparseable URLs are caught elsewhere
	}
	scheme := strings.ToLower(u.Scheme)
	if scheme != "ws" {
		return nil // wss:// and other schemes are fine
	}
	host := u.Hostname()
	if host == "" || IsLoopback(host) {
		return nil // loopback ws:// is allowed for local dev
	}
	if !allowInsecureWs {
		return fmt.Errorf(
			"backend_url uses unencrypted ws:// to a non-loopback host; the API key would be sent in plaintext. Use wss:// (e.g., via a TLS-terminating reverse proxy) or set allow_insecure_ws: true to accept the risk",
		)
	}
	return nil
}

// IsLoopback reports whether host is a loopback address (localhost, 127.x.x.x,
// or ::1).
func IsLoopback(host string) bool {
	if strings.EqualFold(host, "localhost") {
		return true
	}
	ip := net.ParseIP(host)
	if ip != nil && ip.IsLoopback() {
		return true
	}
	return false
}
