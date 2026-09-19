package docker

import (
	"errors"
	"fmt"
	"path/filepath"
	"regexp"
	"strings"

	"unswarm/agent/internal/protocol"
)

// ErrContainerCreationDisabled is returned by CreatePolicy.Validate when the
// operator has not enabled create_container. It names the config key so the
// fix is obvious from the command result.
var ErrContainerCreationDisabled = errors.New(
	"container creation is disabled; set allow_container_creation: true to enable it",
)

// hardDenyHostPaths are always denied as bind sources, regardless of
// AllowedHostPathPrefixes. Mirrors the backend A4 ContainerCreatePolicy
// denylist exactly. "/home" and "/run" are intentionally NOT denied so GPU
// model directories under /home keep working when explicitly allowlisted.
var hardDenyHostPaths = []string{"/", "/etc", "/proc", "/sys", "/dev", "/root", "/boot"}

// hardDenySockNames are socket basenames that must never be bind-mounted
// (docker/containerd control sockets → full host compromise).
var hardDenySockNames = map[string]bool{
	"docker.sock":     true,
	"containerd.sock": true,
}

const (
	maxCreateServerArgs = 64
	maxCreateServerArg  = 4096
	minCreateShmSizeMb  = 64
	maxCreateShmSizeMb  = 65536
	maxCreatePort       = 65535
)

var envKeyRe = regexp.MustCompile(`^[A-Za-z_][A-Za-z0-9_]*$`)

// CreatePolicy is the agent-side create_container policy (H7). It mirrors the
// backend A4 ContainerCreatePolicy denylist. Empty allowlists mean deny-all
// (decision 4).
type CreatePolicy struct {
	Enabled                 bool
	AllowedImages           []string
	AllowedHostPathPrefixes []string
	AllowHostNetwork        bool
	AllowIpcHost            bool
	AllowedDevicePrefixes   []string
	BindAddress             string
}

// BindAddressOrDefault returns the configured bind address, defaulting to
// 127.0.0.1 so mapped container ports are never exposed on 0.0.0.0 (H7).
func (p CreatePolicy) BindAddressOrDefault() string {
	if s := strings.TrimSpace(p.BindAddress); s != "" {
		return s
	}
	return "127.0.0.1"
}

// Validate rejects a create_container payload that violates the policy. It is
// called before any Docker API call so a disallowed image/bind/network/device
// is never even pulled.
func (p CreatePolicy) Validate(payload protocol.CreateContainerPayload) error {
	if !p.Enabled {
		return ErrContainerCreationDisabled
	}
	if err := p.validateImage(payload.Image); err != nil {
		return err
	}
	if err := p.validateNetwork(payload.NetworkMode); err != nil {
		return err
	}
	if err := p.validateIpc(payload.IpcMode); err != nil {
		return err
	}

	// Ports: 0 means "unset"; otherwise must be within 1..65535.
	if payload.ContainerPort < 0 || payload.ContainerPort > maxCreatePort {
		return fmt.Errorf("containerPort %d is out of range (1..%d)", payload.ContainerPort, maxCreatePort)
	}
	if payload.HostPort < 0 || payload.HostPort > maxCreatePort {
		return fmt.Errorf("hostPort %d is out of range (1..%d)", payload.HostPort, maxCreatePort)
	}

	// Shared memory bounds (Docker default is 64MB).
	if payload.ShmSizeMb != 0 &&
		(payload.ShmSizeMb < minCreateShmSizeMb || payload.ShmSizeMb > maxCreateShmSizeMb) {
		return fmt.Errorf("shmSizeMb %d is out of range (%d..%d)", payload.ShmSizeMb, minCreateShmSizeMb, maxCreateShmSizeMb)
	}

	if len(payload.ServerArgs) > maxCreateServerArgs {
		return fmt.Errorf("serverArgs has %d entries; the maximum is %d", len(payload.ServerArgs), maxCreateServerArgs)
	}
	for _, a := range payload.ServerArgs {
		if len(a) > maxCreateServerArg {
			return fmt.Errorf("a serverArgs entry exceeds the maximum length of %d bytes", maxCreateServerArg)
		}
	}

	for k := range payload.Env {
		if !envKeyRe.MatchString(k) {
			return fmt.Errorf("env key %q is invalid (must match %s)", k, envKeyRe.String())
		}
	}

	for _, v := range payload.Volumes {
		if err := p.validateHostPath(v.Host); err != nil {
			return err
		}
		if strings.TrimSpace(v.Container) == "" || !filepath.IsAbs(v.Container) {
			return fmt.Errorf("volume container path %q must be absolute", v.Container)
		}
	}

	for _, d := range payload.Devices {
		if err := p.validateDevice(d); err != nil {
			return err
		}
	}
	return nil
}

func (p CreatePolicy) validateImage(image string) error {
	image = strings.TrimSpace(image)
	if image == "" {
		return fmt.Errorf("image is required")
	}
	if len(p.AllowedImages) == 0 {
		return fmt.Errorf("no images are allowed; set container_creation.allowed_images")
	}
	for _, pattern := range p.AllowedImages {
		if globMatch(strings.TrimSpace(pattern), image) {
			return nil
		}
	}
	return fmt.Errorf("image %q is not in container_creation.allowed_images", image)
}

func (p CreatePolicy) validateNetwork(mode string) error {
	mode = strings.TrimSpace(mode)
	switch {
	case mode == "" || mode == "bridge" || mode == "none":
		return nil
	case mode == "host" || strings.HasPrefix(mode, "container:"):
		if p.AllowHostNetwork {
			return nil
		}
		return fmt.Errorf(
			"network mode %q is not allowed; set container_creation.allow_host_network: true to permit it",
			mode,
		)
	default:
		return fmt.Errorf("network mode %q is not allowed", mode)
	}
}

func (p CreatePolicy) validateIpc(mode string) error {
	mode = strings.TrimSpace(mode)
	switch {
	case mode == "" || mode == "private":
		return nil
	case mode == "host" || strings.HasPrefix(mode, "container:"):
		if p.AllowIpcHost {
			return nil
		}
		return fmt.Errorf(
			"ipc mode %q is not allowed; set container_creation.allow_ipc_host: true to permit it",
			mode,
		)
	default:
		return fmt.Errorf("ipc mode %q is not allowed", mode)
	}
}

func (p CreatePolicy) validateHostPath(host string) error {
	host = strings.TrimSpace(host)
	if host == "" {
		return fmt.Errorf("volume host path is required")
	}
	cleaned := filepath.Clean(host)
	if !filepath.IsAbs(cleaned) {
		return fmt.Errorf("volume host path %q must be absolute", host)
	}
	// Resolve symlinks so a /tmp/x -> /etc style link cannot bypass the
	// prefix tests. Best-effort: nonexistent paths use the cleaned form.
	resolved := resolvePathBestEffort(cleaned)

	// Hard denylist always wins, even over an allowlisted prefix.
	for _, deny := range hardDenyHostPaths {
		if isUnderOrEqual(resolved, deny) {
			return fmt.Errorf("volume host path %q is in a denied location (%s)", host, deny)
		}
	}
	// Any path component naming a container control socket is denied
	// (case-insensitive), matching the backend A4 policy.
	for _, component := range strings.Split(resolved, string(filepath.Separator)) {
		if hardDenySockNames[strings.ToLower(component)] {
			return fmt.Errorf("volume host path %q targets a container control socket", host)
		}
	}

	if len(p.AllowedHostPathPrefixes) == 0 {
		return fmt.Errorf("host path mounts are not allowed; set container_creation.allowed_host_path_prefixes")
	}
	for _, prefix := range p.AllowedHostPathPrefixes {
		pc := filepath.Clean(strings.TrimSpace(prefix))
		if pc == "." || pc == "" || !filepath.IsAbs(pc) {
			continue
		}
		if isUnderOrEqual(resolved, pc) {
			return nil
		}
	}
	return fmt.Errorf("volume host path %q is not under container_creation.allowed_host_path_prefixes", host)
}

func (p CreatePolicy) validateDevice(device string) error {
	device = strings.TrimSpace(device)
	if device == "" {
		return fmt.Errorf("device path is required")
	}
	cleaned := filepath.Clean(device)
	if cleaned != "/dev" && !strings.HasPrefix(cleaned, "/dev/") {
		return fmt.Errorf("device %q must be under /dev", device)
	}
	// Resolve symlinks (parity with the backend ContainerCreatePolicy and
	// validateHostPath) so a device path that is a symlink under /dev cannot
	// point outside /dev or outside the allowed prefixes. Best-effort:
	// nonexistent paths use the cleaned form, matching existing device nodes
	// that are not present in the test/CI environment.
	resolved := resolvePathBestEffort(cleaned)
	// Re-check containment on the resolved path: a symlink under /dev that
	// points elsewhere is rejected before the allowlist tests.
	if resolved != "/dev" && !strings.HasPrefix(resolved, "/dev/") {
		return fmt.Errorf("device %q resolves to %q, outside /dev", device, resolved)
	}
	if len(p.AllowedDevicePrefixes) == 0 {
		return fmt.Errorf("device passthrough is not allowed; set container_creation.allowed_device_prefixes")
	}
	for _, prefix := range p.AllowedDevicePrefixes {
		pc := filepath.Clean(strings.TrimSpace(prefix))
		if pc == "." || pc == "" {
			continue
		}
		// Device prefixes use plain prefix semantics: /dev/nvidia matches
		// /dev/nvidia0 and /dev/nvidiactl.
		if strings.HasPrefix(resolved, pc) {
			return nil
		}
	}
	return fmt.Errorf("device %q is not under container_creation.allowed_device_prefixes", device)
}

// resolvePathBestEffort resolves symlinks in an already-cleaned absolute path,
// falling back to the cleaned path when it does not exist (or cannot be
// resolved). This lets allowlist checks compare the real target of a symlink
// instead of the link path.
func resolvePathBestEffort(cleaned string) string {
	if r, err := filepath.EvalSymlinks(cleaned); err == nil {
		return r
	}
	return cleaned
}

// isUnderOrEqual reports whether path equals prefix or is a descendant of it
// on a path-component boundary. A prefix of "/" matches only "/" itself.
func isUnderOrEqual(path, prefix string) bool {
	if prefix == "/" {
		return path == "/"
	}
	if path == prefix {
		return true
	}
	return strings.HasPrefix(path, prefix+string(filepath.Separator))
}

// globMatch matches pattern against s where '*' matches any sequence of
// (including '/') and '?' matches any single character. Unlike path.Match,
// '*' spans path separators, so image patterns like "ghcr.io/foo/*" work.
func globMatch(pattern, s string) bool {
	pi, si := 0, 0
	star, mark := -1, 0
	for si < len(s) {
		switch {
		case pi < len(pattern) && (pattern[pi] == '?' || pattern[pi] == s[si]):
			pi++
			si++
		case pi < len(pattern) && pattern[pi] == '*':
			star = pi
			mark = si
			pi++
		case star != -1:
			pi = star + 1
			mark++
			si = mark
		default:
			return false
		}
	}
	for pi < len(pattern) && pattern[pi] == '*' {
		pi++
	}
	return pi == len(pattern)
}
