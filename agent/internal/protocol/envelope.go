// Package protocol defines the WebSocket message envelope and types
// used for communication between the Unswarm agent and backend.
package protocol

import (
	"encoding/json"
	"fmt"
)

// Message types flowing over the WebSocket.
const (
	TypeHello         = "hello"
	TypeCommand       = "command"
	TypeCommandResult = "command_result"
	TypeTelemetry     = "telemetry"
	TypeHeartbeat     = "heartbeat"
	TypeError         = "error"
)

// Command names dispatched by the backend.
const (
	CmdStartContainer   = "start_container"
	CmdStopContainer    = "stop_container"
	CmdRestartContainer = "restart_container"
	CmdInspectContainer = "inspect_container"
	CmdListContainers   = "list_containers"
	CmdGetContainerLogs = "get_container_logs"
	CmdRemoveContainer  = "remove_container"
	CmdHealthCheck      = "health_check"
	CmdDiscoverModels   = "discover_models"
)

// Envelope is the top-level JSON structure for every message.
type Envelope struct {
	Type    string          `json:"type"`
	ID      *string         `json:"id,omitempty"`
	Agent   *string         `json:"agent,omitempty"`
	Payload json.RawMessage `json:"payload,omitempty"`
}

// HelloPayload is sent by the agent immediately after connecting.
type HelloPayload struct {
	Name         string `json:"name"`
	DockerSocket string `json:"dockerSocket,omitempty"`
	Version      string `json:"version,omitempty"`
}

// HelloAckPayload is sent by the backend to acknowledge a hello.
type HelloAckPayload struct {
	OK bool `json:"ok"`
}

// CommandPayload is sent by the backend to request an action on the agent.
type CommandPayload struct {
	Command               string            `json:"command"`
	ContainerID           string            `json:"containerId,omitempty"`
	Image                 string            `json:"image,omitempty"`
	RegisteredContainerID string            `json:"registeredContainerId,omitempty"`
	ContainerPort         int               `json:"containerPort,omitempty"`
	GPUDevices            string            `json:"gpuDevices,omitempty"`
	MemoryLimitMb         int               `json:"memoryLimitMb,omitempty"`
	ExtraLabels           map[string]string `json:"extraLabels,omitempty"`
	TailLines             int               `json:"tailLines,omitempty"`
	Port                  int               `json:"port,omitempty"`
}

// CommandResultPayload is sent by the agent in response to a command.
type CommandResultPayload struct {
	OK    bool        `json:"ok"`
	Error *string     `json:"error,omitempty"`
	Data  interface{} `json:"data,omitempty"`
}

// TelemetryPayload carries host/container status info.
type TelemetryPayload struct {
	Hostname      string               `json:"hostname"`
	OsPlatform    string               `json:"osPlatform"`
	GPUInfo       string               `json:"gpuInfo,omitempty"`
	TotalMemoryMb int64                `json:"totalMemoryMb"`
	CPUCores      int                  `json:"cpuCores"`
	Containers    []ContainerTelemetry `json:"containers"`
}

// ContainerTelemetry is per-container info inside a telemetry message.
type ContainerTelemetry struct {
	ID     string `json:"id"`
	Name   string `json:"name"`
	Status string `json:"status"`
	Port   int    `json:"port,omitempty"`
	Memory string `json:"memory,omitempty"`
	CPU    string `json:"cpu,omitempty"`
	Uptime string `json:"uptime,omitempty"`
}

// HeartbeatPayload is a keep-alive message (can be empty).
type HeartbeatPayload struct{}

// ErrorPayload is sent by the backend when it rejects a message.
type ErrorPayload struct {
	Error string `json:"error"`
}

// NewEnvelope creates an Envelope with a JSON-encoded payload.
func NewEnvelope(msgType string, id *string, agent *string, payload interface{}) (Envelope, error) {
	var raw json.RawMessage
	if payload != nil {
		b, err := json.Marshal(payload)
		if err != nil {
			return Envelope{}, fmt.Errorf("marshal payload: %w", err)
		}
		raw = b
	}
	return Envelope{
		Type:    msgType,
		ID:      id,
		Agent:   agent,
		Payload: raw,
	}, nil
}

// MustEnvelope is like NewEnvelope but panics on error (for simple payloads).
func MustEnvelope(msgType string, id *string, agent *string, payload interface{}) Envelope {
	env, err := NewEnvelope(msgType, id, agent, payload)
	if err != nil {
		panic(err)
	}
	return env
}

// Encode serializes the envelope to JSON bytes.
func (e Envelope) Encode() ([]byte, error) {
	return json.Marshal(e)
}

// DecodeEnvelope parses raw JSON bytes into an Envelope.
func DecodeEnvelope(data []byte) (Envelope, error) {
	var env Envelope
	if err := json.Unmarshal(data, &env); err != nil {
		return Envelope{}, fmt.Errorf("decode envelope: %w", err)
	}
	return env, nil
}

// DecodeCommandPayload extracts the command-specific payload from the raw payload.
func DecodeCommandPayload(raw json.RawMessage) (CommandPayload, error) {
	var cp CommandPayload
	if err := json.Unmarshal(raw, &cp); err != nil {
		return CommandPayload{}, fmt.Errorf("decode command payload: %w", err)
	}
	return cp, nil
}

// ContainerName resolves the container name to operate on.
// The "image" field is repurposed as the container name per the agent's design.
// Falls back to containerId if image is empty.
func (cp *CommandPayload) ContainerName() string {
	if cp.Image != "" {
		return cp.Image
	}
	return cp.ContainerID
}
