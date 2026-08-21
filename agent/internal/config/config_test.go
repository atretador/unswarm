package config

import (
	"os"
	"path/filepath"
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
	if cfg != def {
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

func TestValidateInsecureWs(t *testing.T) {
	tests := []struct {
		name           string
		backendURL     string
		allowInsecure  bool
		wantErr        bool
	}{
		{
			name:       "loopback ws:// localhost allowed",
			backendURL: "ws://localhost:5014",
			allowInsecure: false,
			wantErr: false,
		},
		{
			name:       "loopback ws:// 127.0.0.1 allowed",
			backendURL: "ws://127.0.0.1:5014",
			allowInsecure: false,
			wantErr: false,
		},
		{
			name:       "loopback ws:// ::1 allowed",
			backendURL: "ws://[::1]:5014",
			allowInsecure: false,
			wantErr: false,
		},
		{
			name:       "non-loopback ws:// rejected",
			backendURL: "ws://10.0.0.1:5014",
			allowInsecure: false,
			wantErr: true,
		},
		{
			name:       "non-loopback ws:// rejected for hostname",
			backendURL: "ws://backend.example.com:5014",
			allowInsecure: false,
			wantErr: true,
		},
		{
			name:       "non-loopback ws:// with allow_insecure_ws ok",
			backendURL: "ws://10.0.0.1:5014",
			allowInsecure: true,
			wantErr: false,
		},
		{
			name:       "wss:// always ok",
			backendURL: "wss://backend.example.com:8443",
			allowInsecure: false,
			wantErr: false,
		},
		{
			name:       "wss:// non-loopback ok",
			backendURL: "wss://10.0.0.1:8443",
			allowInsecure: false,
			wantErr: false,
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
