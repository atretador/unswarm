package commands

import (
	"encoding/json"
	"fmt"
	"io"
	"os"
	"strconv"
	"strings"
	"time"

	"github.com/spf13/cobra"
	"github.com/unswarm/cli/internal/client"
	"github.com/unswarm/cli/internal/interact"
	"github.com/unswarm/cli/internal/output"
	"github.com/unswarm/cli/internal/resolve"
)

var modelsCmd = &cobra.Command{
	Use:   "models",
	Short: "Manage models",
	Long:  `List, create, update, delete, and test-chat with models.`,
}

// --- models list ---

var modelsListCmd = &cobra.Command{
	Use:   "list",
	Short: "List all models",
	RunE: func(cmd *cobra.Command, args []string) error {
		c := GetClient(cmd)
		w := GetOutput(cmd)

		resp, err := c.Do(cmd.Context(), "GET", "/api/models", nil)
		if err != nil {
			return FormatErrorResponse(w, err)
		}
		if resp.StatusCode >= 400 {
			apiErr := client.ParseError(resp)
			return w.Error(apiErr.ErrCode, apiErr.Message, apiErr.Status, apiErr.Hint, apiErr.ExitCode)
		}

		var models []Model
		if err := json.Unmarshal(resp.Body, &models); err != nil {
			return w.Error("parse_error", "failed to parse response", nil, "", 1)
		}

		// Client-side status filtering
		statusFilter, _ := cmd.Flags().GetString("status")
		if statusFilter != "" {
			filtered := models[:0]
			for _, m := range models {
				if strings.EqualFold(m.Status, statusFilter) {
					filtered = append(filtered, m)
				}
			}
			models = filtered
		}

		headers := []string{"ID", "NAME", "FAMILY", "SIZE", "STATUS", "ORIGIN"}
		rows := make([][]string, 0, len(models))
		for _, m := range models {
			rows = append(rows, []string{
				dashIfEmpty(m.ID),
				dashIfEmpty(m.Name),
				dashIfEmpty(m.Family),
				dashIfEmpty(m.ParameterSize),
				dashIfEmpty(m.Status),
				dashIfEmpty(m.Origin),
			})
		}
		return w.PrintTable(headers, rows)
	},
}

// --- models get ---

var modelsGetCmd = &cobra.Command{
	Use:   "get <name-or-id>",
	Short: "Get a model by name or ID",
	Args:  cobra.ExactArgs(1),
	RunE: func(cmd *cobra.Command, args []string) error {
		c := GetClient(cmd)
		w := GetOutput(cmd)

		resolver := resolve.New(newResolveAdapter(c), "/api/models", "displayName")
		resolvedID, err := resolver.Resolve(cmd.Context(), args[0])
		if err != nil {
			return w.Error("not_found", err.Error(), nil, "use 'models list' to see available models", 1)
		}

		resp, err := c.Do(cmd.Context(), "GET", "/api/models/"+resolvedID, nil)
		if err != nil {
			return FormatErrorResponse(w, err)
		}
		if resp.StatusCode >= 400 {
			apiErr := client.ParseError(resp)
			return w.Error(apiErr.ErrCode, apiErr.Message, apiErr.Status, apiErr.Hint, apiErr.ExitCode)
		}

		var model Model
		if err := json.Unmarshal(resp.Body, &model); err != nil {
			return w.Error("parse_error", "failed to parse response", nil, "", 1)
		}
		return w.Print(model)
	},
}

// --- models create ---

// detectInteractiveModelCreate returns true if no flags were explicitly set,
// meaning we should enter interactive wizard mode.
func detectInteractiveModelCreate(cmd *cobra.Command) bool {
	for _, name := range []string{"name", "family", "parameter-size", "quantization", "context-window", "container-image", "thinking-efforts"} {
		if cmd.Flags().Changed(name) {
			return false
		}
	}
	return true
}

// runInteractiveModelCreate prompts the user for model fields, confirms, then creates.
func runInteractiveModelCreate(cmd *cobra.Command, c *client.Client, w *output.Writer) error {
	// --- prompts ---
	name, err := interact.Input("Model name:")
	if err != nil || name == "" {
		return fmt.Errorf("model name is required")
	}

	familyOptions := []string{"llama", "mistral", "phi", "gemma", "qwen", "deepseek", "codellama", "other/custom"}
	familyIdx, err := interact.Select("Model family", familyOptions)
	if err != nil || familyIdx < 0 {
		return fmt.Errorf("model family is required (select 1-8)")
	}
	family := familyOptions[familyIdx]

	paramSizeStr, err := interact.Input("Parameter size [7B]:")
	if err != nil {
		return fmt.Errorf("invalid parameter size: %w", err)
	}
	if paramSizeStr == "" {
		paramSizeStr = "7B"
	}

	quantizationStr, err := interact.Input("Quantization [Q4_K_M]:")
	if err != nil {
		return fmt.Errorf("invalid quantization: %w", err)
	}
	if quantizationStr == "" {
		quantizationStr = "Q4_K_M"
	}

	contextWindowStr, err := interact.Input("Context window [4096]:")
	if err != nil {
		return fmt.Errorf("invalid context window: %w", err)
	}
	contextWindow := 4096
	if contextWindowStr != "" {
		contextWindow, err = strconv.Atoi(contextWindowStr)
		if err != nil {
			return fmt.Errorf("invalid context window: %w", err)
		}
	}

	containerImage, err := interact.Input("Container image (optional):")
	if err != nil {
		return fmt.Errorf("invalid container image: %w", err)
	}

	thinkingEfforts, err := interact.Input("Thinking efforts JSON (optional):")
	if err != nil {
		return fmt.Errorf("invalid thinking efforts: %w", err)
	}

	// --- summary ---
	summary := fmt.Sprintf("Create model:\n  Name:             %s\n  Family:           %s\n  Parameter size:   %s\n  Quantization:     %s\n  Context window:   %d\n  Container image:  %s\n  Thinking efforts: %s\nProceed?",
		name, family, paramSizeStr, quantizationStr, contextWindow, containerImage, thinkingEfforts)

	confirmed, err := ConfirmOrSkip(cmd, summary)
	if err != nil {
		return err
	}
	if !confirmed {
		return nil
	}

	// --- build body & POST ---
	body := map[string]any{
		"name":          name,
		"family":        family,
		"parameterSize": paramSizeStr,
		"quantization":  quantizationStr,
		"contextWindow": contextWindow,
		"containerImage": containerImage,
	}
	if thinkingEfforts != "" {
		body["supportedThinkingEffortsJson"] = thinkingEfforts
	}

	resp, err := c.Do(cmd.Context(), "POST", "/api/models", body)
	if err != nil {
		return FormatErrorResponse(w, err)
	}
	if resp.StatusCode >= 400 {
		apiErr := client.ParseError(resp)
		return w.Error(apiErr.ErrCode, apiErr.Message, apiErr.Status, apiErr.Hint, apiErr.ExitCode)
	}

	var model map[string]any
	if err := json.Unmarshal(resp.Body, &model); err != nil {
		return w.Error("parse_error", "failed to parse response", nil, "", 1)
	}
	return w.Print(model)
}

var modelsCreateCmd = &cobra.Command{
	Use:   "create",
	Short: "Create a new model",
	RunE: func(cmd *cobra.Command, args []string) error {
		c := GetClient(cmd)
		w := GetOutput(cmd)

		// If no flags were provided, enter interactive wizard mode.
		if detectInteractiveModelCreate(cmd) {
			if IsQuiet(cmd) {
				return w.Error("missing_flags", "interactive mode requires a terminal; use --name and other flags", nil, "run: unswarm models create --name NAME --family FAMILY", 1)
			}
			return runInteractiveModelCreate(cmd, c, w)
		}

		// Scripting mode — exact same behaviour as before.
		name, _ := cmd.Flags().GetString("name")
		family, _ := cmd.Flags().GetString("family")
		paramSize, _ := cmd.Flags().GetString("parameter-size")
		quantization, _ := cmd.Flags().GetString("quantization")
		contextWindow, _ := cmd.Flags().GetInt("context-window")
		containerImage, _ := cmd.Flags().GetString("container-image")
		thinkingEfforts, _ := cmd.Flags().GetString("thinking-efforts")

		body := map[string]any{
			"name":          name,
			"family":        family,
			"parameterSize": paramSize,
			"quantization":  quantization,
			"contextWindow": contextWindow,
			"containerImage": containerImage,
		}
		if thinkingEfforts != "" {
			body["supportedThinkingEffortsJson"] = thinkingEfforts
		}

		resp, err := c.Do(cmd.Context(), "POST", "/api/models", body)
		if err != nil {
			return FormatErrorResponse(w, err)
		}
		if resp.StatusCode >= 400 {
			apiErr := client.ParseError(resp)
			return w.Error(apiErr.ErrCode, apiErr.Message, apiErr.Status, apiErr.Hint, apiErr.ExitCode)
		}

		var model map[string]any
		if err := json.Unmarshal(resp.Body, &model); err != nil {
			return w.Error("parse_error", "failed to parse response", nil, "", 1)
		}
		return w.Print(model)
	},
}

// --- models update ---

var modelsUpdateCmd = &cobra.Command{
	Use:   "update <name-or-id>",
	Short: "Update a model",
	Args:  cobra.ExactArgs(1),
	RunE: func(cmd *cobra.Command, args []string) error {
		c := GetClient(cmd)
		w := GetOutput(cmd)

		resolver := resolve.New(newResolveAdapter(c), "/api/models", "displayName")
		resolvedID, err := resolver.Resolve(cmd.Context(), args[0])
		if err != nil {
			return w.Error("not_found", err.Error(), nil, "use 'models list' to see available models", 1)
		}

		body := make(map[string]any)
		if cmd.Flags().Changed("name") {
			v, _ := cmd.Flags().GetString("name")
			body["name"] = v
		}
		if cmd.Flags().Changed("family") {
			v, _ := cmd.Flags().GetString("family")
			body["family"] = v
		}
		if cmd.Flags().Changed("parameter-size") {
			v, _ := cmd.Flags().GetString("parameter-size")
			body["parameterSize"] = v
		}
		if cmd.Flags().Changed("quantization") {
			v, _ := cmd.Flags().GetString("quantization")
			body["quantization"] = v
		}
		if cmd.Flags().Changed("status") {
			v, _ := cmd.Flags().GetString("status")
			body["status"] = v
		}
		if cmd.Flags().Changed("context-window") {
			v, _ := cmd.Flags().GetInt("context-window")
			body["contextWindow"] = v
		}
		if cmd.Flags().Changed("container-image") {
			v, _ := cmd.Flags().GetString("container-image")
			body["containerImage"] = v
		}
		if cmd.Flags().Changed("display-name") {
			v, _ := cmd.Flags().GetString("display-name")
			body["displayName"] = v
		}
		if cmd.Flags().Changed("thinking-efforts") {
			v, _ := cmd.Flags().GetString("thinking-efforts")
			body["supportedThinkingEffortsJson"] = v
		}

		resp, err := c.Do(cmd.Context(), "PUT", "/api/models/"+resolvedID, body)
		if err != nil {
			return FormatErrorResponse(w, err)
		}
		if resp.StatusCode >= 400 {
			apiErr := client.ParseError(resp)
			return w.Error(apiErr.ErrCode, apiErr.Message, apiErr.Status, apiErr.Hint, apiErr.ExitCode)
		}

		var model map[string]any
		if err := json.Unmarshal(resp.Body, &model); err != nil {
			return w.Error("parse_error", "failed to parse response", nil, "", 1)
		}
		return w.Print(model)
	},
}

// --- models delete ---

var modelsDeleteCmd = &cobra.Command{
	Use:   "delete <name-or-id>",
	Short: "Delete a model",
	Args:  cobra.ExactArgs(1),
	RunE: func(cmd *cobra.Command, args []string) error {
		c := GetClient(cmd)
		w := GetOutput(cmd)

		resolver := resolve.New(newResolveAdapter(c), "/api/models", "displayName")
		resolvedID, err := resolver.Resolve(cmd.Context(), args[0])
		if err != nil {
			return w.Error("not_found", err.Error(), nil, "use 'models list' to see available models", 1)
		}

		if IsDryRun(cmd) {
			w.DryRun(fmt.Sprintf("Would delete model %s", resolvedID))
			return nil
		}

		confirmed, err := ConfirmOrSkip(cmd, fmt.Sprintf("Delete model %s?", resolvedID))
		if err != nil {
			return err
		}
		if !confirmed {
			return nil
		}

		resp, err := c.Do(cmd.Context(), "DELETE", "/api/models/"+resolvedID, nil)
		if err != nil {
			return FormatErrorResponse(w, err)
		}
		if resp.StatusCode >= 400 {
			apiErr := client.ParseError(resp)
			return w.Error(apiErr.ErrCode, apiErr.Message, apiErr.Status, apiErr.Hint, apiErr.ExitCode)
		}

		return w.Print(map[string]any{"message": fmt.Sprintf("Deleted model %s", resolvedID)})
	},
}

// --- models compare ---

var modelsCompareCmd = &cobra.Command{
	Use:   "compare <model1> <model2>",
	Short: "Compare two models side by side",
	Args:  cobra.ExactArgs(2),
	RunE: func(cmd *cobra.Command, args []string) error {
		c := GetClient(cmd)
		w := GetOutput(cmd)
		ctx := cmd.Context()

		// Resolve both model names/IDs to canonical IDs.
		resolver := resolve.New(newResolveAdapter(c), "/api/models", "displayName")
		model1ID, err := resolver.Resolve(ctx, args[0])
		if err != nil {
			return w.Error("not_found", fmt.Sprintf("model 1: %s", err), nil, "use 'models list' to see available models", 1)
		}
		model2ID, err := resolver.Resolve(ctx, args[1])
		if err != nil {
			return w.Error("not_found", fmt.Sprintf("model 2: %s", err), nil, "use 'models list' to see available models", 1)
		}

		// Fetch both models via GET /api/models/{id}.
		resp1, err := c.Do(ctx, "GET", "/api/models/"+model1ID, nil)
		if err != nil {
			return FormatErrorResponse(w, err)
		}
		if resp1.StatusCode >= 400 {
			apiErr := client.ParseError(resp1)
			return w.Error(apiErr.ErrCode, apiErr.Message, apiErr.Status, apiErr.Hint, apiErr.ExitCode)
		}
		var model1 Model
		if err := json.Unmarshal(resp1.Body, &model1); err != nil {
			return w.Error("parse_error", "failed to parse model 1 response", nil, "", 1)
		}

		resp2, err := c.Do(ctx, "GET", "/api/models/"+model2ID, nil)
		if err != nil {
			return FormatErrorResponse(w, err)
		}
		if resp2.StatusCode >= 400 {
			apiErr := client.ParseError(resp2)
			return w.Error(apiErr.ErrCode, apiErr.Message, apiErr.Status, apiErr.Hint, apiErr.ExitCode)
		}
		var model2 Model
		if err := json.Unmarshal(resp2.Body, &model2); err != nil {
			return w.Error("parse_error", "failed to parse model 2 response", nil, "", 1)
		}

		// Fetch all benchmarks and filter client-side.
		benchResp, err := c.Do(ctx, "GET", "/api/benchmarks", nil)
		if err != nil {
			return FormatErrorResponse(w, err)
		}
		if benchResp.StatusCode >= 400 {
			apiErr := client.ParseError(benchResp)
			return w.Error(apiErr.ErrCode, apiErr.Message, apiErr.Status, apiErr.Hint, apiErr.ExitCode)
		}
		var allBenchmarks []Benchmark
		if err := json.Unmarshal(benchResp.Body, &allBenchmarks); err != nil {
			allBenchmarks = []Benchmark{}
		}

		// Find the most recent benchmark for each model.
		var bench1, bench2 Benchmark
		for _, b := range allBenchmarks {
			if b.ModelID == model1ID {
				bench1 = b // last one wins (most recent)
			}
			if b.ModelID == model2ID {
				bench2 = b
			}
		}

		// Fetch containers and filter by model name.
		containerResp, err := c.Do(ctx, "GET", "/api/containers", nil)
		if err != nil {
			return FormatErrorResponse(w, err)
		}
		if containerResp.StatusCode >= 400 {
			apiErr := client.ParseError(containerResp)
			return w.Error(apiErr.ErrCode, apiErr.Message, apiErr.Status, apiErr.Hint, apiErr.ExitCode)
		}
		var containers []map[string]any
		if err := json.Unmarshal(containerResp.Body, &containers); err != nil {
			containers = []map[string]any{}
		}

		name1 := dashIfEmpty(model1.Name)
		name2 := dashIfEmpty(model2.Name)

		var container1Status, container2Status string
		for _, ct := range containers {
			mn, _ := ct["modelName"].(string)
			if mn == name1 {
				s, _ := ct["status"].(string)
				container1Status = dashIfEmpty(s)
			}
			if mn == name2 {
				s, _ := ct["status"].(string)
				container2Status = dashIfEmpty(s)
			}
		}
		if container1Status == "" {
			container1Status = "-"
		}
		if container2Status == "" {
			container2Status = "-"
		}

		// Extract benchmark metrics.
		benchTok1 := floatOrDash(bench1.TokensPerSecond)
		benchTok2 := floatOrDash(bench2.TokensPerSecond)
		benchLat1 := floatOrDash(bench1.LatencyMs)
		benchLat2 := floatOrDash(bench2.LatencyMs)

		headers := []string{"PROPERTY", name1, name2}
		rows := [][]string{
			{"Name", name1, name2},
			{"Family", dashIfEmpty(model1.Family), dashIfEmpty(model2.Family)},
			{"Status", dashIfEmpty(model1.Status), dashIfEmpty(model2.Status)},
			{"Size", dashIfEmpty(model1.ParameterSize), dashIfEmpty(model2.ParameterSize)},
			{"Context", intOrDash(model1.ContextWindow), intOrDash(model2.ContextWindow)},
			{"Container", container1Status, container2Status},
			{"Bench tok/s", benchTok1, benchTok2},
			{"Bench latency", benchLat1, benchLat2},
		}
		return w.PrintTable(headers, rows)
	},
}

// --- models test-chat ---

var modelsTestChatCmd = &cobra.Command{
	Use:   "test-chat [model] [message]",
	Short: "Test a model with a chat completion",
	Args:  cobra.MaximumNArgs(2),
	RunE: func(cmd *cobra.Command, args []string) error {
		c := GetClient(cmd)
		w := GetOutput(cmd)

		// Determine model: flag takes precedence, then first positional arg
		model, _ := cmd.Flags().GetString("model")
		if model == "" && len(args) >= 1 {
			model = args[0]
		}

		// Resolve model name to ID
		if model != "" {
			resolver := resolve.New(newResolveAdapter(c), "/api/models", "displayName")
			resolvedID, err := resolver.Resolve(cmd.Context(), model)
			if err != nil {
				return w.Error("not_found", err.Error(), nil, "use 'models list' to see available models", 1)
			}
			model = resolvedID
		}

		// Determine messages: --messages flag or second positional arg
		var messages []map[string]string
		if len(args) >= 2 {
			// Positional message: auto-wrap as user message
			messages = []map[string]string{{"role": "user", "content": args[1]}}
		} else {
			messagesJSON, _ := cmd.Flags().GetString("messages")
			if err := json.Unmarshal([]byte(messagesJSON), &messages); err != nil {
				return w.Error("invalid_messages", "failed to parse messages JSON", nil, "provide messages as a JSON array of {role, content} objects", 1)
			}
		}

		system, _ := cmd.Flags().GetString("system")
		maxTokens, _ := cmd.Flags().GetInt("max-tokens")
		temperature, _ := cmd.Flags().GetFloat64("temperature")
		stream, _ := cmd.Flags().GetBool("stream")

		body := map[string]any{
			"model":   model,
			"messages": messages,
			"stream":  stream,
		}
		if system != "" {
			body["system"] = system
		}
		if cmd.Flags().Changed("max-tokens") {
			body["maxTokens"] = maxTokens
		}
		if cmd.Flags().Changed("temperature") {
			body["temperature"] = temperature
		}

		isJSON := w.GetFormat() == output.FormatJSON

		if stream {
			return runTestChatStream(cmd, c, w, body, isJSON)
		}

		// Buffered (non-streaming) path
		resp, err := c.Do(cmd.Context(), "POST", "/api/models/test-chat", body)
		if err != nil {
			return FormatErrorResponse(w, err)
		}
		if resp.StatusCode >= 400 {
			apiErr := client.ParseError(resp)
			return w.Error(apiErr.ErrCode, apiErr.Message, apiErr.Status, apiErr.Hint, apiErr.ExitCode)
		}

		var result map[string]any
		if err := json.Unmarshal(resp.Body, &result); err != nil {
			return w.Error("parse_error", "failed to parse response", nil, "", 1)
		}
		return w.Print(result)
	},
}

// openaiChunk represents a chunk in OpenAI-style SSE streaming
type openaiChunk struct {
	Choices []struct {
		Delta struct {
			Content string `json:"content"`
		} `json:"delta"`
	} `json:"choices"`
	Usage *struct {
		PromptTokens     int `json:"prompt_tokens"`
		CompletionTokens int `json:"completion_tokens"`
		TotalTokens      int `json:"total_tokens"`
	} `json:"usage,omitempty"`
}

func runTestChatStream(cmd *cobra.Command, c *client.Client, w *output.Writer, body map[string]any, isJSON bool) error {
	start := time.Now()

	reader, err := c.DoSSE(cmd.Context(), "POST", "/api/models/test-chat", body)
	if err != nil {
		// DoSSE already parsed the API error
		return FormatErrorResponse(w, err)
	}
	defer reader.Close()

	var allContent strings.Builder
	var lastChunk openaiChunk

	for {
		ev, err := reader.ReadEvent()
		if err != nil {
			if err == io.EOF {
				break
			}
			return FormatErrorResponse(w, err)
		}

		// Parse each event's Data as JSON
		var chunk openaiChunk
		if err := json.Unmarshal([]byte(ev.Data), &chunk); err != nil {
			continue // skip non-JSON events (e.g. empty data)
		}

		// Print content deltas to stderr incrementally
		if len(chunk.Choices) > 0 {
			content := chunk.Choices[0].Delta.Content
			if content != "" {
				fmt.Fprint(os.Stderr, content)
				allContent.WriteString(content)
			}
		}

		// Track usage from the last chunk that has it
		if chunk.Usage != nil {
			lastChunk = chunk
		}
	}

	latency := time.Since(start)

	if isJSON {
		// In JSON mode, output the collected text as a structured result
		result := map[string]any{
			"content": allContent.String(),
		}
		if lastChunk.Usage != nil {
			result["usage"] = map[string]any{
				"promptTokens":     lastChunk.Usage.PromptTokens,
				"completionTokens": lastChunk.Usage.CompletionTokens,
				"totalTokens":      lastChunk.Usage.TotalTokens,
			}
		}
		return w.Print(result)
	}

	// In human-readable mode, print summary to stderr
	if lastChunk.Usage != nil {
		tps := float64(0)
		if latency.Seconds() > 0 {
			tps = float64(lastChunk.Usage.CompletionTokens) / latency.Seconds()
		}
		fmt.Fprintf(os.Stderr, "\n[%d tokens, %dms, %.1f tok/s]\n",
			lastChunk.Usage.TotalTokens, latency.Milliseconds(), tps)
	} else {
		fmt.Fprintf(os.Stderr, "\n[%dms]\n", latency.Milliseconds())
	}

	return nil
}

func init() {
	// Create flags
	modelsCreateCmd.Flags().String("name", "", "Model name")
	modelsCreateCmd.Flags().String("family", "", "Model family")
	modelsCreateCmd.Flags().String("parameter-size", "", "Parameter size (e.g. 7B, 13B)")
	modelsCreateCmd.Flags().String("quantization", "", "Quantization method")
	modelsCreateCmd.Flags().Int("context-window", 0, "Context window size")
	modelsCreateCmd.Flags().String("container-image", "", "Container image")
	modelsCreateCmd.Flags().String("thinking-efforts", "", "Supported thinking efforts JSON")

	// Update flags (all optional)
	modelsUpdateCmd.Flags().String("name", "", "Model name")
	modelsUpdateCmd.Flags().String("family", "", "Model family")
	modelsUpdateCmd.Flags().String("parameter-size", "", "Parameter size")
	modelsUpdateCmd.Flags().String("quantization", "", "Quantization method")
	modelsUpdateCmd.Flags().String("status", "", "Status: ready|validating|invalid|deprecated|conflict")
	modelsUpdateCmd.Flags().Int("context-window", 0, "Context window size")
	modelsUpdateCmd.Flags().String("container-image", "", "Container image")
	modelsUpdateCmd.Flags().String("display-name", "", "Display name")
	modelsUpdateCmd.Flags().String("thinking-efforts", "", "Supported thinking efforts JSON")

	// List filters
	modelsListCmd.Flags().String("status", "", "Filter by status (ready, validating, etc.)")

	// Test-chat flags
	modelsTestChatCmd.Flags().String("model", "", "Model name or ID")
	modelsTestChatCmd.Flags().String("messages", "[]", "Messages as JSON array")
	modelsTestChatCmd.Flags().String("system", "", "System prompt")
	modelsTestChatCmd.Flags().Int("max-tokens", 32768, "Maximum tokens")
	modelsTestChatCmd.Flags().Float64("temperature", 0.0, "Temperature")
	modelsTestChatCmd.Flags().Bool("stream", true, "Stream response using SSE")

	modelsCmd.AddCommand(modelsListCmd)
	modelsCmd.AddCommand(modelsGetCmd)
	modelsCmd.AddCommand(modelsCreateCmd)
	modelsCmd.AddCommand(modelsUpdateCmd)
	modelsCmd.AddCommand(modelsDeleteCmd)
	modelsCmd.AddCommand(modelsCompareCmd)
	modelsCmd.AddCommand(modelsTestChatCmd)

	rootCmd.AddCommand(modelsCmd)
}

