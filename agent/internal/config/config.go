// Package config handles YAML configuration parsing with sensible defaults.
package config

import (
	"fmt"
	"net"
	"net/url"
	"os"
	"strings"
	"time"

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
	return nil
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
	if host == "" || isLoopback(host) {
		return nil // loopback ws:// is allowed for local dev
	}
	if !allowInsecureWs {
		return fmt.Errorf(
			"backend_url uses unencrypted ws:// to a non-loopback host; the API key would be sent in plaintext. Use wss:// (e.g., via a TLS-terminating reverse proxy) or set allow_insecure_ws: true to accept the risk",
		)
	}
	return nil
}

// isLoopback reports whether host is a loopback address (localhost, 127.x.x.x,
// or ::1).
func isLoopback(host string) bool {
	if strings.EqualFold(host, "localhost") {
		return true
	}
	ip := net.ParseIP(host)
	if ip != nil && ip.IsLoopback() {
		return true
	}
	return false
}
