package scripts

import (
	"os"
	"path/filepath"
	"syscall"
	"testing"
	"time"
)

func TestListScripts(t *testing.T) {
	dir := t.TempDir()
	// Create some .sh files and non-.sh files.
	os.WriteFile(filepath.Join(dir, "model-a.sh"), []byte("#!/bin/bash\necho hi"), 0o755)
	os.WriteFile(filepath.Join(dir, "model-b.sh"), []byte("#!/bin/bash\necho hi"), 0o755)
	os.WriteFile(filepath.Join(dir, "readme.txt"), []byte("not a script"), 0o644)
	os.Mkdir(filepath.Join(dir, "subdir"), 0o755)

	m := NewManager(dir)
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
	m := NewManager("")
	if scripts := m.ListScripts(); scripts != nil {
		t.Errorf("expected nil for empty scriptsDir, got %v", scripts)
	}
}

func TestListScripts_NonexistentDir(t *testing.T) {
	m := NewManager("/nonexistent/path/that/does/not/exist")
	if scripts := m.ListScripts(); scripts != nil {
		t.Errorf("expected nil for nonexistent dir, got %v", scripts)
	}
}

func TestStartScript(t *testing.T) {
	dir := t.TempDir()
	script := filepath.Join(dir, "loop.sh")
	os.WriteFile(script, []byte("#!/bin/bash\nwhile true; do sleep 0.1; done\n"), 0o755)

	m := NewManager(dir)
	defer m.Shutdown()

	pid, err := m.StartScript(script, 9000)
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
	os.WriteFile(outside, []byte("#!/bin/bash\necho pwned"), 0o755)

	m := NewManager(dir)

	_, err := m.StartScript(outside, 9000)
	if err == nil {
		t.Fatal("expected whitelist rejection for path outside scripts_dir")
	}
}

func TestStartScript_DuplicateGuard(t *testing.T) {
	dir := t.TempDir()
	script := filepath.Join(dir, "dup.sh")
	os.WriteFile(script, []byte("#!/bin/bash\nwhile true; do sleep 0.1; done\n"), 0o755)

	m := NewManager(dir)
	defer m.Shutdown()

	pid1, err := m.StartScript(script, 9000)
	if err != nil {
		t.Fatalf("first StartScript: %v", err)
	}

	// Second start should fail.
	_, err = m.StartScript(script, 9001)
	if err == nil {
		t.Fatal("expected duplicate guard error")
	}

	_ = syscallKill(pid1)
}

func TestStopScript(t *testing.T) {
	dir := t.TempDir()
	script := filepath.Join(dir, "stopme.sh")
	os.WriteFile(script, []byte("#!/bin/bash\nwhile true; do sleep 0.1; done\n"), 0o755)

	m := NewManager(dir)

	pid, err := m.StartScript(script, 9000)
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
	os.WriteFile(script, []byte("#!/bin/bash\nwhile true; do sleep 0.1; done\n"), 0o755)

	m := NewManager(dir)

	_, err := m.StartScript(script, 9000)
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
	os.WriteFile(script, []byte("#!/bin/bash\necho line1\necho line2\necho line3\n"), 0o755)

	m := NewManager(dir)
	defer m.Shutdown()

	_, err := m.StartScript(script, 0)
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
	os.WriteFile(script, []byte("#!/bin/bash\nfor i in $(seq 1 10); do echo line$i; done\n"), 0o755)

	m := NewManager(dir)
	defer m.Shutdown()

	_, err := m.StartScript(script, 0)
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
	os.WriteFile(script, []byte("#!/bin/bash\nwhile true; do sleep 0.1; done\n"), 0o755)

	m := NewManager(dir)
	defer m.Shutdown()

	_, err := m.StartScript(script, 9000)
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
	os.WriteFile(script, []byte("#!/bin/bash\nwhile true; do sleep 0.1; done\n"), 0o755)

	m := NewManager(dir)

	pid, err := m.StartScript(script, 9000)
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
	if NewManager("").IsEnabled() {
		t.Error("empty scriptsDir should not be enabled")
	}
	if !NewManager("/some/path").IsEnabled() {
		t.Error("non-empty scriptsDir should be enabled")
	}
}

func syscallKill(pid int) error {
	return syscall.Kill(pid, syscall.SIGKILL)
}
