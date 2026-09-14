package commands

import "strconv"

// Model represents a model from the API.
type Model struct {
	ID             string   `json:"id"`
	Name           string   `json:"name"`
	Family         string   `json:"family"`
	ParameterSize  string   `json:"parameterSize"`
	Quantization   string   `json:"quantization"`
	Status         string   `json:"status"`
	ContextWindow  int      `json:"contextWindow"`
	Origin         string   `json:"origin"`
	DisplayName    string   `json:"displayName"`
	CreatedAt      string   `json:"createdAt"`
	UpdatedAt      string   `json:"updatedAt"`
}

// Runtime represents a runtime/container registration.
type Runtime struct {
	ID                      string   `json:"id"`
	DisplayName             string   `json:"displayName"`
	Image                   string   `json:"image"`
	ContainerPort           int      `json:"containerPort"`
	MappedPort              int      `json:"mappedPort"`
	Status                  string   `json:"status"`
	RuntimeKind             string   `json:"runtimeKind"`
	Agent                   string   `json:"agent"`
	LauncherPath            string   `json:"launcherPath"`
	CanRunAlongWith         []string `json:"canRunAlongWith"`
	MaxConcurrentInferences int      `json:"maxConcurrentInferences"`
	CurrentCount            int      `json:"currentCount"`
	Models                  []string `json:"models"`
	CreatedAt               string   `json:"createdAt"`
	UpdatedAt               string   `json:"updatedAt"`
}

// Benchmark represents a benchmark result.
type Benchmark struct {
	ID              string  `json:"id"`
	ModelID         string  `json:"modelId"`
	ModelName       string  `json:"modelName"`
	Status          string  `json:"status"`
	TokensPerSecond float64 `json:"tokensPerSecond"`
	LatencyMs       float64 `json:"latencyMs"`
	TotalTokens     int     `json:"totalTokens"`
	PromptID        string  `json:"promptId"`
	PromptName      string  `json:"promptName"`
	PromptText      string  `json:"promptText"`
	StartedAt       string  `json:"startedAt"`
	CompletedAt     string  `json:"completedAt"`
	CreatedAt       string  `json:"createdAt"`
	ErrorMessage    string  `json:"errorMessage"`
}

// dashIfEmpty returns s, or "-" if s is empty.
func dashIfEmpty(s string) string {
	if s == "" {
		return "-"
	}
	return s
}

// intOrDash returns the integer as a string, or "-" if zero.
func intOrDash(v int) string {
	if v == 0 {
		return "-"
	}
	return strconv.Itoa(v)
}

// floatOrDash formats a float to 2 decimal places, or "-" if zero.
func floatOrDash(v float64) string {
	if v == 0 {
		return "-"
	}
	return formatFloat(v)
}

// formatFloat formats a float64 to 2 decimal places.
func formatFloat(v float64) string {
	return strconv.FormatFloat(v, 'f', 2, 64)
}
