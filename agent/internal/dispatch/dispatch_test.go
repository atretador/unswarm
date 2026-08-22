package dispatch

import (
	"context"
	"errors"
	"testing"
	"time"

	"unswarm/agent/internal/protocol"
)

func TestDispatchRoutesToCorrectHandler(t *testing.T) {
	d := New()

	called := make(map[string]bool)

	d.Register(protocol.CmdStartContainer, func(p protocol.CommandPayload) protocol.CommandResultPayload {
		called[protocol.CmdStartContainer] = true
		return protocol.CommandResultPayload{OK: true, Data: "started"}
	})
	d.Register(protocol.CmdStopContainer, func(p protocol.CommandPayload) protocol.CommandResultPayload {
		called[protocol.CmdStopContainer] = true
		return protocol.CommandResultPayload{OK: true}
	})
	d.Register(protocol.CmdRestartContainer, func(p protocol.CommandPayload) protocol.CommandResultPayload {
		called[protocol.CmdRestartContainer] = true
		return protocol.CommandResultPayload{OK: true}
	})
	d.Register(protocol.CmdInspectContainer, func(p protocol.CommandPayload) protocol.CommandResultPayload {
		called[protocol.CmdInspectContainer] = true
		return protocol.CommandResultPayload{OK: true, Data: map[string]string{"state": "running"}}
	})
	d.Register(protocol.CmdListContainers, func(p protocol.CommandPayload) protocol.CommandResultPayload {
		called[protocol.CmdListContainers] = true
		return protocol.CommandResultPayload{OK: true, Data: []string{"c1", "c2"}}
	})
	d.Register(protocol.CmdGetContainerLogs, func(p protocol.CommandPayload) protocol.CommandResultPayload {
		called[protocol.CmdGetContainerLogs] = true
		return protocol.CommandResultPayload{OK: true, Data: "log output"}
	})
	d.Register(protocol.CmdRemoveContainer, func(p protocol.CommandPayload) protocol.CommandResultPayload {
		called[protocol.CmdRemoveContainer] = true
		return protocol.CommandResultPayload{OK: true}
	})
	d.Register(protocol.CmdHealthCheck, func(p protocol.CommandPayload) protocol.CommandResultPayload {
		called[protocol.CmdHealthCheck] = true
		return protocol.CommandResultPayload{OK: true, Data: "healthy"}
	})
	d.Register(protocol.CmdDiscoverModels, func(p protocol.CommandPayload) protocol.CommandResultPayload {
		called[protocol.CmdDiscoverModels] = true
		return protocol.CommandResultPayload{OK: true, Data: []string{"model-a"}}
	})

	allCommands := []string{
		protocol.CmdStartContainer,
		protocol.CmdStopContainer,
		protocol.CmdRestartContainer,
		protocol.CmdInspectContainer,
		protocol.CmdListContainers,
		protocol.CmdGetContainerLogs,
		protocol.CmdRemoveContainer,
		protocol.CmdHealthCheck,
		protocol.CmdDiscoverModels,
	}

	for _, cmd := range allCommands {
		t.Run(cmd, func(t *testing.T) {
			result := d.Dispatch(protocol.CommandPayload{Command: cmd})
			if !result.OK {
				t.Errorf("Command %s returned ok=false", cmd)
			}
			if !called[cmd] {
				t.Errorf("Handler for %s was not called", cmd)
			}
		})
	}
}

func TestDispatchUnknownCommand(t *testing.T) {
	d := New()
	d.Register("known_cmd", func(p protocol.CommandPayload) protocol.CommandResultPayload {
		return protocol.CommandResultPayload{OK: true}
	})

	result := d.Dispatch(protocol.CommandPayload{Command: "unknown_cmd"})
	if result.OK {
		t.Error("Expected ok=false for unknown command")
	}
	if result.Error == nil {
		t.Error("Expected error message for unknown command")
	}
}

func TestHasCommand(t *testing.T) {
	d := New()
	d.Register("a", func(p protocol.CommandPayload) protocol.CommandResultPayload {
		return protocol.CommandResultPayload{OK: true}
	})

	if !d.HasCommand("a") {
		t.Error("HasCommand('a') = false, want true")
	}
	if d.HasCommand("b") {
		t.Error("HasCommand('b') = true, want false")
	}
}

func TestCommandsList(t *testing.T) {
	d := New()
	d.Register("x", func(p protocol.CommandPayload) protocol.CommandResultPayload {
		return protocol.CommandResultPayload{OK: true}
	})
	d.Register("y", func(p protocol.CommandPayload) protocol.CommandResultPayload {
		return protocol.CommandResultPayload{OK: true}
	})

	cmds := d.Commands()
	if len(cmds) != 2 {
		t.Errorf("Commands() returned %d items, want 2", len(cmds))
	}
}

// TestDispatchContext_ContextAwareHandler_Cancels verifies a handler registered via
// RegisterContext receives the connection context and can observe its cancellation
// (backend disconnect / scheduler timeout). The handler blocks on ctx.Done() and
// returns an error result instead of hanging forever.
func TestDispatchContext_ContextAwareHandler_Cancels(t *testing.T) {
	d := New()
	var observed context.Context

	d.RegisterContext(protocol.CmdChatCompletion, func(ctx context.Context, p protocol.CommandPayload) protocol.CommandResultPayload {
		observed = ctx
		select {
		case <-ctx.Done():
			msg := "cancelled: " + ctx.Err().Error()
			return protocol.CommandResultPayload{OK: false, Error: &msg}
		case <-time.After(5 * time.Second):
			return protocol.CommandResultPayload{OK: true, Data: "completed"}
		}
	})

	ctx, cancel := context.WithCancel(context.Background())
	cancel() // cancel immediately

	start := time.Now()
	result := d.DispatchContext(ctx, protocol.CommandPayload{Command: protocol.CmdChatCompletion})
	elapsed := time.Since(start)

	if result.OK {
		t.Error("expected ok=false when context is cancelled")
	}
	if result.Error == nil {
		t.Error("expected a cancellation error message")
	}
	if observed == nil {
		t.Error("context-aware handler did not receive the connection context")
	}
	if elapsed > 2*time.Second {
		t.Errorf("handler did not abort promptly on cancel; took %v", elapsed)
	}
}

// TestDispatchContext_NonContextHandler_IgnoresContext verifies that handlers
// registered with plain Register still work through DispatchContext (back-compat).
func TestDispatchContext_NonContextHandler_IgnoresContext(t *testing.T) {
	d := New()
	d.Register("plain", func(p protocol.CommandPayload) protocol.CommandResultPayload {
		return protocol.CommandResultPayload{OK: true}
	})

	ctx, cancel := context.WithCancel(context.Background())
	cancel()

	result := d.DispatchContext(ctx, protocol.CommandPayload{Command: "plain"})
	if !result.OK {
		t.Error("plain handler should still succeed through DispatchContext")
	}
}

func TestDispatchDuplicateRegistrationPanics(t *testing.T) {
	defer func() {
		if r := recover(); r == nil {
			t.Error("Expected panic on duplicate registration")
		}
	}()
	d := New()
	d.Register("dup", func(p protocol.CommandPayload) protocol.CommandResultPayload {
		return protocol.CommandResultPayload{OK: true}
	})
	d.Register("dup", func(p protocol.CommandPayload) protocol.CommandResultPayload {
		return protocol.CommandResultPayload{OK: true}
	})
}

// TestDispatchStream_ChunksInOrderThenSuccess verifies the streaming handler
// path: chunks are emitted in order via the emit callback, and a nil handler
// error maps to success (the caller then sends exactly one final ok result).
func TestDispatchStream_ChunksInOrderThenSuccess(t *testing.T) {
	d := New()

	var gotCtx context.Context
	var gotPayload protocol.CommandPayload
	d.RegisterStream(protocol.CmdChatCompletionStream, func(ctx context.Context, p protocol.CommandPayload, emit func(chunk []byte) error) error {
		gotCtx = ctx
		gotPayload = p
		for _, chunk := range [][]byte{[]byte("chunk-1"), []byte("chunk-2"), []byte("chunk-3")} {
			if err := emit(chunk); err != nil {
				return err
			}
		}
		return nil
	})

	var chunks [][]byte
	handled, err := d.DispatchStream(context.Background(),
		protocol.CommandPayload{Command: protocol.CmdChatCompletionStream, Port: 8080},
		func(chunk []byte) error {
			chunks = append(chunks, chunk)
			return nil
		})

	if !handled {
		t.Fatal("expected stream handler to be dispatched")
	}
	if err != nil {
		t.Fatalf("expected nil error, got %v", err)
	}
	want := []string{"chunk-1", "chunk-2", "chunk-3"}
	if len(chunks) != len(want) {
		t.Fatalf("got %d chunks, want %d", len(chunks), len(want))
	}
	for i, w := range want {
		if string(chunks[i]) != w {
			t.Errorf("chunk[%d] = %q, want %q", i, chunks[i], w)
		}
	}
	if gotCtx == nil {
		t.Error("stream handler did not receive the context")
	}
	if gotPayload.Port != 8080 {
		t.Errorf("stream handler payload port = %d, want 8080", gotPayload.Port)
	}
	if !d.HasStream(protocol.CmdChatCompletionStream) {
		t.Error("HasStream(chat_completion_stream) = false, want true")
	}
	if !d.HasCommand(protocol.CmdChatCompletionStream) {
		t.Error("HasCommand(chat_completion_stream) = false, want true")
	}
}

// TestDispatchStream_HandlerError verifies that a stream handler's error is
// returned to the caller (which sends exactly one final failed command_result).
func TestDispatchStream_HandlerError(t *testing.T) {
	d := New()
	sentinel := "upstream exploded"

	d.RegisterStream("boom", func(ctx context.Context, p protocol.CommandPayload, emit func(chunk []byte) error) error {
		if err := emit([]byte("partial")); err != nil {
			return err
		}
		return errors.New(sentinel)
	})

	var chunks [][]byte
	handled, err := d.DispatchStream(context.Background(),
		protocol.CommandPayload{Command: "boom"},
		func(chunk []byte) error {
			chunks = append(chunks, chunk)
			return nil
		})

	if !handled {
		t.Fatal("expected stream handler to be dispatched")
	}
	if err == nil || err.Error() != sentinel {
		t.Fatalf("expected error %q, got %v", sentinel, err)
	}
	if len(chunks) != 1 {
		t.Errorf("chunks emitted before error = %d, want 1", len(chunks))
	}
}

// TestDispatchStream_NotRegistered verifies the fallback contract: when no
// stream handler is registered for a command, DispatchStream reports handled=false
// and returns no error so the caller can fall back to regular dispatch.
func TestDispatchStream_NotRegistered(t *testing.T) {
	d := New()
	d.RegisterContext("plain", func(ctx context.Context, p protocol.CommandPayload) protocol.CommandResultPayload {
		return protocol.CommandResultPayload{OK: true}
	})

	emitCalls := 0
	handled, err := d.DispatchStream(context.Background(),
		protocol.CommandPayload{Command: "plain"},
		func(chunk []byte) error {
			emitCalls++
			return nil
		})

	if handled {
		t.Error("handled = true for non-stream command, want false")
	}
	if err != nil {
		t.Errorf("err = %v, want nil", err)
	}
	if emitCalls != 0 {
		t.Errorf("emit called %d times for non-stream command, want 0", emitCalls)
	}
}

// TestRegisterStream_DuplicateAcrossKindsPanics verifies a stream command name
// cannot collide with an existing plain or context handler (and vice versa).
func TestRegisterStream_DuplicateAcrossKindsPanics(t *testing.T) {
	streamHandler := func(ctx context.Context, p protocol.CommandPayload, emit func(chunk []byte) error) error {
		return nil
	}

	t.Run("stream over plain", func(t *testing.T) {
		defer func() {
			if r := recover(); r == nil {
				t.Error("expected panic on duplicate registration")
			}
		}()
		d := New()
		d.Register("dup", func(p protocol.CommandPayload) protocol.CommandResultPayload { return protocol.CommandResultPayload{OK: true} })
		d.RegisterStream("dup", streamHandler)
	})

	t.Run("stream over context", func(t *testing.T) {
		defer func() {
			if r := recover(); r == nil {
				t.Error("expected panic on duplicate registration")
			}
		}()
		d := New()
		d.RegisterContext("dup", func(ctx context.Context, p protocol.CommandPayload) protocol.CommandResultPayload {
			return protocol.CommandResultPayload{OK: true}
		})
		d.RegisterStream("dup", streamHandler)
	})

	t.Run("context over stream", func(t *testing.T) {
		defer func() {
			if r := recover(); r == nil {
				t.Error("expected panic on duplicate registration")
			}
		}()
		d := New()
		d.RegisterStream("dup", streamHandler)
		d.RegisterContext("dup", func(ctx context.Context, p protocol.CommandPayload) protocol.CommandResultPayload {
			return protocol.CommandResultPayload{OK: true}
		})
	})
}
