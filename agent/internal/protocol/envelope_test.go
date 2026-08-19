package protocol

import (
	"encoding/json"
	"strings"
	"testing"
)

func TestEnvelopeSerializationCamelCase(t *testing.T) {
	env := MustEnvelope(TypeHello, nil, nil, HelloPayload{
		Name:         "machine-b",
		DockerSocket: "unix:///var/run/docker.sock",
		Version:      "0.1.0",
	})

	data, err := env.Encode()
	if err != nil {
		t.Fatalf("Encode: %v", err)
	}

	// Verify correct type
	var parsed map[string]interface{}
	if err := json.Unmarshal(data, &parsed); err != nil {
		t.Fatalf("Unmarshal: %v", err)
	}
	if parsed["type"] != "hello" {
		t.Errorf("type = %v, want 'hello'", parsed["type"])
	}

	// Payload should be an embedded object, not escaped string
	payload, ok := parsed["payload"].(map[string]interface{})
	if !ok {
		t.Fatalf("payload is not an object: %T", parsed["payload"])
	}
	if payload["name"] != "machine-b" {
		t.Errorf("payload.name = %v, want 'machine-b'", payload["name"])
	}
	if payload["dockerSocket"] != "unix:///var/run/docker.sock" {
		t.Errorf("payload.dockerSocket = %v", payload["dockerSocket"])
	}
}

func TestEnvelopeCamelCaseNoPascalCase(t *testing.T) {
	env := MustEnvelope(TypeCommandResult, strPtr("cmd-123"), strPtr("machine-b"), CommandResultPayload{
		OK:    true,
		Error: nil,
		Data:  map[string]string{"status": "running"},
	})

	data, err := env.Encode()
	if err != nil {
		t.Fatalf("Encode: %v", err)
	}
	s := string(data)

	// Must NOT contain PascalCase JSON keys
	pascalFields := []string{"\"Type\":", "\"Payload\":", "\"CommandResult\":",
		"\"ContainerId\":", "\"RegisteredContainerId\":", "\"ContainerPort\":",
		"\"GpuDevices\":", "\"MemoryLimitMb\":", "\"ExtraLabels\":",
		"\"BackendUrl\":", "\"ApiKey\":", "\"AgentName\":",
		"\"InitialBackoffMs\":", "\"MaxBackoffMs\":", "\"MaxRetries\":",
		"\"TotalMemoryMb\":", "\"CpuCores\":", "\"OsPlatform\":",
		"\"GpuInfo\":", "\"Error\":", "\"Heartbeat\":", "\"Telemetry\":",
		"\"TailLines\":", "\"Ok\":"}
	for _, pf := range pascalFields {
		if strings.Contains(s, pf) {
			t.Errorf("Found PascalCase field %s in JSON output", pf)
		}
	}
}

func TestDecodeEnvelope(t *testing.T) {
	raw := `{"type":"command","id":"cmd-456","agent":"machine-b","payload":{"command":"start_container","image":"my-ollama"}}`
	env, err := DecodeEnvelope([]byte(raw))
	if err != nil {
		t.Fatalf("DecodeEnvelope: %v", err)
	}
	if env.Type != TypeCommand {
		t.Errorf("Type = %s, want %s", env.Type, TypeCommand)
	}
	if env.ID == nil || *env.ID != "cmd-456" {
		t.Errorf("ID = %v, want 'cmd-456'", env.ID)
	}
	if env.Agent == nil || *env.Agent != "machine-b" {
		t.Errorf("Agent = %v, want 'machine-b'", env.Agent)
	}
}

func TestDecodeCommandPayload(t *testing.T) {
	raw := json.RawMessage(`{"command":"start_container","image":"my-ollama","containerPort":11434}`)
	cp, err := DecodeCommandPayload(raw)
	if err != nil {
		t.Fatalf("DecodeCommandPayload: %v", err)
	}
	if cp.Command != CmdStartContainer {
		t.Errorf("Command = %s, want %s", cp.Command, CmdStartContainer)
	}
	if cp.Image != "my-ollama" {
		t.Errorf("Image = %s, want 'my-ollama'", cp.Image)
	}
	if cp.ContainerPort != 11434 {
		t.Errorf("ContainerPort = %d, want 11434", cp.ContainerPort)
	}
}

func TestCommandPayloadContainerName(t *testing.T) {
	tests := []struct {
		name     string
		cmd      CommandPayload
		expected string
	}{
		{
			name:     "image field used as container name",
			cmd:      CommandPayload{Image: "my-ollama"},
			expected: "my-ollama",
		},
		{
			name:     "fallback to containerId",
			cmd:      CommandPayload{ContainerID: "docker-abc123"},
			expected: "docker-abc123",
		},
		{
			name:     "image takes precedence",
			cmd:      CommandPayload{Image: "my-ollama", ContainerID: "docker-abc123"},
			expected: "my-ollama",
		},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			got := tt.cmd.ContainerName()
			if got != tt.expected {
				t.Errorf("ContainerName() = %q, want %q", got, tt.expected)
			}
		})
	}
}

func TestNewEnvelopeNilPayload(t *testing.T) {
	env, err := NewEnvelope(TypeHeartbeat, nil, nil, nil)
	if err != nil {
		t.Fatalf("NewEnvelope: %v", err)
	}
	if env.Payload != nil {
		t.Errorf("Payload should be nil for nil input, got %v", env.Payload)
	}
}

func strPtr(s string) *string {
	return &s
}
