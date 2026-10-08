package scripts

import (
	"os"
	"path/filepath"
	"strings"
	"testing"
)

// ---------------------------------------------------------------------------
// validateScriptName
// ---------------------------------------------------------------------------

func TestValidateScriptName(t *testing.T) {
	tests := []struct {
		in      string
		want    string
		wantErr bool
	}{
		{"", "", true},
		{"noext", "", true},
		{"good.sh", "good.sh", false},
		{"dir/good.sh", "good.sh", false}, // basename strips path components
		{"../escape.sh", "escape.sh", false},
	}
	for _, tt := range tests {
		got, err := validateScriptName(tt.in)
		if (err != nil) != tt.wantErr {
			t.Errorf("validateScriptName(%q) error = %v, wantErr %v", tt.in, err, tt.wantErr)
			continue
		}
		if got != tt.want {
			t.Errorf("validateScriptName(%q) = %q, want %q", tt.in, got, tt.want)
		}
	}
}

// ---------------------------------------------------------------------------
// ReadScript
// ---------------------------------------------------------------------------

func TestReadScript(t *testing.T) {
	dir := t.TempDir()
	m := mustNewManager(t, dir)

	content := "#!/bin/bash\necho hello\n"
	if _, err := m.WriteScript("svc.sh", content); err != nil {
		t.Fatalf("WriteScript: %v", err)
	}

	got, err := m.ReadScript("svc.sh")
	if err != nil {
		t.Fatalf("ReadScript: %v", err)
	}
	if got != content {
		t.Errorf("ReadScript = %q, want %q", got, content)
	}
}

func TestReadScriptErrors(t *testing.T) {
	t.Run("disabled", func(t *testing.T) {
		m, err := NewManager("", "")
		if err != nil {
			t.Fatal(err)
		}
		if _, err := m.ReadScript("x.sh"); err == nil {
			t.Error("expected error when not configured")
		}
	})
	t.Run("invalid name", func(t *testing.T) {
		m := mustNewManager(t, t.TempDir())
		if _, err := m.ReadScript("not-a-script"); err == nil {
			t.Error("expected error for non-.sh name")
		}
	})
	t.Run("missing file", func(t *testing.T) {
		m := mustNewManager(t, t.TempDir())
		if _, err := m.ReadScript("missing.sh"); err == nil {
			t.Error("expected error for missing file")
		}
	})
}

// ---------------------------------------------------------------------------
// DeleteScript
// ---------------------------------------------------------------------------

func TestDeleteScript(t *testing.T) {
	dir := t.TempDir()
	m := mustNewManager(t, dir)

	info, err := m.WriteScript("del.sh", "#!/bin/bash\n")
	if err != nil {
		t.Fatalf("WriteScript: %v", err)
	}
	got, err := m.DeleteScript("del.sh")
	if err != nil {
		t.Fatalf("DeleteScript: %v", err)
	}
	if got.Name != "del.sh" {
		t.Errorf("deleted name = %q", got.Name)
	}
	if _, err := os.Stat(info.Path); !os.IsNotExist(err) {
		t.Errorf("script file should be gone, stat err = %v", err)
	}
}

func TestDeleteScriptErrors(t *testing.T) {
	t.Run("disabled", func(t *testing.T) {
		m, _ := NewManager("", "")
		if _, err := m.DeleteScript("x.sh"); err == nil {
			t.Error("expected error when not configured")
		}
	})
	t.Run("missing file", func(t *testing.T) {
		m := mustNewManager(t, t.TempDir())
		if _, err := m.DeleteScript("missing.sh"); err == nil {
			t.Error("expected error for missing file")
		}
	})
	t.Run("running script refused", func(t *testing.T) {
		dir := t.TempDir()
		script := filepath.Join(dir, "running.sh")
		if err := os.WriteFile(script, []byte("#!/bin/bash\nwhile true; do sleep 0.1; done\n"), 0o755); err != nil {
			t.Fatal(err)
		}
		m := mustNewManager(t, dir)
		defer m.Shutdown()
		if _, err := m.StartScript(script, 0, ""); err != nil {
			t.Fatalf("StartScript: %v", err)
		}
		if _, err := m.DeleteScript("running.sh"); err == nil {
			t.Error("expected DeleteScript to refuse a running script")
		} else if !strings.Contains(err.Error(), "running") {
			t.Errorf("error = %v, want 'running'", err)
		}
	})
}

// ---------------------------------------------------------------------------
// WriteScript error branches
// ---------------------------------------------------------------------------

func TestWriteScriptErrors(t *testing.T) {
	t.Run("disabled", func(t *testing.T) {
		m, _ := NewManager("", "")
		if _, err := m.WriteScript("x.sh", "#!/bin/bash\n"); err == nil {
			t.Error("expected error when not configured")
		}
	})
	t.Run("invalid name", func(t *testing.T) {
		m := mustNewManager(t, t.TempDir())
		if _, err := m.WriteScript("bad.txt", "#!/bin/bash\n"); err == nil {
			t.Error("expected error for non-.sh name")
		}
	})
	t.Run("empty content", func(t *testing.T) {
		m := mustNewManager(t, t.TempDir())
		if _, err := m.WriteScript("empty.sh", ""); err == nil {
			t.Error("expected error for empty content")
		}
	})
	t.Run("too large", func(t *testing.T) {
		m := mustNewManager(t, t.TempDir())
		big := strings.Repeat("a", int(maxScriptBytes)+1)
		if _, err := m.WriteScript("big.sh", big); err == nil {
			t.Error("expected error for oversized content")
		}
	})
	t.Run("symlink escape target refused", func(t *testing.T) {
		dir := t.TempDir()
		m := mustNewManager(t, dir)
		outside := filepath.Join(t.TempDir(), "outside.sh")
		if err := os.WriteFile(outside, []byte("x"), 0o600); err != nil {
			t.Fatal(err)
		}
		if err := os.Symlink(outside, filepath.Join(dir, "link.sh")); err != nil {
			t.Skipf("symlink unsupported: %v", err)
		}
		if _, err := m.WriteScript("link.sh", "#!/bin/bash\n"); err == nil {
			t.Error("expected write through escaping symlink to be refused")
		}
	})
}

// ---------------------------------------------------------------------------
// GetScriptLogs
// ---------------------------------------------------------------------------

func TestGetScriptLogsMissingLogReturnsEmpty(t *testing.T) {
	dir := t.TempDir()
	m := mustNewManager(t, dir)
	info, err := m.WriteScript("nolog.sh", "#!/bin/bash\n")
	if err != nil {
		t.Fatal(err)
	}
	logs, err := m.GetScriptLogs(info.Path, 10)
	if err != nil {
		t.Fatalf("GetScriptLogs: %v", err)
	}
	if len(logs) != 0 {
		t.Errorf("expected no logs, got %v", logs)
	}
}

func TestGetScriptLogsOutsideScriptsDir(t *testing.T) {
	m := mustNewManager(t, t.TempDir())
	if _, err := m.GetScriptLogs(filepath.Join(t.TempDir(), "outside.sh"), 10); err == nil {
		t.Error("expected error for a path outside scripts_dir")
	}
}

func TestGetScriptLogsSkipsLongLines(t *testing.T) {
	dir := t.TempDir()
	m := mustNewManager(t, dir)
	script := filepath.Join(dir, "long.sh")
	if err := os.WriteFile(script, []byte("#!/bin/bash\n"), 0o700); err != nil {
		t.Fatal(err)
	}
	resolved, err := filepath.EvalSymlinks(script)
	if err != nil {
		t.Fatal(err)
	}
	longLine := strings.Repeat("x", maxLogLineLen+10)
	logContent := longLine + "\n" + "short line\n"
	if err := os.WriteFile(m.logPath(resolved), []byte(logContent), 0o600); err != nil {
		t.Fatal(err)
	}

	logs, err := m.GetScriptLogs(script, 0)
	if err != nil {
		t.Fatalf("GetScriptLogs: %v", err)
	}
	for _, l := range logs {
		if len(l) > maxLogLineLen {
			t.Errorf("log line longer than max returned (len %d)", len(l))
		}
	}
	if len(logs) == 0 || logs[len(logs)-1] != "short line" {
		t.Errorf("expected trailing 'short line', got %v", logs)
	}
}

func TestGetScriptLogsTruncatesLargeFile(t *testing.T) {
	dir := t.TempDir()
	m := mustNewManager(t, dir)
	script := filepath.Join(dir, "biglog.sh")
	if err := os.WriteFile(script, []byte("#!/bin/bash\n"), 0o700); err != nil {
		t.Fatal(err)
	}
	resolved, err := filepath.EvalSymlinks(script)
	if err != nil {
		t.Fatal(err)
	}
	// Write > maxLogReadBytes so only the tail is scanned (covers the seek and
	// truncated-first-line discard branches).
	var sb strings.Builder
	sb.WriteString(strings.Repeat("a", int(maxLogReadBytes)+100))
	sb.WriteString("\nlast line\n")
	if err := os.WriteFile(m.logPath(resolved), []byte(sb.String()), 0o600); err != nil {
		t.Fatal(err)
	}

	logs, err := m.GetScriptLogs(script, 0)
	if err != nil {
		t.Fatalf("GetScriptLogs: %v", err)
	}
	if len(logs) == 0 || logs[len(logs)-1] != "last line" {
		t.Errorf("expected trailing 'last line', got %v", logs)
	}
}

// ---------------------------------------------------------------------------
// StopScriptByPath / resolve helpers
// ---------------------------------------------------------------------------

func TestStopScriptByPathNotFound(t *testing.T) {
	m := mustNewManager(t, t.TempDir())
	if err := m.StopScriptByPath("/no/such/script.sh"); err == nil {
		t.Error("expected error for untracked path")
	}
}

func TestPathWithin(t *testing.T) {
	sep := string(filepath.Separator)
	tests := []struct {
		path, dir string
		want      bool
	}{
		{"/a/b", "/a", true},
		{"/a", "/a", true},
		{"/ab", "/a", false},
		{"/a" + sep + "b", "/a", true},
	}
	for _, tt := range tests {
		if got := pathWithin(tt.path, tt.dir); got != tt.want {
			t.Errorf("pathWithin(%q, %q) = %v, want %v", tt.path, tt.dir, got, tt.want)
		}
	}
}

// ---------------------------------------------------------------------------
// Process liveness / PID-reuse guard helpers
// ---------------------------------------------------------------------------

func TestProcStatStartTime(t *testing.T) {
	if v, err := procStatStartTime(os.Getpid()); err != nil {
		t.Errorf("procStatStartTime(self): %v", err)
	} else if v == 0 {
		t.Error("procStatStartTime(self) = 0, want > 0")
	}
	if _, err := procStatStartTime(-1); err == nil {
		t.Error("expected error for invalid pid")
	}
}

func TestIsOurs(t *testing.T) {
	// A live PID with no recorded start time is treated as ours.
	if !(&scriptProcess{PID: os.Getpid()}).isOurs() {
		t.Error("live pid with ProcStart 0 should be ours")
	}
	// A dead PID is never ours.
	if (&scriptProcess{PID: 1 << 30}).isOurs() {
		t.Error("dead pid should not be ours")
	}
	// A mismatched recorded start time signals PID reuse.
	cur, err := procStatStartTime(os.Getpid())
	if err != nil {
		t.Fatal(err)
	}
	if (&scriptProcess{PID: os.Getpid(), ProcStart: cur + 1}).isOurs() {
		t.Error("mismatched ProcStart should not be ours")
	}
}

func TestSignalGroupRefusesPIDReuse(t *testing.T) {
	proc := &scriptProcess{PID: 1 << 30, ProcStart: 12345}
	if err := signalGroup(proc, 15); err == nil {
		t.Error("signalGroup should refuse a process that is not ours")
	}
}

func TestHashFilenameAndPaths(t *testing.T) {
	m := mustNewManager(t, t.TempDir())
	if hashFilename("/a/b.sh") == hashFilename("/a/c.sh") {
		t.Error("hashFilename should differ for distinct paths")
	}
	if !strings.HasSuffix(m.logPath("/a/b.sh"), ".log") {
		t.Error("logPath should end in .log")
	}
	if !strings.HasSuffix(m.pidPath("/a/b.sh"), ".pid") {
		t.Error("pidPath should end in .pid")
	}
}
