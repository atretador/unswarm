package client

import (
	"fmt"
	"os"
	"path/filepath"
	"strings"

	"gopkg.in/yaml.v3"
)

// Config holds CLI configuration for a single context
type Config struct {
	BaseURL   string `yaml:"url"`
	OutputFmt string `yaml:"output"`
	Color     bool   `yaml:"color"`
}

// ConfigFile is the top-level config file structure with multi-context support
type ConfigFile struct {
	ActiveContext string            `yaml:"active-context"`
	Contexts      map[string]*Config `yaml:"contexts"`
}

// ConfigPath returns ~/.config/unswarm/cli.yaml
func ConfigPath() string {
	home, err := os.UserHomeDir()
	if err != nil {
		return ""
	}
	return filepath.Join(home, ".config", "unswarm", "cli.yaml")
}

// defaultConfig returns the default Config
func defaultConfig() *Config {
	return &Config{
		BaseURL:   "http://localhost:22301",
		OutputFmt: "table",
		Color:     true,
	}
}

// loadConfigFile reads the raw config file data and detects whether it is
// the old flat format or the new context-based format. For old format, it
// auto-migrates to the new format and saves back.
func loadConfigFile(path string) (*ConfigFile, error) {
	data, err := os.ReadFile(path)
	if err != nil {
		return nil, err
	}

	// Check permissions
	info, err := os.Stat(path)
	if err != nil {
		return nil, fmt.Errorf("failed to stat config: %w", err)
	}
	if info.Mode().Perm() > 0600 {
		return nil, fmt.Errorf("config file has unsafe permissions %o (must be 0600 or less)", info.Mode().Perm())
	}

	// Reject symlinks
	lInfo, err := os.Lstat(path)
	if err != nil {
		return nil, fmt.Errorf("failed to lstat config: %w", err)
	}
	if lInfo.Mode()&os.ModeSymlink != 0 {
		return nil, fmt.Errorf("config file is a symlink (rejected for security)")
	}

	// First, try to unmarshal into the context format
	cf := &ConfigFile{}
	if err := yaml.Unmarshal(data, cf); err != nil {
		return nil, fmt.Errorf("failed to parse config: %w", err)
	}

	// Detect old flat format: if "contexts" key is absent but "url" or other
	// Config fields are present at the top level, treat as old format
	if cf.Contexts == nil {
		// Try parsing as flat config
		flat := &Config{
			BaseURL:   "http://localhost:22301",
			OutputFmt: "table",
			Color:     true,
		}
		if err := yaml.Unmarshal(data, flat); err != nil {
			return nil, fmt.Errorf("failed to parse config: %w", err)
		}

		// Auto-migrate: wrap flat config as "default" context
		cf = &ConfigFile{
			ActiveContext: "default",
			Contexts: map[string]*Config{
				"default": flat,
			},
		}

		// Save back in new format
		if err := saveConfigFileRaw(path, cf); err != nil {
			// Non-fatal: migration saved best-effort
			_ = err
		}
	}

	// Ensure ActiveContext defaults to "default" if empty
	if cf.ActiveContext == "" {
		cf.ActiveContext = "default"
	}

	return cf, nil
}

// LoadConfig reads ~/.config/unswarm/cli.yaml and returns the active context's config.
// Transparently handles both old flat format and new context format.
func LoadConfig() (*Config, error) {
	path := ConfigPath()
	if path == "" {
		return defaultConfig(), nil
	}

	cf, err := loadConfigFile(path)
	if err != nil {
		if os.IsNotExist(err) {
			return defaultConfig(), nil
		}
		return nil, err
	}

	// Look up active context
	cfg, ok := cf.Contexts[cf.ActiveContext]
	if !ok {
		// Active context not found, try "default", else use defaults
		cfg, ok = cf.Contexts["default"]
		if !ok {
			return defaultConfig(), nil
		}
	}

	return cfg, nil
}

// LoadConfigFile reads the full config file structure (with all contexts)
func LoadConfigFile() (*ConfigFile, error) {
	path := ConfigPath()
	if path == "" {
		return &ConfigFile{
			ActiveContext: "default",
			Contexts: map[string]*Config{
				"default": defaultConfig(),
			},
		}, nil
	}

	cf, err := loadConfigFile(path)
	if err != nil {
		if os.IsNotExist(err) {
			return &ConfigFile{
				ActiveContext: "default",
				Contexts: map[string]*Config{
					"default": defaultConfig(),
				},
			}, nil
		}
		return nil, err
	}

	return cf, nil
}

// SaveConfigFile writes the full config file structure
func SaveConfigFile(cf *ConfigFile) error {
	path := ConfigPath()
	if path == "" {
		return fmt.Errorf("cannot determine config path")
	}
	return saveConfigFileRaw(path, cf)
}

// SaveConfig writes config atomically with 0600 perms (context format).
// This wraps the single config into the context structure.
func SaveConfig(cfg *Config) error {
	return SaveConfigWithContext(cfg, "default")
}

// SaveConfigWithContext saves a config under a named context
func SaveConfigWithContext(cfg *Config, contextName string) error {
	path := ConfigPath()
	if path == "" {
		return fmt.Errorf("cannot determine config path")
	}

	// Load existing file structure if available
	cf, err := LoadConfigFile()
	if err != nil {
		// Start fresh
		cf = &ConfigFile{
			ActiveContext: contextName,
			Contexts:      make(map[string]*Config),
		}
	}

	if cf.Contexts == nil {
		cf.Contexts = make(map[string]*Config)
	}

	cf.Contexts[contextName] = cfg
	if cf.ActiveContext == "" {
		cf.ActiveContext = contextName
	}

	return saveConfigFileRaw(path, cf)
}

// saveConfigFileRaw handles the low-level file write
func saveConfigFileRaw(path string, cf *ConfigFile) error {
	dir := filepath.Dir(path)

	// Create parent directories if absent
	home, err := os.UserHomeDir()
	if err != nil {
		return fmt.Errorf("cannot determine home directory: %w", err)
	}

	configDir := filepath.Join(home, ".config")
	if err := os.MkdirAll(configDir, 0755); err != nil {
		return fmt.Errorf("failed to create config directory: %w", err)
	}

	unswarmDir := filepath.Join(configDir, "unswarm")
	if err := os.MkdirAll(unswarmDir, 0700); err != nil {
		return fmt.Errorf("failed to create unswarm directory: %w", err)
	}

	// Lstat validation: reject symlinks at any level in the path
	parts := strings.Split(path, string(os.PathSeparator))
	checked := ""
	for _, part := range parts {
		if part == "" {
			checked = "/"
			continue
		}
		if checked == "" {
			checked = part
		} else {
			checked = filepath.Join(checked, part)
		}

		lInfo, err := os.Lstat(checked)
		if err != nil {
			if os.IsNotExist(err) {
				continue
			}
			return fmt.Errorf("failed to lstat %s: %w", checked, err)
		}
		if lInfo.Mode()&os.ModeSymlink != 0 {
			return fmt.Errorf("symlink detected at %s (rejected for security)", checked)
		}
	}

	data, err := yaml.Marshal(cf)
	if err != nil {
		return fmt.Errorf("failed to marshal config: %w", err)
	}

	// Atomic write: temp file + os.Rename
	tmpFile, err := os.CreateTemp(dir, "cli.yaml.tmp.*")
	if err != nil {
		return fmt.Errorf("failed to create temp file: %w", err)
	}
	tmpPath := tmpFile.Name()

	// Clean up on error
	defer func() {
		if err != nil {
			os.Remove(tmpPath)
		}
	}()

	if _, err := tmpFile.Write(data); err != nil {
		tmpFile.Close()
		return fmt.Errorf("failed to write config: %w", err)
	}

	if err := tmpFile.Chmod(0600); err != nil {
		tmpFile.Close()
		return fmt.Errorf("failed to set config permissions: %w", err)
	}

	if err := tmpFile.Close(); err != nil {
		return fmt.Errorf("failed to close temp file: %w", err)
	}

	if err := os.Rename(tmpPath, path); err != nil {
		return fmt.Errorf("failed to rename config file: %w", err)
	}

	return nil
}
