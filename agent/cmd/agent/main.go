// Unswarm Agent: a standalone Go agent that connects outbound via WebSocket
// to the Unswarm backend and manages pre-provisioned Docker containers.
package main

import (
	"context"
	"encoding/base64"
	"encoding/json"
	"flag"
	"fmt"
	"log/slog"
	"os"
	"os/signal"
	"sync"
	"syscall"
	"time"

	"unswarm/agent/internal/backoff"
	"unswarm/agent/internal/client"
	"unswarm/agent/internal/config"
	"unswarm/agent/internal/dispatch"
	"unswarm/agent/internal/docker"
	"unswarm/agent/internal/protocol"
	"unswarm/agent/internal/runtimegate"
	"unswarm/agent/internal/scripts"
	"unswarm/agent/internal/telemetry"
)

const version = "0.3.0"

// commandTimeout bounds every non-chat Docker command handler so a hung
// Docker daemon cannot wedge a session goroutine forever. Container
// stop/restart keep their own internal 10s stop timeout inside this bound.
const commandTimeout = 120 * time.Second

// commandContext returns a context for a bounded Docker command handler.
func commandContext() (context.Context, context.CancelFunc) {
	return context.WithTimeout(context.Background(), commandTimeout)
}

// sessionConfig holds per-session timing knobs (injectable for tests).
type sessionConfig struct {
	telemetryInterval time.Duration
	heartbeatInterval time.Duration
}

func defaultSessionConfig() sessionConfig {
	return sessionConfig{
		telemetryInterval: 10 * time.Second,
		heartbeatInterval: 15 * time.Second,
	}
}

func main() {
	configPath := flag.String("config", "", "Path to YAML config file (defaults: ./agent.yaml, /etc/unswarm/agent.yaml)")
	flag.Parse()

	// Set up structured logger
	logger := slog.New(slog.NewJSONHandler(os.Stdout, &slog.HandlerOptions{
		Level: slog.LevelInfo,
	}))

	// Load config
	cfg, err := loadConfig(*configPath)
	if err != nil {
		logger.Error("failed to load config", "error", err)
		os.Exit(1)
	}
	logger.Info("config loaded",
		"backend_url", client.LoggableURL(cfg.BackendURL),
		"agent_name", cfg.AgentName,
		"docker_socket", cfg.DockerSocket,
	)

	// Set up Docker handler
	dockerHandler, err := docker.New(cfg.DockerSocket)
	if err != nil {
		logger.Error("failed to connect to docker", "error", err)
		// Continue without Docker — agent will return errors for Docker commands
		logger.Warn("continuing without Docker connectivity")
	}

	// Set up script manager
	scriptMgr := scripts.NewManager(cfg.ScriptsDir)
	if scriptMgr.IsEnabled() {
		logger.Info("script support enabled", "scripts_dir", cfg.ScriptsDir)
	}

	// Registered-runtime gate: lifecycle commands are checked against the
	// registered runtime set synced from the backend (sync_registrations).
	gate := runtimegate.NewGate(runtimegate.NewRegistry(), cfg.EnforceRegisteredRuntime)
	if cfg.EnforceRegisteredRuntime {
		logger.Info("registered-runtime enforcement enabled")
	} else {
		logger.Warn("registered-runtime enforcement DISABLED: agent will act on any container on this host")
	}

	// Set up command dispatcher
	disp := setupDispatcher(dockerHandler, scriptMgr, gate, logger)

	// Set up the message router (extension point for future inference
	// message types proxied over the WebSocket). sync_registrations is
	// routed here to keep the gate's mapping current.
	msgRouter := setupMessageRouter(gate, logger)

	// Set up telemetry collector
	telemCollector := telemetry.New(logger)

	// Set up context with signal handling
	ctx, cancel := signal.NotifyContext(context.Background(), syscall.SIGINT, syscall.SIGTERM)
	defer cancel()

	// Set up WS client and backoff
	wsClient := client.New(cfg, logger)
	bo := backoff.New(cfg.InitialBackoff(), cfg.MaxBackoff())

	// Main reconnect loop
	logger.Info("starting agent", "version", version)
	for {
		select {
		case <-ctx.Done():
			logger.Info("shutting down agent")
			scriptMgr.Shutdown()
			wsClient.Close()
			return
		default:
		}

		if err := runSession(ctx, wsClient, bo, cfg, disp, msgRouter, telemCollector, dockerHandler, scriptMgr, defaultSessionConfig(), logger); err != nil {
			logger.Error("session ended", "error", err)
		}

		// Wait before reconnecting
		if reachedMaxRetries(cfg.Reconnect.MaxRetries, bo.Attempt()) {
			logger.Error("max retries reached, giving up", "attempts", bo.Attempt())
			return
		}

		delay := bo.Next()
		logger.Info("reconnecting", "delay", delay, "attempt", bo.Attempt())

		select {
		case <-ctx.Done():
			return
		case <-time.After(delay):
		}
	}
}

// runSession handles one connection lifetime: connect, hello, message loop, return.
//
// All session goroutines (telemetry ticker, heartbeat ticker, in-flight
// command handlers) are bound to a session-scoped context and are joined
// before this function returns. This guarantees nothing from session N can
// write to session N+1's connection after a reconnect.
func runSession(
	ctx context.Context,
	wsClient *client.WSClient,
	bo *backoff.Calculator,
	cfg config.Config,
	disp *dispatch.Dispatcher,
	msgRouter *dispatch.Router,
	telemCollector *telemetry.Collector,
	dockerHandler *docker.Handler,
	scriptMgr *scripts.Manager,
	sc sessionConfig,
	logger *slog.Logger,
) error {
	// sessionCtx is cancelled when the session ends; it gates every send and
	// every session goroutine so stale work cannot leak into a new session.
	sessionCtx, sessionCancel := context.WithCancel(ctx)
	defer sessionCancel()

	if err := wsClient.Connect(sessionCtx); err != nil {
		return fmt.Errorf("connect: %w", err)
	}
	defer wsClient.Close()

	// Interrupt a blocked wsClient.Read() as soon as the session ends: closing
	// the connection makes ReadMessage return immediately instead of waiting
	// out the read deadline, so SIGINT/SIGTERM shutdown is not delayed by up
	// to 60s. The goroutine exits once sessionCtx is cancelled (guaranteed by
	// the deferred sessionCancel before runSession returns).
	go func() {
		<-sessionCtx.Done()
		wsClient.Close()
	}()

	// Send hello
	if err := wsClient.SendHello(sessionCtx, version); err != nil {
		return fmt.Errorf("send hello: %w", err)
	}

	// Wait for hello ack
	if err := wsClient.WaitForHelloAck(sessionCtx); err != nil {
		return fmt.Errorf("hello ack: %w", err)
	}

	// Reset backoff only after a full handshake succeeds so that a
	// misconfigured agent backs off instead of hammering the backend.
	bo.Reset()

	logger.Info("handshake complete, entering message loop")

	// Session goroutines are joined before the connection is torn down.
	var wg sync.WaitGroup

	// Telemetry ticker
	wg.Add(1)
	telemetryDone := runPeriodic(sessionCtx, sc.telemetryInterval, func(ctx context.Context) {
		payload := telemCollector.Collect(func(ctx context.Context) []protocol.ContainerTelemetry {
			if dockerHandler != nil {
				return dockerHandler.ListContainerStatuses(ctx)
			}
			return nil
		})
		// Add script statuses to telemetry.
		if scriptMgr != nil && scriptMgr.IsEnabled() {
			for _, s := range scriptMgr.GetStatuses() {
				payload.Scripts = append(payload.Scripts, protocol.ScriptTelemetry{
					Path:      s.Path,
					PID:       s.PID,
					Status:    s.Status,
					Port:      s.Port,
					StartTime: s.StartTime,
				})
			}
		}
		env := protocol.MustEnvelope(protocol.TypeTelemetry, nil, strPtr(cfg.AgentName), payload)
		if err := wsClient.Send(ctx, env); err != nil {
			logger.Error("send telemetry", "error", err)
		}
	}, &wg)

	// Heartbeat ticker
	wg.Add(1)
	heartbeatDone := runPeriodic(sessionCtx, sc.heartbeatInterval, func(ctx context.Context) {
		env := protocol.MustEnvelope(protocol.TypeHeartbeat, nil, strPtr(cfg.AgentName), nil)
		if err := wsClient.Send(ctx, env); err != nil {
			logger.Error("send heartbeat", "error", err)
		}
	}, &wg)

	// Message loop
	var lastHeartbeatID string
	for {
		select {
		case <-ctx.Done():
			sessionCancel()
			wg.Wait()
			<-telemetryDone
			<-heartbeatDone
			return nil
		default:
		}

		env, err := wsClient.Read()
		if err != nil {
			// Session over: cancel session work and join every session
			// goroutine before returning so the deferred Close() runs only
			// after all sends from this session have completed.
			sessionCancel()
			wg.Wait()
			<-telemetryDone
			<-heartbeatDone
			return fmt.Errorf("read message: %w", err)
		}

		switch env.Type {
		case protocol.TypeCommand:
			wg.Add(1)
			go func(env protocol.Envelope) {
				defer wg.Done()
				handleCommand(sessionCtx, env, disp, wsClient, cfg, logger)
			}(env)

		case protocol.TypeHeartbeat:
			// Respond to server heartbeats with an ack carrying the same id.
			// Deduplicate by id to avoid echo loops if the server acks acks.
			logger.Debug("heartbeat received from server", "id", derefStr(env.ID))
			if env.ID != nil && *env.ID != lastHeartbeatID {
				lastHeartbeatID = *env.ID
				ack := protocol.MustEnvelope(protocol.TypeHeartbeat, env.ID, strPtr(cfg.AgentName), nil)
				if err := wsClient.Send(sessionCtx, ack); err != nil {
					logger.Error("send heartbeat ack", "error", err)
				}
			}

		case protocol.TypeHello:
			logger.Info("server sent hello (reconnect ack)")

		case protocol.TypeError:
			var errPayload protocol.ErrorPayload
			if env.Payload != nil {
				json.Unmarshal(env.Payload, &errPayload)
			}
			logger.Warn("server error", "error", errPayload.Error)

		default:
			// Extension point: route non-command message types (e.g. future
			// inference requests) through the message router.
			if resp, handled := msgRouter.Route(env); handled {
				if resp != nil {
					if err := wsClient.Send(sessionCtx, *resp); err != nil {
						logger.Error("send routed response", "type", env.Type, "error", err)
					}
				}
				continue
			}
			logger.Warn("unknown message type", "type", env.Type)
		}
	}
}

// runPeriodic runs fn on a ticker until sessionCtx is cancelled, then closes
// the returned done channel. wg is used to join the goroutine from the caller.
func runPeriodic(sessionCtx context.Context, interval time.Duration, fn func(context.Context), wg *sync.WaitGroup) <-chan struct{} {
	done := make(chan struct{})
	go func() {
		defer close(done)
		defer wg.Done()
		ticker := time.NewTicker(interval)
		defer ticker.Stop()
		for {
			select {
			case <-sessionCtx.Done():
				return
			case <-ticker.C:
				fn(sessionCtx)
			}
		}
	}()
	return done
}

// handleCommand processes a command and sends the result back.
// Every command — including malformed ones — gets a command_result echoing
// the command id, so the backend never waits forever. The session context is
// threaded through so long-running commands (chat_completion) abort when the
// backend disconnects or cancels.
func handleCommand(
	ctx context.Context,
	env protocol.Envelope,
	disp *dispatch.Dispatcher,
	wsClient *client.WSClient,
	cfg config.Config,
	logger *slog.Logger,
) {
	if env.Payload == nil {
		logger.Warn("command with nil payload", "id", derefStr(env.ID))
		sendCommandResult(ctx, wsClient, cfg, env.ID, errorResult("command payload is nil"), logger)
		return
	}

	cmdPayload, err := protocol.DecodeCommandPayload(env.Payload)
	if err != nil {
		logger.Error("decode command payload", "error", err, "id", derefStr(env.ID))
		sendCommandResult(ctx, wsClient, cfg, env.ID, errorResult(fmt.Sprintf("decode command payload: %v", err)), logger)
		return
	}

	logger.Info("command received", "command", cmdPayload.Command, "id", derefStr(env.ID))

	// Streaming commands emit command_chunk envelopes as the response body
	// arrives, then exactly one final command_result. The invariant: exactly
	// one command_result per command id, chunks only before it.
	if disp.HasStream(cmdPayload.Command) {
		handleStreamCommand(ctx, env, cmdPayload, disp, wsClient, cfg, logger)
		return
	}

	// Use the dispatcher for routing; context-aware handlers get the session ctx
	// so they can cancel in-flight work on backend disconnect.
	result := disp.DispatchContext(ctx, cmdPayload)

	// Send result back
	sendCommandResult(ctx, wsClient, cfg, env.ID, result, logger)
}

// handleStreamCommand dispatches a streaming command: each chunk emitted by the
// handler is sent as a command_chunk envelope (base64-encoded) echoing the
// command id; after the handler returns, exactly one final command_result is
// sent — ok:true on nil error, ok:false with the error message otherwise.
func handleStreamCommand(
	ctx context.Context,
	env protocol.Envelope,
	cmdPayload protocol.CommandPayload,
	disp *dispatch.Dispatcher,
	wsClient *client.WSClient,
	cfg config.Config,
	logger *slog.Logger,
) {
	emit := func(chunk []byte) error {
		chunkPayload := protocol.CommandChunkPayload{Data: base64.StdEncoding.EncodeToString(chunk)}
		chunkEnv := protocol.MustEnvelope(protocol.TypeCommandChunk, env.ID, strPtr(cfg.AgentName), chunkPayload)
		return wsClient.Send(ctx, chunkEnv)
	}

	err := func() error {
		handled, err := disp.DispatchStream(ctx, cmdPayload, emit)
		if !handled {
			msg := fmt.Sprintf("unknown command: %s", cmdPayload.Command)
			return fmt.Errorf("%s", msg)
		}
		return err
	}()

	if err != nil {
		sendCommandResult(ctx, wsClient, cfg, env.ID, errorResult(err.Error()), logger)
		return
	}
	sendCommandResult(ctx, wsClient, cfg, env.ID, protocol.CommandResultPayload{OK: true}, logger)
}

// sendCommandResult sends a command_result envelope echoing the command id.
func sendCommandResult(ctx context.Context, wsClient *client.WSClient, cfg config.Config, id *string, result protocol.CommandResultPayload, logger *slog.Logger) {
	resultEnv := protocol.MustEnvelope(protocol.TypeCommandResult, id, strPtr(cfg.AgentName), result)
	if err := wsClient.Send(ctx, resultEnv); err != nil {
		logger.Error("send command result", "id", derefStr(id), "error", err)
	}
}

// errorResult builds a failed command result.
func errorResult(msg string) protocol.CommandResultPayload {
	return protocol.CommandResultPayload{OK: false, Error: &msg}
}

// setupDispatcher registers all command handlers. Container lifecycle
// commands pass through the registered-runtime gate first: an unregistered
// target is rejected without any Docker API call when enforcement is on.
func setupDispatcher(dh *docker.Handler, scriptMgr *scripts.Manager, gate *runtimegate.Gate, logger *slog.Logger) *dispatch.Dispatcher {
	d := dispatch.New()

	// gated wraps a lifecycle handler with the registered-runtime check.
	gated := func(cmd string, run func(p protocol.CommandPayload, name string) protocol.CommandResultPayload) func(protocol.CommandPayload) protocol.CommandResultPayload {
		return func(p protocol.CommandPayload) protocol.CommandResultPayload {
			name := p.ContainerName()
			if blocked, ok := gate.Check(cmd, name); ok {
				logger.Warn("blocked unregistered container command", "command", cmd, "target", name)
				return blocked
			}
			if dh == nil {
				return notConnectedResult(cmd)
			}
			return run(p, name)
		}
	}

	// start_container
	d.Register(protocol.CmdStartContainer, gated(protocol.CmdStartContainer, func(_ protocol.CommandPayload, name string) protocol.CommandResultPayload {
		logger.Info("starting container", "name", name)
		ctx, cancel := commandContext()
		defer cancel()
		return dh.StartContainer(ctx, name)
	}))

	// stop_container
	d.Register(protocol.CmdStopContainer, gated(protocol.CmdStopContainer, func(_ protocol.CommandPayload, name string) protocol.CommandResultPayload {
		logger.Info("stopping container", "name", name)
		ctx, cancel := commandContext()
		defer cancel()
		return dh.StopContainer(ctx, name)
	}))

	// restart_container
	d.Register(protocol.CmdRestartContainer, gated(protocol.CmdRestartContainer, func(_ protocol.CommandPayload, name string) protocol.CommandResultPayload {
		logger.Info("restarting container", "name", name)
		ctx, cancel := commandContext()
		defer cancel()
		return dh.RestartContainer(ctx, name)
	}))

	// inspect_container
	d.Register(protocol.CmdInspectContainer, gated(protocol.CmdInspectContainer, func(_ protocol.CommandPayload, name string) protocol.CommandResultPayload {
		logger.Info("inspecting container", "name", name)
		ctx, cancel := commandContext()
		defer cancel()
		return dh.InspectContainer(ctx, name)
	}))

	// list_containers — filtered to registered containers when enforcement is on.
	d.Register(protocol.CmdListContainers, func(p protocol.CommandPayload) protocol.CommandResultPayload {
		if dh == nil {
			return notConnectedResult("list_containers")
		}
		logger.Info("listing containers")
		ctx, cancel := commandContext()
		defer cancel()
		return gate.FilterListResult(protocol.CmdListContainers, dh.ListContainers(ctx))
	})

	// get_container_logs
	d.Register(protocol.CmdGetContainerLogs, gated(protocol.CmdGetContainerLogs, func(p protocol.CommandPayload, name string) protocol.CommandResultPayload {
		logger.Info("getting container logs", "name", name, "tailLines", p.TailLines)
		ctx, cancel := commandContext()
		defer cancel()
		return dh.GetContainerLogs(ctx, name, p.TailLines)
	}))

	// remove_container
	d.Register(protocol.CmdRemoveContainer, gated(protocol.CmdRemoveContainer, func(_ protocol.CommandPayload, name string) protocol.CommandResultPayload {
		logger.Info("removing container", "name", name)
		ctx, cancel := commandContext()
		defer cancel()
		return dh.RemoveContainer(ctx, name)
	}))

	// health_check
	d.Register(protocol.CmdHealthCheck, func(p protocol.CommandPayload) protocol.CommandResultPayload {
		logger.Info("health check", "port", p.Port)
		ctx, cancel := commandContext()
		defer cancel()
		return docker.HealthCheck(ctx, p.Port)
	})

	// discover_models
	d.Register(protocol.CmdDiscoverModels, func(p protocol.CommandPayload) protocol.CommandResultPayload {
		logger.Info("discovering models", "port", p.Port)
		ctx, cancel := commandContext()
		defer cancel()
		return docker.DiscoverModels(ctx, p.Port)
	})

	// chat_completion — forwards a raw OpenAI chat-completions body to the local
	// container and returns the raw response body. Context-aware: the session
	// cancellation (backend disconnect) aborts the HTTP call via ctx.
	d.RegisterContext(protocol.CmdChatCompletion, func(ctx context.Context, p protocol.CommandPayload) protocol.CommandResultPayload {
		logger.Info("chat completion", "port", p.Port, "jsonBytes", len(p.JsonBody))
		return docker.ChatCompletion(ctx, p.Port, p.JsonBody)
	})

	// chat_completion_stream — same endpoint as chat_completion, but the raw
	// response body is streamed back incrementally as command_chunk envelopes
	// (base64-encoded), followed by exactly one final command_result.
	d.RegisterStream(protocol.CmdChatCompletionStream, func(ctx context.Context, p protocol.CommandPayload, emit func([]byte) error) error {
		logger.Info("chat completion stream", "port", p.Port, "jsonBytes", len(p.JsonBody))
		return docker.ChatCompletionStream(ctx, p.Port, string(p.JsonBody), emit)
	})

	// list_scripts
	d.Register(protocol.CmdListScripts, func(p protocol.CommandPayload) protocol.CommandResultPayload {
		if scriptMgr == nil || !scriptMgr.IsEnabled() {
			return protocol.CommandResultPayload{OK: true, Data: map[string]interface{}{"scripts": []interface{}{}}}
		}
		return protocol.CommandResultPayload{OK: true, Data: map[string]interface{}{"scripts": scriptMgr.ListScripts()}}
	})

	// start_script
	d.Register(protocol.CmdStartScript, func(p protocol.CommandPayload) protocol.CommandResultPayload {
		if scriptMgr == nil || !scriptMgr.IsEnabled() {
			return errorResult("script support not enabled (scripts_dir not configured)")
		}
		pid, err := scriptMgr.StartScript(p.ScriptPath, p.ScriptPort)
		if err != nil {
			return errorResult(err.Error())
		}
		return protocol.CommandResultPayload{OK: true, Data: map[string]interface{}{"pid": pid}}
	})

	// stop_script
	d.Register(protocol.CmdStopScript, func(p protocol.CommandPayload) protocol.CommandResultPayload {
		if scriptMgr == nil {
			return errorResult("script support not enabled")
		}
		if err := scriptMgr.StopScript(p.PID); err != nil {
			return errorResult(err.Error())
		}
		return protocol.CommandResultPayload{OK: true}
	})

	// get_script_logs
	d.Register(protocol.CmdGetScriptLogs, func(p protocol.CommandPayload) protocol.CommandResultPayload {
		if scriptMgr == nil || !scriptMgr.IsEnabled() {
			return errorResult("script support not enabled")
		}
		logs, err := scriptMgr.GetScriptLogs(p.ScriptPath, p.TailLines)
		if err != nil {
			return errorResult(err.Error())
		}
		return protocol.CommandResultPayload{OK: true, Data: map[string]interface{}{"logs": logs}}
	})

	return d
}

// loadConfig tries the given path, then ./agent.yaml, then /etc/unswarm/agent.yaml.
// Only a missing file falls through to the next candidate; a file that exists
// but fails to parse is a hard error so a typo cannot silently connect with
// default settings.
func loadConfig(path string) (config.Config, error) {
	if path != "" {
		return config.Load(path)
	}
	// Try defaults
	for _, p := range []string{"./agent.yaml", "/etc/unswarm/agent.yaml"} {
		cfg, err := config.Load(p)
		if err == nil {
			return cfg, nil
		}
		if !os.IsNotExist(err) {
			return config.Config{}, fmt.Errorf("config %s: %w", p, err)
		}
	}
	// No config file present anywhere: use defaults and apply env overrides
	// so that UNSWARM_AGENT_BACKEND_URL / UNSWARM_AGENT_API_KEY are not
	// silently ignored.
	cfg := config.DefaultConfig()
	cfg.ApplyEnvOverrides()
	if err := cfg.Validate(); err != nil {
		return config.Config{}, fmt.Errorf("validate default config: %w", err)
	}
	return cfg, nil
}

// reachedMaxRetries reports whether the reconnect loop should give up.
// maxRetries < 0 means infinite retries.
func reachedMaxRetries(maxRetries, attempt int) bool {
	return maxRetries >= 0 && attempt >= maxRetries
}

func strPtr(s string) *string {
	return &s
}

func derefStr(s *string) string {
	if s == nil {
		return ""
	}
	return *s
}

// setupMessageRouter registers handlers for non-command message types.
// This is the extension point for Phase 4 inference proxying: register
// inference message types here, e.g.:
//
//	msgRouter.RegisterMessage("inference_request", func(env protocol.Envelope) *protocol.Envelope {
//	    // proxy to the local model server and return the response envelope
//	})
//
// sync_registrations is routed here as well: the backend pushes the agent's
// registered runtime set on connect and whenever registrations change, and
// the handler atomically replaces the gate's mapping (full snapshot).
func setupMessageRouter(gate *runtimegate.Gate, logger *slog.Logger) *dispatch.Router {
	r := dispatch.NewRouter()
	r.RegisterMessage(protocol.TypeSyncRegistrations, func(env protocol.Envelope) *protocol.Envelope {
		var payload protocol.SyncRegistrationsPayload
		if env.Payload != nil {
			if err := json.Unmarshal(env.Payload, &payload); err != nil {
				logger.Error("decode sync_registrations payload", "error", err)
				return nil
			}
		}
		gate.Registry().Replace(payload.Registrations)
		logger.Info("registered runtime set synced", "entries", len(payload.Registrations))
		return nil
	})
	return r
}

func notConnectedResult(cmd string) protocol.CommandResultPayload {
	msg := fmt.Sprintf("docker not connected, cannot execute %s", cmd)
	return protocol.CommandResultPayload{OK: false, Error: &msg}
}
