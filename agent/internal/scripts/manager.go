// Package scripts manages launcher script processes on the agent host.
// Scripts are bash scripts in the configured scripts_dir that serve
// OpenAI-compatible APIs.
package scripts

import (
	"bufio"
	"bytes"
	"crypto/sha256"
	"encoding/hex"
	"fmt"
	"io"
	"log/slog"
	"os"
	"os/exec"
	"path/filepath"
	"strconv"
	"strings"
	"sync"
	"syscall"
	"time"
)

// Manager tracks launcher script processes spawned on the agent host.
type Manager struct {
	scriptsDir string
	logDir     string
	mu         sync.Mutex
	processes  map[string]*scriptProcess // keyed by resolved absolute path
}

type scriptProcess struct {
	Path string
	PID  int
	Port int
	// StartTime is the wall-clock time the agent registered the process
	// (reported via telemetry).
	StartTime time.Time
	// ProcStart is the OS process start time from /proc/<pid>/stat field 22
	// (clock ticks since boot), captured at registration. It is re-checked
	// before every signal so a recycled PID belonging to an unrelated process
	// is never signalled (PID-reuse guard).
	ProcStart uint64
	Cmd       *exec.Cmd
	LogFile   *os.File
}

// ScriptInfo is a lightweight descriptor returned by ListScripts.
type ScriptInfo struct {
	Path string `json:"path"`
	Name string `json:"name"`
}

// ScriptStatus is the runtime status of a tracked script process.
type ScriptStatus struct {
	Path      string `json:"path"`
	PID       int    `json:"pid"`
	Status    string `json:"status"` // "running" | "stopped"
	Port      int    `json:"port"`
	StartTime int64  `json:"startTime"` // unix ms
}

// NewManager creates a Manager. If scriptsDir is empty the manager is
// disabled (IsEnabled returns false).
func NewManager(scriptsDir string) *Manager {
	logDir := filepath.Join(os.TempDir(), "unswarm-script-logs")
	_ = os.MkdirAll(logDir, 0o700)
	return &Manager{
		scriptsDir: scriptsDir,
		logDir:     logDir,
		processes:  make(map[string]*scriptProcess),
	}
}

// IsEnabled reports whether script support is configured.
func (m *Manager) IsEnabled() bool { return m.scriptsDir != "" }

// ListScripts returns .sh files found at the top level of scriptsDir.
func (m *Manager) ListScripts() []ScriptInfo {
	if m.scriptsDir == "" {
		return nil
	}
	entries, err := os.ReadDir(m.scriptsDir)
	if err != nil {
		return nil
	}
	var out []ScriptInfo
	for _, e := range entries {
		if e.IsDir() || !strings.HasSuffix(e.Name(), ".sh") {
			continue
		}
		abs, err := filepath.Abs(filepath.Join(m.scriptsDir, e.Name()))
		if err != nil {
			continue
		}
		out = append(out, ScriptInfo{Path: abs, Name: e.Name()})
	}
	return out
}

// resolveWithinScriptsDir resolves path to an absolute, symlink-resolved
// location and enforces the scripts_dir whitelist (security boundary). Used by
// both StartScript and GetScriptLogs so log reads cannot escape scripts_dir.
func (m *Manager) resolveWithinScriptsDir(path string) (string, error) {
	resolved, err := filepath.Abs(filepath.Clean(path))
	if err != nil {
		return "", fmt.Errorf("resolve path: %w", err)
	}
	// Resolve symlinks to prevent symlink escapes.
	resolved, err = filepath.EvalSymlinks(resolved)
	if err != nil {
		return "", fmt.Errorf("eval symlinks for %q: %w", path, err)
	}

	scriptsDir, err := filepath.Abs(filepath.Clean(m.scriptsDir))
	if err != nil {
		return "", fmt.Errorf("resolve scripts_dir: %w", err)
	}
	scriptsDir, err = filepath.EvalSymlinks(scriptsDir)
	if err != nil {
		return "", fmt.Errorf("eval symlinks for scripts_dir: %w", err)
	}
	if !strings.HasPrefix(resolved, scriptsDir+string(filepath.Separator)) && resolved != scriptsDir {
		return "", fmt.Errorf("path %q is outside scripts_dir %q", resolved, scriptsDir)
	}
	return resolved, nil
}

// StartScript spawns a bash script in a new process group. The script must
// reside inside the configured scriptsDir (whitelist check). If the script is
// already running (alive process), returns the existing PID without error
// (idempotent). Returns the PID of the spawned process.
//
// TOCTOU narrowing: after whitelist/EvalSymlinks validation the script file is
// opened and validated through the resulting fd (regular-file check), and the
// fd's /proc/self/fd target is re-resolved immediately before exec. If the
// path was swapped after validation, the fd target no longer matches the
// validated path and execution is refused. (bash re-opens the script by path,
// so a full fd-based exec is not possible here; the re-validation shrinks the
// swap window to the minimum this codebase allows.)
func (m *Manager) StartScript(path string, port int) (int, error) {
	resolved, err := m.resolveWithinScriptsDir(path)
	if err != nil {
		return 0, err
	}

	// Open the validated path and verify it through the fd: this pins the
	// inode we inspected even if the directory entry is swapped afterwards.
	f, err := os.Open(resolved)
	if err != nil {
		return 0, fmt.Errorf("open %q: %w", resolved, err)
	}
	defer f.Close()
	info, err := f.Stat()
	if err != nil {
		return 0, fmt.Errorf("stat %q: %w", resolved, err)
	}
	if !info.Mode().IsRegular() {
		return 0, fmt.Errorf("path %q is not a regular file", resolved)
	}

	m.mu.Lock()
	defer m.mu.Unlock()

	// Duplicate guard: return existing PID if process is alive (idempotent).
	if proc, ok := m.processes[resolved]; ok {
		if proc.isOurs() {
			return proc.PID, nil
		}
		// Stale entry — clean up.
		m.cleanupProcess(proc)
		delete(m.processes, resolved)
	}

	// Re-validate immediately before exec via the opened fd: if the file at
	// resolved was replaced after validation, the fd now points elsewhere.
	fdTarget, err := filepath.EvalSymlinks(fmt.Sprintf("/proc/self/fd/%d", f.Fd()))
	if err != nil {
		return 0, fmt.Errorf("re-resolve opened script %q: %w", resolved, err)
	}
	if fdTarget != resolved {
		return 0, fmt.Errorf("script %q changed under validation (fd now resolves to %q); refusing to execute", resolved, fdTarget)
	}

	// Spawn the script.
	cmd := exec.Command("bash", resolved)
	cmd.SysProcAttr = &syscall.SysProcAttr{Setpgid: true}

	logPath := m.logPath(resolved)
	logFile, err := os.OpenFile(logPath, os.O_CREATE|os.O_WRONLY|os.O_APPEND, 0o600)
	if err != nil {
		return 0, fmt.Errorf("open log file: %w", err)
	}
	cmd.Stdout = logFile
	cmd.Stderr = logFile

	if err := cmd.Start(); err != nil {
		logFile.Close()
		return 0, fmt.Errorf("start script: %w", err)
	}

	pid := cmd.Process.Pid
	startTime := time.Now()

	// Capture the OS process start time for the PID-reuse guard. On failure,
	// ProcStart stays 0 and liveness checks degrade to signal-0 only.
	procStart, err := procStatStartTime(pid)
	if err != nil {
		slog.Warn("could not read process start time; PID-reuse guard degraded", "pid", pid, "error", err)
		procStart = 0
	}

	m.processes[resolved] = &scriptProcess{
		Path:      resolved,
		PID:       pid,
		Port:      port,
		StartTime: startTime,
		ProcStart: procStart,
		Cmd:       cmd,
		LogFile:   logFile,
	}

	// Write PID file.
	pidPath := m.pidPath(resolved)
	_ = os.WriteFile(pidPath, []byte(fmt.Sprintf("%d\n", pid)), 0o600)

	// Reap the process in the background so it doesn't become a zombie.
	go func() {
		_ = cmd.Wait()
	}()

	return pid, nil
}

// StopScript kills a script process by PID (entire process group).
func (m *Manager) StopScript(pid int) error {
	m.mu.Lock()
	// Find by PID.
	var found string
	var proc *scriptProcess
	for path, p := range m.processes {
		if p.PID == pid {
			found = path
			proc = p
			break
		}
	}
	m.mu.Unlock()

	if found == "" {
		return fmt.Errorf("no tracked script with pid %d", pid)
	}
	return m.stopAndClean(found, proc)
}

// StopScriptByPath stops a script by its resolved path.
func (m *Manager) StopScriptByPath(path string) error {
	resolved, err := filepath.Abs(filepath.Clean(path))
	if err != nil {
		return fmt.Errorf("resolve path: %w", err)
	}

	m.mu.Lock()
	proc, ok := m.processes[resolved]
	m.mu.Unlock()

	if !ok {
		return fmt.Errorf("no tracked script at %q", resolved)
	}
	return m.stopAndClean(resolved, proc)
}

const (
	// maxLogReadBytes caps the initial full-file read of a script log so a
	// huge log file cannot exhaust memory; only the last 1MB is scanned.
	maxLogReadBytes int64 = 1 << 20 // 1MiB
	// maxLogLineLen is the maximum log line length returned to callers;
	// longer lines are skipped instead of failing the whole read.
	maxLogLineLen = 64 << 10 // 64KiB
)

// GetScriptLogs returns the last tailLines lines from the script's log file.
// The requested path must resolve inside scripts_dir (same whitelist as
// StartScript). At most the last 1MB of the file is read, and lines longer
// than 64KB are skipped rather than causing an error.
func (m *Manager) GetScriptLogs(path string, tailLines int) ([]string, error) {
	resolved, err := m.resolveWithinScriptsDir(path)
	if err != nil {
		return nil, err
	}
	logPath := m.logPath(resolved)
	f, err := os.Open(logPath)
	if err != nil {
		if os.IsNotExist(err) {
			return []string{}, nil
		}
		return nil, fmt.Errorf("open log file: %w", err)
	}
	defer f.Close()

	// Cap the read to the last 1MB of the file.
	offset := int64(0)
	if info, statErr := f.Stat(); statErr == nil && info.Size() > maxLogReadBytes {
		offset = info.Size() - maxLogReadBytes
		if _, seekErr := f.Seek(offset, io.SeekStart); seekErr != nil {
			return nil, fmt.Errorf("seek log file: %w", seekErr)
		}
	}

	var lines []string
	reader := bufio.NewReader(f)

	// When starting mid-file, discard the first (likely truncated) line.
	if offset > 0 {
		if _, err := reader.ReadString('\n'); err != nil && err != io.EOF {
			return nil, fmt.Errorf("read log file: %w", err)
		}
	}

	for {
		line, readErr := reader.ReadString('\n')
		if line != "" {
			line = strings.TrimRight(line, "\n")
			if len(line) <= maxLogLineLen {
				lines = append(lines, line)
			}
		}
		if readErr != nil {
			if readErr != io.EOF {
				return nil, fmt.Errorf("read log file: %w", readErr)
			}
			break
		}
	}

	if tailLines > 0 && len(lines) > tailLines {
		lines = lines[len(lines)-tailLines:]
	}
	return lines, nil
}

// GetStatuses returns the current status of all tracked scripts. Dead
// processes are pruned from the internal map.
func (m *Manager) GetStatuses() []ScriptStatus {
	m.mu.Lock()
	defer m.mu.Unlock()

	var out []ScriptStatus
	for path, proc := range m.processes {
		alive := proc.isOurs()
		status := "running"
		if !alive {
			status = "stopped"
			m.cleanupProcess(proc)
			delete(m.processes, path)
			continue
		}
		out = append(out, ScriptStatus{
			Path:      proc.Path,
			PID:       proc.PID,
			Status:    status,
			Port:      proc.Port,
			StartTime: proc.StartTime.UnixMilli(),
		})
	}
	return out
}

// Shutdown stops all running scripts. Called on agent exit.
func (m *Manager) Shutdown() {
	m.mu.Lock()
	procs := make(map[string]*scriptProcess, len(m.processes))
	for k, v := range m.processes {
		procs[k] = v
	}
	m.mu.Unlock()

	for path, proc := range procs {
		_ = m.stopAndClean(path, proc)
	}
}

// stopAndClean kills a process group, waits, and cleans up resources.
func (m *Manager) stopAndClean(path string, proc *scriptProcess) error {
	// Kill process group with SIGTERM. signalGroup refuses to signal when the
	// PID's recorded start time no longer matches (PID reuse): an unrelated
	// recycled process must never receive our signals.
	err := signalGroup(proc, syscall.SIGTERM)
	if err == nil {
		// Wait up to 5s, then SIGKILL.
		for i := 0; i < 50 && proc.isOurs(); i++ {
			time.Sleep(100 * time.Millisecond)
		}
		if proc.isOurs() {
			_ = signalGroup(proc, syscall.SIGKILL)
		}
	}

	m.mu.Lock()
	m.cleanupProcess(proc)
	delete(m.processes, path)
	m.mu.Unlock()

	return nil
}

func (m *Manager) cleanupProcess(proc *scriptProcess) {
	if proc.LogFile != nil {
		proc.LogFile.Close()
		proc.LogFile = nil
	}
	_ = os.Remove(m.pidPath(proc.Path))
}

// logPath returns the log file path for a given script path. The filename is
// derived from a SHA-256 digest of the resolved absolute path, so distinct
// paths can never collide (unlike separator-mangling).
func (m *Manager) logPath(scriptPath string) string {
	return filepath.Join(m.logDir, hashFilename(scriptPath)+".log")
}

// pidPath returns the PID file path for a given script path.
func (m *Manager) pidPath(scriptPath string) string {
	return filepath.Join(m.logDir, hashFilename(scriptPath)+".pid")
}

// hashFilename derives a collision-free filename component from a resolved
// absolute script path: hex-encoded SHA-256, truncated to 32 chars.
func hashFilename(path string) string {
	sum := sha256.Sum256([]byte(path))
	return hex.EncodeToString(sum[:])[:32]
}

// procStatStartTime reads the OS process start time from /proc/<pid>/stat
// field 22 (starttime, in clock ticks since boot). The comm field (field 2)
// may contain spaces and parentheses, so parsing starts after the last ')'.
func procStatStartTime(pid int) (uint64, error) {
	data, err := os.ReadFile(fmt.Sprintf("/proc/%d/stat", pid))
	if err != nil {
		return 0, err
	}
	idx := bytes.LastIndexByte(data, ')')
	if idx < 0 || idx+2 >= len(data) {
		return 0, fmt.Errorf("malformed /proc/%d/stat", pid)
	}
	fields := strings.Fields(string(data[idx+2:]))
	// fields[0] is state (field 3); starttime (field 22) is therefore index 19.
	if len(fields) < 20 {
		return 0, fmt.Errorf("short /proc/%d/stat", pid)
	}
	return strconv.ParseUint(fields[19], 10, 64)
}

// isOurs reports whether proc.PID still refers to the process we spawned:
// it must be alive AND its current /proc start time must match the value
// recorded at registration. Guards every signal path against PID reuse.
// If no start time was captured (ProcStart == 0), falls back to signal-0
// liveness only.
func (p *scriptProcess) isOurs() bool {
	if !isProcessAlive(p.PID) {
		return false
	}
	if p.ProcStart == 0 {
		return true
	}
	cur, err := procStatStartTime(p.PID)
	if err != nil {
		return false
	}
	return cur == p.ProcStart
}

// signalGroup sends sig to the process's group, but refuses if the PID has
// been recycled by an unrelated process (start-time mismatch).
func signalGroup(proc *scriptProcess, sig syscall.Signal) error {
	if !proc.isOurs() {
		slog.Warn("possible PID reuse: recorded process start time no longer matches; refusing to signal process group",
			"pid", proc.PID, "path", proc.Path)
		return fmt.Errorf("pid %d no longer matches registered process (possible PID reuse)", proc.PID)
	}
	return syscall.Kill(-proc.PID, sig)
}

func isProcessAlive(pid int) bool {
	err := syscall.Kill(pid, 0)
	return err == nil
}
