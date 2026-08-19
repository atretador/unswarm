package dispatch

import (
	"testing"
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
