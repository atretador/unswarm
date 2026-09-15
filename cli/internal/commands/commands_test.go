package commands

import (
	"bytes"
	"context"
	"encoding/json"
	"fmt"
	"io"
	"net/http"
	"net/http/httptest"
	"os"
	"strings"
	"testing"
	"time"

	"github.com/spf13/cobra"
	"github.com/unswarm/cli/internal/client"
	"github.com/unswarm/cli/internal/interact"
	"github.com/unswarm/cli/internal/output"
)

// simulateInput sets interact.In to a pipe that delivers the given lines
// one at a time. This works with interact's readLine() which creates a new
// bufio.Reader on each call (a strings.Reader would be consumed entirely
// on the first read).
func simulateInput(lines ...string) {
	pr, pw := io.Pipe()
	go func() {
		for _, line := range lines {
			fmt.Fprintf(pw, "%s\n", line)
		}
		pw.Close()
	}()
	interact.In = pr
	// Give the goroutine a moment to start writing
	// (io.Pipe is synchronous, so the goroutine blocks on first Write until read)
}

// testHexID is a valid 32-char hex ID used by tests to bypass name resolution.
const testHexID = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"

// setupTest configures the given command with a mock server and JSON output.
// The Writer is created AFTER os.Stdout is redirected so all output is captured.
// The cmd MUST be the actual command under test (not a dummy parent) so flags are accessible.
func setupTest(cmd *cobra.Command, serverURL string) func() string {
	r, wPipe, _ := os.Pipe()
	old := os.Stdout
	os.Stdout = wPipe

	cfg := &client.Config{BaseURL: serverURL, OutputFmt: "json", Color: false}
	c := client.New(cfg, client.WithAPIKey("test-key"))
	ctx := context.Background()
	ctx = context.WithValue(ctx, clientKey, c)
	w := output.NewWriter(output.FormatJSON, true, false)
	ctx = context.WithValue(ctx, outputKey, w)
	cmd.SetContext(ctx)

	return func() string {
		wPipe.Close()
		os.Stdout = old
		var buf bytes.Buffer
		io.Copy(&buf, r)
		return buf.String()
	}
}

// setupTestYes is like setupTest but also sets the yes flag to skip confirmation prompts.
func setupTestYes(cmd *cobra.Command, serverURL string) func() string {
	r, wPipe, _ := os.Pipe()
	old := os.Stdout
	os.Stdout = wPipe

	cfg := &client.Config{BaseURL: serverURL, OutputFmt: "json", Color: false}
	c := client.New(cfg, client.WithAPIKey("test-key"))
	ctx := context.Background()
	ctx = context.WithValue(ctx, clientKey, c)
	ctx = context.WithValue(ctx, yesKey, true)
	w := output.NewWriter(output.FormatJSON, true, false)
	ctx = context.WithValue(ctx, outputKey, w)
	cmd.SetContext(ctx)

	return func() string {
		wPipe.Close()
		os.Stdout = old
		var buf bytes.Buffer
		io.Copy(&buf, r)
		return buf.String()
	}
}

// setupTestFmt configures with a specific output format.
func setupTestFmt(cmd *cobra.Command, serverURL string, fmt output.Format) func() string {
	r, wPipe, _ := os.Pipe()
	old := os.Stdout
	os.Stdout = wPipe

	cfg := &client.Config{BaseURL: serverURL, OutputFmt: string(fmt), Color: false}
	c := client.New(cfg, client.WithAPIKey("test-key"))
	ctx := context.Background()
	ctx = context.WithValue(ctx, clientKey, c)
	w := output.NewWriter(fmt, true, false)
	ctx = context.WithValue(ctx, outputKey, w)
	cmd.SetContext(ctx)

	return func() string {
		wPipe.Close()
		os.Stdout = old
		var buf bytes.Buffer
		io.Copy(&buf, r)
		return buf.String()
	}
}

// ==================== Models Tests ====================

func TestModelsList(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		fmt.Fprint(w, `[{
			"id": "model-1", "name": "llama-7b", "family": "llama",
			"parameterSize": "7B", "quantization": "q4_0", "status": "ready",
			"contextWindow": 4096, "origin": "swarm", "displayName": "Llama 7B",
			"createdAt": "2025-01-01T00:00:00Z", "updatedAt": "2025-01-01T00:00:00Z"
		}]`)
	}))
	defer server.Close()

	capture := setupTest(modelsListCmd, server.URL)
	err := modelsListCmd.RunE(modelsListCmd, nil)
	if err != nil {
		t.Fatalf("expected no error, got: %v", err)
	}

	out := capture()
	if !strings.Contains(out, "model-1") || !strings.Contains(out, "llama-7b") {
		t.Errorf("expected model data in output, got: %s", out)
	}
}

func TestModelsListTable(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		fmt.Fprint(w, `[{
			"id": "m1", "name": "gpt-test", "family": "gpt",
			"parameterSize": "13B", "status": "ready", "origin": "cloud"
		}]`)
	}))
	defer server.Close()

	capture := setupTestFmt(modelsListCmd, server.URL, output.FormatTable)
	err := modelsListCmd.RunE(modelsListCmd, nil)
	if err != nil {
		t.Fatalf("expected no error, got: %v", err)
	}

	out := capture()
	if !strings.Contains(out, "NAME") || !strings.Contains(out, "gpt-test") {
		t.Errorf("expected table headers and data, got: %s", out)
	}
}

func TestModelsGet(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		fmt.Fprint(w, `{"id": "`+testHexID+`", "name": "test-model", "family": "llama"}`)
	}))
	defer server.Close()

	capture := setupTest(modelsGetCmd, server.URL)
	err := modelsGetCmd.RunE(modelsGetCmd, []string{testHexID})
	if err != nil {
		t.Fatalf("expected no error, got: %v", err)
	}

	out := capture()
	if !strings.Contains(out, testHexID) {
		t.Errorf("expected %s in output, got: %s", testHexID, out)
	}
}

func TestModelsGet404(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		w.WriteHeader(404)
		fmt.Fprint(w, `{"error": "not_found", "message": "Model not found", "exitCode": 1}`)
	}))
	defer server.Close()

	capture := setupTest(modelsGetCmd, server.URL)
	modelsGetCmd.RunE(modelsGetCmd, []string{testHexID})

	out := capture()
	if !strings.Contains(out, "not_found") {
		t.Errorf("expected error in output, got: %s", out)
	}
}

func TestModelsCreate(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		var body map[string]any
		json.NewDecoder(r.Body).Decode(&body)
		if body["name"] != "new-model" {
			t.Errorf("expected name=new-model, got %v", body["name"])
		}
		w.Header().Set("Content-Type", "application/json")
		w.WriteHeader(201)
		fmt.Fprint(w, `{"id": "new-id", "name": "new-model", "status": "validating"}`)
	}))
	defer server.Close()

	capture := setupTest(modelsCreateCmd, server.URL)
	modelsCreateCmd.Flags().Set("name", "new-model")

	err := modelsCreateCmd.RunE(modelsCreateCmd, nil)
	if err != nil {
		t.Fatalf("expected no error, got: %v", err)
	}

	out := capture()
	if !strings.Contains(out, "new-model") {
		t.Errorf("expected new-model in output, got: %s", out)
	}
}

func TestModelsCreateInteractive(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.Method != "POST" {
			t.Errorf("expected POST, got %s", r.Method)
		}
		if r.URL.Path != "/api/models" {
			t.Errorf("expected /api/models, got %s", r.URL.Path)
		}

		var body map[string]any
		json.NewDecoder(r.Body).Decode(&body)

		// Verify all fields from interactive input
		if body["name"] != "my-llama" {
			t.Errorf("expected name='my-llama', got %v", body["name"])
		}
		if body["family"] != "llama" {
			t.Errorf("expected family='llama', got %v", body["family"])
		}
		if body["parameterSize"] != "13B" {
			t.Errorf("expected parameterSize='13B', got %v", body["parameterSize"])
		}
		if body["quantization"] != "Q8_0" {
			t.Errorf("expected quantization='Q8_0', got %v", body["quantization"])
		}
		if body["contextWindow"] != 8192.0 { // JSON numbers are float64
			t.Errorf("expected contextWindow=8192, got %v", body["contextWindow"])
		}
		if body["containerImage"] != "nvidia/cuda:12" {
			t.Errorf("expected containerImage='nvidia/cuda:12', got %v", body["containerImage"])
		}
		if body["supportedThinkingEffortsJson"] != `["fast","balanced"]` {
			t.Errorf("expected supportedThinkingEffortsJson, got %v", body["supportedThinkingEffortsJson"])
		}

		w.Header().Set("Content-Type", "application/json")
		w.WriteHeader(201)
		fmt.Fprint(w, `{"id": "new-id", "name": "my-llama", "status": "validating"}`)
	}))
	defer server.Close()

	// Simulate interactive input: name, family (1=llama), paramSize, quantization, contextWindow, containerImage, thinkingEfforts, confirm
	simulateInput("my-llama", "1", "13B", "Q8_0", "8192", "nvidia/cuda:12", `["fast","balanced"]`, "y")
	defer func() { interact.In = nil }()

	cmd := &cobra.Command{
		Use:   "create",
		Short: "Create a new model",
		RunE:  modelsCreateCmd.RunE,
	}
	cmd.Flags().String("name", "", "Model name")
	cmd.Flags().String("family", "", "Model family")
	cmd.Flags().String("parameter-size", "", "Parameter size")
	cmd.Flags().String("quantization", "", "Quantization method")
	cmd.Flags().Int("context-window", 0, "Context window size")
	cmd.Flags().String("container-image", "", "Container image")
	cmd.Flags().String("thinking-efforts", "", "Thinking efforts JSON")

	capture := setupTest(cmd, server.URL)

	err := cmd.RunE(cmd, nil)
	if err != nil {
		t.Fatalf("expected no error, got: %v", err)
	}

	out := capture()
	if !strings.Contains(out, "my-llama") {
		t.Errorf("expected my-llama in output, got: %s", out)
	}
}

func TestModelsCreateFlagsPath(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		var body map[string]any
		json.NewDecoder(r.Body).Decode(&body)

		if body["name"] != "flag-model" {
			t.Errorf("expected name='flag-model', got %v", body["name"])
		}
		if body["family"] != "mistral" {
			t.Errorf("expected family='mistral', got %v", body["family"])
		}

		w.Header().Set("Content-Type", "application/json")
		w.WriteHeader(201)
		fmt.Fprint(w, `{"id": "flag-id", "name": "flag-model", "status": "validating"}`)
	}))
	defer server.Close()

	cmd := &cobra.Command{
		Use:   "create",
		Short: "Create a new model",
		RunE:  modelsCreateCmd.RunE,
	}
	cmd.Flags().String("name", "", "Model name")
	cmd.Flags().String("family", "", "Model family")
	cmd.Flags().String("parameter-size", "", "Parameter size")
	cmd.Flags().String("quantization", "", "Quantization method")
	cmd.Flags().Int("context-window", 0, "Context window size")
	cmd.Flags().String("container-image", "", "Container image")
	cmd.Flags().String("thinking-efforts", "", "Thinking efforts JSON")

	capture := setupTest(cmd, server.URL)
	cmd.Flags().Set("name", "flag-model")
	cmd.Flags().Set("family", "mistral")

	err := cmd.RunE(cmd, nil)
	if err != nil {
		t.Fatalf("expected no error, got: %v", err)
	}

	out := capture()
	if !strings.Contains(out, "flag-model") {
		t.Errorf("expected flag-model in output, got: %s", out)
	}
}

func TestModelsCreateQuietNoFlags(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		t.Error("should not reach server in quiet mode with no flags")
	}))
	defer server.Close()

	cmd := &cobra.Command{
		Use:   "create",
		Short: "Create a new model",
		RunE:  modelsCreateCmd.RunE,
	}
	cmd.Flags().String("name", "", "Model name")
	cmd.Flags().String("family", "", "Model family")
	cmd.Flags().String("parameter-size", "", "Parameter size")
	cmd.Flags().String("quantization", "", "Quantization method")
	cmd.Flags().Int("context-window", 0, "Context window size")
	cmd.Flags().String("container-image", "", "Container image")
	cmd.Flags().String("thinking-efforts", "", "Thinking efforts JSON")

	capture := setupTest(cmd, server.URL)
	ctx := cmd.Context()
	ctx = context.WithValue(ctx, quietKey, true)
	cmd.SetContext(ctx)

	err := cmd.RunE(cmd, nil)
	if err == nil {
		t.Fatal("expected error in quiet mode with no flags")
	}

	_ = capture()
}

func TestModelsUpdate(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.Method != "PUT" {
			t.Errorf("expected PUT, got %s", r.Method)
		}
		var body map[string]any
		json.NewDecoder(r.Body).Decode(&body)
		if body["name"] != "updated-name" {
			t.Errorf("expected name=updated-name, got %v", body["name"])
		}
		w.Header().Set("Content-Type", "application/json")
		fmt.Fprint(w, `{"id": "`+testHexID+`", "name": "updated-name", "status": "ready"}`)
	}))
	defer server.Close()

	capture := setupTest(modelsUpdateCmd, server.URL)
	modelsUpdateCmd.Flags().Set("name", "updated-name")

	err := modelsUpdateCmd.RunE(modelsUpdateCmd, []string{testHexID})
	if err != nil {
		t.Fatalf("expected no error, got: %v", err)
	}

	out := capture()
	if !strings.Contains(out, "updated-name") {
		t.Errorf("expected updated-name in output, got: %s", out)
	}
}

func TestModelsDelete204(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.Method != "DELETE" {
			t.Errorf("expected DELETE, got %s", r.Method)
		}
		w.WriteHeader(204)
	}))
	defer server.Close()

	capture := setupTestYes(modelsDeleteCmd, server.URL)
	err := modelsDeleteCmd.RunE(modelsDeleteCmd, []string{testHexID})
	if err != nil {
		t.Fatalf("expected no error, got: %v", err)
	}

	out := capture()
	if !strings.Contains(out, "Deleted model") {
		t.Errorf("expected deletion message, got: %s", out)
	}
}

func TestModelsTestChat(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		var body map[string]any
		json.NewDecoder(r.Body).Decode(&body)
		if body["stream"] != false {
			t.Errorf("expected stream=false, got %v", body["stream"])
		}
		w.Header().Set("Content-Type", "application/json")
		fmt.Fprint(w, `{"content": "Hello from model!", "usage": {"totalTokens": 10}}`)
	}))
	defer server.Close()

	capture := setupTest(modelsTestChatCmd, server.URL)
	modelsTestChatCmd.Flags().Set("model", testHexID)
	modelsTestChatCmd.Flags().Set("messages", `[{"role":"user","content":"Hi"}]`)
	modelsTestChatCmd.Flags().Set("stream", "false")

	err := modelsTestChatCmd.RunE(modelsTestChatCmd, nil)
	if err != nil {
		t.Fatalf("expected no error, got: %v", err)
	}

	out := capture()
	if !strings.Contains(out, "Hello from model") {
		t.Errorf("expected model response, got: %s", out)
	}

	// Reset stream flag for other tests
	modelsTestChatCmd.Flags().Set("stream", "true")
}

func TestModelsTestChatStreaming(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		var body map[string]any
		json.NewDecoder(r.Body).Decode(&body)
		if body["stream"] != true {
			t.Errorf("expected stream=true, got %v", body["stream"])
		}

		w.Header().Set("Content-Type", "text/event-stream")
		w.Header().Set("Cache-Control", "no-cache")
		flusher, ok := w.(http.Flusher)
		if !ok {
			t.Fatal("streaming not supported")
		}

		// Send SSE events
		events := []string{
			`{"choices":[{"delta":{"content":"Hello"}}]}`,
			`{"choices":[{"delta":{"content":" from"}}]}`,
			`{"choices":[{"delta":{"content":" model!"}}]}`,
			`{"choices":[],"usage":{"prompt_tokens":5,"completion_tokens":3,"total_tokens":8}}`,
		}
		for _, ev := range events {
			fmt.Fprintf(w, "data: %s\n\n", ev)
			flusher.Flush()
		}
	}))
	defer server.Close()

	capture := setupTest(modelsTestChatCmd, server.URL)
	modelsTestChatCmd.Flags().Set("model", testHexID)
	modelsTestChatCmd.Flags().Set("messages", `[{"role":"user","content":"Hi"}]`)
	modelsTestChatCmd.Flags().Set("stream", "true")

	err := modelsTestChatCmd.RunE(modelsTestChatCmd, nil)
	if err != nil {
		t.Fatalf("expected no error, got: %v", err)
	}

	out := capture()
	// In JSON mode, the streaming result is collected and printed as JSON
	if !strings.Contains(out, "Hello from model!") {
		t.Errorf("expected streamed content in output, got: %s", out)
	}
	if !strings.Contains(out, "totalTokens") || !strings.Contains(out, "8") {
		t.Errorf("expected usage data in output, got: %s", out)
	}

	// Reset for other tests
	modelsTestChatCmd.Flags().Set("stream", "true")
}

func TestModelsTestChatStreamFlag(t *testing.T) {
	// Verify the --stream flag is registered and defaults to true
	flag := modelsTestChatCmd.Flags().Lookup("stream")
	if flag == nil {
		t.Fatal("expected --stream flag to be registered")
	}
	if flag.DefValue != "true" {
		t.Errorf("expected --stream default to be 'true', got '%s'", flag.DefValue)
	}
}

// ==================== Runtimes Tests ====================

func TestRuntimesList(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		fmt.Fprint(w, `[{
			"id": "rt-1", "displayName": "GPU Runtime", "image": "nvidia/cuda:12",
			"containerPort": 8080, "mappedPort": 30001, "agent": "host",
			"status": "ready", "runtimeKind": "container"
		}]`)
	}))
	defer server.Close()

	capture := setupTest(runtimesListCmd, server.URL)
	err := runtimesListCmd.RunE(runtimesListCmd, nil)
	if err != nil {
		t.Fatalf("expected no error, got: %v", err)
	}

	out := capture()
	if !strings.Contains(out, "rt-1") || !strings.Contains(out, "GPU Runtime") {
		t.Errorf("expected runtime data in output, got: %s", out)
	}
}

func TestRuntimesListTable(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		fmt.Fprint(w, `[{
			"id": "rt-1", "displayName": "GPU Runtime", "image": "nvidia/cuda:12",
			"containerPort": 8080, "mappedPort": 30001, "agent": "host", "status": "ready"
		}]`)
	}))
	defer server.Close()

	capture := setupTestFmt(runtimesListCmd, server.URL, output.FormatTable)
	err := runtimesListCmd.RunE(runtimesListCmd, nil)
	if err != nil {
		t.Fatalf("expected no error, got: %v", err)
	}

	out := capture()
	if !strings.Contains(out, "NAME") || !strings.Contains(out, "GPU Runtime") {
		t.Errorf("expected table headers and data, got: %s", out)
	}
}

func TestRuntimesGet404(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		w.WriteHeader(404)
		fmt.Fprint(w, `{"error": "not_found", "message": "Runtime not found", "exitCode": 1}`)
	}))
	defer server.Close()

	capture := setupTest(runtimesGetCmd, server.URL)
	runtimesGetCmd.RunE(runtimesGetCmd, []string{"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"})

	out := capture()
	if !strings.Contains(out, "not_found") {
		t.Errorf("expected error in output, got: %s", out)
	}
}

func TestRuntimesDelete204(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.WriteHeader(204)
	}))
	defer server.Close()

	capture := setupTestYes(runtimesDeleteCmd, server.URL)
	err := runtimesDeleteCmd.RunE(runtimesDeleteCmd, []string{testHexID})
	if err != nil {
		t.Fatalf("expected no error, got: %v", err)
	}

	out := capture()
	if !strings.Contains(out, "Deleted runtime") {
		t.Errorf("expected deletion message, got: %s", out)
	}
}

func TestRuntimesDeleteWithModels(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if !strings.Contains(r.URL.RawQuery, "deleteModels=true") {
			t.Errorf("expected deleteModels=true query param, got: %s", r.URL.RawQuery)
		}
		w.WriteHeader(204)
	}))
	defer server.Close()

	capture := setupTestYes(runtimesDeleteCmd, server.URL)
	runtimesDeleteCmd.Flags().Set("delete-models", "true")

	err := runtimesDeleteCmd.RunE(runtimesDeleteCmd, []string{testHexID})
	if err != nil {
		t.Fatalf("expected no error, got: %v", err)
	}

	out := capture()
	if !strings.Contains(out, "Deleted runtime") {
		t.Errorf("expected deletion message, got: %s", out)
	}
}

func TestRuntimesRegister(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		var body map[string]any
		json.NewDecoder(r.Body).Decode(&body)
		if body["displayName"] != "New Runtime" {
			t.Errorf("expected displayName='New Runtime', got %v", body["displayName"])
		}
		w.Header().Set("Content-Type", "application/json")
		w.WriteHeader(201)
		fmt.Fprint(w, `{"id": "new-rt", "displayName": "New Runtime", "status": "registered"}`)
	}))
	defer server.Close()

	capture := setupTest(runtimesRegisterCmd, server.URL)
	runtimesRegisterCmd.Flags().Set("name", "New Runtime")
	runtimesRegisterCmd.Flags().Set("image", "nvidia/cuda:12")

	err := runtimesRegisterCmd.RunE(runtimesRegisterCmd, nil)
	if err != nil {
		t.Fatalf("expected no error, got: %v", err)
	}

	out := capture()
	if !strings.Contains(out, "new-rt") {
		t.Errorf("expected new-rt in output, got: %s", out)
	}
}

func TestRuntimesUpdate(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.Method != "PUT" {
			t.Errorf("expected PUT, got %s", r.Method)
		}
		w.Header().Set("Content-Type", "application/json")
		fmt.Fprint(w, `{"id": "rt-upd", "displayName": "Updated Runtime"}`)
	}))
	defer server.Close()

	capture := setupTest(runtimesUpdateCmd, server.URL)
	runtimesUpdateCmd.Flags().Set("name", "Updated Runtime")

	err := runtimesUpdateCmd.RunE(runtimesUpdateCmd, []string{testHexID})
	if err != nil {
		t.Fatalf("expected no error, got: %v", err)
	}

	out := capture()
	if !strings.Contains(out, "Updated Runtime") {
		t.Errorf("expected Updated Runtime in output, got: %s", out)
	}
}

func TestRuntimesStart(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.WriteHeader(204)
	}))
	defer server.Close()

	capture := setupTest(runtimesStartCmd, server.URL)
	err := runtimesStartCmd.RunE(runtimesStartCmd, []string{testHexID})
	if err != nil {
		t.Fatalf("expected no error, got: %v", err)
	}

	out := capture()
	if !strings.Contains(out, "Started runtime") {
		t.Errorf("expected start message, got: %s", out)
	}
}

func TestRuntimesStop(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.WriteHeader(204)
	}))
	defer server.Close()

	capture := setupTest(runtimesStopCmd, server.URL)
	err := runtimesStopCmd.RunE(runtimesStopCmd, []string{testHexID})
	if err != nil {
		t.Fatalf("expected no error, got: %v", err)
	}

	out := capture()
	if !strings.Contains(out, "Stopped runtime") {
		t.Errorf("expected stop message, got: %s", out)
	}
}

func TestRuntimesRediscover(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.WriteHeader(204)
	}))
	defer server.Close()

	capture := setupTest(runtimesRediscoverCmd, server.URL)
	err := runtimesRediscoverCmd.RunE(runtimesRediscoverCmd, []string{testHexID})
	if err != nil {
		t.Fatalf("expected no error, got: %v", err)
	}

	out := capture()
	if !strings.Contains(out, "Rediscovery started") {
		t.Errorf("expected rediscover message, got: %s", out)
	}
}

func TestRuntimesHealthcheck(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.WriteHeader(204)
	}))
	defer server.Close()

	capture := setupTest(runtimesHealthcheckCmd, server.URL)
	err := runtimesHealthcheckCmd.RunE(runtimesHealthcheckCmd, []string{testHexID})
	if err != nil {
		t.Fatalf("expected no error, got: %v", err)
	}

	out := capture()
	if !strings.Contains(out, "Health check passed") {
		t.Errorf("expected healthcheck message, got: %s", out)
	}
}

func TestRuntimesSetConcurrency(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		fmt.Fprint(w, `{"id": "rt-1", "maxConcurrentInferences": 4}`)
	}))
	defer server.Close()

	capture := setupTest(runtimesSetConcurrencyCmd, server.URL)
	runtimesSetConcurrencyCmd.Flags().Set("max-concurrent", "4")

	err := runtimesSetConcurrencyCmd.RunE(runtimesSetConcurrencyCmd, []string{testHexID})
	if err != nil {
		t.Fatalf("expected no error, got: %v", err)
	}

	out := capture()
	if !strings.Contains(out, "maxConcurrentInferences") {
		t.Errorf("expected concurrency info in output, got: %s", out)
	}
}

func TestRuntimesToggleConcurrency(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		fmt.Fprint(w, `{"A": {"id": "rt-a"}, "B": {"id": "rt-b"}}`)
	}))
	defer server.Close()

	capture := setupTest(runtimesToggleConcurrencyCmd, server.URL)
	runtimesToggleConcurrencyCmd.Flags().Set("runtime-a", testHexID)
	runtimesToggleConcurrencyCmd.Flags().Set("runtime-b", testHexID)

	err := runtimesToggleConcurrencyCmd.RunE(runtimesToggleConcurrencyCmd, nil)
	if err != nil {
		t.Fatalf("expected no error, got: %v", err)
	}

	out := capture()
	if !strings.Contains(out, "rt-a") {
		t.Errorf("expected rt-a in output, got: %s", out)
	}
}

// ==================== Containers Tests ====================

func TestContainersList(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		fmt.Fprint(w, `[{
			"id": "container-abc-def-12345", "modelId": "m1", "modelName": "llama-7b",
			"status": "running", "port": 30001, "cpuPercent": 25.5, "memoryMb": 2048
		}]`)
	}))
	defer server.Close()

	capture := setupTest(containersListCmd, server.URL)
	err := containersListCmd.RunE(containersListCmd, nil)
	if err != nil {
		t.Fatalf("expected no error, got: %v", err)
	}

	out := capture()
	if !strings.Contains(out, "llama-7b") {
		t.Errorf("expected model name in output, got: %s", out)
	}
}

func TestContainersListTable(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		fmt.Fprint(w, `[{
			"id": "cont-1", "modelId": "m1", "modelName": "llama-7b",
			"status": "running", "port": 30001, "cpuPercent": 25.5, "memoryMb": 2048
		}]`)
	}))
	defer server.Close()

	capture := setupTestFmt(containersListCmd, server.URL, output.FormatTable)
	err := containersListCmd.RunE(containersListCmd, nil)
	if err != nil {
		t.Fatalf("expected no error, got: %v", err)
	}

	out := capture()
	if !strings.Contains(out, "ID") || !strings.Contains(out, "MODEL") {
		t.Errorf("expected table headers, got: %s", out)
	}
}

func TestContainersStart(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		var body map[string]any
		json.NewDecoder(r.Body).Decode(&body)
		if body["modelId"] != testHexID {
			t.Errorf("expected modelId=%s, got %v", testHexID, body["modelId"])
		}
		w.Header().Set("Content-Type", "application/json")
		fmt.Fprint(w, `{"id": "new-container", "modelId": "`+testHexID+`", "status": "starting"}`)
	}))
	defer server.Close()

	capture := setupTest(containersStartCmd, server.URL)
	containersStartCmd.Flags().Set("model", testHexID)

	err := containersStartCmd.RunE(containersStartCmd, nil)
	if err != nil {
		t.Fatalf("expected no error, got: %v", err)
	}

	out := capture()
	if !strings.Contains(out, "new-container") {
		t.Errorf("expected new-container in output, got: %s", out)
	}
}

func TestContainersStop204(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.WriteHeader(204)
	}))
	defer server.Close()

	capture := setupTestYes(containersStopCmd, server.URL)
	err := containersStopCmd.RunE(containersStopCmd, []string{testHexID})
	if err != nil {
		t.Fatalf("expected no error, got: %v", err)
	}

	out := capture()
	if !strings.Contains(out, "Stopped container") {
		t.Errorf("expected stop message, got: %s", out)
	}
}

func TestContainersRestart(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.WriteHeader(204)
	}))
	defer server.Close()

	capture := setupTestYes(containersRestartCmd, server.URL)
	err := containersRestartCmd.RunE(containersRestartCmd, []string{testHexID})
	if err != nil {
		t.Fatalf("expected no error, got: %v", err)
	}

	out := capture()
	if !strings.Contains(out, "Restarted container") {
		t.Errorf("expected restart message, got: %s", out)
	}
}

// ==================== Agents Tests ====================

func TestAgentsList(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		fmt.Fprint(w, `[{
			"name": "agent-1", "isConnected": true, "hostname": "gpu-server-01",
			"gpuInfo": "RTX 4090", "totalMemoryMb": 24576, "cpuCores": 16,
			"containers": [{"containerId": "c1"}, {"containerId": "c2"}],
			"scripts": [{"path": "/scripts/run.sh", "status": "running"}]
		}]`)
	}))
	defer server.Close()

	capture := setupTest(agentsListCmd, server.URL)
	err := agentsListCmd.RunE(agentsListCmd, nil)
	if err != nil {
		t.Fatalf("expected no error, got: %v", err)
	}

	out := capture()
	if !strings.Contains(out, "agent-1") || !strings.Contains(out, "Connected") {
		t.Errorf("expected agent-1 and Connected in output, got: %s", out)
	}
}

func TestAgentsListDisconnected(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		fmt.Fprint(w, `[{
			"name": "agent-2", "isConnected": false, "hostname": "old-server",
			"containers": [], "scripts": []
		}]`)
	}))
	defer server.Close()

	capture := setupTest(agentsListCmd, server.URL)
	err := agentsListCmd.RunE(agentsListCmd, nil)
	if err != nil {
		t.Fatalf("expected no error, got: %v", err)
	}

	out := capture()
	if !strings.Contains(out, "Disconnected") {
		t.Errorf("expected Disconnected status, got: %s", out)
	}
}

func TestAgentsListTable(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		fmt.Fprint(w, `[{
			"name": "agent-1", "isConnected": true, "hostname": "gpu-server-01",
			"gpuInfo": "RTX 4090", "totalMemoryMb": 24576, "cpuCores": 16,
			"containers": [{"containerId": "c1"}]
		}]`)
	}))
	defer server.Close()

	capture := setupTestFmt(agentsListCmd, server.URL, output.FormatTable)
	err := agentsListCmd.RunE(agentsListCmd, nil)
	if err != nil {
		t.Fatalf("expected no error, got: %v", err)
	}

	out := capture()
	if !strings.Contains(out, "NAME") || !strings.Contains(out, "agent-1") {
		t.Errorf("expected table output, got: %s", out)
	}
}

func TestAgentsContainers(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		fmt.Fprint(w, `[{"containerId": "c1", "modelName": "llama-7b", "status": "running"}]`)
	}))
	defer server.Close()

	capture := setupTest(agentsContainersCmd, server.URL)
	err := agentsContainersCmd.RunE(agentsContainersCmd, []string{"agent-1"})
	if err != nil {
		t.Fatalf("expected no error, got: %v", err)
	}

	out := capture()
	if !strings.Contains(out, "c1") || !strings.Contains(out, "llama-7b") {
		t.Errorf("expected container data in output, got: %s", out)
	}
}

func TestAgentsScripts(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		fmt.Fprint(w, `[{"path": "/scripts/run.sh", "status": "running"}]`)
	}))
	defer server.Close()

	capture := setupTest(agentsScriptsCmd, server.URL)
	err := agentsScriptsCmd.RunE(agentsScriptsCmd, []string{"agent-1"})
	if err != nil {
		t.Fatalf("expected no error, got: %v", err)
	}

	out := capture()
	if !strings.Contains(out, "run.sh") {
		t.Errorf("expected script path in output, got: %s", out)
	}
}

func TestAgentsScriptsAvailable(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		fmt.Fprint(w, `[{"name": "benchmark", "path": "/scripts/benchmark.sh"}]`)
	}))
	defer server.Close()

	capture := setupTest(agentsScriptsAvailableCmd, server.URL)
	err := agentsScriptsAvailableCmd.RunE(agentsScriptsAvailableCmd, []string{"agent-1"})
	if err != nil {
		t.Fatalf("expected no error, got: %v", err)
	}

	out := capture()
	if !strings.Contains(out, "benchmark") || !strings.Contains(out, "/scripts/benchmark.sh") {
		t.Errorf("expected script data in output, got: %s", out)
	}
}

func TestAgentsScriptsAvailableTable(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		fmt.Fprint(w, `[{"name": "benchmark", "path": "/scripts/benchmark.sh"}]`)
	}))
	defer server.Close()

	capture := setupTestFmt(agentsScriptsAvailableCmd, server.URL, output.FormatTable)
	err := agentsScriptsAvailableCmd.RunE(agentsScriptsAvailableCmd, []string{"agent-1"})
	if err != nil {
		t.Fatalf("expected no error, got: %v", err)
	}

	out := capture()
	if !strings.Contains(out, "NAME") || !strings.Contains(out, "PATH") {
		t.Errorf("expected table headers, got: %s", out)
	}
}

// ==================== Error Handling Tests ====================

func TestModelsListAPIError(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		w.WriteHeader(500)
		fmt.Fprint(w, `{"error": "internal_error", "message": "Server crashed", "exitCode": 1}`)
	}))
	defer server.Close()

	capture := setupTest(modelsListCmd, server.URL)
	modelsListCmd.RunE(modelsListCmd, nil)

	out := capture()
	if !strings.Contains(out, "internal_error") {
		t.Errorf("expected error in output, got: %s", out)
	}
}

func TestContainersListAPIError(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		w.WriteHeader(403)
		fmt.Fprint(w, `{"error": "forbidden", "message": "Access denied", "exitCode": 2}`)
	}))
	defer server.Close()

	capture := setupTest(containersListCmd, server.URL)
	containersListCmd.RunE(containersListCmd, nil)

	out := capture()
	if !strings.Contains(out, "forbidden") {
		t.Errorf("expected error in output, got: %s", out)
	}
}

func TestAgentsListAPIError(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		w.WriteHeader(401)
		fmt.Fprint(w, `{"error": "unauthorized", "message": "Invalid API key", "exitCode": 2}`)
	}))
	defer server.Close()

	capture := setupTest(agentsListCmd, server.URL)
	agentsListCmd.RunE(agentsListCmd, nil)

	out := capture()
	if !strings.Contains(out, "unauthorized") {
		t.Errorf("expected error in output, got: %s", out)
	}
}

// ==================== JSON Output Format Tests ====================

func TestModelsListJSONOutput(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		fmt.Fprint(w, `[{"id": "m1", "name": "test", "status": "ready", "origin": "swarm", "family": "llama", "parameterSize": "7B"}]`)
	}))
	defer server.Close()

	capture := setupTest(modelsListCmd, server.URL)
	err := modelsListCmd.RunE(modelsListCmd, nil)
	if err != nil {
		t.Fatalf("expected no error, got: %v", err)
	}

	out := capture()
	// In JSON mode, PrintTable wraps data as {"headers": [...], "rows": [[...]]}
	var result map[string]any
	if err := json.Unmarshal([]byte(out), &result); err != nil {
		t.Errorf("expected valid JSON object, got error: %v, output: %s", err, out)
	}
	if _, ok := result["headers"]; !ok {
		t.Errorf("expected headers key in JSON output, got: %s", out)
	}
}

// ==================== Args Validation Tests ====================

func TestModelsGetRequiresID(t *testing.T) {
	err := modelsGetCmd.Args(modelsGetCmd, []string{})
	if err == nil {
		t.Error("expected error for missing ID argument")
	}
}

func TestRuntimesGetRequiresID(t *testing.T) {
	err := runtimesGetCmd.Args(runtimesGetCmd, []string{})
	if err == nil {
		t.Error("expected error for missing ID argument")
	}
}

func TestContainersStopRequiresID(t *testing.T) {
	err := containersStopCmd.Args(containersStopCmd, []string{})
	if err == nil {
		t.Error("expected error for missing ID argument")
	}
}

func TestContainersRestartRequiresID(t *testing.T) {
	err := containersRestartCmd.Args(containersRestartCmd, []string{})
	if err == nil {
		t.Error("expected error for missing ID argument")
	}
}

func TestAgentsContainersRequiresName(t *testing.T) {
	err := agentsContainersCmd.Args(agentsContainersCmd, []string{})
	if err == nil {
		t.Error("expected error for missing name argument")
	}
}

func TestAgentsScriptsRequiresName(t *testing.T) {
	err := agentsScriptsCmd.Args(agentsScriptsCmd, []string{})
	if err == nil {
		t.Error("expected error for missing name argument")
	}
}

func TestAgentsScriptsAvailableRequiresName(t *testing.T) {
	err := agentsScriptsAvailableCmd.Args(agentsScriptsAvailableCmd, []string{})
	if err == nil {
		t.Error("expected error for missing name argument")
	}
}

func TestModelsDeleteRequiresID(t *testing.T) {
	err := modelsDeleteCmd.Args(modelsDeleteCmd, []string{})
	if err == nil {
		t.Error("expected error for missing ID argument")
	}
}

func TestModelsUpdateRequiresID(t *testing.T) {
	err := modelsUpdateCmd.Args(modelsUpdateCmd, []string{})
	if err == nil {
		t.Error("expected error for missing ID argument")
	}
}

// ==================== Command Registration Tests ====================

func TestRuntimesSubcommandsRegistered(t *testing.T) {
	cmds := runtimesCmd.Commands()
	names := make(map[string]bool)
	for _, c := range cmds {
		names[c.Name()] = true
	}
	for _, name := range []string{"list", "get", "register", "update", "delete", "start", "stop", "rediscover", "healthcheck", "set-concurrency", "toggle-concurrency"} {
		if !names[name] {
			t.Errorf("expected subcommand %q to be registered", name)
		}
	}
}

func TestContainersSubcommandsRegistered(t *testing.T) {
	cmds := containersCmd.Commands()
	names := make(map[string]bool)
	for _, c := range cmds {
		names[c.Name()] = true
	}
	for _, name := range []string{"list", "start", "stop", "restart"} {
		if !names[name] {
			t.Errorf("expected subcommand %q to be registered", name)
		}
	}
}

func TestAgentsSubcommandsRegistered(t *testing.T) {
	cmds := agentsCmd.Commands()
	names := make(map[string]bool)
	for _, c := range cmds {
		names[c.Name()] = true
	}
	for _, name := range []string{"list", "containers", "scripts", "scripts-available"} {
		if !names[name] {
			t.Errorf("expected subcommand %q to be registered", name)
		}
	}
}

// ==================== Auth Header Test ====================

func TestAPIKeyHeaderSent(t *testing.T) {
	receivedKey := ""
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		receivedKey = r.Header.Get("X-Api-Key")
		w.Header().Set("Content-Type", "application/json")
		fmt.Fprint(w, `[]`)
	}))
	defer server.Close()

	capture := setupTestFmt(modelsListCmd, server.URL, output.FormatJSON)
	// Override the client with specific key
	cfg := &client.Config{BaseURL: server.URL, OutputFmt: "json", Color: false}
	c := client.New(cfg, client.WithAPIKey("test-key-123"))
	ctx := context.Background()
	ctx = context.WithValue(ctx, clientKey, c)
	w := output.NewWriter(output.FormatJSON, true, false)
	ctx = context.WithValue(ctx, outputKey, w)
	modelsListCmd.SetContext(ctx)

	modelsListCmd.RunE(modelsListCmd, nil)
	_ = capture()

	if receivedKey != "test-key-123" {
		t.Errorf("expected X-Api-Key=test-key-123, got %q", receivedKey)
	}
}

// ==================== Transport Error Test ====================

func TestTransportError(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		hj, ok := w.(http.Hijacker)
		if ok {
			conn, _, _ := hj.Hijack()
			conn.Close()
		}
	}))
	defer server.Close()

	capture := setupTest(modelsListCmd, server.URL)
	modelsListCmd.RunE(modelsListCmd, nil)

	out := capture()
	if len(out) == 0 {
		t.Error("expected some output from transport error")
	}
}

// ==================== Phase 2: Users Tests ====================

func TestUsersList(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		fmt.Fprint(w, `[{"id": "u1", "username": "admin", "isTempPassword": false}, {"id": "u2", "username": "user1", "isTempPassword": true}]`)
	}))
	defer server.Close()

	capture := setupTest(usersListCmd, server.URL)
	err := usersListCmd.RunE(usersListCmd, nil)
	if err != nil {
		t.Fatalf("expected no error, got: %v", err)
	}

	out := capture()
	if !strings.Contains(out, "admin") || !strings.Contains(out, "user1") {
		t.Errorf("expected user data in output, got: %s", out)
	}
}

func TestUsersListTable(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		fmt.Fprint(w, `[{"id": "u1", "username": "admin", "isTempPassword": false}]`)
	}))
	defer server.Close()

	capture := setupTestFmt(usersListCmd, server.URL, output.FormatTable)
	err := usersListCmd.RunE(usersListCmd, nil)
	if err != nil {
		t.Fatalf("expected no error, got: %v", err)
	}

	out := capture()
	if !strings.Contains(out, "USERNAME") || !strings.Contains(out, "admin") {
		t.Errorf("expected table headers and data, got: %s", out)
	}
}

func TestUsersCreate(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.Method != "POST" {
			t.Errorf("expected POST, got %s", r.Method)
		}
		var body map[string]any
		json.NewDecoder(r.Body).Decode(&body)
		if body["username"] != "newuser" {
			t.Errorf("expected username=newuser, got %v", body["username"])
		}
		w.Header().Set("Content-Type", "application/json")
		w.WriteHeader(201)
		fmt.Fprint(w, `{"id": "u3", "username": "newuser", "isTempPassword": true}`)
	}))
	defer server.Close()

	capture := setupTest(usersCreateCmd, server.URL)
	usersCreateCmd.Flags().Set("username", "newuser")
	usersCreateCmd.Flags().Set("password", "pass123")

	err := usersCreateCmd.RunE(usersCreateCmd, nil)
	if err != nil {
		t.Fatalf("expected no error, got: %v", err)
	}

	out := capture()
	if !strings.Contains(out, "newuser") {
		t.Errorf("expected newuser in output, got: %s", out)
	}
}

func TestUsersCreateAPIError(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		w.WriteHeader(409)
		fmt.Fprint(w, `{"error": "conflict", "message": "Username already exists", "exitCode": 1}`)
	}))
	defer server.Close()

	capture := setupTest(usersCreateCmd, server.URL)
	usersCreateCmd.Flags().Set("username", "existing")
	usersCreateCmd.Flags().Set("password", "pass123")

	usersCreateCmd.RunE(usersCreateCmd, nil)

	out := capture()
	if !strings.Contains(out, "conflict") {
		t.Errorf("expected error in output, got: %s", out)
	}
}

func TestUsersResetPassword(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.Method != "POST" {
			t.Errorf("expected POST, got %s", r.Method)
		}
		var body map[string]any
		json.NewDecoder(r.Body).Decode(&body)
		if body["newPassword"] != "newpass456" {
			t.Errorf("expected newPassword=newpass456, got %v", body["newPassword"])
		}
		w.WriteHeader(200)
	}))
	defer server.Close()

	capture := setupTest(usersResetPasswordCmd, server.URL)
	usersResetPasswordCmd.Flags().Set("new-password", "newpass456")

	err := usersResetPasswordCmd.RunE(usersResetPasswordCmd, []string{testHexID})
	if err != nil {
		t.Fatalf("expected no error, got: %v", err)
	}

	out := capture()
	if !strings.Contains(out, "Password reset") {
		t.Errorf("expected reset message, got: %s", out)
	}
}

func TestUsersResetPasswordRequiresID(t *testing.T) {
	err := usersResetPasswordCmd.Args(usersResetPasswordCmd, []string{})
	if err == nil {
		t.Error("expected error for missing ID argument")
	}
}

func TestUsersDelete200(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.Method != "DELETE" {
			t.Errorf("expected DELETE, got %s", r.Method)
		}
		w.WriteHeader(200)
	}))
	defer server.Close()

	capture := setupTestYes(usersDeleteCmd, server.URL)

	err := usersDeleteCmd.RunE(usersDeleteCmd, []string{testHexID})
	if err != nil {
		t.Fatalf("expected no error, got: %v", err)
	}

	out := capture()
	if !strings.Contains(out, "Deleted user") {
		t.Errorf("expected deletion message, got: %s", out)
	}
}

func TestUsersDeleteRequiresConfirmation(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.WriteHeader(200)
	}))
	defer server.Close()

	// Set up with quiet mode so ConfirmOrSkip returns an error (no stdin needed)
	r, wPipe, _ := os.Pipe()
	old := os.Stdout
	os.Stdout = wPipe

	cfg := &client.Config{BaseURL: server.URL, OutputFmt: "json", Color: false}
	c := client.New(cfg, client.WithAPIKey("test-key"))
	ctx := context.Background()
	ctx = context.WithValue(ctx, clientKey, c)
	ctx = context.WithValue(ctx, quietKey, true)
	w := output.NewWriter(output.FormatJSON, true, true)
	ctx = context.WithValue(ctx, outputKey, w)
	usersDeleteCmd.SetContext(ctx)

	// Don't check err here — w.Error() returns ExitError which is expected for error cases
	usersDeleteCmd.RunE(usersDeleteCmd, []string{testHexID})
	wPipe.Close()
	os.Stdout = old
	var buf bytes.Buffer
	io.Copy(&buf, r)
	out := buf.String()

	if !strings.Contains(out, "confirmation_required") {
		t.Errorf("expected confirmation error, got: %s", out)
	}
}

func TestUsersDeleteRequiresID(t *testing.T) {
	err := usersDeleteCmd.Args(usersDeleteCmd, []string{})
	if err == nil {
		t.Error("expected error for missing ID argument")
	}
}

func TestUsersSubcommandsRegistered(t *testing.T) {
	cmds := usersCmd.Commands()
	names := make(map[string]bool)
	for _, c := range cmds {
		names[c.Name()] = true
	}
	for _, name := range []string{"list", "create", "reset-password", "delete"} {
		if !names[name] {
			t.Errorf("expected subcommand %q to be registered", name)
		}
	}
}

// ==================== Phase 2: API Keys Tests ====================

func TestAPIKeysList(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		fmt.Fprint(w, `[{"id": "k1", "name": "my-key", "keyPrefix": "sk-abc", "scope": "inference", "isActive": true, "createdAt": "2025-01-01T00:00:00Z"}]`)
	}))
	defer server.Close()

	capture := setupTest(apikeysListCmd, server.URL)
	err := apikeysListCmd.RunE(apikeysListCmd, nil)
	if err != nil {
		t.Fatalf("expected no error, got: %v", err)
	}

	out := capture()
	if !strings.Contains(out, "my-key") || !strings.Contains(out, "sk-abc") {
		t.Errorf("expected key data in output, got: %s", out)
	}
}

func TestAPIKeysListTable(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		fmt.Fprint(w, `[{"id": "k1", "name": "my-key", "keyPrefix": "sk-abc", "scope": "inference", "isActive": true, "createdAt": "2025-01-01T00:00:00Z"}]`)
	}))
	defer server.Close()

	capture := setupTestFmt(apikeysListCmd, server.URL, output.FormatTable)
	err := apikeysListCmd.RunE(apikeysListCmd, nil)
	if err != nil {
		t.Fatalf("expected no error, got: %v", err)
	}

	out := capture()
	if !strings.Contains(out, "NAME") || !strings.Contains(out, "SCOPE") {
		t.Errorf("expected table headers, got: %s", out)
	}
}

func TestAPIKeysGet(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		fmt.Fprint(w, `{"id": "`+testHexID+`", "name": "my-key", "keyPrefix": "sk-abc", "scope": "inference", "isActive": true}`)
	}))
	defer server.Close()

	capture := setupTest(apikeysGetCmd, server.URL)
	err := apikeysGetCmd.RunE(apikeysGetCmd, []string{testHexID})
	if err != nil {
		t.Fatalf("expected no error, got: %v", err)
	}

	out := capture()
	if !strings.Contains(out, "my-key") {
		t.Errorf("expected key name in output, got: %s", out)
	}
}

func TestAPIKeysGetRequiresID(t *testing.T) {
	err := apikeysGetCmd.Args(apikeysGetCmd, []string{})
	if err == nil {
		t.Error("expected error for missing name-or-id argument")
	}
}

func TestAPIKeysCreate(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.Method != "POST" {
			t.Errorf("expected POST, got %s", r.Method)
		}
		var body map[string]any
		json.NewDecoder(r.Body).Decode(&body)
		if body["name"] != "new-key" {
			t.Errorf("expected name=new-key, got %v", body["name"])
		}
		w.Header().Set("Content-Type", "application/json")
		w.WriteHeader(201)
		fmt.Fprint(w, `{"id": "k2", "name": "new-key", "keyPrefix": "sk-def", "secret": "sk-def123secret", "scope": "inference", "isActive": true}`)
	}))
	defer server.Close()

	capture := setupTest(apikeysCreateCmd, server.URL)
	apikeysCreateCmd.Flags().Set("name", "new-key")

	err := apikeysCreateCmd.RunE(apikeysCreateCmd, nil)
	if err != nil {
		t.Fatalf("expected no error, got: %v", err)
	}

	out := capture()
	if !strings.Contains(out, "sk-def123secret") {
		t.Errorf("expected secret in output (shown once), got: %s", out)
	}
}

func TestAPIKeysCreateAgent(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.URL.Path != "/api/api-keys/agent" {
			t.Errorf("expected /api/api-keys/agent, got %s", r.URL.Path)
		}
		var body map[string]any
		json.NewDecoder(r.Body).Decode(&body)
		if body["name"] != "agent-key" {
			t.Errorf("expected name=agent-key, got %v", body["name"])
		}
		if body["boundAgentName"] != "agent-1" {
			t.Errorf("expected boundAgentName=agent-1, got %v", body["boundAgentName"])
		}
		w.Header().Set("Content-Type", "application/json")
		w.WriteHeader(201)
		fmt.Fprint(w, `{"id": "k3", "name": "agent-key", "keyPrefix": "ak-abc", "secret": "ak-abc123", "scope": "agent", "isActive": true}`)
	}))
	defer server.Close()

	capture := setupTest(apikeysCreateAgentCmd, server.URL)
	apikeysCreateAgentCmd.Flags().Set("name", "agent-key")
	apikeysCreateAgentCmd.Flags().Set("bound-agent", "agent-1")

	err := apikeysCreateAgentCmd.RunE(apikeysCreateAgentCmd, nil)
	if err != nil {
		t.Fatalf("expected no error, got: %v", err)
	}

	out := capture()
	if !strings.Contains(out, "ak-abc123") {
		t.Errorf("expected secret in output, got: %s", out)
	}
}

func TestAPIKeysCreateAgentNoBound(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		var body map[string]any
		json.NewDecoder(r.Body).Decode(&body)
		if _, exists := body["boundAgentName"]; exists {
			t.Errorf("expected no boundAgentName in body, got %v", body)
		}
		w.Header().Set("Content-Type", "application/json")
		w.WriteHeader(201)
		fmt.Fprint(w, `{"id": "k4", "name": "unbound-key", "keyPrefix": "ak-def", "scope": "agent", "isActive": true}`)
	}))
	defer server.Close()

	capture := setupTest(apikeysCreateAgentCmd, server.URL)
	// Explicitly reset flags that may have been set by prior test
	apikeysCreateAgentCmd.Flags().Set("name", "unbound-key")
	apikeysCreateAgentCmd.Flags().Set("bound-agent", "")

	err := apikeysCreateAgentCmd.RunE(apikeysCreateAgentCmd, nil)
	if err != nil {
		t.Fatalf("expected no error, got: %v", err)
	}

	out := capture()
	if !strings.Contains(out, "unbound-key") {
		t.Errorf("expected key name in output, got: %s", out)
	}
}

func TestAPIKeysCreateControlPlane(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.URL.Path != "/api/api-keys/control-plane" {
			t.Errorf("expected /api/api-keys/control-plane, got %s", r.URL.Path)
		}
		var body map[string]any
		json.NewDecoder(r.Body).Decode(&body)
		if body["name"] != "cp-key" {
			t.Errorf("expected name=cp-key, got %v", body["name"])
		}
		perms, ok := body["permissions"].(map[string]any)
		if !ok || perms["domain"] != "rw" {
			t.Errorf("expected permissions.domain=rw, got %v", body["permissions"])
		}
		w.Header().Set("Content-Type", "application/json")
		w.WriteHeader(201)
		fmt.Fprint(w, `{"id": "k5", "name": "cp-key", "keyPrefix": "cp-abc", "secret": "cp-abc123", "scope": "control-plane", "isActive": true}`)
	}))
	defer server.Close()

	capture := setupTest(apikeysCreateControlPlaneCmd, server.URL)
	apikeysCreateControlPlaneCmd.Flags().Set("name", "cp-key")
	apikeysCreateControlPlaneCmd.Flags().Set("permissions", `{"domain": "rw"}`)

	err := apikeysCreateControlPlaneCmd.RunE(apikeysCreateControlPlaneCmd, nil)
	if err != nil {
		t.Fatalf("expected no error, got: %v", err)
	}

	out := capture()
	if !strings.Contains(out, "cp-abc123") {
		t.Errorf("expected secret in output, got: %s", out)
	}
}

func TestAPIKeysRevoke204(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.Method != "DELETE" {
			t.Errorf("expected DELETE, got %s", r.Method)
		}
		w.WriteHeader(204)
	}))
	defer server.Close()

	capture := setupTestYes(apikeysRevokeCmd, server.URL)

	err := apikeysRevokeCmd.RunE(apikeysRevokeCmd, []string{testHexID})
	if err != nil {
		t.Fatalf("expected no error, got: %v", err)
	}

	out := capture()
	if !strings.Contains(out, "Revoked API key") {
		t.Errorf("expected revocation message, got: %s", out)
	}
}

func TestAPIKeysRevokeRequiresConfirmation(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.WriteHeader(204)
	}))
	defer server.Close()

	// Set up with quiet mode so ConfirmOrSkip returns an error (no stdin needed)
	r, wPipe, _ := os.Pipe()
	old := os.Stdout
	os.Stdout = wPipe

	cfg := &client.Config{BaseURL: server.URL, OutputFmt: "json", Color: false}
	c := client.New(cfg, client.WithAPIKey("test-key"))
	ctx := context.Background()
	ctx = context.WithValue(ctx, clientKey, c)
	ctx = context.WithValue(ctx, quietKey, true)
	w := output.NewWriter(output.FormatJSON, true, true)
	ctx = context.WithValue(ctx, outputKey, w)
	apikeysRevokeCmd.SetContext(ctx)

	// Don't check err here — w.Error() returns ExitError which is expected for error cases
	apikeysRevokeCmd.RunE(apikeysRevokeCmd, []string{testHexID})
	wPipe.Close()
	os.Stdout = old
	var buf bytes.Buffer
	io.Copy(&buf, r)
	out := buf.String()

	if !strings.Contains(out, "confirmation_required") {
		t.Errorf("expected confirmation error, got: %s", out)
	}
}

func TestAPIKeysRevokeRequiresID(t *testing.T) {
	err := apikeysRevokeCmd.Args(apikeysRevokeCmd, []string{})
	if err == nil {
		t.Error("expected error for missing name-or-id argument")
	}
}

func TestAPIKeysRotate200(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.Method != "POST" {
			t.Errorf("expected POST, got %s", r.Method)
		}
		w.Header().Set("Content-Type", "application/json")
		fmt.Fprint(w, `{"id": "`+testHexID+`", "name": "my-key", "keyPrefix": "sk-new", "secret": "sk-newsecret", "scope": "inference", "isActive": true}`)
	}))
	defer server.Close()

	capture := setupTestYes(apikeysRotateCmd, server.URL)

	err := apikeysRotateCmd.RunE(apikeysRotateCmd, []string{testHexID})
	if err != nil {
		t.Fatalf("expected no error, got: %v", err)
	}

	out := capture()
	if !strings.Contains(out, "sk-newsecret") {
		t.Errorf("expected new secret in output, got: %s", out)
	}
}

func TestAPIKeysRotateRequiresConfirmation(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.WriteHeader(200)
	}))
	defer server.Close()

	// Set up with quiet mode so ConfirmOrSkip returns an error (no stdin needed)
	r, wPipe, _ := os.Pipe()
	old := os.Stdout
	os.Stdout = wPipe

	cfg := &client.Config{BaseURL: server.URL, OutputFmt: "json", Color: false}
	c := client.New(cfg, client.WithAPIKey("test-key"))
	ctx := context.Background()
	ctx = context.WithValue(ctx, clientKey, c)
	ctx = context.WithValue(ctx, quietKey, true)
	w := output.NewWriter(output.FormatJSON, true, true)
	ctx = context.WithValue(ctx, outputKey, w)
	apikeysRotateCmd.SetContext(ctx)

	// Don't check err here — w.Error() returns ExitError which is expected for error cases
	apikeysRotateCmd.RunE(apikeysRotateCmd, []string{testHexID})
	wPipe.Close()
	os.Stdout = old
	var buf bytes.Buffer
	io.Copy(&buf, r)
	out := buf.String()

	if !strings.Contains(out, "confirmation_required") {
		t.Errorf("expected confirmation error, got: %s", out)
	}
}

func TestAPIKeysRotateRequiresID(t *testing.T) {
	err := apikeysRotateCmd.Args(apikeysRotateCmd, []string{})
	if err == nil {
		t.Error("expected error for missing name-or-id argument")
	}
}

func TestAPIKeysGetAccess(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		fmt.Fprint(w, `{"providers": ["openai", "anthropic"], "models": ["gpt-4", "claude-3"]}`)
	}))
	defer server.Close()

	capture := setupTest(apikeysGetAccessCmd, server.URL)
	err := apikeysGetAccessCmd.RunE(apikeysGetAccessCmd, []string{testHexID})
	if err != nil {
		t.Fatalf("expected no error, got: %v", err)
	}

	out := capture()
	if !strings.Contains(out, "openai") || !strings.Contains(out, "gpt-4") {
		t.Errorf("expected access data in output, got: %s", out)
	}
}

func TestAPIKeysGetAccessRequiresID(t *testing.T) {
	err := apikeysGetAccessCmd.Args(apikeysGetAccessCmd, []string{})
	if err == nil {
		t.Error("expected error for missing name-or-id argument")
	}
}

func TestAPIKeysSetAccess(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.Method != "PUT" {
			t.Errorf("expected PUT, got %s", r.Method)
		}
		var body map[string]any
		json.NewDecoder(r.Body).Decode(&body)
		providers, ok := body["providers"].([]any)
		if !ok || len(providers) != 1 {
			t.Errorf("expected 1 provider, got %v", body["providers"])
		}
		w.Header().Set("Content-Type", "application/json")
		fmt.Fprint(w, `{"providers": ["openai"], "models": ["gpt-4"]}`)
	}))
	defer server.Close()

	capture := setupTest(apikeysSetAccessCmd, server.URL)
	apikeysSetAccessCmd.Flags().Set("providers", `["openai"]`)
	apikeysSetAccessCmd.Flags().Set("models", `["gpt-4"]`)

	err := apikeysSetAccessCmd.RunE(apikeysSetAccessCmd, []string{testHexID})
	if err != nil {
		t.Fatalf("expected no error, got: %v", err)
	}

	out := capture()
	if !strings.Contains(out, "openai") {
		t.Errorf("expected access data in output, got: %s", out)
	}
}

func TestAPIKeysSetAccessInvalidJSON(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.WriteHeader(200)
	}))
	defer server.Close()

	capture := setupTest(apikeysSetAccessCmd, server.URL)
	apikeysSetAccessCmd.Flags().Set("providers", `not-json`)

	apikeysSetAccessCmd.RunE(apikeysSetAccessCmd, []string{testHexID})

	out := capture()
	if !strings.Contains(out, "invalid_providers") {
		t.Errorf("expected parse error, got: %s", out)
	}
}

func TestAPIKeysSetAccessRequiresID(t *testing.T) {
	err := apikeysSetAccessCmd.Args(apikeysSetAccessCmd, []string{})
	if err == nil {
		t.Error("expected error for missing name-or-id argument")
	}
}

func TestAPIKeysGetPermissions(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		fmt.Fprint(w, `{"permissions": {"domain": "rw"}}`)
	}))
	defer server.Close()

	capture := setupTest(apikeysGetPermissionsCmd, server.URL)
	err := apikeysGetPermissionsCmd.RunE(apikeysGetPermissionsCmd, []string{testHexID})
	if err != nil {
		t.Fatalf("expected no error, got: %v", err)
	}

	out := capture()
	if !strings.Contains(out, "permissions") || !strings.Contains(out, "rw") {
		t.Errorf("expected permissions data in output, got: %s", out)
	}
}

func TestAPIKeysGetPermissionsRequiresID(t *testing.T) {
	err := apikeysGetPermissionsCmd.Args(apikeysGetPermissionsCmd, []string{})
	if err == nil {
		t.Error("expected error for missing name-or-id argument")
	}
}

func TestAPIKeysSetPermissions(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.Method != "PUT" {
			t.Errorf("expected PUT, got %s", r.Method)
		}
		var body map[string]any
		json.NewDecoder(r.Body).Decode(&body)
		perms, ok := body["permissions"].(map[string]any)
		if !ok || perms["domain"] != "r" {
			t.Errorf("expected permissions.domain=r, got %v", body["permissions"])
		}
		w.Header().Set("Content-Type", "application/json")
		fmt.Fprint(w, `{"permissions": {"domain": "r"}}`)
	}))
	defer server.Close()

	capture := setupTest(apikeysSetPermissionsCmd, server.URL)
	apikeysSetPermissionsCmd.Flags().Set("permissions", `{"domain": "r"}`)

	err := apikeysSetPermissionsCmd.RunE(apikeysSetPermissionsCmd, []string{testHexID})
	if err != nil {
		t.Fatalf("expected no error, got: %v", err)
	}

	out := capture()
	if !strings.Contains(out, `"domain"`) {
		t.Errorf("expected permissions in output, got: %s", out)
	}
}

func TestAPIKeysSetPermissionsRequiresID(t *testing.T) {
	err := apikeysSetPermissionsCmd.Args(apikeysSetPermissionsCmd, []string{})
	if err == nil {
		t.Error("expected error for missing name-or-id argument")
	}
}

func TestAPIKeysSubcommandsRegistered(t *testing.T) {
	cmds := apikeysCmd.Commands()
	names := make(map[string]bool)
	for _, c := range cmds {
		names[c.Name()] = true
	}
	for _, name := range []string{"list", "get", "create", "create-agent", "create-control-plane", "revoke", "rotate", "get-access", "set-access", "get-permissions", "set-permissions"} {
		if !names[name] {
			t.Errorf("expected subcommand %q to be registered", name)
		}
	}
}

func TestAPIKeysListAPIError(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		w.WriteHeader(500)
		fmt.Fprint(w, `{"error": "internal_error", "message": "Server error", "exitCode": 1}`)
	}))
	defer server.Close()

	capture := setupTest(apikeysListCmd, server.URL)
	apikeysListCmd.RunE(apikeysListCmd, nil)

	out := capture()
	if !strings.Contains(out, "internal_error") {
		t.Errorf("expected error in output, got: %s", out)
	}
}

// ==================== Phase 2: Settings Tests ====================

func TestSettingsGet(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		fmt.Fprint(w, `{"requestTimeout": 30, "healthCheckInterval": 10, "autoShutdownIdle": true, "priorityMode": "round-robin"}`)
	}))
	defer server.Close()

	capture := setupTest(settingsGetCmd, server.URL)
	err := settingsGetCmd.RunE(settingsGetCmd, nil)
	if err != nil {
		t.Fatalf("expected no error, got: %v", err)
	}

	out := capture()
	if !strings.Contains(out, "requestTimeout") || !strings.Contains(out, "round-robin") {
		t.Errorf("expected settings data in output, got: %s", out)
	}
}

func TestSettingsUpdate(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.Method != "PUT" {
			t.Errorf("expected PUT, got %s", r.Method)
		}
		var body map[string]any
		json.NewDecoder(r.Body).Decode(&body)
		if body["requestTimeout"] != float64(60) {
			t.Errorf("expected requestTimeout=60, got %v", body["requestTimeout"])
		}
		if body["autoShutdownIdle"] != false {
			t.Errorf("expected autoShutdownIdle=false, got %v", body["autoShutdownIdle"])
		}
		w.Header().Set("Content-Type", "application/json")
		fmt.Fprint(w, `{"requestTimeout": 60, "autoShutdownIdle": false}`)
	}))
	defer server.Close()

	capture := setupTest(settingsUpdateCmd, server.URL)
	settingsUpdateCmd.Flags().Set("request-timeout", "60")
	settingsUpdateCmd.Flags().Set("auto-shutdown-idle", "false")

	err := settingsUpdateCmd.RunE(settingsUpdateCmd, nil)
	if err != nil {
		t.Fatalf("expected no error, got: %v", err)
	}

	out := capture()
	if !strings.Contains(out, "requestTimeout") {
		t.Errorf("expected updated settings in output, got: %s", out)
	}
}

func TestSettingsUpdateNoChanges(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		w.WriteHeader(200)
		fmt.Fprint(w, `{}`)
	}))
	defer server.Close()

	// Create a fresh command to avoid flag state leakage from prior tests
	freshCmd := &cobra.Command{
		Use:   "update",
		Short: "Update settings",
		RunE:  settingsUpdateCmd.RunE,
	}
	freshCmd.Flags().Float64("request-timeout", 0, "")
	freshCmd.Flags().Bool("auto-shutdown-idle", false, "")

	capture := setupTest(freshCmd, server.URL)

	freshCmd.RunE(freshCmd, nil)

	out := capture()
	if !strings.Contains(out, "no_changes") {
		t.Errorf("expected no_changes error, got: %s", out)
	}
}

func TestSettingsGetAPIError(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		w.WriteHeader(403)
		fmt.Fprint(w, `{"error": "forbidden", "message": "Admin access required", "exitCode": 2}`)
	}))
	defer server.Close()

	capture := setupTest(settingsGetCmd, server.URL)
	settingsGetCmd.RunE(settingsGetCmd, nil)

	out := capture()
	if !strings.Contains(out, "forbidden") {
		t.Errorf("expected error in output, got: %s", out)
	}
}

func TestSettingsUpdateBoolFlags(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		var body map[string]any
		json.NewDecoder(r.Body).Decode(&body)
		if body["enableBenchmarking"] != true {
			t.Errorf("expected enableBenchmarking=true, got %v", body["enableBenchmarking"])
		}
		if body["batchDrain"] != true {
			t.Errorf("expected batchDrain=true, got %v", body["batchDrain"])
		}
		w.Header().Set("Content-Type", "application/json")
		fmt.Fprint(w, `{"enableBenchmarking": true, "batchDrain": true}`)
	}))
	defer server.Close()

	capture := setupTest(settingsUpdateCmd, server.URL)
	settingsUpdateCmd.Flags().Set("enable-benchmarking", "true")
	settingsUpdateCmd.Flags().Set("batch-drain", "true")

	err := settingsUpdateCmd.RunE(settingsUpdateCmd, nil)
	if err != nil {
		t.Fatalf("expected no error, got: %v", err)
	}

	out := capture()
	if !strings.Contains(out, "enableBenchmarking") {
		t.Errorf("expected settings in output, got: %s", out)
	}
}

func TestSettingsUpdateIntFlags(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		var body map[string]any
		json.NewDecoder(r.Body).Decode(&body)
		if body["maxQueueDepth"] != float64(100) {
			t.Errorf("expected maxQueueDepth=100, got %v", body["maxQueueDepth"])
		}
		w.Header().Set("Content-Type", "application/json")
		fmt.Fprint(w, `{"maxQueueDepth": 100}`)
	}))
	defer server.Close()

	capture := setupTest(settingsUpdateCmd, server.URL)
	settingsUpdateCmd.Flags().Set("max-queue-depth", "100")

	err := settingsUpdateCmd.RunE(settingsUpdateCmd, nil)
	if err != nil {
		t.Fatalf("expected no error, got: %v", err)
	}

	out := capture()
	if !strings.Contains(out, "maxQueueDepth") {
		t.Errorf("expected settings in output, got: %s", out)
	}
}

func TestSettingsSubcommandsRegistered(t *testing.T) {
	cmds := settingsCmd.Commands()
	names := make(map[string]bool)
	for _, c := range cmds {
		names[c.Name()] = true
	}
	for _, name := range []string{"get", "set", "update"} {
		if !names[name] {
			t.Errorf("expected subcommand %q to be registered", name)
		}
	}
}

// ==================== Phase 2: Queue Tests ====================

func TestQueueSnapshot(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		fmt.Fprint(w, `{"currentSlot": "slot-1", "processing": 2, "waiting": 5, "recentCompleted": 10, "activeTransitions": 1, "skipsUsed": 0, "skipsRemaining": 3}`)
	}))
	defer server.Close()

	capture := setupTest(queueSnapshotCmd, server.URL)
	err := queueSnapshotCmd.RunE(queueSnapshotCmd, nil)
	if err != nil {
		t.Fatalf("expected no error, got: %v", err)
	}

	out := capture()
	if !strings.Contains(out, "processing") || !strings.Contains(out, "waiting") {
		t.Errorf("expected snapshot data in output, got: %s", out)
	}
}

func TestQueueSnapshotAPIError(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		w.WriteHeader(503)
		fmt.Fprint(w, `{"error": "service_unavailable", "message": "Queue not ready", "exitCode": 1}`)
	}))
	defer server.Close()

	capture := setupTest(queueSnapshotCmd, server.URL)
	queueSnapshotCmd.RunE(queueSnapshotCmd, nil)

	out := capture()
	if !strings.Contains(out, "service_unavailable") {
		t.Errorf("expected error in output, got: %s", out)
	}
}

func TestQueueCancel200(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.Method != "DELETE" {
			t.Errorf("expected DELETE, got %s", r.Method)
		}
		w.Header().Set("Content-Type", "application/json")
		fmt.Fprint(w, `{"cancelled": true}`)
	}))
	defer server.Close()

	capture := setupTest(queueCancelCmd, server.URL)
	err := queueCancelCmd.RunE(queueCancelCmd, []string{"item-123"})
	if err != nil {
		t.Fatalf("expected no error, got: %v", err)
	}

	out := capture()
	if !strings.Contains(out, "cancelled") || !strings.Contains(out, "true") {
		t.Errorf("expected cancellation result in output, got: %s", out)
	}
}

func TestQueueCancelRequiresID(t *testing.T) {
	err := queueCancelCmd.Args(queueCancelCmd, []string{})
	if err == nil {
		t.Error("expected error for missing ID argument")
	}
}

func TestQueueReleaseHold200(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.Method != "POST" {
			t.Errorf("expected POST, got %s", r.Method)
		}
		if !strings.Contains(r.URL.Path, "/hold/release") {
			t.Errorf("expected /hold/release path, got %s", r.URL.Path)
		}
		w.Header().Set("Content-Type", "application/json")
		fmt.Fprint(w, `{"released": true}`)
	}))
	defer server.Close()

	capture := setupTest(queueReleaseHoldCmd, server.URL)
	err := queueReleaseHoldCmd.RunE(queueReleaseHoldCmd, []string{"target-456"})
	if err != nil {
		t.Fatalf("expected no error, got: %v", err)
	}

	out := capture()
	if !strings.Contains(out, "released") || !strings.Contains(out, "true") {
		t.Errorf("expected release result in output, got: %s", out)
	}
}

func TestQueueReleaseHoldRequiresID(t *testing.T) {
	err := queueReleaseHoldCmd.Args(queueReleaseHoldCmd, []string{})
	if err == nil {
		t.Error("expected error for missing ID argument")
	}
}

func TestQueueSubcommandsRegistered(t *testing.T) {
	cmds := queueCmd.Commands()
	names := make(map[string]bool)
	for _, c := range cmds {
		names[c.Name()] = true
	}
	for _, name := range []string{"list", "snapshot", "cancel", "release-hold"} {
		if !names[name] {
			t.Errorf("expected subcommand %q to be registered", name)
		}
	}
}

func TestQueueCancelAPIError(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		w.WriteHeader(404)
		fmt.Fprint(w, `{"error": "not_found", "message": "Item not found", "exitCode": 1}`)
	}))
	defer server.Close()

	capture := setupTest(queueCancelCmd, server.URL)
	queueCancelCmd.RunE(queueCancelCmd, []string{"nonexistent"})

	out := capture()
	if !strings.Contains(out, "not_found") {
		t.Errorf("expected error in output, got: %s", out)
	}
}

func TestQueueReleaseHoldAPIError(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		w.WriteHeader(409)
		fmt.Fprint(w, `{"error": "conflict", "message": "No hold to release", "exitCode": 1}`)
	}))
	defer server.Close()

	capture := setupTest(queueReleaseHoldCmd, server.URL)
	queueReleaseHoldCmd.RunE(queueReleaseHoldCmd, []string{"target-789"})

	out := capture()
	if !strings.Contains(out, "conflict") {
		t.Errorf("expected error in output, got: %s", out)
	}
}

func TestQueueSnapshotJSONOutput(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		fmt.Fprint(w, `{"currentSlot": "slot-1", "processing": 2, "waiting": 5, "recentCompleted": 10}`)
	}))
	defer server.Close()

	capture := setupTest(queueSnapshotCmd, server.URL)
	err := queueSnapshotCmd.RunE(queueSnapshotCmd, nil)
	if err != nil {
		t.Fatalf("expected no error, got: %v", err)
	}

	out := capture()
	var result map[string]any
	if err := json.Unmarshal([]byte(out), &result); err != nil {
		t.Errorf("expected valid JSON, got error: %v, output: %s", err, out)
	}
	if _, ok := result["processing"]; !ok {
		t.Errorf("expected processing key in JSON output, got: %s", out)
	}
}

// ==================== Phase 2: Additional Error Handling Tests ====================

func TestUsersListAPIError(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		w.WriteHeader(401)
		fmt.Fprint(w, `{"error": "unauthorized", "message": "Admin access required", "exitCode": 2}`)
	}))
	defer server.Close()

	capture := setupTest(usersListCmd, server.URL)
	usersListCmd.RunE(usersListCmd, nil)

	out := capture()
	if !strings.Contains(out, "unauthorized") {
		t.Errorf("expected error in output, got: %s", out)
	}
}

func TestUsersResetPasswordAPIError(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		w.WriteHeader(404)
		fmt.Fprint(w, `{"error": "not_found", "message": "User not found", "exitCode": 1}`)
	}))
	defer server.Close()

	capture := setupTest(usersResetPasswordCmd, server.URL)
	usersResetPasswordCmd.Flags().Set("new-password", "newpass")

	usersResetPasswordCmd.RunE(usersResetPasswordCmd, []string{testHexID})

	out := capture()
	if !strings.Contains(out, "not_found") {
		t.Errorf("expected error in output, got: %s", out)
	}
}

func TestAPIKeysGetAPIError(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		w.WriteHeader(404)
		fmt.Fprint(w, `{"error": "not_found", "message": "Key not found", "exitCode": 1}`)
	}))
	defer server.Close()

	capture := setupTest(apikeysGetCmd, server.URL)
	apikeysGetCmd.RunE(apikeysGetCmd, []string{testHexID})

	out := capture()
	if !strings.Contains(out, "not_found") {
		t.Errorf("expected error in output, got: %s", out)
	}
}

func TestAPIKeysCreateInvalidPermissions(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.WriteHeader(200)
	}))
	defer server.Close()

	capture := setupTest(apikeysCreateControlPlaneCmd, server.URL)
	apikeysCreateControlPlaneCmd.Flags().Set("name", "bad-key")
	apikeysCreateControlPlaneCmd.Flags().Set("permissions", `not-json`)

	apikeysCreateControlPlaneCmd.RunE(apikeysCreateControlPlaneCmd, nil)

	out := capture()
	if !strings.Contains(out, "invalid_permissions") {
		t.Errorf("expected parse error, got: %s", out)
	}
}

func TestAPIKeysSetPermissionsInvalidJSON(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.WriteHeader(200)
	}))
	defer server.Close()

	capture := setupTest(apikeysSetPermissionsCmd, server.URL)
	apikeysSetPermissionsCmd.Flags().Set("permissions", `invalid`)

	apikeysSetPermissionsCmd.RunE(apikeysSetPermissionsCmd, []string{testHexID})

	out := capture()
	if !strings.Contains(out, "invalid_permissions") {
		t.Errorf("expected parse error, got: %s", out)
	}
}

func TestSettingsUpdateStringFlags(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		var body map[string]any
		json.NewDecoder(r.Body).Decode(&body)
		if body["priorityMode"] != "least-connections" {
			t.Errorf("expected priorityMode=least-connections, got %v", body["priorityMode"])
		}
		w.Header().Set("Content-Type", "application/json")
		fmt.Fprint(w, `{"priorityMode": "least-connections"}`)
	}))
	defer server.Close()

	capture := setupTest(settingsUpdateCmd, server.URL)
	settingsUpdateCmd.Flags().Set("priority-mode", "least-connections")

	err := settingsUpdateCmd.RunE(settingsUpdateCmd, nil)
	if err != nil {
		t.Fatalf("expected no error, got: %v", err)
	}

	out := capture()
	if !strings.Contains(out, "priorityMode") {
		t.Errorf("expected settings in output, got: %s", out)
	}
}

func TestSettingsUpdateFloatFlags(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		var body map[string]any
		json.NewDecoder(r.Body).Decode(&body)
		if body["idleTimeout"] != float64(300) {
			t.Errorf("expected idleTimeout=300, got %v", body["idleTimeout"])
		}
		w.Header().Set("Content-Type", "application/json")
		fmt.Fprint(w, `{"idleTimeout": 300}`)
	}))
	defer server.Close()

	capture := setupTest(settingsUpdateCmd, server.URL)
	settingsUpdateCmd.Flags().Set("idle-timeout", "300")

	err := settingsUpdateCmd.RunE(settingsUpdateCmd, nil)
	if err != nil {
		t.Fatalf("expected no error, got: %v", err)
	}

	out := capture()
	if !strings.Contains(out, "idleTimeout") {
		t.Errorf("expected settings in output, got: %s", out)
	}
}

// ==================== Phase 3: Benchmarks Tests ====================

func TestBenchmarksRun(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.Method != "POST" {
			t.Errorf("expected POST, got %s", r.Method)
		}
		if r.URL.Path != "/api/benchmarks/run" {
			t.Errorf("expected /api/benchmarks/run, got %s", r.URL.Path)
		}
		w.Header().Set("Content-Type", "application/json")
		fmt.Fprint(w, `{"id": "bm-1", "modelId": "m1", "tokensPerSecond": 42.5, "latencyMs": 1500, "status": "completed"}`)
	}))
	defer server.Close()

	capture := setupTest(benchmarksRunCmd, server.URL)
	// Use testHexID to bypass name resolution.
	err := benchmarksRunCmd.RunE(benchmarksRunCmd, []string{testHexID})
	if err != nil {
		t.Fatalf("expected no error, got: %v", err)
	}

	out := capture()
	if !strings.Contains(out, "bm-1") {
		t.Errorf("expected benchmark data in output, got: %s", out)
	}
}

func TestBenchmarksRunWithFlags(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.URL.Path != "/api/benchmarks/run" {
			t.Errorf("expected /api/benchmarks/run, got %s", r.URL.Path)
		}
		var body map[string]any
		json.NewDecoder(r.Body).Decode(&body)
		if body["modelId"] != testHexID {
			t.Errorf("expected modelId=%s in body, got %v", testHexID, body["modelId"])
		}
		if body["prompt"] != "test prompt" {
			t.Errorf("expected prompt='test prompt', got %v", body["prompt"])
		}
		w.Header().Set("Content-Type", "application/json")
		fmt.Fprint(w, `{"id": "bm-2", "status": "completed"}`)
	}))
	defer server.Close()

	capture := setupTest(benchmarksRunCmd, server.URL)
	benchmarksRunCmd.Flags().Set("prompt", "test prompt")

	err := benchmarksRunCmd.RunE(benchmarksRunCmd, []string{testHexID})
	if err != nil {
		t.Fatalf("expected no error, got: %v", err)
	}

	out := capture()
	if !strings.Contains(out, "bm-2") {
		t.Errorf("expected benchmark data in output, got: %s", out)
	}
}

func TestBenchmarksList(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		fmt.Fprint(w, `[{"id": "bm-1", "modelId": "m1", "tokensPerSecond": 42.5, "latencyMs": 1500, "status": "completed", "createdAt": "2025-01-01T00:00:00Z"}]`)
	}))
	defer server.Close()

	capture := setupTest(benchmarksListCmd, server.URL)
	err := benchmarksListCmd.RunE(benchmarksListCmd, nil)
	if err != nil {
		t.Fatalf("expected no error, got: %v", err)
	}

	out := capture()
	if !strings.Contains(out, "bm-1") {
		t.Errorf("expected benchmark data in output, got: %s", out)
	}
}

func TestBenchmarksListTable(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		fmt.Fprint(w, `[{"id": "bm-1", "modelId": "m1", "tokensPerSecond": 42.5, "latencyMs": 1500, "status": "completed", "createdAt": "2025-01-01T00:00:00Z"}]`)
	}))
	defer server.Close()

	capture := setupTestFmt(benchmarksListCmd, server.URL, output.FormatTable)
	err := benchmarksListCmd.RunE(benchmarksListCmd, nil)
	if err != nil {
		t.Fatalf("expected no error, got: %v", err)
	}

	out := capture()
	if !strings.Contains(out, "TOKENS/S") || !strings.Contains(out, "42.5") {
		t.Errorf("expected table headers and data, got: %s", out)
	}
}

func TestBenchmarksRunRequiresID(t *testing.T) {
	err := benchmarksRunCmd.Args(benchmarksRunCmd, []string{})
	if err == nil {
		t.Error("expected error for missing model ID argument")
	}
}

func TestBenchmarksSubcommandsRegistered(t *testing.T) {
	cmds := benchmarksCmd.Commands()
	names := make(map[string]bool)
	for _, c := range cmds {
		names[c.Name()] = true
	}
	for _, name := range []string{"run", "get", "list", "compare"} {
		if !names[name] {
			t.Errorf("expected subcommand %q to be registered", name)
		}
	}
}

// ==================== Phase 3: Prompts Tests ====================

func TestPromptsList(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		fmt.Fprint(w, `[{"id": "p1", "name": "system-prompt", "text": "You are helpful.", "isDefault": true, "currentVersion": 3, "createdAt": "2025-01-01T00:00:00Z", "updatedAt": "2025-06-01T00:00:00Z"}]`)
	}))
	defer server.Close()

	capture := setupTest(promptsListCmd, server.URL)
	err := promptsListCmd.RunE(promptsListCmd, nil)
	if err != nil {
		t.Fatalf("expected no error, got: %v", err)
	}

	out := capture()
	if !strings.Contains(out, "system-prompt") || !strings.Contains(out, "yes") {
		t.Errorf("expected prompt data in output, got: %s", out)
	}
}

func TestPromptsListTable(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		fmt.Fprint(w, `[{"id": "p1", "name": "system-prompt", "isDefault": true, "currentVersion": 3, "updatedAt": "2025-06-01T00:00:00Z"}]`)
	}))
	defer server.Close()

	capture := setupTestFmt(promptsListCmd, server.URL, output.FormatTable)
	err := promptsListCmd.RunE(promptsListCmd, nil)
	if err != nil {
		t.Fatalf("expected no error, got: %v", err)
	}

	out := capture()
	if !strings.Contains(out, "NAME") || !strings.Contains(out, "system-prompt") {
		t.Errorf("expected table headers and data, got: %s", out)
	}
}

func TestPromptsGet(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		fmt.Fprint(w, `{"id": "p1", "name": "system-prompt", "text": "You are helpful."}`)
	}))
	defer server.Close()

	capture := setupTest(promptsGetCmd, server.URL)
	err := promptsGetCmd.RunE(promptsGetCmd, []string{testHexID})
	if err != nil {
		t.Fatalf("expected no error, got: %v", err)
	}

	out := capture()
	if !strings.Contains(out, "system-prompt") {
		t.Errorf("expected prompt name in output, got: %s", out)
	}
}

func TestPromptsCreate(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.Method != "POST" {
			t.Errorf("expected POST, got %s", r.Method)
		}
		var body map[string]any
		json.NewDecoder(r.Body).Decode(&body)
		if body["name"] != "new-prompt" {
			t.Errorf("expected name=new-prompt, got %v", body["name"])
		}
		w.Header().Set("Content-Type", "application/json")
		w.WriteHeader(201)
		fmt.Fprint(w, `{"id": "p2", "name": "new-prompt", "text": "test"}`)
	}))
	defer server.Close()

	capture := setupTest(promptsCreateCmd, server.URL)
	promptsCreateCmd.Flags().Set("name", "new-prompt")
	promptsCreateCmd.Flags().Set("text", "test")

	err := promptsCreateCmd.RunE(promptsCreateCmd, nil)
	if err != nil {
		t.Fatalf("expected no error, got: %v", err)
	}

	out := capture()
	if !strings.Contains(out, "new-prompt") {
		t.Errorf("expected new-prompt in output, got: %s", out)
	}
}

func TestPromptsUpdate(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.Method != "PUT" {
			t.Errorf("expected PUT, got %s", r.Method)
		}
		var body map[string]any
		json.NewDecoder(r.Body).Decode(&body)
		if body["name"] != "updated-prompt" {
			t.Errorf("expected name=updated-prompt, got %v", body["name"])
		}
		w.Header().Set("Content-Type", "application/json")
		fmt.Fprint(w, `{"id": "p1", "name": "updated-prompt"}`)
	}))
	defer server.Close()

	capture := setupTest(promptsUpdateCmd, server.URL)
	promptsUpdateCmd.Flags().Set("name", "updated-prompt")

	err := promptsUpdateCmd.RunE(promptsUpdateCmd, []string{testHexID})
	if err != nil {
		t.Fatalf("expected no error, got: %v", err)
	}

	out := capture()
	if !strings.Contains(out, "updated-prompt") {
		t.Errorf("expected updated-prompt in output, got: %s", out)
	}
}

func TestPromptsDelete204(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.Method != "DELETE" {
			t.Errorf("expected DELETE, got %s", r.Method)
		}
		w.WriteHeader(204)
	}))
	defer server.Close()

	capture := setupTestYes(promptsDeleteCmd, server.URL)

	err := promptsDeleteCmd.RunE(promptsDeleteCmd, []string{testHexID})
	if err != nil {
		t.Fatalf("expected no error, got: %v", err)
	}

	out := capture()
	if !strings.Contains(out, "Deleted prompt") {
		t.Errorf("expected deletion message, got: %s", out)
	}
}

func TestPromptsDeleteRequiresConfirmation(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.WriteHeader(204)
	}))
	defer server.Close()

	// Set up with quiet mode so ConfirmOrSkip returns an error (no stdin needed)
	r, wPipe, _ := os.Pipe()
	old := os.Stdout
	os.Stdout = wPipe

	cfg := &client.Config{BaseURL: server.URL, OutputFmt: "json", Color: false}
	c := client.New(cfg, client.WithAPIKey("test-key"))
	ctx := context.Background()
	ctx = context.WithValue(ctx, clientKey, c)
	ctx = context.WithValue(ctx, quietKey, true)
	w := output.NewWriter(output.FormatJSON, true, true)
	ctx = context.WithValue(ctx, outputKey, w)
	promptsDeleteCmd.SetContext(ctx)

	// Don't check err here — w.Error() returns ExitError which is expected for error cases
	promptsDeleteCmd.RunE(promptsDeleteCmd, []string{testHexID})
	wPipe.Close()
	os.Stdout = old
	var buf bytes.Buffer
	io.Copy(&buf, r)
	out := buf.String()

	if !strings.Contains(out, "confirmation_required") {
		t.Errorf("expected confirmation error, got: %s", out)
	}
}

func TestPromptsSetDefault(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.Method != "POST" {
			t.Errorf("expected POST, got %s", r.Method)
		}
		w.Header().Set("Content-Type", "application/json")
		fmt.Fprint(w, `{"id": "p1", "name": "system-prompt", "isDefault": true}`)
	}))
	defer server.Close()

	capture := setupTest(promptsSetDefaultCmd, server.URL)
	err := promptsSetDefaultCmd.RunE(promptsSetDefaultCmd, []string{testHexID})
	if err != nil {
		t.Fatalf("expected no error, got: %v", err)
	}

	out := capture()
	if !strings.Contains(out, "isDefault") {
		t.Errorf("expected isDefault in output, got: %s", out)
	}
}

func TestPromptsVersions(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		fmt.Fprint(w, `[{"id": "v1", "promptId": "p1", "version": 1, "text": "v1 text", "createdAt": "2025-01-01T00:00:00Z"}, {"id": "v2", "promptId": "p1", "version": 2, "text": "v2 text", "createdAt": "2025-06-01T00:00:00Z"}]`)
	}))
	defer server.Close()

	capture := setupTest(promptsVersionsCmd, server.URL)
	err := promptsVersionsCmd.RunE(promptsVersionsCmd, []string{testHexID})
	if err != nil {
		t.Fatalf("expected no error, got: %v", err)
	}

	out := capture()
	if !strings.Contains(out, "1") || !strings.Contains(out, "2") {
		t.Errorf("expected version data in output, got: %s", out)
	}
}

func TestPromptsVersion(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if !strings.Contains(r.URL.Path, "/versions/2") {
			t.Errorf("expected /versions/2 path, got: %s", r.URL.Path)
		}
		w.Header().Set("Content-Type", "application/json")
		fmt.Fprint(w, `{"id": "v2", "promptId": "p1", "version": 2, "text": "v2 text"}`)
	}))
	defer server.Close()

	capture := setupTest(promptsVersionCmd, server.URL)
	err := promptsVersionCmd.RunE(promptsVersionCmd, []string{testHexID, "2"})
	if err != nil {
		t.Fatalf("expected no error, got: %v", err)
	}

	out := capture()
	if !strings.Contains(out, "v2 text") {
		t.Errorf("expected version text in output, got: %s", out)
	}
}

func TestPromptsVersionRequiresTwoArgs(t *testing.T) {
	err := promptsVersionCmd.Args(promptsVersionCmd, []string{"p1"})
	if err == nil {
		t.Error("expected error for missing version argument")
	}
}

func TestPromptsRollback(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.Method != "POST" {
			t.Errorf("expected POST, got %s", r.Method)
		}
		var body map[string]any
		json.NewDecoder(r.Body).Decode(&body)
		if body["version"] != float64(1) {
			t.Errorf("expected version=1, got %v", body["version"])
		}
		w.Header().Set("Content-Type", "application/json")
		fmt.Fprint(w, `{"id": "p1", "name": "system-prompt", "currentVersion": 1}`)
	}))
	defer server.Close()

	capture := setupTest(promptsRollbackCmd, server.URL)
	promptsRollbackCmd.Flags().Set("version", "1")

	err := promptsRollbackCmd.RunE(promptsRollbackCmd, []string{testHexID})
	if err != nil {
		t.Fatalf("expected no error, got: %v", err)
	}

	out := capture()
	if !strings.Contains(out, "currentVersion") {
		t.Errorf("expected rollback result in output, got: %s", out)
	}
}

func TestPromptsGetRequiresID(t *testing.T) {
	err := promptsGetCmd.Args(promptsGetCmd, []string{})
	if err == nil {
		t.Error("expected error for missing ID argument")
	}
}

func TestPromptsDeleteRequiresID(t *testing.T) {
	err := promptsDeleteCmd.Args(promptsDeleteCmd, []string{})
	if err == nil {
		t.Error("expected error for missing ID argument")
	}
}

func TestPromptsSubcommandsRegistered(t *testing.T) {
	cmds := promptsCmd.Commands()
	names := make(map[string]bool)
	for _, c := range cmds {
		names[c.Name()] = true
	}
	for _, name := range []string{"list", "get", "create", "update", "delete", "set-default", "versions", "version", "diff", "rollback"} {
		if !names[name] {
			t.Errorf("expected subcommand %q to be registered", name)
		}
	}
}

func TestPromptsCreateWithFile(t *testing.T) {
	// Reset flags from previous tests
	promptsCreateCmd.Flags().Set("text", "")
	promptsCreateCmd.Flags().Set("file", "")
	promptsCreateCmd.Flags().Set("stdin", "false")
	promptsCreateCmd.Flags().Set("edit", "false")
	promptsCreateCmd.Flags().Set("name", "")

	// Create a temp file with test content
	tmpFile, err := os.CreateTemp("", "prompt-test-*.txt")
	if err != nil {
		t.Fatalf("failed to create temp file: %v", err)
	}
	defer os.Remove(tmpFile.Name())
	tmpFile.WriteString("Hello from file")
	tmpFile.Close()

	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.Method != "POST" {
			t.Errorf("expected POST, got %s", r.Method)
		}
		var body map[string]any
		json.NewDecoder(r.Body).Decode(&body)
		if body["name"] != "file-prompt" {
			t.Errorf("expected name=file-prompt, got %v", body["name"])
		}
		if body["text"] != "Hello from file" {
			t.Errorf("expected text='Hello from file', got %v", body["text"])
		}
		w.Header().Set("Content-Type", "application/json")
		w.WriteHeader(201)
		fmt.Fprint(w, `{"id": "p3", "name": "file-prompt", "text": "Hello from file"}`)
	}))
	defer server.Close()

	capture := setupTest(promptsCreateCmd, server.URL)
	promptsCreateCmd.Flags().Set("name", "file-prompt")
	promptsCreateCmd.Flags().Set("file", tmpFile.Name())

	err = promptsCreateCmd.RunE(promptsCreateCmd, nil)
	if err != nil {
		t.Fatalf("expected no error, got: %v", err)
	}

	promptsCreateCmd.Flags().Set("file", "")
	promptsCreateCmd.Flags().Set("name", "")

	out := capture()
	if !strings.Contains(out, "file-prompt") {
		t.Errorf("expected file-prompt in output, got: %s", out)
	}
}

func TestPromptsCreateWithStdin(t *testing.T) {
	// Reset flags from previous tests
	promptsCreateCmd.Flags().Set("text", "")
	promptsCreateCmd.Flags().Set("file", "")
	promptsCreateCmd.Flags().Set("stdin", "false")
	promptsCreateCmd.Flags().Set("edit", "false")
	promptsCreateCmd.Flags().Set("name", "")

	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		var body map[string]any
		json.NewDecoder(r.Body).Decode(&body)
		if body["text"] != "Hello from stdin" {
			t.Errorf("expected text='Hello from stdin', got %v", body["text"])
		}
		w.Header().Set("Content-Type", "application/json")
		w.WriteHeader(201)
		fmt.Fprint(w, `{"id": "p4", "name": "stdin-prompt", "text": "Hello from stdin"}`)
	}))
	defer server.Close()

	// Mock stdin
	oldStdin := os.Stdin
	r, w, _ := os.Pipe()
	os.Stdin = r
	fmt.Fprint(w, "Hello from stdin")
	w.Close()
	defer func() { os.Stdin = oldStdin }()

	capture := setupTest(promptsCreateCmd, server.URL)
	promptsCreateCmd.Flags().Set("name", "stdin-prompt")
	promptsCreateCmd.Flags().Set("stdin", "true")

	err := promptsCreateCmd.RunE(promptsCreateCmd, nil)
	if err != nil {
		t.Fatalf("expected no error, got: %v", err)
	}

	promptsCreateCmd.Flags().Set("stdin", "false")
	promptsCreateCmd.Flags().Set("name", "")

	out := capture()
	if !strings.Contains(out, "stdin-prompt") {
		t.Errorf("expected stdin-prompt in output, got: %s", out)
	}
}

func TestPromptsCreateTextFileConflict(t *testing.T) {
	// Reset flags from previous tests
	promptsCreateCmd.Flags().Set("text", "")
	promptsCreateCmd.Flags().Set("file", "")
	promptsCreateCmd.Flags().Set("stdin", "false")
	promptsCreateCmd.Flags().Set("edit", "false")
	promptsCreateCmd.Flags().Set("name", "")

	// Create a temp file
	tmpFile, err := os.CreateTemp("", "prompt-test-*.txt")
	if err != nil {
		t.Fatalf("failed to create temp file: %v", err)
	}
	defer os.Remove(tmpFile.Name())
	tmpFile.WriteString("file content")
	tmpFile.Close()

	capture := setupTest(promptsCreateCmd, "http://localhost:1")
	promptsCreateCmd.Flags().Set("name", "conflict-prompt")
	promptsCreateCmd.Flags().Set("text", "text content")
	promptsCreateCmd.Flags().Set("file", tmpFile.Name())

	err = promptsCreateCmd.RunE(promptsCreateCmd, nil)
	if err == nil {
		t.Error("expected error when both --text and --file are specified")
	}

	promptsCreateCmd.Flags().Set("text", "")
	promptsCreateCmd.Flags().Set("file", "")
	promptsCreateCmd.Flags().Set("name", "")

	_ = capture()
}

func TestPromptsDiff(t *testing.T) {
	// Reset flags from previous tests
	promptsDiffCmd.Flags().Set("from", "0")
	promptsDiffCmd.Flags().Set("to", "0")
	promptsDiffCmd.Flags().Set("output", "")

	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		if strings.Contains(r.URL.Path, "/versions/1") {
			fmt.Fprint(w, `{"id": "v1", "promptId": "p1", "version": 1, "text": "line1\nline2\nline3"}`)
		} else if strings.Contains(r.URL.Path, "/versions/2") {
			fmt.Fprint(w, `{"id": "v2", "promptId": "p1", "version": 2, "text": "line1\nmodified line\nline3\nnew line"}`)
		}
	}))
	defer server.Close()

	capture := setupTest(promptsDiffCmd, server.URL)
	promptsDiffCmd.Flags().Set("from", "1")
	promptsDiffCmd.Flags().Set("to", "2")

	err := promptsDiffCmd.RunE(promptsDiffCmd, []string{testHexID})
	if err != nil {
		t.Fatalf("expected no error, got: %v", err)
	}

	promptsDiffCmd.Flags().Set("from", "0")
	promptsDiffCmd.Flags().Set("to", "0")

	out := capture()
	if !strings.Contains(out, "--- version 1") || !strings.Contains(out, "+++ version 2") {
		t.Errorf("expected diff headers, got: %s", out)
	}
	if !strings.Contains(out, "-line2") || !strings.Contains(out, "+modified line") || !strings.Contains(out, "+new line") {
		t.Errorf("expected diff content with +/- lines, got: %s", out)
	}
}

// ==================== Phase 3: Metrics Tests ====================

func TestMetricsUsage(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		fmt.Fprint(w, `{"items": [{"timestamp": "2025-06-01", "provider": "openai", "model": "gpt-4", "promptTokens": 100, "completionTokens": 50, "latencyMs": 1200}], "total": 1, "page": 1, "pageSize": 20}`)
	}))
	defer server.Close()

	capture := setupTest(metricsUsageCmd, server.URL)
	err := metricsUsageCmd.RunE(metricsUsageCmd, nil)
	if err != nil {
		t.Fatalf("expected no error, got: %v", err)
	}

	out := capture()
	if !strings.Contains(out, "openai") || !strings.Contains(out, "gpt-4") {
		t.Errorf("expected usage data in output, got: %s", out)
	}
}

func TestMetricsSummary(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		fmt.Fprint(w, `[{"bucket": "2025-06-01T00:00:00Z", "requests": 42, "totalTokens": 12345, "avgLatencyMs": 1500}]`)
	}))
	defer server.Close()

	capture := setupTest(metricsSummaryCmd, server.URL)
	err := metricsSummaryCmd.RunE(metricsSummaryCmd, nil)
	if err != nil {
		t.Fatalf("expected no error, got: %v", err)
	}

	out := capture()
	if !strings.Contains(out, "42") {
		t.Errorf("expected summary data in output, got: %s", out)
	}
}

func TestMetricsModels(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		fmt.Fprint(w, `[{"model": "gpt-4", "requests": 100, "promptTokens": 5000, "completionTokens": 3000, "avgLatencyMs": 1200}]`)
	}))
	defer server.Close()

	capture := setupTest(metricsModelsCmd, server.URL)
	err := metricsModelsCmd.RunE(metricsModelsCmd, nil)
	if err != nil {
		t.Fatalf("expected no error, got: %v", err)
	}

	out := capture()
	if !strings.Contains(out, "gpt-4") {
		t.Errorf("expected model data in output, got: %s", out)
	}
}

func TestMetricsProviders(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		fmt.Fprint(w, `[{"provider": "openai", "requests": 200, "totalTokens": 50000}]`)
	}))
	defer server.Close()

	capture := setupTest(metricsProvidersCmd, server.URL)
	err := metricsProvidersCmd.RunE(metricsProvidersCmd, nil)
	if err != nil {
		t.Fatalf("expected no error, got: %v", err)
	}

	out := capture()
	if !strings.Contains(out, "openai") {
		t.Errorf("expected provider data in output, got: %s", out)
	}
}

func TestMetricsTotals(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		fmt.Fprint(w, `{"totalRequests": 1000, "promptTokens": 50000, "completionTokens": 30000, "avgLatencyMs": 1200}`)
	}))
	defer server.Close()

	capture := setupTest(metricsTotalsCmd, server.URL)
	err := metricsTotalsCmd.RunE(metricsTotalsCmd, nil)
	if err != nil {
		t.Fatalf("expected no error, got: %v", err)
	}

	out := capture()
	if !strings.Contains(out, "1000") {
		t.Errorf("expected totals data in output, got: %s", out)
	}
}

func TestMetricsLatencyBands(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		fmt.Fprint(w, `[{"band": "<500ms", "count": 500}, {"band": "500-1000ms", "count": 300}]`)
	}))
	defer server.Close()

	capture := setupTest(metricsLatencyBandsCmd, server.URL)
	err := metricsLatencyBandsCmd.RunE(metricsLatencyBandsCmd, nil)
	if err != nil {
		t.Fatalf("expected no error, got: %v", err)
	}

	out := capture()
	if !strings.Contains(out, "500ms") {
		t.Errorf("expected latency band data in output, got: %s", out)
	}
}

func TestMetricsApiKeys(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		fmt.Fprint(w, `[{"keyId": "k1", "name": "my-key", "requests": 100, "totalTokens": 5000}]`)
	}))
	defer server.Close()

	capture := setupTest(metricsApiKeysCmd, server.URL)
	err := metricsApiKeysCmd.RunE(metricsApiKeysCmd, nil)
	if err != nil {
		t.Fatalf("expected no error, got: %v", err)
	}

	out := capture()
	if !strings.Contains(out, "my-key") {
		t.Errorf("expected key data in output, got: %s", out)
	}
}

func TestMetricsApiKeyUsage(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if !strings.Contains(r.URL.Path, "/api-keys/k1/usage") {
			t.Errorf("expected /api-keys/k1/usage path, got: %s", r.URL.Path)
		}
		w.Header().Set("Content-Type", "application/json")
		fmt.Fprint(w, `{"totalRequests": 100, "totalTokens": 5000}`)
	}))
	defer server.Close()

	capture := setupTest(metricsApiKeyUsageCmd, server.URL)
	err := metricsApiKeyUsageCmd.RunE(metricsApiKeyUsageCmd, []string{"k1"})
	if err != nil {
		t.Fatalf("expected no error, got: %v", err)
	}

	out := capture()
	if !strings.Contains(out, "totalRequests") {
		t.Errorf("expected usage data in output, got: %s", out)
	}
}

func TestMetricsApiKeyUsageRequiresID(t *testing.T) {
	err := metricsApiKeyUsageCmd.Args(metricsApiKeyUsageCmd, []string{})
	if err == nil {
		t.Error("expected error for missing key ID argument")
	}
}

func TestMetricsProviderCatalog(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		fmt.Fprint(w, `[{"name": "openai", "kind": "chatgpt"}, {"name": "anthropic", "kind": "apikey"}]`)
	}))
	defer server.Close()

	capture := setupTest(metricsProviderCatalogCmd, server.URL)
	err := metricsProviderCatalogCmd.RunE(metricsProviderCatalogCmd, nil)
	if err != nil {
		t.Fatalf("expected no error, got: %v", err)
	}

	out := capture()
	if !strings.Contains(out, "openai") || !strings.Contains(out, "anthropic") {
		t.Errorf("expected provider catalog data in output, got: %s", out)
	}
}

func TestMetricsPurge200(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.Method != "DELETE" {
			t.Errorf("expected DELETE, got %s", r.Method)
		}
		w.Header().Set("Content-Type", "application/json")
		fmt.Fprint(w, `{"deleted": 42}`)
	}))
	defer server.Close()

	capture := setupTestYes(metricsPurgeCmd, server.URL)

	err := metricsPurgeCmd.RunE(metricsPurgeCmd, nil)
	if err != nil {
		t.Fatalf("expected no error, got: %v", err)
	}

	out := capture()
	if !strings.Contains(out, "deleted") || !strings.Contains(out, "42") {
		t.Errorf("expected purge result in output, got: %s", out)
	}
}

func TestMetricsPurgeRequiresConfirmation(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.WriteHeader(200)
	}))
	defer server.Close()

	// Set up with quiet mode so ConfirmOrSkip returns an error (no stdin needed)
	r, wPipe, _ := os.Pipe()
	old := os.Stdout
	os.Stdout = wPipe

	cfg := &client.Config{BaseURL: server.URL, OutputFmt: "json", Color: false}
	c := client.New(cfg, client.WithAPIKey("test-key"))
	ctx := context.Background()
	ctx = context.WithValue(ctx, clientKey, c)
	ctx = context.WithValue(ctx, quietKey, true)
	w := output.NewWriter(output.FormatJSON, true, true)
	ctx = context.WithValue(ctx, outputKey, w)
	metricsPurgeCmd.SetContext(ctx)

	metricsPurgeCmd.RunE(metricsPurgeCmd, nil)
	wPipe.Close()
	os.Stdout = old
	var buf bytes.Buffer
	io.Copy(&buf, r)
	out := buf.String()

	if !strings.Contains(out, "confirmation_required") {
		t.Errorf("expected confirmation error, got: %s", out)
	}
}

func TestMetricsSubcommandsRegistered(t *testing.T) {
	cmds := metricsCmd.Commands()
	names := make(map[string]bool)
	for _, c := range cmds {
		names[c.Name()] = true
	}
	for _, name := range []string{"usage", "today", "last", "summary", "models", "providers", "totals", "latency-bands", "api-keys", "api-key-usage", "provider-catalog", "purge"} {
		if !names[name] {
			t.Errorf("expected subcommand %q to be registered", name)
		}
	}
}

// ==================== Phase 3: Logs Tests ====================

func TestLogsList(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		fmt.Fprint(w, `[{"timestamp": "2025-06-01T00:00:00Z", "level": "info", "source": "router", "message": "Request routed successfully"}]`)
	}))
	defer server.Close()

	capture := setupTest(logsListCmd, server.URL)
	err := logsListCmd.RunE(logsListCmd, nil)
	if err != nil {
		t.Fatalf("expected no error, got: %v", err)
	}

	out := capture()
	if !strings.Contains(out, "router") || !strings.Contains(out, "Request routed") {
		t.Errorf("expected log data in output, got: %s", out)
	}
}

func TestLogsListTable(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		fmt.Fprint(w, `[{"timestamp": "2025-06-01T00:00:00Z", "level": "info", "source": "router", "message": "OK"}]`)
	}))
	defer server.Close()

	capture := setupTestFmt(logsListCmd, server.URL, output.FormatTable)
	err := logsListCmd.RunE(logsListCmd, nil)
	if err != nil {
		t.Fatalf("expected no error, got: %v", err)
	}

	out := capture()
	if !strings.Contains(out, "LEVEL") || !strings.Contains(out, "SOURCE") {
		t.Errorf("expected table headers, got: %s", out)
	}
}

func TestLogsSubcommandsRegistered(t *testing.T) {
	cmds := logsCmd.Commands()
	names := make(map[string]bool)
	for _, c := range cmds {
		names[c.Name()] = true
	}
	for _, name := range []string{"list", "follow", "search", "last"} {
		if !names[name] {
			t.Errorf("expected subcommand %q to be registered", name)
		}
	}
}

// ==================== Phase 3: Scripts Tests ====================

func TestScriptsList(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		fmt.Fprint(w, `[{"name": "run.sh", "path": "/scripts/run.sh", "sizeBytes": 1024, "lastModified": "2025-06-01T00:00:00Z"}]`)
	}))
	defer server.Close()

	capture := setupTest(scriptsListCmd, server.URL)
	err := scriptsListCmd.RunE(scriptsListCmd, nil)
	if err != nil {
		t.Fatalf("expected no error, got: %v", err)
	}

	out := capture()
	if !strings.Contains(out, "run.sh") {
		t.Errorf("expected script data in output, got: %s", out)
	}
}

func TestScriptsContent(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "text/plain")
		fmt.Fprint(w, "#!/bin/bash\necho hello")
	}))
	defer server.Close()

	capture := setupTest(scriptsContentCmd, server.URL)
	err := scriptsContentCmd.RunE(scriptsContentCmd, []string{"run.sh"})
	if err != nil {
		t.Fatalf("expected no error, got: %v", err)
	}

	out := capture()
	if !strings.Contains(out, "#!/bin/bash") || !strings.Contains(out, "echo hello") {
		t.Errorf("expected script content in output, got: %s", out)
	}
}

func TestScriptsDelete204(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.Method != "DELETE" {
			t.Errorf("expected DELETE, got %s", r.Method)
		}
		w.WriteHeader(204)
	}))
	defer server.Close()

	capture := setupTestYes(scriptsDeleteCmd, server.URL)

	err := scriptsDeleteCmd.RunE(scriptsDeleteCmd, []string{"run.sh"})
	if err != nil {
		t.Fatalf("expected no error, got: %v", err)
	}

	out := capture()
	if !strings.Contains(out, "Deleted script") {
		t.Errorf("expected deletion message, got: %s", out)
	}
}

func TestScriptsDeleteRequiresConfirmation(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.WriteHeader(204)
	}))
	defer server.Close()

	// Set up with quiet mode so ConfirmOrSkip returns an error (no stdin needed)
	r, wPipe, _ := os.Pipe()
	old := os.Stdout
	os.Stdout = wPipe

	cfg := &client.Config{BaseURL: server.URL, OutputFmt: "json", Color: false}
	c := client.New(cfg, client.WithAPIKey("test-key"))
	ctx := context.Background()
	ctx = context.WithValue(ctx, clientKey, c)
	ctx = context.WithValue(ctx, quietKey, true)
	w := output.NewWriter(output.FormatJSON, true, true)
	ctx = context.WithValue(ctx, outputKey, w)
	scriptsDeleteCmd.SetContext(ctx)

	scriptsDeleteCmd.RunE(scriptsDeleteCmd, []string{"run.sh"})
	wPipe.Close()
	os.Stdout = old
	var buf bytes.Buffer
	io.Copy(&buf, r)
	out := buf.String()

	if !strings.Contains(out, "confirmation_required") {
		t.Errorf("expected confirmation error, got: %s", out)
	}
}

func TestScriptsContentRequiresFileName(t *testing.T) {
	err := scriptsContentCmd.Args(scriptsContentCmd, []string{})
	if err == nil {
		t.Error("expected error for missing fileName argument")
	}
}

func TestScriptsDeleteRequiresFileName(t *testing.T) {
	err := scriptsDeleteCmd.Args(scriptsDeleteCmd, []string{})
	if err == nil {
		t.Error("expected error for missing fileName argument")
	}
}

func TestScriptsSubcommandsRegistered(t *testing.T) {
	cmds := scriptsCmd.Commands()
	names := make(map[string]bool)
	for _, c := range cmds {
		names[c.Name()] = true
	}
	for _, name := range []string{"list", "upload", "content", "delete", "upload-agent", "content-agent"} {
		if !names[name] {
			t.Errorf("expected subcommand %q to be registered", name)
		}
	}
}

// ==================== Phase 3: Stats Tests ====================

func TestStats(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		fmt.Fprint(w, `{"totalRequests": 1000, "activeRequests": 5, "avgLatencyMs": 1200, "totalTokensProcessed": 50000, "modelsLoaded": 3, "containersRunning": 2, "queueDepth": 10, "requestsPerMinute": 25.5, "errorsLast24h": 2, "switchCount": 15}`)
	}))
	defer server.Close()

	capture := setupTest(statsCmd, server.URL)
	err := statsCmd.RunE(statsCmd, nil)
	if err != nil {
		t.Fatalf("expected no error, got: %v", err)
	}

	out := capture()
	if !strings.Contains(out, "Total Requests") || !strings.Contains(out, "1000") {
		t.Errorf("expected stats data in output, got: %s", out)
	}
}

func TestStatsTable(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		fmt.Fprint(w, `{"totalRequests": 1000, "activeRequests": 5, "avgLatencyMs": 1200, "totalTokensProcessed": 50000, "modelsLoaded": 3, "containersRunning": 2, "queueDepth": 10, "requestsPerMinute": 25.5, "errorsLast24h": 2, "switchCount": 15}`)
	}))
	defer server.Close()

	capture := setupTestFmt(statsCmd, server.URL, output.FormatTable)
	err := statsCmd.RunE(statsCmd, nil)
	if err != nil {
		t.Fatalf("expected no error, got: %v", err)
	}

	out := capture()
	if !strings.Contains(out, "METRIC") || !strings.Contains(out, "Total Requests") {
		t.Errorf("expected table output, got: %s", out)
	}
}

// ==================== Health Watch Tests ====================

func TestHealthWatchFlag(t *testing.T) {
	if healthCmd.Flags().Lookup("watch") == nil {
		t.Error("expected --watch flag to be registered on health command")
	}
}

func TestHealthWatchIntervalFlag(t *testing.T) {
	if healthCmd.Flags().Lookup("interval") == nil {
		t.Error("expected --interval flag to be registered on health command")
	}
}

func TestHealthNonWatchUnchanged(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		switch r.URL.Path {
		case "/api/containers/registered":
			fmt.Fprint(w, `[{"id":"rt1","status":"ready","displayName":"Test Runtime"}]`)
		case "/api/containers":
			fmt.Fprint(w, `[{"id":"ct1","runtimeId":"rt1","status":"running"}]`)
		case "/api/stats":
			fmt.Fprint(w, `{"totalRequests":100,"containersRunning":1,"avgLatencyMs":50}`)
		case "/api/queue/snapshot":
			fmt.Fprint(w, `{"depth":5,"pending":2,"holding":1,"processing":2}`)
		default:
			w.WriteHeader(404)
		}
	}))
	defer server.Close()

	capture := setupTest(healthCmd, server.URL)
	err := healthCmd.RunE(healthCmd, nil)
	if err != nil {
		t.Fatalf("expected no error, got: %v", err)
	}

	out := capture()
	if !strings.Contains(out, "runtimes") || !strings.Contains(out, "containers") || !strings.Contains(out, "queue") {
		t.Errorf("expected all health sections in output, got: %s", out)
	}
}

// ==================== Stats Watch Tests ====================

func TestStatsWatchFlag(t *testing.T) {
	if statsCmd.Flags().Lookup("watch") == nil {
		t.Error("expected --watch flag to be registered on stats command")
	}
}

func TestStatsWatchIntervalFlag(t *testing.T) {
	if statsCmd.Flags().Lookup("interval") == nil {
		t.Error("expected --interval flag to be registered on stats command")
	}
}

func TestStatsNonWatchUnchanged(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		fmt.Fprint(w, `{"totalRequests": 1000, "activeRequests": 5, "avgLatencyMs": 1200, "totalTokensProcessed": 50000, "modelsLoaded": 3, "containersRunning": 2, "queueDepth": 10, "requestsPerMinute": 25.5, "errorsLast24h": 2, "switchCount": 15}`)
	}))
	defer server.Close()

	capture := setupTest(statsCmd, server.URL)
	err := statsCmd.RunE(statsCmd, nil)
	if err != nil {
		t.Fatalf("expected no error, got: %v", err)
	}

	out := capture()
	if !strings.Contains(out, "Total Requests") || !strings.Contains(out, "1000") {
		t.Errorf("expected stats data in output, got: %s", out)
	}
}

// ==================== Phase 3: Cloud Providers Tests ====================

func TestCloudProvidersList(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		fmt.Fprint(w, `[{"id": "cp1", "name": "OpenAI", "baseUrl": "https://api.openai.com", "modelCount": 5, "authType": 0, "updatedAt": "2025-06-01T00:00:00Z"}]`)
	}))
	defer server.Close()

	capture := setupTest(cloudProvidersListCmd, server.URL)
	err := cloudProvidersListCmd.RunE(cloudProvidersListCmd, nil)
	if err != nil {
		t.Fatalf("expected no error, got: %v", err)
	}

	out := capture()
	if !strings.Contains(out, "OpenAI") || !strings.Contains(out, "apikey") {
		t.Errorf("expected provider data in output, got: %s", out)
	}
}

func TestCloudProvidersListTable(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		fmt.Fprint(w, `[{"id": "cp1", "name": "OpenAI", "baseUrl": "https://api.openai.com", "modelCount": 5, "authType": 0, "updatedAt": "2025-06-01T00:00:00Z"}]`)
	}))
	defer server.Close()

	capture := setupTestFmt(cloudProvidersListCmd, server.URL, output.FormatTable)
	err := cloudProvidersListCmd.RunE(cloudProvidersListCmd, nil)
	if err != nil {
		t.Fatalf("expected no error, got: %v", err)
	}

	out := capture()
	if !strings.Contains(out, "NAME") || !strings.Contains(out, "AUTH TYPE") {
		t.Errorf("expected table headers, got: %s", out)
	}
}

func TestCloudProvidersGet(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		fmt.Fprint(w, `{"id": "`+testHexID+`", "name": "OpenAI", "baseUrl": "https://api.openai.com", "baseUrlFull": "https://api.openai.com/v1"}`)
	}))
	defer server.Close()

	capture := setupTest(cloudProvidersGetCmd, server.URL)
	err := cloudProvidersGetCmd.RunE(cloudProvidersGetCmd, []string{testHexID})
	if err != nil {
		t.Fatalf("expected no error, got: %v", err)
	}

	out := capture()
	if !strings.Contains(out, "OpenAI") {
		t.Errorf("expected provider data in output, got: %s", out)
	}
}

func TestCloudProvidersCreate(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.Method != "POST" {
			t.Errorf("expected POST, got %s", r.Method)
		}
		var body map[string]any
		json.NewDecoder(r.Body).Decode(&body)
		if body["name"] != "My Provider" {
			t.Errorf("expected name='My Provider', got %v", body["name"])
		}
		w.Header().Set("Content-Type", "application/json")
		w.WriteHeader(201)
		fmt.Fprint(w, `{"id": "cp2", "name": "My Provider", "baseUrl": "https://api.example.com", "authType": 0}`)
	}))
	defer server.Close()

	capture := setupTest(cloudProvidersCreateCmd, server.URL)
	cloudProvidersCreateCmd.Flags().Set("name", "My Provider")
	cloudProvidersCreateCmd.Flags().Set("base-url", "https://api.example.com")

	err := cloudProvidersCreateCmd.RunE(cloudProvidersCreateCmd, nil)
	if err != nil {
		t.Fatalf("expected no error, got: %v", err)
	}

	out := capture()
	if !strings.Contains(out, "My Provider") {
		t.Errorf("expected provider name in output, got: %s", out)
	}
}

func TestCloudProvidersUpdate(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.Method != "PUT" {
			t.Errorf("expected PUT, got %s", r.Method)
		}
		var body map[string]any
		json.NewDecoder(r.Body).Decode(&body)
		if body["baseUrl"] != "https://new-url.com" {
			t.Errorf("expected baseUrl='https://new-url.com', got %v", body["baseUrl"])
		}
		w.Header().Set("Content-Type", "application/json")
		fmt.Fprint(w, `{"id": "`+testHexID+`", "baseUrl": "https://new-url.com"}`)
	}))
	defer server.Close()

	capture := setupTest(cloudProvidersUpdateCmd, server.URL)
	cloudProvidersUpdateCmd.Flags().Set("base-url", "https://new-url.com")

	err := cloudProvidersUpdateCmd.RunE(cloudProvidersUpdateCmd, []string{testHexID})
	if err != nil {
		t.Fatalf("expected no error, got: %v", err)
	}

	out := capture()
	if !strings.Contains(out, "new-url.com") {
		t.Errorf("expected updated URL in output, got: %s", out)
	}
}

func TestCloudProvidersDelete204(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.Method != "DELETE" {
			t.Errorf("expected DELETE, got %s", r.Method)
		}
		w.WriteHeader(204)
	}))
	defer server.Close()

	capture := setupTestYes(cloudProvidersDeleteCmd, server.URL)

	err := cloudProvidersDeleteCmd.RunE(cloudProvidersDeleteCmd, []string{testHexID})
	if err != nil {
		t.Fatalf("expected no error, got: %v", err)
	}

	out := capture()
	if !strings.Contains(out, "Deleted cloud provider") {
		t.Errorf("expected deletion message, got: %s", out)
	}
}

func TestCloudProvidersDeleteRequiresConfirmation(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.WriteHeader(204)
	}))
	defer server.Close()

	// Set up with quiet mode so ConfirmOrSkip returns an error (no stdin needed)
	r, wPipe, _ := os.Pipe()
	old := os.Stdout
	os.Stdout = wPipe

	cfg := &client.Config{BaseURL: server.URL, OutputFmt: "json", Color: false}
	c := client.New(cfg, client.WithAPIKey("test-key"))
	ctx := context.Background()
	ctx = context.WithValue(ctx, clientKey, c)
	ctx = context.WithValue(ctx, quietKey, true)
	w := output.NewWriter(output.FormatJSON, true, true)
	ctx = context.WithValue(ctx, outputKey, w)
	cloudProvidersDeleteCmd.SetContext(ctx)

	// Don't check err here — w.Error() returns ExitError which is expected for error cases
	cloudProvidersDeleteCmd.RunE(cloudProvidersDeleteCmd, []string{testHexID})
	wPipe.Close()
	os.Stdout = old
	var buf bytes.Buffer
	io.Copy(&buf, r)
	out := buf.String()

	if !strings.Contains(out, "confirmation_required") {
		t.Errorf("expected confirmation error, got: %s", out)
	}
}

func TestCloudProvidersFetchModels(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.Method != "POST" {
			t.Errorf("expected POST, got %s", r.Method)
		}
		w.Header().Set("Content-Type", "application/json")
		fmt.Fprint(w, `{"modelIds": ["gpt-4", "gpt-3.5-turbo"]}`)
	}))
	defer server.Close()

	capture := setupTest(cloudProvidersFetchModelsCmd, server.URL)
	err := cloudProvidersFetchModelsCmd.RunE(cloudProvidersFetchModelsCmd, []string{testHexID})
	if err != nil {
		t.Fatalf("expected no error, got: %v", err)
	}

	out := capture()
	if !strings.Contains(out, "gpt-4") {
		t.Errorf("expected model IDs in output, got: %s", out)
	}
}

func TestCloudProvidersGetRequiresID(t *testing.T) {
	err := cloudProvidersGetCmd.Args(cloudProvidersGetCmd, []string{})
	if err == nil {
		t.Error("expected error for missing ID argument")
	}
}

func TestCloudProvidersDeleteRequiresID(t *testing.T) {
	err := cloudProvidersDeleteCmd.Args(cloudProvidersDeleteCmd, []string{})
	if err == nil {
		t.Error("expected error for missing ID argument")
	}
}

func TestCloudProvidersSubcommandsRegistered(t *testing.T) {
	cmds := cloudProvidersCmd.Commands()
	names := make(map[string]bool)
	for _, c := range cmds {
		names[c.Name()] = true
	}
	for _, name := range []string{"list", "get", "create", "update", "delete", "fetch-models", "test-and-fetch", "save-models", "oauth", "oauth-start", "oauth-poll", "oauth-refresh"} {
		if !names[name] {
			t.Errorf("expected subcommand %q to be registered", name)
		}
	}
}

// ==================== Phase 3: Router Profiles Tests ====================

func TestRouterProfilesList(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		fmt.Fprint(w, `[{"id": "rp1", "name": "default", "mode": "auto", "entries": [{"modelId": "m1"}, {"modelId": "m2"}], "activeModelId": "m1"}]`)
	}))
	defer server.Close()

	capture := setupTest(routerProfilesListCmd, server.URL)
	err := routerProfilesListCmd.RunE(routerProfilesListCmd, nil)
	if err != nil {
		t.Fatalf("expected no error, got: %v", err)
	}

	out := capture()
	if !strings.Contains(out, "default") || !strings.Contains(out, "auto") {
		t.Errorf("expected profile data in output, got: %s", out)
	}
}

func TestRouterProfilesListTable(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		fmt.Fprint(w, `[{"id": "rp1", "name": "default", "mode": "auto", "entries": [{"modelId": "m1"}], "activeModelId": "m1"}]`)
	}))
	defer server.Close()

	capture := setupTestFmt(routerProfilesListCmd, server.URL, output.FormatTable)
	err := routerProfilesListCmd.RunE(routerProfilesListCmd, nil)
	if err != nil {
		t.Fatalf("expected no error, got: %v", err)
	}

	out := capture()
	if !strings.Contains(out, "NAME") || !strings.Contains(out, "MODE") {
		t.Errorf("expected table headers, got: %s", out)
	}
}

func TestRouterProfilesGet(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		if r.URL.Path == "/api/router-profiles" {
			fmt.Fprint(w, `[{"id": "rp1", "name": "default"}]`)
		} else {
			fmt.Fprint(w, `{"id": "rp1", "name": "default", "mode": "auto", "entries": [{"modelId": "m1", "priority": 1, "isEnabled": true}]}`)
		}
	}))
	defer server.Close()

	capture := setupTest(routerProfilesGetCmd, server.URL)
	err := routerProfilesGetCmd.RunE(routerProfilesGetCmd, []string{"default"})
	if err != nil {
		t.Fatalf("expected no error, got: %v", err)
	}

	out := capture()
	if !strings.Contains(out, "default") {
		t.Errorf("expected profile data in output, got: %s", out)
	}
}

func TestRouterProfilesCreate(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.Method != "POST" {
			t.Errorf("expected POST, got %s", r.Method)
		}
		var body map[string]any
		json.NewDecoder(r.Body).Decode(&body)
		if body["name"] != "new-profile" {
			t.Errorf("expected name='new-profile', got %v", body["name"])
		}
		if body["mode"] != "auto" {
			t.Errorf("expected mode='auto', got %v", body["mode"])
		}
		w.Header().Set("Content-Type", "application/json")
		fmt.Fprint(w, `{"id": "rp2", "name": "new-profile", "mode": "auto", "entries": []}`)
	}))
	defer server.Close()

	capture := setupTest(routerProfilesCreateCmd, server.URL)
	routerProfilesCreateCmd.Flags().Set("name", "new-profile")
	routerProfilesCreateCmd.Flags().Set("mode", "auto")
	routerProfilesCreateCmd.Flags().Set("entries", `[{"modelId":"m1","priority":1,"isEnabled":true}]`)

	err := routerProfilesCreateCmd.RunE(routerProfilesCreateCmd, nil)
	if err != nil {
		t.Fatalf("expected no error, got: %v", err)
	}

	out := capture()
	if !strings.Contains(out, "new-profile") {
		t.Errorf("expected profile name in output, got: %s", out)
	}
}

func TestRouterProfilesUpdate(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		if r.URL.Path == "/api/router-profiles" && r.Method == "GET" {
			fmt.Fprint(w, `[{"id": "rp1", "name": "default"}]`)
			return
		}
		if r.Method != "PUT" {
			t.Errorf("expected PUT, got %s", r.Method)
		}
		fmt.Fprint(w, `{"id": "rp1", "name": "updated-profile"}`)
	}))
	defer server.Close()

	capture := setupTest(routerProfilesUpdateCmd, server.URL)
	routerProfilesUpdateCmd.Flags().Set("name", "updated-profile")

	err := routerProfilesUpdateCmd.RunE(routerProfilesUpdateCmd, []string{"default"})
	if err != nil {
		t.Fatalf("expected no error, got: %v", err)
	}

	out := capture()
	if !strings.Contains(out, "updated-profile") {
		t.Errorf("expected updated name in output, got: %s", out)
	}
}

func TestRouterProfilesDelete204(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		if r.URL.Path == "/api/router-profiles" && r.Method == "GET" {
			fmt.Fprint(w, `[{"id": "rp1", "name": "default"}]`)
			return
		}
		if r.Method != "DELETE" {
			t.Errorf("expected DELETE, got %s", r.Method)
		}
		w.WriteHeader(204)
	}))
	defer server.Close()

	capture := setupTestYes(routerProfilesDeleteCmd, server.URL)

	err := routerProfilesDeleteCmd.RunE(routerProfilesDeleteCmd, []string{"default"})
	if err != nil {
		t.Fatalf("expected no error, got: %v", err)
	}

	out := capture()
	if !strings.Contains(out, "Deleted router profile") {
		t.Errorf("expected deletion message, got: %s", out)
	}
}

func TestRouterProfilesDeleteRequiresConfirmation(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		if r.URL.Path == "/api/router-profiles" && r.Method == "GET" {
			fmt.Fprint(w, `[{"id": "rp1", "name": "default"}]`)
			return
		}
		w.WriteHeader(204)
	}))
	defer server.Close()

	capture := setupTest(routerProfilesDeleteCmd, server.URL)
	// Set quiet mode so ConfirmOrSkip returns error instead of prompting
	ctx := routerProfilesDeleteCmd.Context()
	ctx = context.WithValue(ctx, quietKey, true)
	routerProfilesDeleteCmd.SetContext(ctx)

	err := routerProfilesDeleteCmd.RunE(routerProfilesDeleteCmd, []string{"default"})
	if err == nil {
		t.Fatal("expected error for unconfirmed deletion in quiet mode")
	}

	out := capture()
	if !strings.Contains(err.Error(), "confirmation required") {
		t.Errorf("expected confirmation error, got: %s (output: %s)", err.Error(), out)
	}
}

func TestRouterProfilesSetActiveEntry(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		if r.URL.Path == "/api/router-profiles" && r.Method == "GET" {
			fmt.Fprint(w, `[{"id": "rp1", "name": "default"}]`)
			return
		}
		if r.URL.Path == "/api/models" {
			fmt.Fprint(w, `[{"id": "m2", "displayName": "m2"}]`)
			return
		}
		if r.Method != "PATCH" {
			t.Errorf("expected PATCH, got %s", r.Method)
		}
		var body map[string]any
		json.NewDecoder(r.Body).Decode(&body)
		if body["activeModelId"] != "m2" {
			t.Errorf("expected activeModelId='m2', got %v", body["activeModelId"])
		}
		fmt.Fprint(w, `{"id": "rp1", "activeModelId": "m2"}`)
	}))
	defer server.Close()

	capture := setupTest(routerProfilesSetActiveEntryCmd, server.URL)
	routerProfilesSetActiveEntryCmd.Flags().Set("model-id", "m2")

	err := routerProfilesSetActiveEntryCmd.RunE(routerProfilesSetActiveEntryCmd, []string{"default"})
	if err != nil {
		t.Fatalf("expected no error, got: %v", err)
	}

	out := capture()
	if !strings.Contains(out, "activeModelId") {
		t.Errorf("expected active model in output, got: %s", out)
	}
}

func TestRouterProfilesSetThinkingEffort(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		if r.URL.Path == "/api/router-profiles" && r.Method == "GET" {
			fmt.Fprint(w, `[{"id": "rp1", "name": "default"}]`)
			return
		}
		if r.URL.Path == "/api/models" {
			fmt.Fprint(w, `[{"id": "m1", "displayName": "m1"}]`)
			return
		}
		if r.Method != "PATCH" {
			t.Errorf("expected PATCH, got %s", r.Method)
		}
		var body map[string]any
		json.NewDecoder(r.Body).Decode(&body)
		if body["modelId"] != "m1" {
			t.Errorf("expected modelId='m1', got %v", body["modelId"])
		}
		fmt.Fprint(w, `{"id": "rp1", "thinkingEffortOverride": "high"}`)
	}))
	defer server.Close()

	capture := setupTest(routerProfilesSetThinkingEffortCmd, server.URL)
	routerProfilesSetThinkingEffortCmd.Flags().Set("model-id", "m1")
	routerProfilesSetThinkingEffortCmd.Flags().Set("effort", "high")

	err := routerProfilesSetThinkingEffortCmd.RunE(routerProfilesSetThinkingEffortCmd, []string{"default"})
	if err != nil {
		t.Fatalf("expected no error, got: %v", err)
	}

	out := capture()
	if !strings.Contains(out, "thinkingEffortOverride") {
		t.Errorf("expected thinking effort in output, got: %s", out)
	}
}

func TestRouterProfilesStatus(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		fmt.Fprint(w, `{"default": 5, "fallback": 0}`)
	}))
	defer server.Close()

	capture := setupTest(routerProfilesStatusCmd, server.URL)
	err := routerProfilesStatusCmd.RunE(routerProfilesStatusCmd, nil)
	if err != nil {
		t.Fatalf("expected no error, got: %v", err)
	}

	out := capture()
	if !strings.Contains(out, "default") {
		t.Errorf("expected profile status in output, got: %s", out)
	}
}

func TestRouterProfilesGetRequiresID(t *testing.T) {
	err := routerProfilesGetCmd.Args(routerProfilesGetCmd, []string{})
	if err == nil {
		t.Error("expected error for missing ID argument")
	}
}

func TestRouterProfilesDeleteRequiresID(t *testing.T) {
	err := routerProfilesDeleteCmd.Args(routerProfilesDeleteCmd, []string{})
	if err == nil {
		t.Error("expected error for missing ID argument")
	}
}

func TestRouterProfilesSubcommandsRegistered(t *testing.T) {
	cmds := routerProfilesCmd.Commands()
	names := make(map[string]bool)
	for _, c := range cmds {
		names[c.Name()] = true
	}
	for _, name := range []string{"list", "get", "create", "update", "delete", "add-entry", "remove-entry", "set-active-entry", "set-thinking-effort", "status"} {
		if !names[name] {
			t.Errorf("expected subcommand %q to be registered", name)
		}
	}
}

// ==================== Router Profile Name Resolution Tests ====================

func TestRouterProfileSetActiveEntryResolution(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		switch r.URL.Path {
		case "/api/router-profiles":
			fmt.Fprint(w, `[{"id": "rp1", "name": "default"}]`)
		case "/api/models":
			fmt.Fprint(w, `[{"id": "`+testHexID+`", "displayName": "Llama 7B"}]`)
		case "/api/router-profiles/rp1/active-entry":
			var body map[string]any
			json.NewDecoder(r.Body).Decode(&body)
			if body["activeModelId"] != testHexID {
				t.Errorf("expected activeModelId=%s, got %v", testHexID, body["activeModelId"])
			}
			fmt.Fprint(w, `{"id": "rp1", "activeModelId": "`+testHexID+`"}`)
		default:
			w.WriteHeader(404)
		}
	}))
	defer server.Close()

	capture := setupTest(routerProfilesSetActiveEntryCmd, server.URL)
	routerProfilesSetActiveEntryCmd.Flags().Set("model-id", "Llama 7B")

	err := routerProfilesSetActiveEntryCmd.RunE(routerProfilesSetActiveEntryCmd, []string{"default"})
	if err != nil {
		t.Fatalf("expected no error, got: %v", err)
	}

	out := capture()
	if !strings.Contains(out, testHexID) {
		t.Errorf("expected resolved model ID %s in output, got: %s", testHexID, out)
	}
}

func TestRouterProfileSetThinkingEffortValidation(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		// Handle profile resolver
		if r.URL.Path == "/api/router-profiles" {
			fmt.Fprint(w, `[{"id": "rp1", "name": "default"}]`)
			return
		}
		// Default: empty list (model resolver won't be called for hex IDs)
		fmt.Fprint(w, `[]`)
	}))
	defer server.Close()

	// Test with invalid effort value — use a hex ID to bypass model name resolution
	capture := setupTest(routerProfilesSetThinkingEffortCmd, server.URL)
	routerProfilesSetThinkingEffortCmd.Flags().Set("model-id", testHexID)
	routerProfilesSetThinkingEffortCmd.Flags().Set("effort", "invalid-value")

	err := routerProfilesSetThinkingEffortCmd.RunE(routerProfilesSetThinkingEffortCmd, []string{"default"})
	if err == nil {
		t.Fatal("expected error for invalid effort value")
	}

	_ = capture()
	if !strings.Contains(err.Error(), "invalid_effort") {
		t.Errorf("expected invalid_effort error, got: %s", err.Error())
	}
}

func TestRouterProfileSetThinkingEffortValidValues(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		switch r.URL.Path {
		case "/api/router-profiles":
			fmt.Fprint(w, `[{"id": "rp1", "name": "default"}]`)
		case "/api/models":
			fmt.Fprint(w, `[{"id": "`+testHexID+`", "displayName": "Llama 7B"}]`)
		case "/api/router-profiles/rp1/thinking-effort":
			var body map[string]any
			json.NewDecoder(r.Body).Decode(&body)
			fmt.Fprintf(w, `{"id": "rp1", "thinkingEffortOverride": "%v"}`, body["thinkingEffortOverride"])
		default:
			w.WriteHeader(404)
		}
	}))
	defer server.Close()

	for _, effort := range []string{"low", "medium", "high"} {
		capture := setupTest(routerProfilesSetThinkingEffortCmd, server.URL)
		routerProfilesSetThinkingEffortCmd.Flags().Set("model-id", "Llama 7B")
		routerProfilesSetThinkingEffortCmd.Flags().Set("effort", effort)

		err := routerProfilesSetThinkingEffortCmd.RunE(routerProfilesSetThinkingEffortCmd, []string{"default"})
		if err != nil {
			t.Fatalf("expected no error for effort=%s, got: %v", effort, err)
		}

		out := capture()
		if !strings.Contains(out, effort) {
			t.Errorf("expected effort %s in output, got: %s", effort, out)
		}
	}
}

func TestRouterProfileAddEntryThinkingEffort(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		// Handle profile name resolution
		if r.URL.Path == "/api/router-profiles" && r.Method == "GET" {
			fmt.Fprint(w, `[{"id": "rp1", "name": "default"}]`)
			return
		}
		// Handle profile fetch
		if r.URL.Path == "/api/router-profiles/rp1" && r.Method == "GET" {
			fmt.Fprint(w, `{"id": "rp1", "name": "default", "entries": []}`)
			return
		}
		// Handle profile update
		if r.URL.Path == "/api/router-profiles/rp1" && r.Method == "PUT" {
			var body map[string]any
			json.NewDecoder(r.Body).Decode(&body)
			entries, _ := body["entries"].([]any)
			if len(entries) == 1 {
				entry, _ := entries[0].(map[string]any)
				if entry["thinkingEffortOverride"] != "high" {
					t.Errorf("expected thinkingEffortOverride=high, got %v", entry["thinkingEffortOverride"])
				}
			}
			fmt.Fprint(w, `{"id": "rp1", "name": "default", "entries": [{"modelId": "`+testHexID+`", "thinkingEffortOverride": "high"}]}`)
			return
		}
		w.WriteHeader(404)
	}))
	defer server.Close()

	capture := setupTest(routerProfilesAddEntryCmd, server.URL)
	routerProfilesAddEntryCmd.Flags().Set("model", testHexID)
	routerProfilesAddEntryCmd.Flags().Set("thinking-effort", "high")

	err := routerProfilesAddEntryCmd.RunE(routerProfilesAddEntryCmd, []string{"default"})
	if err != nil {
		t.Fatalf("expected no error, got: %v", err)
	}

	out := capture()
	if !strings.Contains(out, "thinkingEffortOverride") {
		t.Errorf("expected thinkingEffortOverride in output, got: %s", out)
	}
}

// ==================== Phase 3: Provider Model Catalog Tests ====================

func TestProviderModelCatalog(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		fmt.Fprint(w, `[{"name": "openai", "kind": "chatgpt", "models": ["gpt-4", "gpt-3.5-turbo"], "modelDisplayNames": ["GPT-4", "GPT-3.5 Turbo"]}]`)
	}))
	defer server.Close()

	capture := setupTest(providerModelCatalogCmd, server.URL)
	err := providerModelCatalogCmd.RunE(providerModelCatalogCmd, nil)
	if err != nil {
		t.Fatalf("expected no error, got: %v", err)
	}

	out := capture()
	if !strings.Contains(out, "openai") || !strings.Contains(out, "gpt-4") {
		t.Errorf("expected catalog data in output, got: %s", out)
	}
}

func TestProviderModelCatalogTable(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		fmt.Fprint(w, `[{"name": "openai", "kind": "chatgpt", "models": ["gpt-4"]}]`)
	}))
	defer server.Close()

	capture := setupTestFmt(providerModelCatalogCmd, server.URL, output.FormatTable)
	err := providerModelCatalogCmd.RunE(providerModelCatalogCmd, nil)
	if err != nil {
		t.Fatalf("expected no error, got: %v", err)
	}

	out := capture()
	if !strings.Contains(out, "NAME") || !strings.Contains(out, "KIND") {
		t.Errorf("expected table headers, got: %s", out)
	}
}

// ==================== Config B1+B2+B3 Tests ====================

// setupTempConfig creates a temp HOME directory with a config file and returns a cleanup function.
func setupTempConfig(t *testing.T, content string) func() {
	t.Helper()
	tmpDir := t.TempDir()

	// Write config file if content is provided
	if content != "" {
		configDir := tmpDir + "/.config/unswarm"
		if err := os.MkdirAll(configDir, 0700); err != nil {
			t.Fatal(err)
		}
		if err := os.WriteFile(configDir+"/cli.yaml", []byte(content), 0600); err != nil {
			t.Fatal(err)
		}
	}

	// Override HOME
	origHome := os.Getenv("HOME")
	os.Setenv("HOME", tmpDir)

	return func() {
		os.Setenv("HOME", origHome)
	}
}

func TestConfigSubcommandsRegistered(t *testing.T) {
	cmds := configCmd.Commands()
	names := make(map[string]bool)
	for _, c := range cmds {
		names[c.Name()] = true
	}
	for _, name := range []string{"init", "show", "get", "set", "contexts", "use", "test"} {
		if !names[name] {
			t.Errorf("expected subcommand %q to be registered", name)
		}
	}
}

func TestConfigGetURL(t *testing.T) {
	cleanup := setupTempConfig(t, `active-context: default
contexts:
  default:
    url: http://localhost:22301
    output: table
    color: true
`)
	defer cleanup()

	capture := setupTest(configGetCmd, "http://localhost:22301")
	err := configGetCmd.RunE(configGetCmd, []string{"url"})
	if err != nil {
		t.Fatalf("expected no error, got: %v", err)
	}

	out := capture()
	if !strings.Contains(out, "url") || !strings.Contains(out, "http://localhost:22301") {
		t.Errorf("expected url value in output, got: %s", out)
	}
}

func TestConfigGetOutput(t *testing.T) {
	cleanup := setupTempConfig(t, `active-context: default
contexts:
  default:
    url: http://localhost:22301
    output: json
    color: false
`)
	defer cleanup()

	capture := setupTest(configGetCmd, "http://localhost:22301")
	err := configGetCmd.RunE(configGetCmd, []string{"output"})
	if err != nil {
		t.Fatalf("expected no error, got: %v", err)
	}

	out := capture()
	if !strings.Contains(out, "json") {
		t.Errorf("expected output value 'json' in output, got: %s", out)
	}
}

func TestConfigGetColor(t *testing.T) {
	cleanup := setupTempConfig(t, `active-context: default
contexts:
  default:
    url: http://localhost:22301
    output: table
    color: false
`)
	defer cleanup()

	capture := setupTest(configGetCmd, "http://localhost:22301")
	err := configGetCmd.RunE(configGetCmd, []string{"color"})
	if err != nil {
		t.Fatalf("expected no error, got: %v", err)
	}

	out := capture()
	if !strings.Contains(out, "false") {
		t.Errorf("expected color value 'false' in output, got: %s", out)
	}
}

func TestConfigGetInvalidKey(t *testing.T) {
	capture := setupTest(configGetCmd, "http://localhost:22301")
	err := configGetCmd.RunE(configGetCmd, []string{"invalid_key"})
	if err == nil {
		t.Error("expected error for invalid config key")
	}

	out := capture()
	if !strings.Contains(out, "invalid_config_key") {
		t.Errorf("expected invalid_config_key error, got: %s", out)
	}
}

func TestConfigSetURL(t *testing.T) {
	cleanup := setupTempConfig(t, `active-context: default
contexts:
  default:
    url: http://localhost:22301
    output: table
    color: true
`)
	defer cleanup()

	capture := setupTest(configSetCmd, "http://localhost:22301")
	err := configSetCmd.RunE(configSetCmd, []string{"url", "http://staging:22301"})
	if err != nil {
		t.Fatalf("expected no error, got: %v", err)
	}

	out := capture()
	if !strings.Contains(out, "http://staging:22301") {
		t.Errorf("expected new URL in output, got: %s", out)
	}
}

func TestConfigSetOutput(t *testing.T) {
	cleanup := setupTempConfig(t, `active-context: default
contexts:
  default:
    url: http://localhost:22301
    output: table
    color: true
`)
	defer cleanup()

	capture := setupTest(configSetCmd, "http://localhost:22301")
	err := configSetCmd.RunE(configSetCmd, []string{"output", "json"})
	if err != nil {
		t.Fatalf("expected no error, got: %v", err)
	}

	out := capture()
	if !strings.Contains(out, "json") {
		t.Errorf("expected output value in output, got: %s", out)
	}
}

func TestConfigSetColorTrue(t *testing.T) {
	cleanup := setupTempConfig(t, `active-context: default
contexts:
  default:
    url: http://localhost:22301
    output: table
    color: false
`)
	defer cleanup()

	capture := setupTest(configSetCmd, "http://localhost:22301")
	err := configSetCmd.RunE(configSetCmd, []string{"color", "true"})
	if err != nil {
		t.Fatalf("expected no error, got: %v", err)
	}

	out := capture()
	if !strings.Contains(out, "true") {
		t.Errorf("expected color=true in output, got: %s", out)
	}
}

func TestConfigSetColorFalse(t *testing.T) {
	cleanup := setupTempConfig(t, `active-context: default
contexts:
  default:
    url: http://localhost:22301
    output: table
    color: true
`)
	defer cleanup()

	capture := setupTest(configSetCmd, "http://localhost:22301")
	err := configSetCmd.RunE(configSetCmd, []string{"color", "false"})
	if err != nil {
		t.Fatalf("expected no error, got: %v", err)
	}

	out := capture()
	if !strings.Contains(out, "false") {
		t.Errorf("expected color=false in output, got: %s", out)
	}
}

func TestConfigSetColorInvalid(t *testing.T) {
	capture := setupTest(configSetCmd, "http://localhost:22301")
	err := configSetCmd.RunE(configSetCmd, []string{"color", "invalid"})
	if err == nil {
		t.Error("expected error for invalid color value")
	}

	out := capture()
	if !strings.Contains(out, "invalid_color_value") {
		t.Errorf("expected invalid_color_value error, got: %s", out)
	}
}

func TestConfigSetInvalidKey(t *testing.T) {
	capture := setupTest(configSetCmd, "http://localhost:22301")
	err := configSetCmd.RunE(configSetCmd, []string{"bad_key", "value"})
	if err == nil {
		t.Error("expected error for invalid config key")
	}

	out := capture()
	if !strings.Contains(out, "invalid_config_key") {
		t.Errorf("expected invalid_config_key error, got: %s", out)
	}
}

func TestConfigSetRequiresArgs(t *testing.T) {
	err := configSetCmd.Args(configSetCmd, []string{})
	if err == nil {
		t.Error("expected error for missing arguments")
	}
}

func TestConfigGetRequiresArgs(t *testing.T) {
	err := configGetCmd.Args(configGetCmd, []string{})
	if err == nil {
		t.Error("expected error for missing argument")
	}
}

func TestConfigContexts(t *testing.T) {
	cleanup := setupTempConfig(t, `active-context: staging
contexts:
  default:
    url: http://localhost:22301
    output: table
    color: true
  staging:
    url: http://staging:22301
    output: json
    color: false
`)
	defer cleanup()

	capture := setupTest(configContextsCmd, "http://staging:22301")
	err := configContextsCmd.RunE(configContextsCmd, nil)
	if err != nil {
		t.Fatalf("expected no error, got: %v", err)
	}

	out := capture()
	if !strings.Contains(out, "default") || !strings.Contains(out, "staging") {
		t.Errorf("expected both contexts in output, got: %s", out)
	}
	if !strings.Contains(out, "staging:22301") {
		t.Errorf("expected staging URL in output, got: %s", out)
	}
}

func TestConfigUse(t *testing.T) {
	cleanup := setupTempConfig(t, `active-context: default
contexts:
  default:
    url: http://localhost:22301
    output: table
    color: true
  staging:
    url: http://staging:22301
    output: json
    color: false
`)
	defer cleanup()

	capture := setupTest(configUseCmd, "http://staging:22301")
	err := configUseCmd.RunE(configUseCmd, []string{"staging"})
	if err != nil {
		t.Fatalf("expected no error, got: %v", err)
	}

	out := capture()
	if !strings.Contains(out, "staging") {
		t.Errorf("expected staging context in output, got: %s", out)
	}
}

func TestConfigUseNotFound(t *testing.T) {
	cleanup := setupTempConfig(t, `active-context: default
contexts:
  default:
    url: http://localhost:22301
    output: table
    color: true
`)
	defer cleanup()

	capture := setupTest(configUseCmd, "http://localhost:22301")
	err := configUseCmd.RunE(configUseCmd, []string{"nonexistent"})
	if err == nil {
		t.Error("expected error for nonexistent context")
	}

	out := capture()
	if !strings.Contains(out, "context_not_found") {
		t.Errorf("expected context_not_found error, got: %s", out)
	}
}

func TestConfigUseRequiresArgs(t *testing.T) {
	err := configUseCmd.Args(configUseCmd, []string{})
	if err == nil {
		t.Error("expected error for missing argument")
	}
}

func TestConfigTestConnection(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		fmt.Fprint(w, `{"totalRequests": 100, "version": "1.0.0"}`)
	}))
	defer server.Close()

	capture := setupTest(configTestCmd, server.URL)
	err := configTestCmd.RunE(configTestCmd, nil)
	if err != nil {
		t.Fatalf("expected no error, got: %v", err)
	}

	out := capture()
	if !strings.Contains(out, "ok") {
		t.Errorf("expected ok status in output, got: %s", out)
	}
	if !strings.Contains(out, "latency") {
		t.Errorf("expected latency in output, got: %s", out)
	}
}

func TestConfigTestConnectionError(t *testing.T) {
	// Create a server that closes immediately
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		hj, ok := w.(http.Hijacker)
		if ok {
			conn, _, _ := hj.Hijack()
			conn.Close()
		}
	}))
	defer server.Close()

	capture := setupTest(configTestCmd, server.URL)
	configTestCmd.RunE(configTestCmd, nil)

	out := capture()
	if !strings.Contains(out, "error") {
		t.Errorf("expected error status in output, got: %s", out)
	}
}

func TestConfigGetMissingConfig(t *testing.T) {
	cleanup := setupTempConfig(t, "")
	defer cleanup()

	// When no config exists, LoadConfig returns defaults
	capture := setupTest(configGetCmd, "http://localhost:22301")
	err := configGetCmd.RunE(configGetCmd, []string{"url"})
	if err != nil {
		t.Fatalf("expected no error, got: %v", err)
	}

	out := capture()
	if !strings.Contains(out, "http://localhost:22301") {
		t.Errorf("expected default URL in output, got: %s", out)
	}
}

func TestConfigFlatMigration(t *testing.T) {
	// Write an old flat config and verify it auto-migrates
	cleanup := setupTempConfig(t, `url: http://old-host:22301
output: csv
color: false
`)
	defer cleanup()

	// LoadConfig should auto-migrate
	cfg, err := client.LoadConfig()
	if err != nil {
		t.Fatalf("expected no error, got: %v", err)
	}

	if cfg.BaseURL != "http://old-host:22301" {
		t.Errorf("expected migrated URL, got: %s", cfg.BaseURL)
	}
	if cfg.OutputFmt != "csv" {
		t.Errorf("expected migrated output format, got: %s", cfg.OutputFmt)
	}
	if cfg.Color != false {
		t.Errorf("expected migrated color=false, got: %v", cfg.Color)
	}

	// Verify the file was saved in new format
	cf, err := client.LoadConfigFile()
	if err != nil {
		t.Fatalf("expected no error reading migrated file, got: %v", err)
	}

	if cf.ActiveContext != "default" {
		t.Errorf("expected active-context=default after migration, got: %s", cf.ActiveContext)
	}
	if _, ok := cf.Contexts["default"]; !ok {
		t.Error("expected 'default' context after migration")
	}
}

// ==================== Runtimes Register Interactive Tests ====================

// newFreshRegisterCmd creates a fresh runtimesRegisterCmd with clean flags for isolated testing.
func newFreshRegisterCmd() *cobra.Command {
	cmd := &cobra.Command{
		Use:   "register",
		Short: "Register a new runtime",
		RunE:  runtimesRegisterCmd.RunE,
	}
	cmd.Flags().String("name", "", "Runtime display name")
	cmd.Flags().String("image", "", "Container image")
	cmd.Flags().Int("container-port", 8080, "Container port")
	cmd.Flags().Int("mapped-port", 0, "Mapped host port (0 for auto)")
	cmd.Flags().String("kind", "", "Runtime kind: container|script")
	cmd.Flags().String("launcher-path", "", "Launcher script path (for script kind)")
	cmd.Flags().String("agent", "host", "Agent to run on")
	cmd.Flags().StringSlice("can-run-along-with", nil, "Runtime IDs this can run alongside")
	cmd.Flags().Int("max-concurrent", 1, "Max concurrent inferences")
	return cmd
}

func TestRuntimesRegisterInteractive(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.Method != "POST" {
			t.Errorf("expected POST, got %s", r.Method)
		}
		if r.URL.Path != "/api/containers/register" {
			t.Errorf("expected /api/containers/register, got %s", r.URL.Path)
		}

		var body map[string]any
		json.NewDecoder(r.Body).Decode(&body)

		// Verify all fields
		if body["displayName"] != "Test Runtime" {
			t.Errorf("expected displayName='Test Runtime', got %v", body["displayName"])
		}
		if body["image"] != "nvidia/cuda:12" {
			t.Errorf("expected image='nvidia/cuda:12', got %v", body["image"])
		}
		if body["containerPort"] != 8080.0 { // JSON numbers are float64
			t.Errorf("expected containerPort=8080, got %v", body["containerPort"])
		}
		if body["mappedPort"] != 9090.0 {
			t.Errorf("expected mappedPort=9090, got %v", body["mappedPort"])
		}
		if body["runtimeKind"] != "docker" {
			t.Errorf("expected runtimeKind='docker', got %v", body["runtimeKind"])
		}
		if body["agent"] != "my-agent" {
			t.Errorf("expected agent='my-agent', got %v", body["agent"])
		}
		if body["maxConcurrentInferences"] != 3.0 {
			t.Errorf("expected maxConcurrentInferences=3, got %v", body["maxConcurrentInferences"])
		}

		w.Header().Set("Content-Type", "application/json")
		w.WriteHeader(201)
		fmt.Fprint(w, `{"id": "new-rt", "displayName": "Test Runtime", "status": "registered"}`)
	}))
	defer server.Close()

	// Simulate interactive input: name, image, container port, mapped port, kind, agent, max concurrent, confirm
	simulateInput("Test Runtime", "nvidia/cuda:12", "8080", "9090", "1", "my-agent", "3", "y")
	defer func() { interact.In = nil }()

	cmd := newFreshRegisterCmd()
	capture := setupTest(cmd, server.URL)

	err := cmd.RunE(cmd, nil)
	if err != nil {
		t.Fatalf("expected no error, got: %v", err)
	}

	out := capture()
	if !strings.Contains(out, "new-rt") {
		t.Errorf("expected new-rt in output, got: %s", out)
	}
}

func TestRuntimesRegisterFlagsPath(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		var body map[string]any
		json.NewDecoder(r.Body).Decode(&body)

		if body["displayName"] != "Flag Runtime" {
			t.Errorf("expected displayName='Flag Runtime', got %v", body["displayName"])
		}
		if body["image"] != "python:3.11" {
			t.Errorf("expected image='python:3.11', got %v", body["image"])
		}

		w.Header().Set("Content-Type", "application/json")
		w.WriteHeader(201)
		fmt.Fprint(w, `{"id": "flag-rt", "displayName": "Flag Runtime", "status": "registered"}`)
	}))
	defer server.Close()

	cmd := newFreshRegisterCmd()
	capture := setupTest(cmd, server.URL)
	cmd.Flags().Set("name", "Flag Runtime")
	cmd.Flags().Set("image", "python:3.11")

	err := cmd.RunE(cmd, nil)
	if err != nil {
		t.Fatalf("expected no error, got: %v", err)
	}

	out := capture()
	if !strings.Contains(out, "flag-rt") {
		t.Errorf("expected flag-rt in output, got: %s", out)
	}
}

func TestRuntimesRegisterQuietNoFlags(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		t.Error("should not reach server in quiet mode with no flags")
	}))
	defer server.Close()

	cmd := newFreshRegisterCmd()
	capture := setupTest(cmd, server.URL)
	ctx := cmd.Context()
	ctx = context.WithValue(ctx, quietKey, true)
	cmd.SetContext(ctx)

	err := cmd.RunE(cmd, nil)
	if err == nil {
		t.Fatal("expected error in quiet mode with no flags")
	}

	_ = capture()
}

// ==================== Phase 3: Logs Follow/Search/Last Tests ====================

func TestLogsFollow(t *testing.T) {
	callCount := 0
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		callCount++
		w.Header().Set("Content-Type", "application/json")
		// Return different entries on successive calls to test dedup and polling
		if callCount == 1 {
			fmt.Fprint(w, `[{"timestamp": "2025-06-01T00:00:00Z", "level": "info", "source": "router", "message": "first entry"}]`)
		} else {
			fmt.Fprint(w, `[{"timestamp": "2025-06-01T00:00:01Z", "level": "info", "source": "router", "message": "second entry"}]`)
		}
	}))
	defer server.Close()

	capture := setupTest(logsFollowCmd, server.URL)
	// Set a very short interval so the test runs quickly
	logsFollowCmd.Flags().Set("interval", "1")

	// Run the follow command in a goroutine
	done := make(chan error, 1)
	go func() {
		done <- logsFollowCmd.RunE(logsFollowCmd, nil)
	}()

	// Let it poll a couple times then close the server to trigger errors and exit
	time.Sleep(3 * time.Second)
	server.Close()
	time.Sleep(2 * time.Second)

	_ = capture()

	// Verify the server was called at least once
	if callCount < 1 {
		t.Errorf("expected at least 1 poll, got %d", callCount)
	}
}

func TestLogsSearch(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		fmt.Fprint(w, `[
			{"timestamp": "2025-06-01T00:00:00Z", "level": "info", "source": "router", "message": "Request routed"},
			{"timestamp": "2025-06-01T00:00:01Z", "level": "error", "source": "auth", "message": "Authentication failed"},
			{"timestamp": "2025-06-01T00:00:02Z", "level": "info", "source": "router", "message": "Response sent"},
			{"timestamp": "2025-06-01T00:00:03Z", "level": "warn", "source": "cache", "message": "Cache miss for user-123"},
			{"timestamp": "2025-06-01T00:00:04Z", "level": "error", "source": "db", "message": "Connection timeout"},
			{"timestamp": "2025-06-01T00:00:05Z", "level": "info", "source": "router", "message": "Request routed to backend"},
			{"timestamp": "2025-06-01T00:00:06Z", "level": "debug", "source": "metrics", "message": "Collected 100 samples"},
			{"timestamp": "2025-06-01T00:00:07Z", "level": "error", "source": "worker", "message": "Error processing job"},
			{"timestamp": "2025-06-01T00:00:08Z", "level": "info", "source": "router", "message": "All good"},
			{"timestamp": "2025-06-01T00:00:09Z", "level": "info", "source": "health", "message": "System healthy"}
		]`)
	}))
	defer server.Close()

	capture := setupTest(logsSearchCmd, server.URL)
	err := logsSearchCmd.RunE(logsSearchCmd, []string{"error"})
	if err != nil {
		t.Fatalf("expected no error, got: %v", err)
	}

	out := capture()
	// Should match entries containing "error" (case-insensitive) in level or message
	if !strings.Contains(out, "Authentication failed") {
		t.Errorf("expected 'Authentication failed' in filtered output, got: %s", out)
	}
	if !strings.Contains(out, "Connection timeout") {
		t.Errorf("expected 'Connection timeout' in filtered output, got: %s", out)
	}
	if !strings.Contains(out, "Error processing job") {
		t.Errorf("expected 'Error processing job' in filtered output, got: %s", out)
	}
	// Should NOT contain entries without "error"
	if strings.Contains(out, "Cache miss") {
		t.Errorf("expected 'Cache miss' to be filtered out, got: %s", out)
	}
	if strings.Contains(out, "System healthy") {
		t.Errorf("expected 'System healthy' to be filtered out, got: %s", out)
	}
}

func TestLogsSearchNoMatch(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		fmt.Fprint(w, `[
			{"timestamp": "2025-06-01T00:00:00Z", "level": "info", "source": "router", "message": "All good"},
			{"timestamp": "2025-06-01T00:00:01Z", "level": "info", "source": "cache", "message": "Cache hit"}
		]`)
	}))
	defer server.Close()

	capture := setupTest(logsSearchCmd, server.URL)
	err := logsSearchCmd.RunE(logsSearchCmd, []string{"xyznonexistent"})
	if err == nil {
		t.Fatal("expected error for no matching logs")
	}

	out := capture()
	if !strings.Contains(out, "no_matches") {
		t.Errorf("expected no_matches error, got: %s", out)
	}
}

func TestLogsLast(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		// Verify the limit parameter is 50
		if !strings.Contains(r.URL.RawQuery, "limit=50") {
			t.Errorf("expected limit=50 in query, got: %s", r.URL.RawQuery)
		}
		w.Header().Set("Content-Type", "application/json")
		fmt.Fprint(w, `[{"timestamp": "2025-06-01T00:00:00Z", "level": "info", "source": "router", "message": "test entry"}]`)
	}))
	defer server.Close()

	capture := setupTest(logsLastCmd, server.URL)
	err := logsLastCmd.RunE(logsLastCmd, []string{"50"})
	if err != nil {
		t.Fatalf("expected no error, got: %v", err)
	}

	out := capture()
	if !strings.Contains(out, "test entry") {
		t.Errorf("expected 'test entry' in output, got: %s", out)
	}
}

func TestLogsLastDefault(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		// Verify the default limit is 20
		if !strings.Contains(r.URL.RawQuery, "limit=20") {
			t.Errorf("expected limit=20 in query, got: %s", r.URL.RawQuery)
		}
		w.Header().Set("Content-Type", "application/json")
		fmt.Fprint(w, `[{"timestamp": "2025-06-01T00:00:00Z", "level": "info", "source": "router", "message": "default entry"}]`)
	}))
	defer server.Close()

	capture := setupTest(logsLastCmd, server.URL)
	err := logsLastCmd.RunE(logsLastCmd, nil)
	if err != nil {
		t.Fatalf("expected no error, got: %v", err)
	}

	out := capture()
	if !strings.Contains(out, "default entry") {
		t.Errorf("expected 'default entry' in output, got: %s", out)
	}
}

// ==================== Models Compare Tests ====================

func TestModelsCompare(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		switch r.URL.Path {
		case "/api/models":
			// List endpoint for resolver - return both models
			fmt.Fprint(w, `[{"id":"model-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa1","name":"llama-7b","displayName":"llama-7b"},{"id":"model-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa2","name":"mistral-7b","displayName":"mistral-7b"}]`)
		case "/api/models/model-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa1":
			fmt.Fprint(w, `{"id":"model-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa1","name":"llama-7b","family":"llama","parameterSize":"7B","contextWindow":4096,"status":"ready"}`)
		case "/api/models/model-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa2":
			fmt.Fprint(w, `{"id":"model-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa2","name":"mistral-7b","family":"mistral","parameterSize":"7B","contextWindow":8192,"status":"ready"}`)
		case "/api/benchmarks":
			fmt.Fprint(w, `[{"modelId":"model-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa1","tokensPerSecond":42.3,"latencyMs":120},{"modelId":"model-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa2","tokensPerSecond":38.1,"latencyMs":135}]`)
		case "/api/containers":
			fmt.Fprint(w, `[{"modelName":"llama-7b","status":"running"},{"modelName":"mistral-7b","status":"stopped"}]`)
		default:
			w.WriteHeader(404)
		}
	}))
	defer server.Close()

	capture := setupTest(modelsCompareCmd, server.URL)
	err := modelsCompareCmd.RunE(modelsCompareCmd, []string{"llama-7b", "mistral-7b"})
	if err != nil {
		t.Fatalf("expected no error, got: %v", err)
	}

	out := capture()
	if !strings.Contains(out, "llama-7b") || !strings.Contains(out, "mistral-7b") {
		t.Errorf("expected both model names in output, got: %s", out)
	}
	if !strings.Contains(out, "42.30") || !strings.Contains(out, "38.10") {
		t.Errorf("expected benchmark data in output, got: %s", out)
	}
	if !strings.Contains(out, "running") || !strings.Contains(out, "stopped") {
		t.Errorf("expected container statuses in output, got: %s", out)
	}
}

func TestModelsCompareNotFound(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		switch r.URL.Path {
		case "/api/models":
			fmt.Fprint(w, `[{"id":"model-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa1","name":"llama-7b","displayName":"llama-7b"},{"id":"model-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa2","name":"mistral-7b","displayName":"mistral-7b"}]`)
		case "/api/models/model-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa1":
			fmt.Fprint(w, `{"id":"model-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa1","name":"llama-7b"}`)
		case "/api/models/model-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa2":
			w.WriteHeader(404)
			fmt.Fprint(w, `{"error":"not_found","message":"Model not found","exitCode":1}`)
		default:
			w.WriteHeader(404)
		}
	}))
	defer server.Close()

	capture := setupTest(modelsCompareCmd, server.URL)
	modelsCompareCmd.RunE(modelsCompareCmd, []string{"llama-7b", "mistral-7b"})

	out := capture()
	if !strings.Contains(out, "not_found") {
		t.Errorf("expected not_found error in output, got: %s", out)
	}
}

func TestModelsCompareRequiresArgs(t *testing.T) {
	err := modelsCompareCmd.Args(modelsCompareCmd, []string{})
	if err == nil {
		t.Error("expected error for missing arguments")
	}
}

// ==================== Metrics Today/Last Tests ====================

func TestMetricsToday(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		// Verify the URL contains from and to parameters
		if !strings.Contains(r.URL.RawQuery, "from=") {
			t.Errorf("expected 'from=' in query, got: %s", r.URL.RawQuery)
		}
		if !strings.Contains(r.URL.RawQuery, "to=") {
			t.Errorf("expected 'to=' in query, got: %s", r.URL.RawQuery)
		}
		fmt.Fprint(w, `{"items": [{"timestamp": "2025-06-14T10:00:00Z", "provider": "openai", "model": "gpt-4", "promptTokens": 100, "completionTokens": 50, "latencyMs": 1200}], "total": 1, "page": 1, "pageSize": 20}`)
	}))
	defer server.Close()

	capture := setupTest(metricsTodayCmd, server.URL)
	err := metricsTodayCmd.RunE(metricsTodayCmd, nil)
	if err != nil {
		t.Fatalf("expected no error, got: %v", err)
	}

	out := capture()
	if !strings.Contains(out, "openai") || !strings.Contains(out, "gpt-4") {
		t.Errorf("expected usage data in output, got: %s", out)
	}
}

func TestMetricsLast7d(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		// Verify the URL contains a from parameter
		if !strings.Contains(r.URL.RawQuery, "from=") {
			t.Errorf("expected 'from=' in query, got: %s", r.URL.RawQuery)
		}
		fmt.Fprint(w, `{"items": [{"timestamp": "2025-06-10T00:00:00Z", "provider": "anthropic", "model": "claude-3", "promptTokens": 200, "completionTokens": 100, "latencyMs": 800}], "total": 1, "page": 1, "pageSize": 20}`)
	}))
	defer server.Close()

	capture := setupTest(metricsLastCmd, server.URL)
	err := metricsLastCmd.RunE(metricsLastCmd, []string{"7d"})
	if err != nil {
		t.Fatalf("expected no error, got: %v", err)
	}

	out := capture()
	if !strings.Contains(out, "anthropic") || !strings.Contains(out, "claude-3") {
		t.Errorf("expected usage data in output, got: %s", out)
	}
}

func TestMetricsLastInvalidDuration(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		t.Error("should not reach server with invalid duration")
	}))
	defer server.Close()

	capture := setupTest(metricsLastCmd, server.URL)
	err := metricsLastCmd.RunE(metricsLastCmd, []string{"invalid"})
	if err == nil {
		t.Fatal("expected error for invalid duration")
	}

	_ = capture()
}

func TestMetricsLastRequiresArg(t *testing.T) {
	err := metricsLastCmd.Args(metricsLastCmd, []string{})
	if err == nil {
		t.Error("expected error for missing duration argument")
	}
}

func TestModelsSubcommandsRegistered(t *testing.T) {
	cmds := modelsCmd.Commands()
	names := make(map[string]bool)
	for _, c := range cmds {
		names[c.Name()] = true
	}
	for _, name := range []string{"list", "get", "create", "update", "delete", "compare", "test-chat"} {
		if !names[name] {
			t.Errorf("expected subcommand %q to be registered", name)
		}
	}
}

// ==================== Phase 4: Scripts Upload Stdin Tests ====================

func TestScriptsUploadStdin(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.Method != "POST" {
			t.Errorf("expected POST, got %s", r.Method)
		}
		if r.URL.Path != "/api/scripts/upload" {
			t.Errorf("expected /api/scripts/upload, got %s", r.URL.Path)
		}
		// Verify multipart form
		if err := r.ParseMultipartForm(10 << 20); err != nil {
			t.Errorf("failed to parse multipart form: %v", err)
		}
		file, header, err := r.FormFile("file")
		if err != nil {
			t.Fatalf("expected file in form: %v", err)
		}
		defer file.Close()
		if header.Filename != "test.sh" {
			t.Errorf("expected filename 'test.sh', got %q", header.Filename)
		}
		w.Header().Set("Content-Type", "application/json")
		fmt.Fprint(w, `{"name": "test.sh", "sizeBytes": 14, "message": "Uploaded"}`)
	}))
	defer server.Close()

	// Create a fresh command to avoid flag state leaking from other tests
	cmd := &cobra.Command{
		Use:  "upload",
		RunE: scriptsUploadCmd.RunE,
	}
	cmd.Flags().Bool("stdin", false, "Read script content from stdin")
	cmd.Flags().String("filename", "", "Filename for stdin upload")
	cmd.Flags().String("output", "", "Output format")
	cmd.Flags().Bool("yes", false, "Skip confirmation")
	cmd.Flags().Bool("quiet", false, "Quiet mode")
	cmd.Flags().Bool("dry-run", false, "Dry run")

	capture := setupTest(cmd, server.URL)
	cmd.Flags().Set("stdin", "true")
	cmd.Flags().Set("filename", "test.sh")

	// Simulate stdin input
	stdinReader = strings.NewReader("#!/bin/bash\necho hello")
	defer func() { stdinReader = nil }()

	err := cmd.RunE(cmd, nil)
	if err != nil {
		t.Fatalf("expected no error, got: %v", err)
	}

	out := capture()
	if !strings.Contains(out, "test.sh") || !strings.Contains(out, "Uploaded") {
		t.Errorf("expected upload result in output, got: %s", out)
	}
}

func TestScriptsUploadStdinDefaultFilename(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if err := r.ParseMultipartForm(10 << 20); err != nil {
			t.Errorf("failed to parse multipart form: %v", err)
		}
		_, header, err := r.FormFile("file")
		if err != nil {
			t.Fatalf("expected file in form: %v", err)
		}
		defer func() {
			if header != nil {
				_ = header
			}
		}()
		if header.Filename != "script.sh" {
			t.Errorf("expected default filename 'script.sh', got %q", header.Filename)
		}
		w.Header().Set("Content-Type", "application/json")
		fmt.Fprint(w, `{"name": "script.sh", "message": "Uploaded"}`)
	}))
	defer server.Close()

	cmd := &cobra.Command{
		Use:  "upload",
		RunE: scriptsUploadCmd.RunE,
	}
	cmd.Flags().Bool("stdin", false, "Read script content from stdin")
	cmd.Flags().String("filename", "", "Filename for stdin upload")
	cmd.Flags().String("output", "", "Output format")
	cmd.Flags().Bool("yes", false, "Skip confirmation")
	cmd.Flags().Bool("quiet", false, "Quiet mode")
	cmd.Flags().Bool("dry-run", false, "Dry run")

	capture := setupTest(cmd, server.URL)
	cmd.Flags().Set("stdin", "true")

	stdinReader = strings.NewReader("#!/bin/bash\necho hello")
	defer func() { stdinReader = nil }()

	err := cmd.RunE(cmd, nil)
	if err != nil {
		t.Fatalf("expected no error, got: %v", err)
	}

	out := capture()
	if !strings.Contains(out, "script.sh") {
		t.Errorf("expected default filename in output, got: %s", out)
	}
}

func TestScriptsUploadStdinEmpty(t *testing.T) {
	cmd := &cobra.Command{
		Use:  "upload",
		RunE: scriptsUploadCmd.RunE,
	}
	cmd.Flags().Bool("stdin", false, "Read script content from stdin")
	cmd.Flags().String("filename", "", "Filename for stdin upload")
	cmd.Flags().String("output", "", "Output format")
	cmd.Flags().Bool("yes", false, "Skip confirmation")
	cmd.Flags().Bool("quiet", false, "Quiet mode")
	cmd.Flags().Bool("dry-run", false, "Dry run")

	setupTest(cmd, "http://unused")
	cmd.Flags().Set("stdin", "true")

	stdinReader = strings.NewReader("")
	defer func() { stdinReader = nil }()

	err := cmd.RunE(cmd, nil)
	if err == nil {
		t.Fatal("expected error for empty stdin")
	}
}

func TestScriptsUploadNoFileNoStdin(t *testing.T) {
	cmd := &cobra.Command{
		Use:  "upload",
		RunE: scriptsUploadCmd.RunE,
	}
	cmd.Flags().Bool("stdin", false, "Read script content from stdin")
	cmd.Flags().String("filename", "", "Filename for stdin upload")
	cmd.Flags().String("output", "", "Output format")
	cmd.Flags().Bool("yes", false, "Skip confirmation")
	cmd.Flags().Bool("quiet", false, "Quiet mode")
	cmd.Flags().Bool("dry-run", false, "Dry run")

	setupTest(cmd, "http://unused")

	err := cmd.RunE(cmd, nil)
	if err == nil {
		t.Fatal("expected error for no file and no --stdin")
	}
}

// ==================== Phase 4: Queue List Tests ====================

func TestQueueList(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.URL.Path != "/api/queue/snapshot" {
			t.Errorf("expected /api/queue/snapshot, got %s", r.URL.Path)
		}
		w.Header().Set("Content-Type", "application/json")
		fmt.Fprint(w, `{"processing": [
			{"id": "item-2", "status": "processing", "target": "agent-2", "priority": "normal", "createdAt": "2025-06-01T01:00:00Z"}
		], "waiting": [
			{"id": "item-1", "status": "waiting", "target": "agent-1", "priority": "high", "createdAt": "2025-06-01T00:00:00Z"},
			{"id": "item-3", "status": "waiting", "target": "agent-3", "priority": "low", "createdAt": "2025-06-01T02:00:00Z"}
		], "recentCompleted": [], "skipsUsed": 0, "skipsRemaining": 0}`)
	}))
	defer server.Close()

	capture := setupTest(queueListCmd, server.URL)
	err := queueListCmd.RunE(queueListCmd, nil)
	if err != nil {
		t.Fatalf("expected no error, got: %v", err)
	}

	out := capture()
	if !strings.Contains(out, "item-1") || !strings.Contains(out, "item-2") || !strings.Contains(out, "item-3") {
		t.Errorf("expected all items in output, got: %s", out)
	}
}

func TestQueueListFilterStatus(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		fmt.Fprint(w, `{"processing": [
			{"id": "item-2", "status": "processing", "target": "agent-2", "priority": "normal", "createdAt": "2025-06-01T01:00:00Z"}
		], "waiting": [
			{"id": "item-1", "status": "waiting", "target": "agent-1", "priority": "high", "createdAt": "2025-06-01T00:00:00Z"},
			{"id": "item-3", "status": "waiting", "target": "agent-3", "priority": "low", "createdAt": "2025-06-01T02:00:00Z"}
		], "recentCompleted": [], "skipsUsed": 0, "skipsRemaining": 0}`)
	}))
	defer server.Close()

	cmd := &cobra.Command{
		Use:  "list",
		RunE: queueListCmd.RunE,
	}
	cmd.Flags().String("status", "", "Filter by status")
	cmd.Flags().String("output", "", "Output format")
	cmd.Flags().Bool("yes", false, "Skip confirmation")
	cmd.Flags().Bool("quiet", false, "Quiet mode")
	cmd.Flags().Bool("dry-run", false, "Dry run")

	capture := setupTest(cmd, server.URL)
	cmd.Flags().Set("status", "waiting")

	err := cmd.RunE(cmd, nil)
	if err != nil {
		t.Fatalf("expected no error, got: %v", err)
	}

	out := capture()
	if !strings.Contains(out, "item-1") || !strings.Contains(out, "item-3") {
		t.Errorf("expected waiting items in output, got: %s", out)
	}
	if strings.Contains(out, "item-2") {
		t.Errorf("expected processing item to be filtered out, got: %s", out)
	}
}

func TestQueueListEmpty(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		fmt.Fprint(w, `{"processing": [], "waiting": [], "recentCompleted": [], "skipsUsed": 0, "skipsRemaining": 0}`)
	}))
	defer server.Close()

	capture := setupTest(queueListCmd, server.URL)
	err := queueListCmd.RunE(queueListCmd, nil)
	if err != nil {
		t.Fatalf("expected no error, got: %v", err)
	}

	out := capture()
	// Should produce valid output (empty table)
	if strings.Contains(out, "error") {
		t.Errorf("expected no error in output, got: %s", out)
	}
}

func TestQueueListNoItemsKey(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		fmt.Fprint(w, `{"currentSlot": "slot-1", "processing": 0}`)
	}))
	defer server.Close()

	capture := setupTest(queueListCmd, server.URL)
	err := queueListCmd.RunE(queueListCmd, nil)
	if err != nil {
		t.Fatalf("expected no error, got: %v", err)
	}

	out := capture()
	// Should produce valid output (empty)
	if strings.Contains(out, "error") {
		t.Errorf("expected no error in output, got: %s", out)
	}
}

func TestQueueListAPIError(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		w.WriteHeader(500)
		fmt.Fprint(w, `{"error": "internal_error", "message": "Server error", "exitCode": 1}`)
	}))
	defer server.Close()

	capture := setupTest(queueListCmd, server.URL)
	queueListCmd.RunE(queueListCmd, nil)

	out := capture()
	if !strings.Contains(out, "internal_error") {
		t.Errorf("expected error in output, got: %s", out)
	}
}

func TestQueueListJSONOutput(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		fmt.Fprint(w, `{"processing": [], "waiting": [
			{"id": "item-1", "status": "waiting", "target": "agent-1", "priority": "high", "createdAt": "2025-06-01T00:00:00Z"}
		], "recentCompleted": [], "skipsUsed": 0, "skipsRemaining": 0}`)
	}))
	defer server.Close()

	capture := setupTestFmt(queueListCmd, server.URL, output.FormatJSON)
	err := queueListCmd.RunE(queueListCmd, nil)
	if err != nil {
		t.Fatalf("expected no error, got: %v", err)
	}

	out := capture()
	if !strings.Contains(out, "item-1") || !strings.Contains(out, "waiting") {
		t.Errorf("expected queue item data in output, got: %s", out)
	}
}

// ==================== Benchmarks Wait Tests ====================

func TestBenchmarksRunWaitFlag(t *testing.T) {
	flag := benchmarksRunCmd.Flags().Lookup("wait")
	if flag == nil {
		t.Fatal("expected --wait flag to be registered")
	}
	if flag.DefValue != "false" {
		t.Errorf("expected --wait default to be 'false', got '%s'", flag.DefValue)
	}
}

func TestBenchmarksRunWait(t *testing.T) {
	callCount := 0
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		callCount++
		switch r.URL.Path {
		case "/api/benchmarks/run":
			fmt.Fprint(w, `{"id": "bm-wait-1", "status": "running"}`)
		case "/api/benchmarks/bm-wait-1":
			// First poll: still running, second poll: completed
			if callCount <= 2 {
				fmt.Fprint(w, `{"id": "bm-wait-1", "modelId": "m1", "modelName": "gpt-4o", "tokensPerSecond": 55.3, "latencyMs": 800, "totalTokens": 340, "status": "running"}`)
			} else {
				fmt.Fprint(w, `{"id": "bm-wait-1", "modelId": "m1", "modelName": "gpt-4o", "tokensPerSecond": 55.3, "latencyMs": 800, "totalTokens": 340, "status": "completed"}`)
			}
		default:
			w.WriteHeader(404)
		}
	}))
	defer server.Close()

	// Use a fresh command to avoid flag state leaking.
	cmd := &cobra.Command{
		Use:  "run",
		RunE: benchmarksRunCmd.RunE,
	}
	cmd.Flags().String("prompt", "", "")
	cmd.Flags().String("prompt-id", "", "")
	cmd.Flags().Bool("wait", false, "")

	capture := setupTest(cmd, server.URL)
	cmd.Flags().Set("wait", "true")

	err := cmd.RunE(cmd, []string{testHexID})
	if err != nil {
		t.Fatalf("expected no error, got: %v", err)
	}

	out := capture()
	if !strings.Contains(out, "bm-wait-1") {
		t.Errorf("expected benchmark ID in output, got: %s", out)
	}
	if !strings.Contains(out, "completed") {
		t.Errorf("expected completed status in output, got: %s", out)
	}
}

// ==================== Agent Stats Tests ====================

func TestAgentStats(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		fmt.Fprint(w, `[{
			"name": "agent-1",
			"telemetry": {
				"host": {"cpuPercent": 45.2, "ramPercent": 62.1, "ramUsedMb": 20000, "ramTotalMb": 32768},
				"gpus": [{"index": 0, "name": "RTX 4090", "corePercent": 78.0, "memoryPercent": 50.2, "memoryUsedMb": 12345, "memoryTotalMb": 24564}],
				"containers": {"abc12345": {"cpuPercent": 45.0, "ramPercent": 33.0, "ramUsedMb": 2048, "ramTotalMb": 6144}}
			}
		}]`)
	}))
	defer server.Close()

	capture := setupTestFmt(agentsStatsCmd, server.URL, output.FormatTable)
	err := agentsStatsCmd.RunE(agentsStatsCmd, []string{"agent-1"})
	if err != nil {
		t.Fatalf("expected no error, got: %v", err)
	}

	out := capture()
	if !strings.Contains(out, "CPU") {
		t.Errorf("expected CPU resource in output, got: %s", out)
	}
	if !strings.Contains(out, "RAM") {
		t.Errorf("expected RAM resource in output, got: %s", out)
	}
	if !strings.Contains(out, "RTX 4090") {
		t.Errorf("expected GPU name in output, got: %s", out)
	}
	if !strings.Contains(out, "45.2%") {
		t.Errorf("expected CPU percent in output, got: %s", out)
	}
	if !strings.Contains(out, "abc12345") {
		t.Errorf("expected container ID in output, got: %s", out)
	}
}

func TestAgentStatsNotFound(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		fmt.Fprint(w, `[{"name": "agent-1"}]`)
	}))
	defer server.Close()

	capture := setupTest(agentsStatsCmd, server.URL)
	err := agentsStatsCmd.RunE(agentsStatsCmd, []string{"nonexistent"})
	if err == nil {
		t.Fatal("expected error for nonexistent agent")
	}

	_ = capture()
}

func TestAgentStatsNoTelemetry(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		fmt.Fprint(w, `[{"name": "agent-no-tel"}]`)
	}))
	defer server.Close()

	capture := setupTest(agentsStatsCmd, server.URL)
	err := agentsStatsCmd.RunE(agentsStatsCmd, []string{"agent-no-tel"})
	if err == nil {
		t.Fatal("expected error for agent with no telemetry")
	}

	_ = capture()
}

func TestAgentStatsSubcommandRegistered(t *testing.T) {
	cmds := agentsCmd.Commands()
	names := make(map[string]bool)
	for _, c := range cmds {
		names[c.Name()] = true
	}
	if !names["stats"] {
		t.Error("expected 'stats' subcommand to be registered")
	}
}

// ==================== Metrics Cost Tests ====================

func TestMetricsCost(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		fmt.Fprint(w, `{"items": [
			{"model": "gpt-4o", "promptTokens": 1000, "completionTokens": 500, "provider": "openai"},
			{"model": "gpt-4o", "promptTokens": 2000, "completionTokens": 1000, "provider": "openai"},
			{"model": "llama-7b", "promptTokens": 5000, "completionTokens": 3000, "provider": "local"}
		]}`)
	}))
	defer server.Close()

	capture := setupTestFmt(metricsCostCmd, server.URL, output.FormatTable)
	metricsCostCmd.Flags().Set("rates", `{"gpt-4o":{"prompt":0.0025,"completion":0.01}}`)

	err := metricsCostCmd.RunE(metricsCostCmd, nil)
	if err != nil {
		t.Fatalf("expected no error, got: %v", err)
	}

	metricsCostCmd.Flags().Set("rates", "")

	out := capture()
	if !strings.Contains(out, "gpt-4o") {
		t.Errorf("expected gpt-4o in output, got: %s", out)
	}
	if !strings.Contains(out, "llama-7b") {
		t.Errorf("expected llama-7b in output, got: %s", out)
	}
	// gpt-4o: (3000 * 0.0025 + 1500 * 0.01) / 1000 = (7.5 + 15) / 1000 = 0.0225
	if !strings.Contains(out, "$0.02") {
		t.Errorf("expected cost calculation in output, got: %s", out)
	}
	if !strings.Contains(out, "TOTAL") {
		t.Errorf("expected TOTAL row in output, got: %s", out)
	}
}

func TestMetricsCostRequiresRates(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		fmt.Fprint(w, `{"items": []}`)
	}))
	defer server.Close()

	capture := setupTest(metricsCostCmd, server.URL)
	// Make sure rates is not set.
	metricsCostCmd.Flags().Set("rates", "")
	err := metricsCostCmd.RunE(metricsCostCmd, nil)
	if err == nil {
		t.Fatal("expected error when --rates not provided")
	}

	_ = capture()
}

func TestMetricsCostSubcommandRegistered(t *testing.T) {
	cmds := metricsCmd.Commands()
	names := make(map[string]bool)
	for _, c := range cmds {
		names[c.Name()] = true
	}
	if !names["cost"] {
		t.Error("expected 'cost' subcommand to be registered")
	}
}

// ==================== Logs Follow SSE Tests ====================

func TestLogsFollowSSE(t *testing.T) {
	// Start a mock SSE server
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.URL.Path == "/api/logs/stream" {
			w.Header().Set("Content-Type", "text/event-stream")
			w.Header().Set("Cache-Control", "no-cache")
			flusher, ok := w.(http.Flusher)
			if !ok {
				t.Fatal("streaming not supported")
			}

			// Send two SSE events then close
			events := []string{
				`{"id":"1","timestamp":"2025-06-01T00:00:00Z","level":"Info","source":"router","message":"first log line"}`,
				`{"id":"2","timestamp":"2025-06-01T00:00:01Z","level":"Error","source":"auth","message":"second log line"}`,
			}
			for _, ev := range events {
				fmt.Fprintf(w, "data: %s\n\n", ev)
				flusher.Flush()
			}
			// Signal done
			fmt.Fprintf(w, "data: [DONE]\n\n")
			flusher.Flush()
		} else {
			w.WriteHeader(404)
		}
	}))
	defer server.Close()

	capture := setupTestFmt(logsFollowCmd, server.URL, output.FormatJSON)
	// Override output format to non-JSON to test formatted output
	cfg := &client.Config{BaseURL: server.URL, OutputFmt: "", Color: false}
	c := client.New(cfg, client.WithAPIKey("test-key"))
	ctx := context.Background()
	ctx = context.WithValue(ctx, clientKey, c)
	w := output.NewWriter(output.FormatTable, true, false)
	ctx = context.WithValue(ctx, outputKey, w)
	logsFollowCmd.SetContext(ctx)

	// Set short interval for fallback
	logsFollowCmd.Flags().Set("interval", "1")

	// The SSE path returns [DONE] which makes the reader return io.EOF,
	// so it should break out of the SSE loop and try polling fallback.
	// With the server still up, it'll hit /api/logs for polling too.
	done := make(chan error, 1)
	go func() {
		done <- logsFollowCmd.RunE(logsFollowCmd, nil)
	}()

	// Let it run briefly then close server to force exit
	time.Sleep(2 * time.Second)
	server.Close()
	time.Sleep(2 * time.Second)

	_ = capture()

	select {
	case <-done:
	default:
		// Command may still be running polling fallback - that's ok
	}
}

func TestLogsFollowFallback(t *testing.T) {
	// Server returns 404 for SSE endpoint, but serves polling endpoint
	pollCalled := false
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.URL.Path == "/api/logs/stream" {
			w.WriteHeader(404)
			return
		}
		pollCalled = true
		w.Header().Set("Content-Type", "application/json")
		fmt.Fprint(w, `[{"timestamp":"2025-06-01T00:00:00Z","level":"info","source":"router","message":"poll entry"}]`)
	}))
	defer server.Close()

	capture := setupTestFmt(logsFollowCmd, server.URL, output.FormatJSON)
	cfg := &client.Config{BaseURL: server.URL, OutputFmt: "", Color: false}
	c := client.New(cfg, client.WithAPIKey("test-key"))
	ctx := context.Background()
	ctx = context.WithValue(ctx, clientKey, c)
	w := output.NewWriter(output.FormatTable, true, false)
	ctx = context.WithValue(ctx, outputKey, w)
	logsFollowCmd.SetContext(ctx)

	logsFollowCmd.Flags().Set("interval", "1")

	done := make(chan error, 1)
	go func() {
		done <- logsFollowCmd.RunE(logsFollowCmd, nil)
	}()

	// Let it poll once
	time.Sleep(2 * time.Second)
	server.Close()
	time.Sleep(2 * time.Second)

	_ = capture()

	if !pollCalled {
		t.Error("expected polling fallback to be used when SSE returns 404")
	}
}

// ==================== Health Summary Tests ====================

func TestHealthSummaryFlag(t *testing.T) {
	flag := healthCmd.Flags().Lookup("summary")
	if flag == nil {
		t.Fatal("expected --summary flag to be registered on health command")
	}
	if flag.DefValue != "false" {
		t.Errorf("expected --summary default to be 'false', got '%s'", flag.DefValue)
	}
}

func TestHealthSummaryOutput(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		switch r.URL.Path {
		case "/api/containers/registered":
			fmt.Fprint(w, `[{"id":"rt1","status":"ready","displayName":"GPU RT"},{"id":"rt2","status":"error","displayName":"CPU RT"}]`)
		case "/api/containers":
			fmt.Fprint(w, `[{"id":"c1","runtimeId":"rt1","status":"running"},{"id":"c2","runtimeId":"rt1","status":"running"},{"id":"c3","runtimeId":"rt2","status":"stopped"}]`)
		case "/api/stats":
			fmt.Fprint(w, `{"totalRequests":100,"containersRunning":2,"avgLatencyMs":450,"requestsPerMinute":12.5}`)
		case "/api/queue/snapshot":
			fmt.Fprint(w, `{"depth":2,"pending":1,"holding":0,"processing":1}`)
		default:
			w.WriteHeader(404)
		}
	}))
	defer server.Close()

	capture := setupTest(healthCmd, server.URL)
	healthCmd.Flags().Set("summary", "true")

	err := healthCmd.RunE(healthCmd, nil)
	if err != nil {
		t.Fatalf("expected no error, got: %v", err)
	}

	healthCmd.Flags().Set("summary", "false")

	out := capture()
	// Verify the summary line contains expected sections
	if !strings.Contains(out, "Runtimes:") {
		t.Errorf("expected 'Runtimes:' section in summary, got: %s", out)
	}
	if !strings.Contains(out, "Containers:") {
		t.Errorf("expected 'Containers:' section in summary, got: %s", out)
	}
	if !strings.Contains(out, "Queue:") {
		t.Errorf("expected 'Queue:' section in summary, got: %s", out)
	}
	if !strings.Contains(out, "Latency:") {
		t.Errorf("expected 'Latency:' section in summary, got: %s", out)
	}
	if !strings.Contains(out, "RPM:") {
		t.Errorf("expected 'RPM:' section in summary, got: %s", out)
	}
	if !strings.Contains(out, "450") {
		t.Errorf("expected latency value '450' in summary, got: %s", out)
	}
	if !strings.Contains(out, "12.5") {
		t.Errorf("expected RPM value '12.5' in summary, got: %s", out)
	}
}
