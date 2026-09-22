// Package config handles YAML configuration parsing with sensible defaults.
package config

import (
	"encoding/hex"
	"fmt"
	"log/slog"
	"net"
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

// ContainerCreationConfig configures the agent-side create_container policy.
// Empty allowlists mean deny-all (see docker.CreatePolicy).
type ContainerCreationConfig struct {
	// AllowedImages is a list of image glob patterns; at least one must match
	// for create_container to proceed. Empty = deny all.
	AllowedImages []string `yaml:"allowed_images"`
	// AllowedHostPathPrefixes restricts host paths mounted into created
	// containers, on a path-component boundary. Empty = deny all host mounts.
	AllowedHostPathPrefixes []string `yaml:"allowed_host_path_prefixes"`
	// AllowHostNetwork permits NetworkMode=host and NetworkMode=container:*.
	AllowHostNetwork bool `yaml:"allow_host_network"`
	// AllowIpcHost permits IpcMode=host and IpcMode=container:*.
	AllowIpcHost bool `yaml:"allow_ipc_host"`
	// AllowedDevicePrefixes lists device paths that may be passed through on a
	// path-component boundary. Empty = deny all devices.
	AllowedDevicePrefixes []string `yaml:"allowed_device_prefixes"`
	// BindAddress is the host IP that mapped container ports bind to. Empty
	// defaults to 127.0.0.1 (never 0.0.0.0).
	BindAddress string `yaml:"bind_address"`
}

// Config is the top-level agent configuration.
type Config struct {
	BackendURL      string          `yaml:"backend_url"`
	APIKey          string          `yaml:"api_key"`
	AgentName       string          `yaml:"agent_name"`
	DockerSocket    string          `yaml:"docker_socket"`
	ScriptsDir      string          `yaml:"scripts_dir"`
	AllowInsecureWs bool            `yaml:"allow_insecure_ws"` // deprecated no-op (H6)
	Reconnect       ReconnectConfig `yaml:"reconnect"`

	// ExpectedServerFingerprint is an optional SHA-256 hex fingerprint of the
	// backend's TLS certificate. When set and the backend URL uses wss://, the
	// agent verifies the peer certificate during the TLS handshake (before any
	// API key material is sent) and refuses to connect on mismatch.
	// Parsing is case/space-insensitive; colons are accepted and stripped.
	ExpectedServerFingerprint string `yaml:"expected_server_fingerprint"`

	// AllowedLoopbackPorts restricts which 127.0.0.1 ports the agent will dial
	// for health_check / discover_models / chat_completion commands. When
	// non-empty it always scopes the policy, regardless of
	// AllowUnrestrictedLoopback (an explicit list is never silently widened on
	// upgrade). An empty list means unrestricted only when
	// AllowUnrestrictedLoopback is true. Port ranges are not supported yet
	// (decision 19): scoping requires explicit host ports on the backend;
	// auto-assigned container ports are not knowable in advance.
	AllowedLoopbackPorts []int `yaml:"allowed_loopback_ports"`

	// AllowUnrestrictedLoopback preserves legacy behavior: when true (and
	// AllowedLoopbackPorts is empty) the agent will dial any 127.0.0.1 port.
	// Default true for back-compat; a non-empty AllowedLoopbackPorts scopes it
	// regardless of this flag.
	AllowUnrestrictedLoopback bool `yaml:"allow_unrestricted_loopback"`

	// AllowContainerCreation enables the create_container command. Default
	// false: creation is denied entirely unless explicitly enabled.
	AllowContainerCreation bool `yaml:"allow_container_creation"`

	// ContainerCreation configures the create_container policy (images, bind
	// paths, networking, devices) when AllowContainerCreation is true.
	ContainerCreation ContainerCreationConfig `yaml:"container_creation"`

	// AllowScriptUpload enables upload_script / update_script /
	// delete_script / get_script_content. Default false: the backend cannot
	// write executable scripts to the host. Only enable with a root-owned,
	// agent-read-only scripts_dir.
	AllowScriptUpload bool `yaml:"allow_script_upload"`

	// AllowScriptStart enables start_script. Default true so pre-provisioned
	// launcher scripts keep working. stop_script is never gated.
	AllowScriptStart bool `yaml:"allow_script_start"`

	// ScriptLogDir overrides where script logs/PID files are written. Empty
	// derives the path from scripts_dir. It MUST live under a path listed in
	// the systemd unit's ReadWritePaths or the agent refuses to start.
	ScriptLogDir string `yaml:"script_log_dir"`

	// TelemetryIntervalMs is how often telemetry (host + per-container status,
	// including Docker inspect/stats calls) is collected and sent, in
	// milliseconds. Default 30000 (30s); values below 5000 are rejected so a
	// typo cannot turn telemetry into a hot loop against the Docker daemon.
	TelemetryIntervalMs int `yaml:"telemetry_interval_ms"`

	// APIKeyFromYAML reports whether api_key was set in the YAML config file
	// itself (as opposed to the UNSWARM_AGENT_API_KEY environment fallback).
	// Used to decide whether a plaintext-key-on-disk permission warning applies.
	APIKeyFromYAML bool `yaml:"-"`

	// BackendURLSource / APIKeySource record where the effective value came
	// from ("env", "yaml" or "default") for the startup log. Not parsed from
	// YAML. Never logged with the value.
	BackendURLSource string `yaml:"-"`
	APIKeySource     string `yaml:"-"`

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
		// Loopback access is unrestricted by default for back-compat (RC1):
		// YAML is unmarshalled over these defaults, and a zero value would
		// otherwise fail Validate() on no-config-file runs. Operators should
		// set allow_unrestricted_loopback: false plus allowed_loopback_ports.
		AllowUnrestrictedLoopback: true,
		// Script start is enabled by default (decision 6); upload is disabled
		// by default (decision 7) so the backend cannot drop executables on the
		// host unless the operator opts in.
		AllowScriptStart:  true,
		AllowScriptUpload: false,
		// Container creation is disabled by default (decision 3); requires
		// allow_container_creation: true and an explicit container_creation
		// allowlist policy.
		AllowContainerCreation: false,
		// Sensible GPU device prefixes when creation is enabled. Empty lists
		// would deny all devices (decision 4).
		ContainerCreation: ContainerCreationConfig{
			AllowedDevicePrefixes: []string{"/dev/kfd", "/dev/dri/", "/dev/nvidia"},
		},
		// Telemetry every 30s by default.
		TelemetryIntervalMs: 30000,
		BackendURLSource:    "default",
		APIKeySource:        "default",
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

	// Detect which fields were explicitly present in the YAML file so the
	// startup log can report the effective source. Defaults were applied
	// before unmarshalling, so non-empty values alone cannot distinguish
	// "yaml" from "default".
	var present map[string]yaml.Node
	if err := yaml.Unmarshal(data, &present); err == nil {
		if _, ok := present["backend_url"]; ok {
			cfg.BackendURLSource = "yaml"
		}
		if _, ok := present["api_key"]; ok {
			cfg.APIKeySource = "yaml"
		}
	}

	// Record whether the API key came from the YAML file before environment
	// overrides can fill it in from UNSWARM_AGENT_API_KEY.
	cfg.APIKeyFromYAML = strings.TrimSpace(cfg.APIKey) != ""

	// allow_insecure_ws is a deprecated no-op for one release (H6): plaintext
	// is now allowed to any host with a warning. Still parse it so existing
	// files with the key load cleanly, and warn once that it does nothing.
	if cfg.AllowInsecureWs {
		slog.Warn("allow_insecure_ws is deprecated and no longer has any effect; " +
			"plaintext ws:// is permitted with a warning for non-loopback hosts")
	}

	cfg.ApplyEnvOverrides()

	if err := cfg.Validate(); err != nil {
		return Config{}, fmt.Errorf("validate config %s: %w", path, err)
	}

	return cfg, nil
}

// ApplyEnvOverrides applies environment overrides. Precedence (H5, decision 1):
// the environment wins over YAML for UNSWARM_AGENT_BACKEND_URL and
// UNSWARM_AGENT_API_KEY. When env and YAML disagree a warning names the field
// and both sources (never the value/secret); enforcement never fails. The
// signature stays void so no call site changes.
func (c *Config) ApplyEnvOverrides() {
	if v := os.Getenv("UNSWARM_AGENT_BACKEND_URL"); v != "" {
		if c.BackendURL != "" && c.BackendURL != v {
			slog.Warn("environment overrides YAML config value",
				"field", "backend_url",
				"env_var", "UNSWARM_AGENT_BACKEND_URL",
				"yaml_source", c.BackendURLSource,
				"effective_source", "env",
			)
		}
		c.BackendURL = v
		c.BackendURLSource = "env"
	}
	if v := os.Getenv("UNSWARM_AGENT_API_KEY"); v != "" {
		if c.APIKey != "" && c.APIKey != v {
			// Never log either value: only the field name and sources.
			slog.Warn("environment overrides YAML config value",
				"field", "api_key",
				"env_var", "UNSWARM_AGENT_API_KEY",
				"yaml_source", c.APIKeySource,
				"effective_source", "env",
			)
		}
		c.APIKey = v
		c.APIKeySource = "env"
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
	if _, err := NormalizeFingerprint(c.ExpectedServerFingerprint); err != nil {
		return err
	}
	// H10 / decision 11: default is unrestricted (DefaultConfig seeds true).
	// This only fires when the operator explicitly opts into scoping but lists
	// no ports, which would silently deny every loopback command. An explicit
	// non-empty list is honored regardless of the flag (see
	// loopbackPolicyFromConfig in cmd/agent), so no error is needed there.
	if !c.AllowUnrestrictedLoopback && len(c.AllowedLoopbackPorts) == 0 {
		return fmt.Errorf(
			"allow_unrestricted_loopback is false but allowed_loopback_ports is empty; " +
				"either set allow_unrestricted_loopback: true or list the local ports the agent may dial",
		)
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
