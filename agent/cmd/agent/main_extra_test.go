package main

import (
	"bytes"
	"context"
	"encoding/json"
	"log/slog"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"

	"unswarm/agent/internal/client"
	"unswarm/agent/internal/config"
	"unswarm/agent/internal/dispatch"
	"unswarm/agent/internal/docker"
	"unswarm/agent/internal/protocol"
	"unswarm/agent/internal/runtimegate"
	"unswarm/agent/internal/scripts"
)

func bufferLogger(level slog.Level) (*slog.Logger, *bytes.Buffer) {
	buf := &bytes.Buffer{}
	return slog.New(slog.NewJSONHandler(buf, &slog.HandlerOptions{Level: level})), buf
}

func commandEnvelope(t *testing.T, cmd protocol.CommandPayload) protocol.Envelope {
	t.Helper()
	raw, err := json.Marshal(cmd)
	if err != nil {
		t.Fatalf("marshal command payload: %v", err)
	}
	id := "cmd-1"
	return protocol.Envelope{Type: protocol.TypeCommand, ID: &id, Payload: raw}
}

// ---------------------------------------------------------------------------
// Small pure helpers
// ---------------------------------------------------------------------------

func TestCommandContextHasDeadline(t *testing.T) {
	ctx, cancel := commandContext()
	defer cancel()
	deadline, ok := ctx.Deadline()
	if !ok {
		t.Fatal("commandContext has no deadline")
	}
	if remaining := time.Until(deadline); remaining <= 0 || remaining > commandTimeout {
		t.Errorf("deadline remaining = %v, want within (0, %v]", remaining, commandTimeout)
	}
}

func TestDefaultSessionConfig(t *testing.T) {
	cfg := config.DefaultConfig()
	sc := defaultSessionConfig(cfg)
	if sc.telemetryInterval != time.Duration(cfg.TelemetryIntervalMs)*time.Millisecond {
		t.Errorf("telemetryInterval = %v, want %v", sc.telemetryInterval, time.Duration(cfg.TelemetryIntervalMs)*time.Millisecond)
	}
	if sc.heartbeatInterval != 15*time.Second {
		t.Errorf("heartbeatInterval = %v, want 15s", sc.heartbeatInterval)
	}
}

func TestStrPtrAndDerefStr(t *testing.T) {
	p := strPtr("hello")
	if *p != "hello" {
		t.Errorf("strPtr = %q, want hello", *p)
	}
	if got := derefStr(p); got != "hello" {
		t.Errorf("derefStr(ptr) = %q, want hello", got)
	}
	if got := derefStr(nil); got != "" {
		t.Errorf("derefStr(nil) = %q, want empty", got)
	}
}

func TestNotConnectedResult(t *testing.T) {
	res := notConnectedResult("start_container")
	if res.OK {
		t.Fatal("notConnectedResult should be a failure")
	}
	if res.Error == nil || !strings.Contains(*res.Error, "start_container") {
		t.Errorf("error should name the command, got %v", res.Error)
	}
}

// ---------------------------------------------------------------------------
// sendCommandResult
// ---------------------------------------------------------------------------

func TestSendCommandResultUnconnected(t *testing.T) {
	logger := discardLogger()
	ws := client.New(config.Config{}, logger)
	id := "abc"
	// Success and failure (including the nil-error "<nil>" path) must not panic.
	sendCommandResult(context.Background(), ws, config.DefaultConfig(), &id, protocol.CommandResultPayload{OK: true}, logger)
	sendCommandResult(context.Background(), ws, config.DefaultConfig(), &id, protocol.CommandResultPayload{OK: false}, logger)
	msg := "boom"
	sendCommandResult(context.Background(), ws, config.DefaultConfig(), nil, protocol.CommandResultPayload{OK: false, Error: &msg}, logger)
}

// ---------------------------------------------------------------------------
// handleCommand / handleStreamCommand
// ---------------------------------------------------------------------------

func TestHandleCommandNilPayload(t *testing.T) {
	logger, buf := bufferLogger(slog.LevelDebug)
	ws := client.New(config.Config{}, logger)
	// nil payload: logs a warning and sends an error result.
	handleCommand(context.Background(), protocol.Envelope{Type: protocol.TypeCommand}, dispatch.New(), ws, config.DefaultConfig(), logger, nil)
	if !strings.Contains(buf.String(), "command with nil payload") {
		t.Errorf("expected nil-payload warning, got: %s", buf.String())
	}
}

func TestHandleCommandDecodeError(t *testing.T) {
	logger, buf := bufferLogger(slog.LevelDebug)
	ws := client.New(config.Config{}, logger)
	env := protocol.Envelope{Type: protocol.TypeCommand, Payload: json.RawMessage("this is not json")}
	handleCommand(context.Background(), env, dispatch.New(), ws, config.DefaultConfig(), logger, nil)
	if !strings.Contains(buf.String(), "decode command payload") {
		t.Errorf("expected decode error log, got: %s", buf.String())
	}
}

func TestHandleCommandUnknownCommand(t *testing.T) {
	logger := discardLogger()
	ws := client.New(config.Config{}, logger)
	env := commandEnvelope(t, protocol.CommandPayload{Command: "does_not_exist"})
	// No panic; unknown command produces a failed result internally.
	handleCommand(context.Background(), env, dispatch.New(), ws, config.DefaultConfig(), logger, nil)
}

func TestHandleCommandTriggersLifecycleCallback(t *testing.T) {
	logger := discardLogger()
	ws := client.New(config.Config{}, logger)
	disp := dispatch.New()
	disp.Register(protocol.CmdStartContainer, func(protocol.CommandPayload) protocol.CommandResultPayload {
		return protocol.CommandResultPayload{OK: true}
	})

	called := false
	env := commandEnvelope(t, protocol.CommandPayload{Command: protocol.CmdStartContainer, Image: "svc"})
	handleCommand(context.Background(), env, disp, ws, config.DefaultConfig(), logger, func(context.Context) {
		called = true
	})
	if !called {
		t.Error("lifecycle callback was not invoked for start_container")
	}

	// A non-lifecycle command must not trigger the callback.
	called = false
	disp.Register(protocol.CmdListContainers, func(protocol.CommandPayload) protocol.CommandResultPayload {
		return protocol.CommandResultPayload{OK: true}
	})
	env = commandEnvelope(t, protocol.CommandPayload{Command: protocol.CmdListContainers})
	handleCommand(context.Background(), env, disp, ws, config.DefaultConfig(), logger, func(context.Context) {
		called = true
	})
	if called {
		t.Error("lifecycle callback should not fire for list_containers")
	}
}

func TestHandleStreamCommand(t *testing.T) {
	logger := discardLogger()
	ws := client.New(config.Config{}, logger)
	cfg := config.DefaultConfig()
	ctx := context.Background()

	t.Run("unknown stream command", func(t *testing.T) {
		env := commandEnvelope(t, protocol.CommandPayload{Command: "nope"})
		handleStreamCommand(ctx, env, protocol.CommandPayload{Command: "nope"}, dispatch.New(), ws, cfg, logger)
	})

	t.Run("successful handler with no chunks", func(t *testing.T) {
		disp := dispatch.New()
		disp.RegisterStream("ok_stream", func(context.Context, protocol.CommandPayload, func([]byte) error) error {
			return nil
		})
		env := commandEnvelope(t, protocol.CommandPayload{Command: "ok_stream"})
		handleStreamCommand(ctx, env, protocol.CommandPayload{Command: "ok_stream"}, disp, ws, cfg, logger)
	})

	t.Run("emit failure propagates", func(t *testing.T) {
		disp := dispatch.New()
		disp.RegisterStream("emit_stream", func(_ context.Context, _ protocol.CommandPayload, emit func([]byte) error) error {
			return emit([]byte("chunk"))
		})
		env := commandEnvelope(t, protocol.CommandPayload{Command: "emit_stream"})
		// ws is unconnected, so emit's Send fails and the error path is taken.
		handleStreamCommand(ctx, env, protocol.CommandPayload{Command: "emit_stream"}, disp, ws, cfg, logger)
	})
}

// ---------------------------------------------------------------------------
// setupMessageRouter
// ---------------------------------------------------------------------------

func TestSetupMessageRouterSyncRegistrations(t *testing.T) {
	logger := discardLogger()
	gate := runtimegate.NewGate(runtimegate.NewRegistry(), true)
	router := setupMessageRouter(gate, logger)

	payload := protocol.SyncRegistrationsPayload{Registrations: []protocol.RegistrationEntry{
		{RegisteredRuntimeID: "r1", ContainerName: "svc", ContainerID: "abcdef1234567890"},
	}}
	raw, err := json.Marshal(payload)
	if err != nil {
		t.Fatal(err)
	}
	env := protocol.Envelope{Type: protocol.TypeSyncRegistrations, Payload: raw}
	if _, handled := router.Route(env); !handled {
		t.Fatal("sync_registrations should be handled by the router")
	}
	if blocked, ok := gate.Check(protocol.CmdStartContainer, "svc"); ok {
		t.Fatalf("svc should be registered after sync, got blocked: %v", blocked.Error)
	}
}

func TestSetupMessageRouterMalformedAndNilPayload(t *testing.T) {
	logger, buf := bufferLogger(slog.LevelDebug)
	gate := runtimegate.NewGate(runtimegate.NewRegistry(), true)
	router := setupMessageRouter(gate, logger)

	// Malformed payload: logged and no panic.
	env := protocol.Envelope{Type: protocol.TypeSyncRegistrations, Payload: json.RawMessage("not json")}
	if _, handled := router.Route(env); !handled {
		t.Fatal("sync_registrations should still be handled on malformed payload")
	}
	if !strings.Contains(buf.String(), "decode sync_registrations") {
		t.Errorf("expected decode error log, got: %s", buf.String())
	}

	// nil payload: replaces with an empty snapshot.
	env = protocol.Envelope{Type: protocol.TypeSyncRegistrations}
	if _, handled := router.Route(env); !handled {
		t.Fatal("sync_registrations with nil payload should be handled")
	}
	if gate.Registry().Size() != 0 {
		t.Errorf("registry size = %d, want 0 after empty sync", gate.Registry().Size())
	}
}

// ---------------------------------------------------------------------------
// loadConfig
// ---------------------------------------------------------------------------

func TestLoadConfigExplicitPath(t *testing.T) {
	t.Setenv("UNSWARM_AGENT_BACKEND_URL", "")
	t.Setenv("UNSWARM_AGENT_API_KEY", "")
	dir := t.TempDir()
	path := filepath.Join(dir, "agent.yaml")
	content := "backend_url: ws://127.0.0.1:5999\nagent_name: extra-agent\n"
	if err := os.WriteFile(path, []byte(content), 0o600); err != nil {
		t.Fatal(err)
	}
	cfg, gotPath, err := loadConfig(path)
	if err != nil {
		t.Fatalf("loadConfig: %v", err)
	}
	if gotPath != path {
		t.Errorf("path = %q, want %q", gotPath, path)
	}
	if cfg.BackendURL != "ws://127.0.0.1:5999" {
		t.Errorf("BackendURL = %q", cfg.BackendURL)
	}
	if cfg.AgentName != "extra-agent" {
		t.Errorf("AgentName = %q", cfg.AgentName)
	}
}

func TestLoadConfigExplicitParseError(t *testing.T) {
	dir := t.TempDir()
	path := filepath.Join(dir, "bad.yaml")
	if err := os.WriteFile(path, []byte("backend_url: [unterminated\n"), 0o600); err != nil {
		t.Fatal(err)
	}
	if _, _, err := loadConfig(path); err == nil {
		t.Fatal("expected parse error")
	}
}

func TestLoadConfigExplicitMissingPath(t *testing.T) {
	if _, _, err := loadConfig(filepath.Join(t.TempDir(), "nope.yaml")); err == nil {
		t.Fatal("expected error for missing explicit path")
	}
}

func TestLoadConfigDefaultsFromCWD(t *testing.T) {
	t.Setenv("UNSWARM_AGENT_BACKEND_URL", "")
	t.Setenv("UNSWARM_AGENT_API_KEY", "")
	dir := t.TempDir()
	content := "backend_url: ws://127.0.0.1:6001\nagent_name: cwd-agent\n"
	if err := os.WriteFile(filepath.Join(dir, "agent.yaml"), []byte(content), 0o600); err != nil {
		t.Fatal(err)
	}
	t.Chdir(dir)

	cfg, path, err := loadConfig("")
	if err != nil {
		t.Fatalf("loadConfig: %v", err)
	}
	if path != "./agent.yaml" {
		t.Errorf("path = %q, want ./agent.yaml", path)
	}
	if cfg.AgentName != "cwd-agent" {
		t.Errorf("AgentName = %q, want cwd-agent", cfg.AgentName)
	}
}

// TestLoadConfigNoFileFallback proves the no-config-file path falls through to
// defaults + env overrides (empty returned path) rather than hard-erroring on
// the missing ./agent.yaml. This is the regression guard for using
// errors.Is(err, fs.ErrNotExist) instead of os.IsNotExist on the wrapped
// config.Load error. Skipped when a system-wide /etc/unswarm/agent.yaml exists.
func TestLoadConfigNoFileFallback(t *testing.T) {
	if _, err := os.Stat("/etc/unswarm/agent.yaml"); err == nil {
		t.Skip("/etc/unswarm/agent.yaml exists on this host")
	}
	t.Setenv("UNSWARM_AGENT_BACKEND_URL", "")
	t.Setenv("UNSWARM_AGENT_API_KEY", "")
	t.Chdir(t.TempDir())

	cfg, path, err := loadConfig("")
	if err != nil {
		t.Fatalf("loadConfig(\"\") with no config file should fall back to defaults, got error: %v", err)
	}
	if path != "" {
		t.Errorf("path = %q, want empty on defaults fallback", path)
	}
	if cfg.BackendURL != config.DefaultConfig().BackendURL {
		t.Errorf("BackendURL = %q, want default %q", cfg.BackendURL, config.DefaultConfig().BackendURL)
	}
}

// ---------------------------------------------------------------------------
// warnConfigPermissions
// ---------------------------------------------------------------------------

func TestWarnConfigPermissions(t *testing.T) {
	dir := t.TempDir()
	path := filepath.Join(dir, "agent.yaml")
	if err := os.WriteFile(path, []byte("api_key: secret\n"), 0o644); err != nil {
		t.Fatal(err)
	}
	if err := os.Chmod(path, 0o644); err != nil {
		t.Fatal(err)
	}

	t.Run("warns on group/other-readable key file", func(t *testing.T) {
		logger, buf := bufferLogger(slog.LevelWarn)
		warnConfigPermissions(path, config.Config{APIKey: "secret", APIKeyFromYAML: true}, logger)
		if !strings.Contains(buf.String(), "group/other-readable") {
			t.Errorf("expected permission warning, got: %s", buf.String())
		}
	})

	t.Run("no warning when file is owner-only", func(t *testing.T) {
		secure := filepath.Join(dir, "secure.yaml")
		if err := os.WriteFile(secure, []byte("api_key: secret\n"), 0o600); err != nil {
			t.Fatal(err)
		}
		if err := os.Chmod(secure, 0o600); err != nil {
			t.Fatal(err)
		}
		logger, buf := bufferLogger(slog.LevelWarn)
		warnConfigPermissions(secure, config.Config{APIKey: "secret", APIKeyFromYAML: true}, logger)
		if strings.Contains(buf.String(), "group/other-readable") {
			t.Errorf("did not expect warning for 0600 file, got: %s", buf.String())
		}
	})

	t.Run("skips when key not from yaml", func(t *testing.T) {
		logger, buf := bufferLogger(slog.LevelWarn)
		warnConfigPermissions(path, config.Config{APIKey: "secret"}, logger)
		if buf.Len() != 0 {
			t.Errorf("did not expect any log, got: %s", buf.String())
		}
	})

	t.Run("skips when path is empty", func(t *testing.T) {
		logger, buf := bufferLogger(slog.LevelWarn)
		warnConfigPermissions("", config.Config{APIKey: "secret", APIKeyFromYAML: true}, logger)
		if buf.Len() != 0 {
			t.Errorf("did not expect any log, got: %s", buf.String())
		}
	})

	t.Run("skips on stat error", func(t *testing.T) {
		logger, buf := bufferLogger(slog.LevelWarn)
		warnConfigPermissions(filepath.Join(dir, "missing.yaml"), config.Config{APIKey: "secret", APIKeyFromYAML: true}, logger)
		if buf.Len() != 0 {
			t.Errorf("did not expect any log, got: %s", buf.String())
		}
	})
}

// ---------------------------------------------------------------------------
// warnScriptsDirWritable
// ---------------------------------------------------------------------------

func TestWarnScriptsDirWritable(t *testing.T) {
	t.Run("empty scripts dir", func(t *testing.T) {
		logger, buf := bufferLogger(slog.LevelWarn)
		warnScriptsDirWritable(config.Config{}, logger)
		if buf.Len() != 0 {
			t.Errorf("did not expect any log, got: %s", buf.String())
		}
	})

	t.Run("warns for writable dir", func(t *testing.T) {
		logger, buf := bufferLogger(slog.LevelWarn)
		warnScriptsDirWritable(config.Config{ScriptsDir: t.TempDir()}, logger)
		if !strings.Contains(buf.String(), "writable") {
			t.Errorf("expected writable warning, got: %s", buf.String())
		}
	})

	t.Run("no warning for read-only dir", func(t *testing.T) {
		dir := t.TempDir()
		if err := os.Chmod(dir, 0o500); err != nil {
			t.Fatal(err)
		}
		t.Cleanup(func() { _ = os.Chmod(dir, 0o700) })
		logger, buf := bufferLogger(slog.LevelWarn)
		warnScriptsDirWritable(config.Config{ScriptsDir: dir}, logger)
		if strings.Contains(buf.String(), "writable") {
			t.Errorf("did not expect warning for read-only dir, got: %s", buf.String())
		}
	})

	t.Run("skips on stat error", func(t *testing.T) {
		logger, buf := bufferLogger(slog.LevelWarn)
		warnScriptsDirWritable(config.Config{ScriptsDir: filepath.Join(t.TempDir(), "missing")}, logger)
		if buf.Len() != 0 {
			t.Errorf("did not expect any log, got: %s", buf.String())
		}
	})
}

// ---------------------------------------------------------------------------
// setupDispatcher — script handlers
// ---------------------------------------------------------------------------

func TestSetupDispatcherScriptHandlers(t *testing.T) {
	logger := discardLogger()
	dir := t.TempDir()
	mgr, err := scripts.NewManager(dir, "")
	if err != nil {
		t.Fatalf("NewManager: %v", err)
	}
	cfg := config.DefaultConfig()
	cfg.AllowScriptStart = true
	cfg.AllowScriptUpload = true
	disp := setupDispatcher(nil, mgr, runtimegate.NewGate(nil, false), cfg, logger)

	// list_scripts on an empty dir.
	res := disp.Dispatch(protocol.CommandPayload{Command: protocol.CmdListScripts})
	if !res.OK {
		t.Fatalf("list_scripts failed: %v", res.Error)
	}

	// upload_script writes a file.
	res = disp.Dispatch(protocol.CommandPayload{
		Command:       protocol.CmdUploadScript,
		ScriptPath:    "svc.sh",
		ScriptContent: "#!/bin/bash\nexit 0\n",
	})
	if !res.OK {
		t.Fatalf("upload_script failed: %v", res.Error)
	}

	// get_script_content reads it back.
	res = disp.Dispatch(protocol.CommandPayload{Command: protocol.CmdGetScriptContent, ScriptPath: "svc.sh"})
	if !res.OK {
		t.Fatalf("get_script_content failed: %v", res.Error)
	}

	// update_script overwrites it.
	res = disp.Dispatch(protocol.CommandPayload{
		Command:       protocol.CmdUpdateScript,
		ScriptPath:    "svc.sh",
		ScriptContent: "#!/bin/bash\nexit 0\n",
	})
	if !res.OK {
		t.Fatalf("update_script failed: %v", res.Error)
	}

	// start_script / get_script_logs address the script by its absolute path
	// inside scripts_dir.
	absPath := filepath.Join(dir, "svc.sh")

	// get_script_logs returns an empty list before the script has run.
	res = disp.Dispatch(protocol.CommandPayload{Command: protocol.CmdGetScriptLogs, ScriptPath: absPath, TailLines: 10})
	if !res.OK {
		t.Fatalf("get_script_logs failed: %v", res.Error)
	}

	// start_script spawns bash and returns a PID; stop_script cleans it up.
	res = disp.Dispatch(protocol.CommandPayload{Command: protocol.CmdStartScript, ScriptPath: absPath, ScriptPort: 1234})
	if !res.OK {
		t.Fatalf("start_script failed: %v", res.Error)
	}
	data, ok := res.Data.(map[string]interface{})
	if !ok {
		t.Fatalf("start_script data = %T, want map", res.Data)
	}
	pid, ok := data["pid"].(int)
	if !ok || pid <= 0 {
		t.Fatalf("start_script pid = %v", data["pid"])
	}
	res = disp.Dispatch(protocol.CommandPayload{Command: protocol.CmdStopScript, PID: pid})
	if !res.OK {
		t.Fatalf("stop_script failed: %v", res.Error)
	}

	// delete_script removes the file.
	res = disp.Dispatch(protocol.CommandPayload{Command: protocol.CmdDeleteScript, ScriptPath: "svc.sh"})
	if !res.OK {
		t.Fatalf("delete_script failed: %v", res.Error)
	}
}

func TestSetupDispatcherScriptGating(t *testing.T) {
	logger := discardLogger()
	mgr, err := scripts.NewManager(t.TempDir(), "")
	if err != nil {
		t.Fatalf("NewManager: %v", err)
	}
	cfg := config.DefaultConfig()
	cfg.AllowScriptStart = false
	cfg.AllowScriptUpload = false
	disp := setupDispatcher(nil, mgr, runtimegate.NewGate(nil, false), cfg, logger)

	// start_script is gated off.
	if res := disp.Dispatch(protocol.CommandPayload{Command: protocol.CmdStartScript, ScriptPath: "x.sh"}); res.OK {
		t.Error("start_script should be disabled")
	}
	// upload_script / update_script / get_script_content / delete_script gated off.
	for _, cmd := range []string{protocol.CmdUploadScript, protocol.CmdUpdateScript, protocol.CmdGetScriptContent, protocol.CmdDeleteScript} {
		if res := disp.Dispatch(protocol.CommandPayload{Command: cmd, ScriptPath: "x.sh", ScriptContent: "x"}); res.OK {
			t.Errorf("%s should be disabled", cmd)
		}
	}
	// stop_script is never gated by allow_script_start (but still requires a manager).
	if res := disp.Dispatch(protocol.CommandPayload{Command: protocol.CmdStopScript, PID: 999999}); res.OK {
		t.Error("stop_script on a stale pid should fail")
	}
}

func TestSetupDispatcherScriptsDisabled(t *testing.T) {
	logger := discardLogger()
	cfg := config.DefaultConfig()
	// nil manager => script support not enabled.
	disp := setupDispatcher(nil, nil, runtimegate.NewGate(nil, false), cfg, logger)

	// list_scripts is allowed and returns an empty list when disabled.
	res := disp.Dispatch(protocol.CommandPayload{Command: protocol.CmdListScripts})
	if !res.OK {
		t.Fatalf("list_scripts should succeed when disabled, got %v", res.Error)
	}
	// Every other script command reports the feature is off.
	for _, cmd := range []string{
		protocol.CmdStartScript, protocol.CmdStopScript, protocol.CmdGetScriptLogs,
		protocol.CmdUploadScript, protocol.CmdUpdateScript, protocol.CmdGetScriptContent,
		protocol.CmdDeleteScript,
	} {
		if res := disp.Dispatch(protocol.CommandPayload{Command: cmd}); res.OK {
			t.Errorf("%s should fail when script support is disabled", cmd)
		}
	}
}

// ---------------------------------------------------------------------------
// setupDispatcher — docker/loopback handlers with no live daemon
// ---------------------------------------------------------------------------

func TestSetupDispatcherDockerNotConnected(t *testing.T) {
	logger := discardLogger()
	disp := setupDispatcher(nil, nil, runtimegate.NewGate(nil, false), config.DefaultConfig(), logger)

	for _, cmd := range []string{
		protocol.CmdStartContainer, protocol.CmdStopContainer, protocol.CmdRestartContainer,
		protocol.CmdInspectContainer, protocol.CmdRemoveContainer, protocol.CmdGetContainerLogs,
		protocol.CmdListContainers, protocol.CmdCreateContainer,
	} {
		res := disp.Dispatch(protocol.CommandPayload{Command: cmd, Image: "svc"})
		if res.OK {
			t.Errorf("%s with nil docker handler should fail", cmd)
		}
		if res.Error == nil || !strings.Contains(*res.Error, "docker not connected") {
			t.Errorf("%s error = %v, want 'docker not connected'", cmd, res.Error)
		}
	}
}

func TestSetupDispatcherCreateContainerValidation(t *testing.T) {
	logger := discardLogger()
	dh, err := docker.New("tcp://127.0.0.1:1")
	if err != nil {
		t.Fatalf("docker.New: %v", err)
	}
	cfg := config.DefaultConfig()
	disp := setupDispatcher(dh, nil, runtimegate.NewGate(nil, false), cfg, logger)

	tests := []struct {
		name    string
		payload protocol.CommandPayload
		wantSub string
	}{
		{
			name:    "invalid json body",
			payload: protocol.CommandPayload{Command: protocol.CmdCreateContainer, JsonBody: json.RawMessage("not json")},
			wantSub: "decode create_container payload",
		},
		{
			name:    "image required",
			payload: protocol.CommandPayload{Command: protocol.CmdCreateContainer},
			wantSub: "image is required",
		},
		{
			name:    "container name required",
			payload: protocol.CommandPayload{Command: protocol.CmdCreateContainer, JsonBody: json.RawMessage(`{"image":"alpine:latest"}`)},
			wantSub: "containerName is required",
		},
		{
			name:    "creation disabled by default",
			payload: protocol.CommandPayload{Command: protocol.CmdCreateContainer, JsonBody: json.RawMessage(`{"image":"alpine:latest","containerName":"c"}`)},
			wantSub: "container creation is disabled",
		},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			res := disp.Dispatch(tt.payload)
			if res.OK {
				t.Fatalf("expected failure")
			}
			if res.Error == nil || !strings.Contains(*res.Error, tt.wantSub) {
				t.Errorf("error = %v, want substring %q", res.Error, tt.wantSub)
			}
		})
	}
}

func TestSetupDispatcherLifecycleGatedReachesDocker(t *testing.T) {
	logger := discardLogger()
	// An unroutable daemon: the handler reaches Docker and fails on the call.
	dh, err := docker.New("tcp://127.0.0.1:1")
	if err != nil {
		t.Fatalf("docker.New: %v", err)
	}
	disp := setupDispatcher(dh, nil, runtimegate.NewGate(nil, false), config.DefaultConfig(), logger)

	res := disp.Dispatch(protocol.CommandPayload{Command: protocol.CmdStartContainer, Image: "svc"})
	if res.OK {
		t.Fatal("start_container against an unreachable daemon should fail")
	}
}

func TestSetupDispatcherLifecycleBlockedByGate(t *testing.T) {
	logger := discardLogger()
	dh, err := docker.New("tcp://127.0.0.1:1")
	if err != nil {
		t.Fatalf("docker.New: %v", err)
	}
	// Enforcement on with an empty registry: the command must be blocked
	// before any Docker call.
	disp := setupDispatcher(dh, nil, runtimegate.NewGate(runtimegate.NewRegistry(), true), config.DefaultConfig(), logger)

	res := disp.Dispatch(protocol.CommandPayload{Command: protocol.CmdStartContainer, Image: "unregistered"})
	if res.OK {
		t.Fatal("unregistered container command should be blocked")
	}
	if res.Error == nil || !strings.Contains(*res.Error, "not registered") {
		t.Errorf("error = %v, want 'not registered'", res.Error)
	}
}

func TestSetupDispatcherLoopbackCommands(t *testing.T) {
	logger := discardLogger()
	disp := setupDispatcher(nil, nil, runtimegate.NewGate(nil, false), config.DefaultConfig(), logger)

	// health_check with an invalid port fails before any network dial.
	if res := disp.Dispatch(protocol.CommandPayload{Command: protocol.CmdHealthCheck, Port: 0}); res.OK {
		t.Error("health_check port 0 should fail")
	}
	// discover_models with an invalid port fails before any network dial.
	if res := disp.Dispatch(protocol.CommandPayload{Command: protocol.CmdDiscoverModels, Port: 0}); res.OK {
		t.Error("discover_models port 0 should fail")
	}
	// chat_completion with an invalid port fails before any network dial.
	if res := disp.Dispatch(protocol.CommandPayload{Command: protocol.CmdChatCompletion, Port: 0}); res.OK {
		t.Error("chat_completion port 0 should fail")
	}
	// chat_completion_stream routed via DispatchStream.
	handled, err := disp.DispatchStream(context.Background(), protocol.CommandPayload{Command: protocol.CmdChatCompletionStream, Port: 0}, func([]byte) error { return nil })
	if !handled {
		t.Fatal("chat_completion_stream should be registered as a stream command")
	}
	if err == nil {
		t.Error("chat_completion_stream port 0 should return an error")
	}
}
