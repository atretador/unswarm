// Package scripts manages launcher script processes on the agent host.
// Scripts are bash scripts in the configured scripts_dir that serve
// OpenAI-compatible APIs.
package scripts

import (
	"bufio"
	"fmt"
	"os"
	"os/exec"
	"path/filepath"
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
	Path      string
	PID       int
	Port      int
	StartTime time.Time
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

// StartScript spawns a bash script in a new process group. The script must
// reside inside the configured scriptsDir (whitelist check). Returns the PID
// of the spawned process.
func (m *Manager) StartScript(path string, port int) (int, error) {
	resolved, err := filepath.Abs(filepath.Clean(path))
	if err != nil {
		return 0, fmt.Errorf("resolve path: %w", err)
	}
	// Resolve symlinks to prevent symlink escapes (security boundary).
	resolved, err = filepath.EvalSymlinks(resolved)
	if err != nil {
		return 0, fmt.Errorf("eval symlinks for %q: %w", path, err)
	}

	// Whitelist: resolved path must be within scriptsDir.
	scriptsDir, err := filepath.Abs(filepath.Clean(m.scriptsDir))
	if err != nil {
		return 0, fmt.Errorf("resolve scripts_dir: %w", err)
	}
	scriptsDir, err = filepath.EvalSymlinks(scriptsDir)
	if err != nil {
		return 0, fmt.Errorf("eval symlinks for scripts_dir: %w", err)
	}
	if !strings.HasPrefix(resolved, scriptsDir+string(filepath.Separator)) && resolved != scriptsDir {
		return 0, fmt.Errorf("path %q is outside scripts_dir %q", resolved, scriptsDir)
	}

	// File must exist and be regular.
	info, err := os.Stat(resolved)
	if err != nil {
		return 0, fmt.Errorf("stat %q: %w", resolved, err)
	}
	if info.IsDir() {
		return 0, fmt.Errorf("path %q is a directory", resolved)
	}

	m.mu.Lock()
	defer m.mu.Unlock()

	// Duplicate guard.
	if proc, ok := m.processes[resolved]; ok {
		if isProcessAlive(proc.PID) {
			return 0, fmt.Errorf("script %q is already running (pid %d)", resolved, proc.PID)
		}
		// Stale entry — clean up.
		m.cleanupProcess(proc)
		delete(m.processes, resolved)
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

	m.processes[resolved] = &scriptProcess{
		Path:      resolved,
		PID:       pid,
		Port:      port,
		StartTime: startTime,
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

// GetScriptLogs returns the last tailLines lines from the script's log file.
func (m *Manager) GetScriptLogs(path string, tailLines int) ([]string, error) {
	resolved, err := filepath.Abs(filepath.Clean(path))
	if err != nil {
		return nil, fmt.Errorf("resolve path: %w", err)
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

	var lines []string
	scanner := bufio.NewScanner(f)
	for scanner.Scan() {
		lines = append(lines, scanner.Text())
	}
	if err := scanner.Err(); err != nil {
		return nil, fmt.Errorf("read log file: %w", err)
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
		alive := isProcessAlive(proc.PID)
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
	// Kill process group with SIGTERM.
	err := syscall.Kill(-proc.PID, syscall.SIGTERM)
	if err != nil && !isAlreadyDead(err) {
		// Process might already be gone.
	}

	// Wait up to 5s, then SIGKILL.
	done := make(chan struct{})
	go func() {
		for i := 0; i < 50; i++ {
			if !isProcessAlive(proc.PID) {
				break
			}
			time.Sleep(100 * time.Millisecond)
		}
		close(done)
	}()
	<-done

	if isProcessAlive(proc.PID) {
		_ = syscall.Kill(-proc.PID, syscall.SIGKILL)
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

// logPath returns the log file path for a given script path.
func (m *Manager) logPath(scriptPath string) string {
	return filepath.Join(m.logDir, sanitizeFilename(scriptPath)+".log")
}

// pidPath returns the PID file path for a given script path.
func (m *Manager) pidPath(scriptPath string) string {
	return filepath.Join(m.logDir, sanitizeFilename(scriptPath)+".pid")
}

func sanitizeFilename(path string) string {
	return strings.ReplaceAll(path, string(filepath.Separator), "_")
}

func isProcessAlive(pid int) bool {
	err := syscall.Kill(pid, 0)
	return err == nil
}

func isAlreadyDead(err error) bool {
	return err == syscall.ESRCH
}
