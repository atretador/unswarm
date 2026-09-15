package commands

import (
	"context"
	"encoding/json"
	"fmt"
	"os"
	"time"

	"github.com/spf13/cobra"
	"github.com/unswarm/cli/internal/client"
	"github.com/unswarm/cli/internal/resolve"
)

var benchmarksCmd = &cobra.Command{
	Use:   "benchmarks",
	Short: "Manage benchmarks",
	Long:  `Run and list model benchmarks.`,
}

// --- benchmarks run ---

var benchmarksRunCmd = &cobra.Command{
	Use:   "run <model-name-or-id>",
	Short: "Run a benchmark for a model",
	Args:  cobra.ExactArgs(1),
	RunE: func(cmd *cobra.Command, args []string) error {
		c := GetClient(cmd)
		w := GetOutput(cmd)

		// Resolve model name/ID to canonical ID.
		resolver := resolve.New(newResolveAdapter(c), "/api/models", "displayName")
		modelID, err := resolver.Resolve(cmd.Context(), args[0])
		if err != nil {
			return w.Error("resolve_error", err.Error(), nil, "", 1)
		}

		body := map[string]any{
			"modelId": modelID,
		}
		if cmd.Flags().Changed("prompt") {
			v, _ := cmd.Flags().GetString("prompt")
			body["prompt"] = v
		}
		if cmd.Flags().Changed("prompt-id") {
			v, _ := cmd.Flags().GetString("prompt-id")
			body["promptId"] = v
		}

		resp, err := c.Do(cmd.Context(), "POST", "/api/benchmarks/run", body)
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

		// If --wait is not set, print and return.
		wait, _ := cmd.Flags().GetBool("wait")
		if !wait {
			return w.Print(result)
		}

		// Extract benchmark ID from response.
		id, _ := result["id"].(string)
		if id == "" {
			return w.Error("missing_id", "benchmark response did not contain an id", nil, "", 1)
		}

		// Poll until completed or error, with 5-minute timeout.
		ctx, cancel := context.WithTimeout(cmd.Context(), 5*time.Minute)
		defer cancel()

		fmt.Fprint(os.Stderr, "Benchmarking")
		ticker := time.NewTicker(3 * time.Second)
		defer ticker.Stop()

		for {
			select {
			case <-ctx.Done():
				fmt.Fprintln(os.Stderr)
				return w.Error("timeout", "benchmark timed out after 5 minutes", nil, "", 1)
			case <-ticker.C:
				fmt.Fprint(os.Stderr, ".")

				getResp, err := c.Do(ctx, "GET", "/api/benchmarks/"+id, nil)
				if err != nil {
					fmt.Fprintln(os.Stderr)
					return FormatErrorResponse(w, err)
				}
				if getResp.StatusCode >= 400 {
					fmt.Fprintln(os.Stderr)
					apiErr := client.ParseError(getResp)
					return w.Error(apiErr.ErrCode, apiErr.Message, apiErr.Status, apiErr.Hint, apiErr.ExitCode)
				}

				var bm Benchmark
				if err := json.Unmarshal(getResp.Body, &bm); err != nil {
					fmt.Fprintln(os.Stderr)
					return w.Error("parse_error", "failed to parse benchmark response", nil, "", 1)
				}

				switch bm.Status {
				case "completed":
					fmt.Fprintln(os.Stderr)
					return w.Print(map[string]any{
						"id":              bm.ID,
						"modelId":         bm.ModelID,
						"modelName":       bm.ModelName,
						"tokensPerSecond": bm.TokensPerSecond,
						"latencyMs":       bm.LatencyMs,
						"tokensGenerated": bm.TotalTokens,
						"status":          bm.Status,
					})
				case "error":
					fmt.Fprintln(os.Stderr)
					return w.Error("benchmark_error", bm.ErrorMessage, nil, "", 1)
				}
			}
		}
	},
}

// --- benchmarks get ---

var benchmarksGetCmd = &cobra.Command{
	Use:   "get <id>",
	Short: "Get a single benchmark result",
	Args:  cobra.ExactArgs(1),
	RunE: func(cmd *cobra.Command, args []string) error {
		c := GetClient(cmd)
		w := GetOutput(cmd)

		path := "/api/benchmarks/" + args[0]
		resp, err := c.Do(cmd.Context(), "GET", path, nil)
		if err != nil {
			return FormatErrorResponse(w, err)
		}
		if resp.StatusCode >= 400 {
			apiErr := client.ParseError(resp)
			return w.Error(apiErr.ErrCode, apiErr.Message, apiErr.Status, apiErr.Hint, apiErr.ExitCode)
		}

		var result Benchmark
		if err := json.Unmarshal(resp.Body, &result); err != nil {
			return w.Error("parse_error", "failed to parse response", nil, "", 1)
		}
		return w.Print(result)
	},
}

// --- benchmarks list ---

var benchmarksListCmd = &cobra.Command{
	Use:   "list",
	Short: "List benchmarks",
	RunE: func(cmd *cobra.Command, args []string) error {
		c := GetClient(cmd)
		w := GetOutput(cmd)

		resp, err := c.Do(cmd.Context(), "GET", "/api/benchmarks", nil)
		if err != nil {
			return FormatErrorResponse(w, err)
		}
		if resp.StatusCode >= 400 {
			apiErr := client.ParseError(resp)
			return w.Error(apiErr.ErrCode, apiErr.Message, apiErr.Status, apiErr.Hint, apiErr.ExitCode)
		}

		var benchmarks []Benchmark
		if err := json.Unmarshal(resp.Body, &benchmarks); err != nil {
			return w.Error("parse_error", "failed to parse response", nil, "", 1)
		}

		// Apply --model filter (resolve name/ID first).
		if cmd.Flags().Changed("model") {
			modelInput, _ := cmd.Flags().GetString("model")
			resolver := resolve.New(newResolveAdapter(c), "/api/models", "displayName")
			modelID, err := resolver.Resolve(cmd.Context(), modelInput)
			if err != nil {
				return w.Error("resolve_error", err.Error(), nil, "", 1)
			}
			filtered := make([]Benchmark, 0)
			for _, b := range benchmarks {
				if b.ModelID == modelID {
					filtered = append(filtered, b)
				}
			}
			benchmarks = filtered
		}

		// Apply --status filter.
		if cmd.Flags().Changed("status") {
			statusFilter, _ := cmd.Flags().GetString("status")
			filtered := make([]Benchmark, 0)
			for _, b := range benchmarks {
				if b.Status == statusFilter {
					filtered = append(filtered, b)
				}
			}
			benchmarks = filtered
		}

		headers := []string{"ID", "MODEL", "TOKENS/S", "LATENCY", "STATUS", "TIME"}
		rows := make([][]string, 0, len(benchmarks))
		for _, b := range benchmarks {
			// Show model name from response data, fall back to model ID.
			modelDisplay := b.ModelName
			if modelDisplay == "" {
				modelDisplay = b.ModelID
			}
			if modelDisplay == "" {
				modelDisplay = "-"
			}
			rows = append(rows, []string{
				dashIfEmpty(b.ID),
				modelDisplay,
				floatOrDash(b.TokensPerSecond),
				floatOrDash(b.LatencyMs),
				dashIfEmpty(b.Status),
				dashIfEmpty(b.CreatedAt),
			})
		}
		return w.PrintTable(headers, rows)
	},
}

// --- benchmarks compare ---

var benchmarksCompareCmd = &cobra.Command{
	Use:   "compare <model1> <model2>",
	Short: "Compare benchmarks for two models side by side",
	Args:  cobra.ExactArgs(2),
	RunE: func(cmd *cobra.Command, args []string) error {
		c := GetClient(cmd)
		w := GetOutput(cmd)
		ctx := cmd.Context()

		// Resolve both model names/IDs.
		resolver := resolve.New(newResolveAdapter(c), "/api/models", "displayName")
		model1ID, err := resolver.Resolve(ctx, args[0])
		if err != nil {
			return w.Error("resolve_error", fmt.Sprintf("model 1: %s", err), nil, "", 1)
		}
		model2ID, err := resolver.Resolve(ctx, args[1])
		if err != nil {
			return w.Error("resolve_error", fmt.Sprintf("model 2: %s", err), nil, "", 1)
		}

		// Fetch all benchmarks.
		resp, err := c.Do(ctx, "GET", "/api/benchmarks", nil)
		if err != nil {
			return FormatErrorResponse(w, err)
		}
		if resp.StatusCode >= 400 {
			apiErr := client.ParseError(resp)
			return w.Error(apiErr.ErrCode, apiErr.Message, apiErr.Status, apiErr.Hint, apiErr.ExitCode)
		}

		var benchmarks []Benchmark
		if err := json.Unmarshal(resp.Body, &benchmarks); err != nil {
			return w.Error("parse_error", "failed to parse response", nil, "", 1)
		}

		// Split benchmarks by model.
		var model1Benchmarks, model2Benchmarks []Benchmark
		for _, b := range benchmarks {
			switch b.ModelID {
			case model1ID:
				model1Benchmarks = append(model1Benchmarks, b)
			case model2ID:
				model2Benchmarks = append(model2Benchmarks, b)
			}
		}

		// Build display names from response data.
		model1Name := args[0]
		model2Name := args[1]
		if len(model1Benchmarks) > 0 && model1Benchmarks[0].ModelName != "" {
			model1Name = model1Benchmarks[0].ModelName
		}
		if len(model2Benchmarks) > 0 && model2Benchmarks[0].ModelName != "" {
			model2Name = model2Benchmarks[0].ModelName
		}

		// Compute aggregates for each model (use most recent benchmark).
		aggregate := func(bms []Benchmark) (tokensPerSec, latency, status string, count int) {
			count = len(bms)
			if count == 0 {
				return "-", "-", "-", 0
			}
			latest := bms[count-1]
			tokensPerSec = floatOrDash(latest.TokensPerSecond)
			latency = floatOrDash(latest.LatencyMs)
			status = dashIfEmpty(latest.Status)
			return
		}

		t1, l1, s1, c1 := aggregate(model1Benchmarks)
		t2, l2, s2, c2 := aggregate(model2Benchmarks)

		headers := []string{"METRIC", model1Name, model2Name}
		rows := [][]string{
			{"Tokens/s", t1, t2},
			{"Latency (ms)", l1, l2},
			{"Status", s1, s2},
			{"Prompt Count", fmt.Sprintf("%d", c1), fmt.Sprintf("%d", c2)},
		}
		return w.PrintTable(headers, rows)
	},
}

func init() {
	benchmarksRunCmd.Flags().String("prompt", "", "Benchmark prompt text (optional)")
	benchmarksRunCmd.Flags().String("prompt-id", "", "Prompt ID to use (optional)")
	benchmarksRunCmd.Flags().Bool("wait", false, "Wait for benchmark to complete and show results")

	benchmarksListCmd.Flags().String("model", "", "Filter by model name or ID")
	benchmarksListCmd.Flags().String("status", "", "Filter by status (completed, error)")

	benchmarksCmd.AddCommand(benchmarksRunCmd)
	benchmarksCmd.AddCommand(benchmarksGetCmd)
	benchmarksCmd.AddCommand(benchmarksListCmd)
	benchmarksCmd.AddCommand(benchmarksCompareCmd)

	rootCmd.AddCommand(benchmarksCmd)
}
