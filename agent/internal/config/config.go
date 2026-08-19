// Package config handles YAML configuration parsing with sensible defaults.
package config

import (
	"fmt"
	"os"
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
	BackendURL   string          `yaml:"backend_url"`
	APIKey       string          `yaml:"api_key"`
	AgentName    string          `yaml:"agent_name"`
	DockerSocket string          `yaml:"docker_socket"`
	Reconnect    ReconnectConfig `yaml:"reconnect"`
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
