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
	// RC1: YAML is unmarshalled over these defaults, so they must be seeded.
	if !cfg.AllowScriptStart {
		t.Error("AllowScriptStart default = false, want true (decision 6)")
	}
	if cfg.AllowScriptUpload {
		t.Error("AllowScriptUpload default = true, want false (decision 7)")
	}
	if !cfg.AllowUnrestrictedLoopback {
		t.Error("AllowUnrestrictedLoopback default = false, want true (RC1)")
	}
	if cfg.AllowContainerCreation {
		t.Error("AllowContainerCreation default = true, want false (decision 3)")
	}
}

func TestValidate_LoopbackScopingRequiresPorts(t *testing.T) {
	cfg := DefaultConfig()
	cfg.AllowUnrestrictedLoopback = false
	if err := cfg.Validate(); err == nil {
		t.Fatal("expected validation error when scoping is enabled with no ports")
	}
	cfg.AllowedLoopbackPorts = []int{8080}
	if err := cfg.Validate(); err != nil {
		t.Fatalf("Validate() = %v, want nil once ports are listed", err)
	}
}

// TestValidate_LoopbackPrecedence covers the interplay between
// allow_unrestricted_loopback and allowed_loopback_ports: an explicit
// non-empty port list is valid and honored regardless of the flag, and only
// the false + empty combination is rejected (it would silently deny every
// loopback command).
func TestValidate_LoopbackPrecedence(t *testing.T) {
	tests := []struct {
		name         string
		unrestricted bool
		ports        []int
		wantErr      bool
	}{
		{name: "flag true empty ports unrestricted", unrestricted: true, ports: nil, wantErr: false},
		{name: "flag true explicit ports scoped", unrestricted: true, ports: []int{8080}, wantErr: false},
		{name: "flag false explicit ports scoped", unrestricted: false, ports: []int{8080}, wantErr: false},
		{name: "flag false empty ports invalid", unrestricted: false, ports: nil, wantErr: true},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			cfg := DefaultConfig()
			cfg.AllowUnrestrictedLoopback = tt.unrestricted
			cfg.AllowedLoopbackPorts = tt.ports
			err := cfg.Validate()
			if (err != nil) != tt.wantErr {
				t.Fatalf("Validate() error = %v, wantErr %v", err, tt.wantErr)
			}
		})
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

	t.Run("env wins over non-empty yaml", func(t *testing.T) {
		t.Setenv("UNSWARM_AGENT_BACKEND_URL", "ws://127.0.0.1:5014")
		t.Setenv("UNSWARM_AGENT_API_KEY", "env-key")
		cfg, err := Load(writeYAML(t, `
backend_url: "wss://yaml.example.com:8443"
api_key: "yaml-key"
agent_name: "env-wins"
`))
		if err != nil {
			t.Fatalf("Load: %v", err)
		}
		if cfg.BackendURL != "ws://127.0.0.1:5014" {
			t.Errorf("BackendURL = %q, want ws://127.0.0.1:5014 (env precedence)", cfg.BackendURL)
		}
		if cfg.APIKey != "env-key" {
			t.Errorf("APIKey = %q, want env-key (env precedence)", cfg.APIKey)
		}
		if cfg.BackendURLSource != "env" {
			t.Errorf("BackendURLSource = %q, want env", cfg.BackendURLSource)
		}
		if cfg.APIKeySource != "env" {
			t.Errorf("APIKeySource = %q, want env", cfg.APIKeySource)
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

func TestLoad_PlaintextRemoteAllowed(t *testing.T) {
	// H6: plaintext ws:// and http:// to non-loopback hosts load fine (the
	// agent logs a warning at connect time; it does not refuse configs).
	for _, backendURL := range []string{"ws://10.0.0.1:5014", "http://10.0.0.1:5014"} {
		t.Run(backendURL, func(t *testing.T) {
			dir := t.TempDir()
			path := filepath.Join(dir, "agent.yaml")
			content := "backend_url: \"" + backendURL + "\"\nagent_name: \"plaintext\"\n"
			if err := os.WriteFile(path, []byte(content), 0644); err != nil {
				t.Fatalf("WriteFile: %v", err)
			}
			cfg, err := Load(path)
			if err != nil {
				t.Fatalf("Load(%q) = %v, want no error (plaintext allowed)", backendURL, err)
			}
			if cfg.BackendURL != backendURL {
				t.Errorf("BackendURL = %q, want %q", cfg.BackendURL, backendURL)
			}
		})
	}
}
