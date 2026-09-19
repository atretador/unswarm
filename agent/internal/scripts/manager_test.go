package scripts

import (
	"os"
	"path/filepath"
	"strings"
	"syscall"
	"testing"
	"time"
)

func TestListScripts(t *testing.T) {
	dir := t.TempDir()
	// Create some .sh files and non-.sh files.
	if err := os.WriteFile(filepath.Join(dir, "model-a.sh"), []byte("#!/bin/bash\necho hi"), 0o755); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(filepath.Join(dir, "model-b.sh"), []byte("#!/bin/bash\necho hi"), 0o755); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(filepath.Join(dir, "readme.txt"), []byte("not a script"), 0o644); err != nil {
		t.Fatal(err)
	}
	if err := os.Mkdir(filepath.Join(dir, "subdir"), 0o755); err != nil {
		t.Fatal(err)
	}

	m := mustNewManager(t, dir)
	scripts := m.ListScripts()

	if len(scripts) != 2 {
		t.Fatalf("expected 2 scripts, got %d", len(scripts))
	}

	names := map[string]bool{}
	for _, s := range scripts {
		names[s.Name] = true
		if s.Path == "" {
			t.Error("script path should not be empty")
		}
	}
	if !names["model-a.sh"] || !names["model-b.sh"] {
		t.Errorf("unexpected script names: %v", names)
	}
}

func TestListScripts_EmptyDir(t *testing.T) {
	m := mustNewManager(t, "")
	if scripts := m.ListScripts(); scripts != nil {
		t.Errorf("expected nil for empty scriptsDir, got %v", scripts)
	}
}

func TestListScripts_NonexistentDir(t *testing.T) {
	// Explicit valid log dir: the scripts dir itself is not required to exist.
	m, err := NewManager("/nonexistent/path/that/does/not/exist", t.TempDir())
	if err != nil {
		t.Fatalf("NewManager: %v", err)
	}
	if scripts := m.ListScripts(); scripts != nil {
		t.Errorf("expected nil for nonexistent dir, got %v", scripts)
	}
}

func TestStartScript(t *testing.T) {
	dir := t.TempDir()
	script := filepath.Join(dir, "loop.sh")
	if err := os.WriteFile(script, []byte("#!/bin/bash\nwhile true; do sleep 0.1; done\n"), 0o755); err != nil {
		t.Fatal(err)
	}

	m := mustNewManager(t, dir)
	defer m.Shutdown()

	pid, err := m.StartScript(script, 9000, "")
	if err != nil {
		t.Fatalf("StartScript: %v", err)
	}
	if pid <= 0 {
		t.Fatalf("expected positive pid, got %d", pid)
	}

	// Should be alive.
	if !isProcessAlive(pid) {
		t.Error("process should be alive after StartScript")
	}

	// Cleanup
	_ = syscallKill(pid)
}

func TestStartScript_WhitelistReject(t *testing.T) {
	dir := t.TempDir()
	outside := filepath.Join(t.TempDir(), "evil.sh")
	if err := os.WriteFile(outside, []byte("#!/bin/bash\necho pwned"), 0o755); err != nil {
		t.Fatal(err)
	}

	m := mustNewManager(t, dir)

	_, err := m.StartScript(outside, 9000, "")
	if err == nil {
		t.Fatal("expected whitelist rejection for path outside scripts_dir")
	}
}

func TestStartScript_DuplicateGuard(t *testing.T) {
	dir := t.TempDir()
	script := filepath.Join(dir, "dup.sh")
	if err := os.WriteFile(script, []byte("#!/bin/bash\nwhile true; do sleep 0.1; done\n"), 0o755); err != nil {
		t.Fatal(err)
	}

	m := mustNewManager(t, dir)
	defer m.Shutdown()

	pid1, err := m.StartScript(script, 9000, "")
	if err != nil {
		t.Fatalf("first StartScript: %v", err)
	}

	// Second start should return the same PID without error (idempotent).
	pid2, err := m.StartScript(script, 9001, "")
	if err != nil {
		t.Fatalf("second StartScript (idempotent): %v", err)
	}
	if pid2 != pid1 {
		t.Errorf("expected same pid %d, got %d", pid1, pid2)
	}

	_ = syscallKill(pid1)
}

func TestStopScript(t *testing.T) {
	dir := t.TempDir()
	script := filepath.Join(dir, "stopme.sh")
	if err := os.WriteFile(script, []byte("#!/bin/bash\nwhile true; do sleep 0.1; done\n"), 0o755); err != nil {
		t.Fatal(err)
	}

	m := mustNewManager(t, dir)

	pid, err := m.StartScript(script, 9000, "")
	if err != nil {
		t.Fatalf("StartScript: %v", err)
	}

	if err := m.StopScript(pid); err != nil {
		t.Fatalf("StopScript: %v", err)
	}

	// Process should be dead.
	time.Sleep(200 * time.Millisecond)
	if isProcessAlive(pid) {
		t.Error("process should be dead after StopScript")
	}
}

func TestStopScriptByPath(t *testing.T) {
	dir := t.TempDir()
	script := filepath.Join(dir, "stopbypath.sh")
	if err := os.WriteFile(script, []byte("#!/bin/bash\nwhile true; do sleep 0.1; done\n"), 0o755); err != nil {
		t.Fatal(err)
	}

	m := mustNewManager(t, dir)

	_, err := m.StartScript(script, 9000, "")
	if err != nil {
		t.Fatalf("StartScript: %v", err)
	}

	if err := m.StopScriptByPath(script); err != nil {
		t.Fatalf("StopScriptByPath: %v", err)
	}

	time.Sleep(200 * time.Millisecond)
}

func TestGetScriptLogs(t *testing.T) {
	dir := t.TempDir()
	script := filepath.Join(dir, "logger.sh")
	if err := os.WriteFile(script, []byte("#!/bin/bash\necho line1\necho line2\necho line3\n"), 0o755); err != nil {
		t.Fatal(err)
	}

	m := mustNewManager(t, dir)
	defer m.Shutdown()

	_, err := m.StartScript(script, 0, "")
	if err != nil {
		t.Fatalf("StartScript: %v", err)
	}

	// Wait for the script to produce output.
	time.Sleep(500 * time.Millisecond)

	logs, err := m.GetScriptLogs(script, 10)
	if err != nil {
		t.Fatalf("GetScriptLogs: %v", err)
	}
	if len(logs) == 0 {
		t.Error("expected some log lines")
	}
}

func TestGetScriptLogs_TailLines(t *testing.T) {
	dir := t.TempDir()
	script := filepath.Join(dir, "many.sh")
	// Script that outputs 10 lines.
	if err := os.WriteFile(script, []byte("#!/bin/bash\nfor i in $(seq 1 10); do echo line$i; done\n"), 0o755); err != nil {
		t.Fatal(err)
	}

	m := mustNewManager(t, dir)
	defer m.Shutdown()

	_, err := m.StartScript(script, 0, "")
	if err != nil {
		t.Fatalf("StartScript: %v", err)
	}
	time.Sleep(500 * time.Millisecond)

	logs, err := m.GetScriptLogs(script, 3)
	if err != nil {
		t.Fatalf("GetScriptLogs: %v", err)
	}
	if len(logs) > 3 {
		t.Errorf("expected at most 3 lines, got %d", len(logs))
	}
}

func TestGetStatuses(t *testing.T) {
	dir := t.TempDir()
	script := filepath.Join(dir, "status.sh")
	if err := os.WriteFile(script, []byte("#!/bin/bash\nwhile true; do sleep 0.1; done\n"), 0o755); err != nil {
		t.Fatal(err)
	}

	m := mustNewManager(t, dir)
	defer m.Shutdown()

	_, err := m.StartScript(script, 9000, "")
	if err != nil {
		t.Fatalf("StartScript: %v", err)
	}

	statuses := m.GetStatuses()
	if len(statuses) != 1 {
		t.Fatalf("expected 1 status, got %d", len(statuses))
	}
	if statuses[0].Status != "running" {
		t.Errorf("expected status 'running', got %q", statuses[0].Status)
	}
	if statuses[0].Port != 9000 {
		t.Errorf("expected port 9000, got %d", statuses[0].Port)
	}
	if statuses[0].StartTime == 0 {
		t.Error("expected non-zero StartTime")
	}
}

func TestShutdown(t *testing.T) {
	dir := t.TempDir()
	script := filepath.Join(dir, "shutdown.sh")
	if err := os.WriteFile(script, []byte("#!/bin/bash\nwhile true; do sleep 0.1; done\n"), 0o755); err != nil {
		t.Fatal(err)
	}

	m := mustNewManager(t, dir)

	pid, err := m.StartScript(script, 9000, "")
	if err != nil {
		t.Fatalf("StartScript: %v", err)
	}

	m.Shutdown()

	time.Sleep(200 * time.Millisecond)
	if isProcessAlive(pid) {
		t.Error("process should be dead after Shutdown")
	}
}

func TestIsEnabled(t *testing.T) {
	if m, err := NewManager("", ""); err != nil {
		t.Fatalf("NewManager(disabled): %v", err)
	} else if m.IsEnabled() {
		t.Error("empty scriptsDir should not be enabled")
	}
	m, err := NewManager("/some/path", t.TempDir())
	if err != nil {
		t.Fatalf("NewManager: %v", err)
	}
	if !m.IsEnabled() {
		t.Error("non-empty scriptsDir should be enabled")
	}
}

// mustNewManager builds a Manager with the default derived log directory.
func mustNewManager(t *testing.T, dir string) *Manager {
	t.Helper()
	m, err := NewManager(dir, "")
	if err != nil {
		t.Fatalf("NewManager(%q): %v", dir, err)
	}
	return m
}

// TestNewManager_LogDirError verifies the fail-loud error names the path, the
// script_log_dir key, and the ReadWritePaths fix (H8 / decision 8).
func TestNewManager_LogDirError(t *testing.T) {
	// A regular file used as the log dir cannot be MkdirAll'd.
	file := filepath.Join(t.TempDir(), "not-a-dir")
	if err := os.WriteFile(file, []byte("x"), 0o600); err != nil {
		t.Fatal(err)
	}
	_, err := NewManager(t.TempDir(), file)
	if err == nil {
		t.Fatal("expected an error when the log dir cannot be created")
	}
	msg := err.Error()
	for _, want := range []string{file, "script_log_dir", "ReadWritePaths"} {
		if !strings.Contains(msg, want) {
			t.Errorf("error %q does not mention %q", msg, want)
		}
	}
}

// TestWriteScript_Mode0700 verifies scripts are owner-only on create and after
// update (H8).
func TestWriteScript_Mode0700(t *testing.T) {
	dir := t.TempDir()
	m := mustNewManager(t, dir)

	info, err := m.WriteScript("perm.sh", "#!/bin/bash\necho hi\n")
	if err != nil {
		t.Fatalf("WriteScript: %v", err)
	}
	fi, err := os.Stat(info.Path)
	if err != nil {
		t.Fatalf("Stat: %v", err)
	}
	if got := fi.Mode().Perm(); got != 0o700 {
		t.Errorf("WriteScript mode = %o, want 0700", got)
	}

	if _, err := m.UpdateScript("perm.sh", "#!/bin/bash\necho bye\n"); err != nil {
		t.Fatalf("UpdateScript: %v", err)
	}
	fi, err = os.Stat(info.Path)
	if err != nil {
		t.Fatalf("Stat after update: %v", err)
	}
	if got := fi.Mode().Perm(); got != 0o700 {
		t.Errorf("UpdateScript mode = %o, want 0700", got)
	}
}

func TestStartScript_StaleEntryCleanup(t *testing.T) {
	dir := t.TempDir()
	script := filepath.Join(dir, "stale.sh")
	if err := os.WriteFile(script, []byte("#!/bin/bash\nwhile true; do sleep 0.1; done\n"), 0o755); err != nil {
		t.Fatal(err)
	}

	m := mustNewManager(t, dir)

	pid1, err := m.StartScript(script, 9000, "")
	if err != nil {
		t.Fatalf("first StartScript: %v", err)
	}

	// Kill the process externally to simulate a stale entry.
	_ = syscallKill(pid1)
	time.Sleep(200 * time.Millisecond)

	// Verify the old process is dead.
	if isProcessAlive(pid1) {
		t.Fatal("process should be dead after external kill")
	}

	// Start should detect the stale entry, clean it up, and spawn a new process.
	pid2, err := m.StartScript(script, 9001, "")
	if err != nil {
		t.Fatalf("second StartScript after stale cleanup: %v", err)
	}
	if pid2 == pid1 {
		t.Errorf("expected new pid, got same pid %d", pid1)
	}
	if pid2 <= 0 {
		t.Errorf("expected positive pid, got %d", pid2)
	}

	// Verify the new process is alive.
	if !isProcessAlive(pid2) {
		t.Error("new process should be alive")
	}

	_ = syscallKill(pid2)
}

func syscallKill(pid int) error {
	return syscall.Kill(pid, syscall.SIGKILL)
}
