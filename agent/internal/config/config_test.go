package config

import (
	"os"
	"path/filepath"
	"reflect"
	"strings"
	"testing"
)

func TestDefaultConfig(t *testing.T) {
	cfg := DefaultConfig()
	if cfg.BackendURL != "ws://localhost:5014" {
		t.Errorf("BackendURL = %q, want ws://localhost:5014", cfg.BackendURL)
	}
	if cfg.APIKey != "" {
		t.Errorf("APIKey = %q, want empty", cfg.APIKey)
	}
	if cfg.AgentName != "machine-b" {
		t.Errorf("AgentName = %q, want machine-b", cfg.AgentName)
	}
	if cfg.DockerSocket != "unix:///var/run/docker.sock" {
		t.Errorf("DockerSocket = %q, want unix:///var/run/docker.sock", cfg.DockerSocket)
	}
	if cfg.Reconnect.InitialBackoffMs != 1000 {
		t.Errorf("InitialBackoffMs = %d, want 1000", cfg.Reconnect.InitialBackoffMs)
	}
	if cfg.Reconnect.MaxBackoffMs != 30000 {
		t.Errorf("MaxBackoffMs = %d, want 30000", cfg.Reconnect.MaxBackoffMs)
	}
	if cfg.Reconnect.MaxRetries != -1 {
		t.Errorf("MaxRetries = %d, want -1", cfg.Reconnect.MaxRetries)
	}
}

func TestLoadEmptyPath(t *testing.T) {
	cfg, err := Load("")
	if err != nil {
		t.Fatalf("Load empty path: %v", err)
	}
	def := DefaultConfig()
	if !reflect.DeepEqual(cfg, def) {
		t.Errorf("Empty path should return defaults, got %+v", cfg)
	}
}

func TestLoadNonexistentFile(t *testing.T) {
	_, err := Load("/nonexistent/path/config.yaml")
	if err == nil {
		t.Error("Expected error for nonexistent file")
	}
}

func TestLoadFullOverride(t *testing.T) {
	content := `
backend_url: "wss://remote.example.com:8443"
api_key: "secret-key-123"
agent_name: "gpu-node-1"
docker_socket: "unix:///custom/docker.sock"
reconnect:
  initial_backoff_ms: 500
  max_backoff_ms: 60000
  max_retries: 10
`
	dir := t.TempDir()
	path := filepath.Join(dir, "agent.yaml")
	if err := os.WriteFile(path, []byte(content), 0644); err != nil {
		t.Fatalf("WriteFile: %v", err)
	}

	cfg, err := Load(path)
	if err != nil {
		t.Fatalf("Load: %v", err)
	}
	if cfg.BackendURL != "wss://remote.example.com:8443" {
		t.Errorf("BackendURL = %q, want wss://remote.example.com:8443", cfg.BackendURL)
	}
	if cfg.APIKey != "secret-key-123" {
		t.Errorf("APIKey = %q, want secret-key-123", cfg.APIKey)
	}
	if cfg.AgentName != "gpu-node-1" {
		t.Errorf("AgentName = %q, want gpu-node-1", cfg.AgentName)
	}
	if cfg.DockerSocket != "unix:///custom/docker.sock" {
		t.Errorf("DockerSocket = %q, want unix:///custom/docker.sock", cfg.DockerSocket)
	}
	if cfg.Reconnect.InitialBackoffMs != 500 {
		t.Errorf("InitialBackoffMs = %d, want 500", cfg.Reconnect.InitialBackoffMs)
	}
	if cfg.Reconnect.MaxBackoffMs != 60000 {
		t.Errorf("MaxBackoffMs = %d, want 60000", cfg.Reconnect.MaxBackoffMs)
	}
	if cfg.Reconnect.MaxRetries != 10 {
		t.Errorf("MaxRetries = %d, want 10", cfg.Reconnect.MaxRetries)
	}
}

func TestLoadPartialOverride(t *testing.T) {
	content := `
backend_url: "ws://10.0.0.1:5014"
agent_name: "partial-test"
allow_insecure_ws: true
`
	dir := t.TempDir()
	path := filepath.Join(dir, "agent.yaml")
	if err := os.WriteFile(path, []byte(content), 0644); err != nil {
		t.Fatalf("WriteFile: %v", err)
	}

	cfg, err := Load(path)
	if err != nil {
		t.Fatalf("Load: %v", err)
	}
	// Overridden
	if cfg.BackendURL != "ws://10.0.0.1:5014" {
		t.Errorf("BackendURL = %q", cfg.BackendURL)
	}
	if cfg.AgentName != "partial-test" {
		t.Errorf("AgentName = %q", cfg.AgentName)
	}
	// Default
	if cfg.DockerSocket != "unix:///var/run/docker.sock" {
		t.Errorf("DockerSocket should default, got %q", cfg.DockerSocket)
	}
	if cfg.Reconnect.InitialBackoffMs != 1000 {
		t.Errorf("InitialBackoffMs should default to 1000, got %d", cfg.Reconnect.InitialBackoffMs)
	}
}

func TestInitialBackoffDuration(t *testing.T) {
	cfg := DefaultConfig()
	d := cfg.InitialBackoff()
	if d != 1000000000 { // 1s in nanoseconds
		t.Errorf("InitialBackoff() = %v, want 1s", d)
	}
}

func TestMaxBackoffDuration(t *testing.T) {
	cfg := DefaultConfig()
	d := cfg.MaxBackoff()
	if d != 30000000000 { // 30s in nanoseconds
		t.Errorf("MaxBackoff() = %v, want 30s", d)
	}
}

func TestValidateRequiredFields(t *testing.T) {
	tests := []struct {
		name    string
		mutate  func(*Config)
		wantErr string
	}{
		{
			name:   "valid default config",
			mutate: func(c *Config) {},
		},
		{
			name:    "empty backend_url rejected",
			mutate:  func(c *Config) { c.BackendURL = "" },
			wantErr: "backend_url is required",
		},
		{
			name:    "whitespace backend_url rejected",
			mutate:  func(c *Config) { c.BackendURL = "   " },
			wantErr: "backend_url is required",
		},
		{
			name:    "empty agent_name rejected",
			mutate:  func(c *Config) { c.AgentName = "" },
			wantErr: "agent_name is required",
		},
		{
			name:    "whitespace agent_name rejected",
			mutate:  func(c *Config) { c.AgentName = " \t " },
			wantErr: "agent_name is required",
		},
		{
			name: "max_backoff below initial rejected",
			mutate: func(c *Config) {
				c.Reconnect.InitialBackoffMs = 5000
				c.Reconnect.MaxBackoffMs = 1000
			},
			wantErr: "must be >= reconnect.initial_backoff_ms",
		},
		{
			name: "max_backoff equal to initial ok",
			mutate: func(c *Config) {
				c.Reconnect.InitialBackoffMs = 5000
				c.Reconnect.MaxBackoffMs = 5000
			},
		},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			cfg := DefaultConfig()
			tt.mutate(&cfg)
			err := cfg.Validate()
			if tt.wantErr == "" {
				if err != nil {
					t.Errorf("Validate() unexpected error: %v", err)
				}
				return
			}
			if err == nil {
				t.Errorf("Validate() = nil, want error containing %q", tt.wantErr)
			} else if !strings.Contains(err.Error(), tt.wantErr) {
				t.Errorf("Validate() error = %v, want containing %q", err, tt.wantErr)
			}
		})
	}
}

func TestLoadValidationErrors(t *testing.T) {
	tests := []struct {
		name    string
		content string
		wantErr string
	}{
		{
			name: "missing backend_url",
			content: `
backend_url: ""
agent_name: "x"
`,
			wantErr: "backend_url is required",
		},
		{
			name: "missing agent_name",
			content: `
backend_url: "ws://localhost:5014"
agent_name: ""
`,
			wantErr: "agent_name is required",
		},
		{
			name: "max_backoff_ms below initial_backoff_ms",
			content: `
backend_url: "ws://localhost:5014"
agent_name: "x"
reconnect:
  initial_backoff_ms: 10000
  max_backoff_ms: 5000
`,
			wantErr: "must be >= reconnect.initial_backoff_ms",
		},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			dir := t.TempDir()
			path := filepath.Join(dir, "agent.yaml")
			if err := os.WriteFile(path, []byte(tt.content), 0644); err != nil {
				t.Fatalf("WriteFile: %v", err)
			}
			_, err := Load(path)
			if err == nil {
				t.Fatalf("Load() = nil error, want %q", tt.wantErr)
			}
			if !strings.Contains(err.Error(), tt.wantErr) {
				t.Errorf("Load() error = %v, want containing %q", err, tt.wantErr)
			}
		})
	}
}

func TestEnvOverridePrecedence(t *testing.T) {
	writeYAML := func(t *testing.T, content string) string {
		t.Helper()
		dir := t.TempDir()
		path := filepath.Join(dir, "agent.yaml")
		if err := os.WriteFile(path, []byte(content), 0644); err != nil {
			t.Fatalf("WriteFile: %v", err)
		}
		return path
	}

	t.Run("env fills empty yaml fields", func(t *testing.T) {
		t.Setenv("UNSWARM_AGENT_BACKEND_URL", "ws://127.0.0.1:5014")
		t.Setenv("UNSWARM_AGENT_API_KEY", "env-key")
		cfg, err := Load(writeYAML(t, `
backend_url: ""
api_key: ""
agent_name: "env-test"
`))
		if err != nil {
			t.Fatalf("Load: %v", err)
		}
		if cfg.BackendURL != "ws://127.0.0.1:5014" {
			t.Errorf("BackendURL = %q, want ws://127.0.0.1:5014 (from env)", cfg.BackendURL)
		}
		if cfg.APIKey != "env-key" {
			t.Errorf("APIKey = %q, want env-key (from env)", cfg.APIKey)
		}
	})

	t.Run("non-empty yaml wins over env", func(t *testing.T) {
		t.Setenv("UNSWARM_AGENT_BACKEND_URL", "ws://127.0.0.1:5014")
		t.Setenv("UNSWARM_AGENT_API_KEY", "env-key")
		cfg, err := Load(writeYAML(t, `
backend_url: "wss://yaml.example.com:8443"
api_key: "yaml-key"
agent_name: "yaml-wins"
`))
		if err != nil {
			t.Fatalf("Load: %v", err)
		}
		if cfg.BackendURL != "wss://yaml.example.com:8443" {
			t.Errorf("BackendURL = %q, want wss://yaml.example.com:8443 (yaml precedence)", cfg.BackendURL)
		}
		if cfg.APIKey != "yaml-key" {
			t.Errorf("APIKey = %q, want yaml-key (yaml precedence)", cfg.APIKey)
		}
	})

	t.Run("no env set leaves empty field unset", func(t *testing.T) {
		t.Setenv("UNSWARM_AGENT_BACKEND_URL", "")
		t.Setenv("UNSWARM_AGENT_API_KEY", "")
		cfg, err := Load(writeYAML(t, `
backend_url: "ws://localhost:5014"
api_key: ""
agent_name: "no-env"
`))
		if err != nil {
			t.Fatalf("Load: %v", err)
		}
		if cfg.APIKey != "" {
			t.Errorf("APIKey = %q, want empty (no env fallback)", cfg.APIKey)
		}
	})
}

func TestValidateInsecureWs(t *testing.T) {
	tests := []struct {
		name          string
		backendURL    string
		allowInsecure bool
		wantErr       bool
	}{
		{
			name:          "loopback ws:// localhost allowed",
			backendURL:    "ws://localhost:5014",
			allowInsecure: false,
			wantErr:       false,
		},
		{
			name:          "loopback ws:// 127.0.0.1 allowed",
			backendURL:    "ws://127.0.0.1:5014",
			allowInsecure: false,
			wantErr:       false,
		},
		{
			name:          "loopback ws:// ::1 allowed",
			backendURL:    "ws://[::1]:5014",
			allowInsecure: false,
			wantErr:       false,
		},
		{
			name:          "non-loopback ws:// rejected",
			backendURL:    "ws://10.0.0.1:5014",
			allowInsecure: false,
			wantErr:       true,
		},
		{
			name:          "non-loopback ws:// rejected for hostname",
			backendURL:    "ws://backend.example.com:5014",
			allowInsecure: false,
			wantErr:       true,
		},
		{
			name:          "non-loopback ws:// with allow_insecure_ws ok",
			backendURL:    "ws://10.0.0.1:5014",
			allowInsecure: true,
			wantErr:       false,
		},
		{
			name:          "wss:// always ok",
			backendURL:    "wss://backend.example.com:8443",
			allowInsecure: false,
			wantErr:       false,
		},
		{
			name:          "wss:// non-loopback ok",
			backendURL:    "wss://10.0.0.1:8443",
			allowInsecure: false,
			wantErr:       false,
		},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			err := validateInsecureWs(tt.backendURL, tt.allowInsecure)
			if (err != nil) != tt.wantErr {
				t.Errorf("validateInsecureWs(%q, %v) error = %v, wantErr %v",
					tt.backendURL, tt.allowInsecure, err, tt.wantErr)
			}
		})
	}
}
