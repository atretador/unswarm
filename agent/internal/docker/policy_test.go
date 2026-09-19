package docker

import (
	"errors"
	"fmt"
	"os"
	"path/filepath"
	"strings"
	"testing"

	"unswarm/agent/internal/protocol"
)

// basePolicy is a permissive-but-scoped policy used as the starting point for
// each test case; individual tests tighten fields as needed.
func basePolicy() CreatePolicy {
	return CreatePolicy{
		Enabled:                 true,
		AllowedImages:           []string{"ghcr.io/ggml-org/llama.cpp:*"},
		AllowedHostPathPrefixes: []string{"/srv/models"},
		AllowedDevicePrefixes:   []string{"/dev/kfd", "/dev/dri/", "/dev/nvidia"},
		BindAddress:             "127.0.0.1",
	}
}

func validPayload() protocol.CreateContainerPayload {
	return protocol.CreateContainerPayload{
		Image:         "ghcr.io/ggml-org/llama.cpp:server",
		ContainerName: "llama",
		NetworkMode:   "bridge",
		IpcMode:       "private",
	}
}

func TestCreatePolicy_Disabled(t *testing.T) {
	p := basePolicy()
	p.Enabled = false
	err := p.Validate(validPayload())
	if !errors.Is(err, ErrContainerCreationDisabled) {
		t.Fatalf("Validate() = %v, want ErrContainerCreationDisabled", err)
	}
	if !strings.Contains(err.Error(), "allow_container_creation") {
		t.Errorf("error %q should name allow_container_creation", err)
	}
}

func TestCreatePolicy_ImageAllowlist(t *testing.T) {
	tests := []struct {
		name    string
		images  []string
		image   string
		wantErr bool
	}{
		{name: "glob match", images: []string{"ghcr.io/ggml-org/llama.cpp:*"}, image: "ghcr.io/ggml-org/llama.cpp:server", wantErr: false},
		{name: "star spans slashes", images: []string{"ghcr.io/*"}, image: "ghcr.io/ggml-org/llama.cpp:server", wantErr: false},
		{name: "question mark", images: []string{"alpine:3.?"}, image: "alpine:3.9", wantErr: false},
		{name: "no match", images: []string{"alpine:*"}, image: "ubuntu:latest", wantErr: true},
		{name: "empty allowlist denies", images: nil, image: "alpine:latest", wantErr: true},
		{name: "empty image denied", images: []string{"*"}, image: "  ", wantErr: true},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			p := basePolicy()
			p.AllowedImages = tt.images
			payload := validPayload()
			payload.Image = tt.image
			err := p.Validate(payload)
			if (err != nil) != tt.wantErr {
				t.Fatalf("Validate() error = %v, wantErr %v", err, tt.wantErr)
			}
		})
	}
}

func TestCreatePolicy_NetworkAndIpc(t *testing.T) {
	tests := []struct {
		name      string
		allowHost bool
		allowIpc  bool
		network   string
		ipc       string
		wantErr   bool
	}{
		{name: "bridge/private ok", network: "bridge", ipc: "private"},
		{name: "empty ok", network: "", ipc: ""},
		{name: "none ok", network: "none", ipc: "private"},
		{name: "host network denied", network: "host", ipc: "private", wantErr: true},
		{name: "container network denied", network: "container:abc", ipc: "private", wantErr: true},
		{name: "host network allowed", allowHost: true, network: "host", ipc: "private"},
		{name: "container network allowed", allowHost: true, network: "container:abc", ipc: "private"},
		{name: "host ipc denied", network: "bridge", ipc: "host", wantErr: true},
		{name: "host ipc allowed", allowIpc: true, network: "bridge", ipc: "host"},
		{name: "container ipc denied", network: "bridge", ipc: "container:x", wantErr: true},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			p := basePolicy()
			p.AllowHostNetwork = tt.allowHost
			p.AllowIpcHost = tt.allowIpc
			payload := validPayload()
			payload.NetworkMode = tt.network
			payload.IpcMode = tt.ipc
			err := p.Validate(payload)
			if (err != nil) != tt.wantErr {
				t.Fatalf("Validate() error = %v, wantErr %v", err, tt.wantErr)
			}
		})
	}
}

func TestCreatePolicy_HostPathDenylistAndAllowlist(t *testing.T) {
	allowed := filepath.Join(t.TempDir(), "models")
	if err := os.MkdirAll(allowed, 0o755); err != nil {
		t.Fatal(err)
	}

	tests := []struct {
		name    string
		prefix  []string
		host    string
		wantErr bool
	}{
		{name: "allowed prefix", prefix: []string{allowed}, host: filepath.Join(allowed, "m.gguf")},
		{name: "prefix boundary rejects sibling", prefix: []string{allowed}, host: allowed + "-evil/x", wantErr: true},
		{name: "empty allowlist denies", prefix: nil, host: filepath.Join(allowed, "m.gguf"), wantErr: true},
		{name: "hard deny /etc", prefix: []string{allowed}, host: "/etc/passwd", wantErr: true},
		{name: "hard deny /", prefix: []string{allowed}, host: "/", wantErr: true},
		{name: "hard deny /root", prefix: []string{allowed}, host: "/root/.ssh/id_rsa", wantErr: true},
		{name: "hard deny docker.sock", prefix: []string{allowed}, host: "/var/run/docker.sock", wantErr: true},
		{name: "relative denied", prefix: []string{allowed}, host: "relative/path", wantErr: true},
		{name: "empty denied", prefix: []string{allowed}, host: "", wantErr: true},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			p := basePolicy()
			p.AllowedHostPathPrefixes = tt.prefix
			payload := validPayload()
			payload.Volumes = []protocol.VolumeMount{{Host: tt.host, Container: "/data"}}
			err := p.Validate(payload)
			if (err != nil) != tt.wantErr {
				t.Fatalf("Validate(%q) error = %v, wantErr %v", tt.host, err, tt.wantErr)
			}
		})
	}
}

func TestCreatePolicy_SymlinkEscapeDenied(t *testing.T) {
	root := t.TempDir()
	allowed := filepath.Join(root, "models")
	if err := os.MkdirAll(allowed, 0o755); err != nil {
		t.Fatal(err)
	}
	// A symlink inside the allowlisted prefix pointing at /etc must be denied.
	link := filepath.Join(allowed, "escape")
	if err := os.Symlink("/etc", link); err != nil {
		t.Skipf("symlink unsupported: %v", err)
	}
	p := basePolicy()
	p.AllowedHostPathPrefixes = []string{allowed}
	payload := validPayload()
	payload.Volumes = []protocol.VolumeMount{{Host: link, Container: "/data"}}
	if err := p.Validate(payload); err == nil {
		t.Fatal("symlink escape to /etc should be denied")
	}
}

func TestCreatePolicy_Devices(t *testing.T) {
	tests := []struct {
		name    string
		devices []string
		wantErr bool
	}{
		{name: "kfd allowed", devices: []string{"/dev/kfd"}},
		{name: "dri allowed", devices: []string{"/dev/dri/renderD128"}},
		{name: "nvidia prefix allowed", devices: []string{"/dev/nvidia0", "/dev/nvidiactl"}},
		{name: "sda denied", devices: []string{"/dev/sda"}, wantErr: true},
		{name: "outside dev denied", devices: []string{"/etc/passwd"}, wantErr: true},
		{name: "empty entry denied", devices: []string{""}, wantErr: true},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			p := basePolicy()
			payload := validPayload()
			payload.Devices = tt.devices
			err := p.Validate(payload)
			if (err != nil) != tt.wantErr {
				t.Fatalf("Validate() error = %v, wantErr %v", err, tt.wantErr)
			}
		})
	}
}

func TestCreatePolicy_EmptyDeviceAllowlistDenies(t *testing.T) {
	p := basePolicy()
	p.AllowedDevicePrefixes = nil
	payload := validPayload()
	payload.Devices = []string{"/dev/nvidia0"}
	if err := p.Validate(payload); err == nil {
		t.Fatal("empty device allowlist should deny all devices")
	}
}

// TestCreatePolicy_DeviceSymlinkEscapeDenied verifies that a device path which
// is a symlink under /dev but resolves outside /dev is rejected (parity with
// the backend ContainerCreatePolicy, which resolves symlinks). The path must
// start under /dev, so creating the link requires write access to /dev; the
// test skips when that is unavailable (e.g. non-root CI).
func TestCreatePolicy_DeviceSymlinkEscapeDenied(t *testing.T) {
	link := filepath.Join("/dev", fmt.Sprintf("unswarm-test-dev-%d", os.Getpid()))
	if err := os.Symlink("/etc/passwd", link); err != nil {
		t.Skipf("cannot create symlink under /dev (needs write access to /dev): %v", err)
	}
	defer func() { _ = os.Remove(link) }()

	p := basePolicy()
	payload := validPayload()
	payload.Devices = []string{link}
	if err := p.Validate(payload); err == nil {
		t.Fatal("device symlink resolving outside /dev should be denied")
	}
}

func TestCreatePolicy_Bounds(t *testing.T) {
	tests := []struct {
		name    string
		mutate  func(*protocol.CreateContainerPayload)
		wantErr bool
	}{
		{name: "valid ports", mutate: func(p *protocol.CreateContainerPayload) { p.ContainerPort = 8080; p.HostPort = 8081 }},
		{name: "container port too high", mutate: func(p *protocol.CreateContainerPayload) { p.ContainerPort = 70000 }, wantErr: true},
		{name: "negative host port", mutate: func(p *protocol.CreateContainerPayload) { p.HostPort = -1 }, wantErr: true},
		{name: "shm too small", mutate: func(p *protocol.CreateContainerPayload) { p.ShmSizeMb = 32 }, wantErr: true},
		{name: "shm too large", mutate: func(p *protocol.CreateContainerPayload) { p.ShmSizeMb = 70000 }, wantErr: true},
		{name: "shm valid", mutate: func(p *protocol.CreateContainerPayload) { p.ShmSizeMb = 1024 }},
		{name: "bad env key", mutate: func(p *protocol.CreateContainerPayload) { p.Env = map[string]string{"1BAD": "x"} }, wantErr: true},
		{name: "good env key", mutate: func(p *protocol.CreateContainerPayload) { p.Env = map[string]string{"LD_PRELOAD": "x"} }},
		{name: "too many server args", mutate: func(p *protocol.CreateContainerPayload) {
			p.ServerArgs = make([]string, maxCreateServerArgs+1)
		}, wantErr: true},
		{name: "server arg too long", mutate: func(p *protocol.CreateContainerPayload) {
			p.ServerArgs = []string{strings.Repeat("a", maxCreateServerArg+1)}
		}, wantErr: true},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			p := basePolicy()
			payload := validPayload()
			tt.mutate(&payload)
			err := p.Validate(payload)
			if (err != nil) != tt.wantErr {
				t.Fatalf("Validate() error = %v, wantErr %v", err, tt.wantErr)
			}
		})
	}
}

func TestCreatePolicy_BindAddressDefault(t *testing.T) {
	if got := (CreatePolicy{}).BindAddressOrDefault(); got != "127.0.0.1" {
		t.Errorf("default bind address = %q, want 127.0.0.1", got)
	}
	if got := (CreatePolicy{BindAddress: "10.0.0.5"}).BindAddressOrDefault(); got != "10.0.0.5" {
		t.Errorf("configured bind address = %q, want 10.0.0.5", got)
	}
}

func TestGlobMatch(t *testing.T) {
	tests := []struct {
		pattern, s string
		want       bool
	}{
		{"*", "anything/at-all", true},
		{"ghcr.io/*", "ghcr.io/a/b:c", true},
		{"alpine:3.?", "alpine:3.9", true},
		{"alpine:3.?", "alpine:3.10", false},
		{"exact", "exact", true},
		{"exact", "nope", false},
		{"", "", true},
		{"", "x", false},
	}
	for _, tt := range tests {
		if got := globMatch(tt.pattern, tt.s); got != tt.want {
			t.Errorf("globMatch(%q, %q) = %v, want %v", tt.pattern, tt.s, got, tt.want)
		}
	}
}

func TestIsUnderOrEqual(t *testing.T) {
	tests := []struct {
		path, prefix string
		want         bool
	}{
		{"/srv/models", "/srv/models", true},
		{"/srv/models/x", "/srv/models", true},
		{"/srv/models-evil/x", "/srv/models", false},
		{"/", "/", true},
		{"/etc", "/", false},
		{"/etc/passwd", "/etc", true},
	}
	for _, tt := range tests {
		if got := isUnderOrEqual(tt.path, tt.prefix); got != tt.want {
			t.Errorf("isUnderOrEqual(%q, %q) = %v, want %v", tt.path, tt.prefix, got, tt.want)
		}
	}
}
