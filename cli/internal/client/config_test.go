package client

import (
	"os"
	"path/filepath"
	"strings"
	"testing"
)

func TestConfigPathUsesHome(t *testing.T) {
	home := t.TempDir()
	t.Setenv("HOME", home)
	want := filepath.Join(home, ".config", "unswarm", "cli.yaml")
	if got := ConfigPath(); got != want {
		t.Errorf("ConfigPath() = %q, want %q", got, want)
	}
}

func TestDefaultConfig(t *testing.T) {
	cfg := defaultConfig()
	if cfg.BaseURL != "http://localhost:22301" {
		t.Errorf("BaseURL = %q", cfg.BaseURL)
	}
	if cfg.OutputFmt != "table" {
		t.Errorf("OutputFmt = %q", cfg.OutputFmt)
	}
	if !cfg.Color {
		t.Error("expected Color=true by default")
	}
}

func TestSaveAndLoadConfigRoundTrip(t *testing.T) {
	t.Setenv("HOME", t.TempDir())

	want := &Config{BaseURL: "http://example.test:9", OutputFmt: "json", Color: false}
	if err := SaveConfig(want); err != nil {
		t.Fatalf("SaveConfig failed: %v", err)
	}

	got, err := LoadConfig()
	if err != nil {
		t.Fatalf("LoadConfig failed: %v", err)
	}
	if got.BaseURL != want.BaseURL || got.OutputFmt != want.OutputFmt || got.Color != want.Color {
		t.Errorf("LoadConfig() = %+v, want %+v", got, want)
	}
}

func TestLoadConfigMissingFileReturnsDefaults(t *testing.T) {
	t.Setenv("HOME", t.TempDir())
	got, err := LoadConfig()
	if err != nil {
		t.Fatalf("LoadConfig failed: %v", err)
	}
	if got.BaseURL != "http://localhost:22301" || got.OutputFmt != "table" || !got.Color {
		t.Errorf("expected defaults, got %+v", got)
	}
}

func TestLoadConfigFileMissingReturnsDefaultContext(t *testing.T) {
	t.Setenv("HOME", t.TempDir())
	cf, err := LoadConfigFile()
	if err != nil {
		t.Fatalf("LoadConfigFile failed: %v", err)
	}
	if cf.ActiveContext != "default" {
		t.Errorf("ActiveContext = %q, want default", cf.ActiveContext)
	}
	if _, ok := cf.Contexts["default"]; !ok {
		t.Errorf("expected default context, got %+v", cf.Contexts)
	}
}

func TestSaveConfigWithContext(t *testing.T) {
	t.Setenv("HOME", t.TempDir())

	prod := &Config{BaseURL: "http://prod", OutputFmt: "json", Color: false}
	dev := &Config{BaseURL: "http://dev", OutputFmt: "table", Color: true}
	if err := SaveConfigWithContext(prod, "prod"); err != nil {
		t.Fatalf("SaveConfigWithContext(prod) failed: %v", err)
	}
	if err := SaveConfigWithContext(dev, "dev"); err != nil {
		t.Fatalf("SaveConfigWithContext(dev) failed: %v", err)
	}

	cf, err := LoadConfigFile()
	if err != nil {
		t.Fatalf("LoadConfigFile failed: %v", err)
	}
	if cf.Contexts["prod"] == nil || cf.Contexts["prod"].BaseURL != "http://prod" {
		t.Errorf("prod context missing/wrong: %+v", cf.Contexts["prod"])
	}
	if cf.Contexts["dev"] == nil || cf.Contexts["dev"].BaseURL != "http://dev" {
		t.Errorf("dev context missing/wrong: %+v", cf.Contexts["dev"])
	}
}

func TestSaveConfigFileRoundTrip(t *testing.T) {
	t.Setenv("HOME", t.TempDir())

	cf := &ConfigFile{
		ActiveContext: "beta",
		Contexts: map[string]*Config{
			"beta": {BaseURL: "http://beta", OutputFmt: "json", Color: false},
		},
	}
	if err := SaveConfigFile(cf); err != nil {
		t.Fatalf("SaveConfigFile failed: %v", err)
	}
	got, err := LoadConfigFile()
	if err != nil {
		t.Fatalf("LoadConfigFile failed: %v", err)
	}
	if got.ActiveContext != "beta" || got.Contexts["beta"] == nil {
		t.Errorf("round-trip mismatch: %+v", got)
	}
	if c, _ := LoadConfig(); c == nil || c.BaseURL != "http://beta" {
		t.Errorf("LoadConfig returned %+v", c)
	}
}

func writeConfigFile(t *testing.T, home, contents string, mode os.FileMode) string {
	t.Helper()
	dir := filepath.Join(home, ".config", "unswarm")
	if err := os.MkdirAll(dir, 0o700); err != nil {
		t.Fatal(err)
	}
	path := filepath.Join(dir, "cli.yaml")
	if err := os.WriteFile(path, []byte(contents), mode); err != nil {
		t.Fatal(err)
	}
	if err := os.Chmod(path, mode); err != nil {
		t.Fatal(err)
	}
	return path
}

func TestLoadConfigRejectsUnsafePermissions(t *testing.T) {
	home := t.TempDir()
	t.Setenv("HOME", home)
	writeConfigFile(t, home, "contexts: {}\n", 0o644)

	_, err := LoadConfig()
	if err == nil || !strings.Contains(err.Error(), "unsafe permissions") {
		t.Errorf("expected unsafe-permissions error, got: %v", err)
	}
}

func TestLoadConfigRejectsSymlink(t *testing.T) {
	home := t.TempDir()
	t.Setenv("HOME", home)

	target := filepath.Join(home, "real.yaml")
	if err := os.WriteFile(target, []byte("contexts: {}\n"), 0o600); err != nil {
		t.Fatal(err)
	}
	dir := filepath.Join(home, ".config", "unswarm")
	if err := os.MkdirAll(dir, 0o700); err != nil {
		t.Fatal(err)
	}
	if err := os.Symlink(target, filepath.Join(dir, "cli.yaml")); err != nil {
		t.Fatal(err)
	}

	_, err := LoadConfig()
	if err == nil || !strings.Contains(err.Error(), "symlink") {
		t.Errorf("expected symlink rejection, got: %v", err)
	}
}

func TestLoadConfigRejectsInvalidYAML(t *testing.T) {
	home := t.TempDir()
	t.Setenv("HOME", home)
	writeConfigFile(t, home, "contexts: [this is not valid: {\n", 0o600)

	_, err := LoadConfig()
	if err == nil || !strings.Contains(err.Error(), "failed to parse config") {
		t.Errorf("expected parse error, got: %v", err)
	}
}

func TestLoadConfigMigratesFlatFormat(t *testing.T) {
	home := t.TempDir()
	t.Setenv("HOME", home)
	path := writeConfigFile(t, home, "url: http://flat:1\noutput: json\ncolor: false\n", 0o600)

	cfg, err := LoadConfig()
	if err != nil {
		t.Fatalf("LoadConfig failed: %v", err)
	}
	if cfg.BaseURL != "http://flat:1" || cfg.OutputFmt != "json" || cfg.Color {
		t.Errorf("flat config not migrated correctly: %+v", cfg)
	}

	// Migration should have rewritten the file in context format.
	data, err := os.ReadFile(path)
	if err != nil {
		t.Fatal(err)
	}
	if !strings.Contains(string(data), "contexts:") {
		t.Errorf("expected migrated context format, got:\n%s", data)
	}
}

func TestLoadConfigFallsBackToDefaultContext(t *testing.T) {
	home := t.TempDir()
	t.Setenv("HOME", home)
	writeConfigFile(t, home, "active-context: missing\ncontexts:\n  default:\n    url: http://d\n    output: json\n    color: false\n", 0o600)

	cfg, err := LoadConfig()
	if err != nil {
		t.Fatalf("LoadConfig failed: %v", err)
	}
	if cfg.BaseURL != "http://d" {
		t.Errorf("expected fallback to default context, got %+v", cfg)
	}
}

func TestLoadConfigNoContextsReturnsDefaults(t *testing.T) {
	home := t.TempDir()
	t.Setenv("HOME", home)
	// Contexts present but without an ordinary populated map triggers the
	// missing-context fallback path back to defaults.
	writeConfigFile(t, home, "active-context: x\ncontexts: {}\n", 0o600)

	cfg, err := LoadConfig()
	if err != nil {
		t.Fatalf("LoadConfig failed: %v", err)
	}
	if cfg.BaseURL != "http://localhost:22301" {
		t.Errorf("expected defaults, got %+v", cfg)
	}
}
