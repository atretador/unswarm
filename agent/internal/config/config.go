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
	BackendURL       string          `yaml:"backend_url"`
	APIKey           string          `yaml:"api_key"`
	AgentName        string          `yaml:"agent_name"`
	DockerSocket     string          `yaml:"docker_socket"`
	ScriptsDir       string          `yaml:"scripts_dir"`
	AllowInsecureWs  bool            `yaml:"allow_insecure_ws"`
	Reconnect        ReconnectConfig `yaml:"reconnect"`
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
	}
}

// Load reads a YAML config file, applies defaults, and returns the config.
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

	if err := cfg.Validate(); err != nil {
		return Config{}, fmt.Errorf("validate config %s: %w", path, err)
	}

	return cfg, nil
}

// InitialBackoff returns the initial backoff as a time.Duration.
func (c Config) InitialBackoff() time.Duration {
	return time.Duration(c.Reconnect.InitialBackoffMs) * time.Millisecond
}

// MaxBackoff returns the max backoff as a time.Duration.
func (c Config) MaxBackoff() time.Duration {
	return time.Duration(c.Reconnect.MaxBackoffMs) * time.Millisecond
}

// Validate checks the config for security issues and returns an error if any
// are found. Called automatically by Load.
func (c Config) Validate() error {
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
