package commands

import (
	"encoding/json"
	"fmt"
	"strconv"
	"time"

	"github.com/spf13/cobra"
	"github.com/unswarm/cli/internal/client"
	"github.com/unswarm/cli/internal/helpers"
	"github.com/unswarm/cli/internal/output"
	"github.com/unswarm/cli/internal/parse"
)

var metricsCmd = &cobra.Command{
	Use:   "metrics",
	Short: "View metrics and usage data",
	Long:  `Query usage metrics, summaries, provider stats, and more.`,
}

// --- metrics usage ---

var metricsUsageCmd = &cobra.Command{
	Use:   "usage",
	Short: "List usage records",
	RunE: func(cmd *cobra.Command, args []string) error {
		c := GetClient(cmd)
		w := GetOutput(cmd)

		path := "/api/metrics/usage"
		params := buildQueryString(cmd, "from", "to", "page", "page-size", "provider", "model")
		if params != "" {
			path += "?" + params
		}

		resp, err := c.Do(cmd.Context(), "GET", path, nil)
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

		return renderUsageResult(w, result)
	},
}

// renderUsageResult renders a usage API response as a table.
func renderUsageResult(w *output.Writer, result map[string]any) error {
	items, _ := result["items"].([]any)
	if items == nil {
		items = []any{}
	}

	headers := []string{"TIME", "PROVIDER", "MODEL", "PROMPT", "COMPLETION", "LATENCY"}
	rows := make([][]string, 0, len(items))
	for _, item := range items {
		m, ok := item.(map[string]any)
		if !ok {
			continue
		}
		rows = append(rows, []string{
			helpers.StrOrDash(m, "timestamp"),
			helpers.StrOrDash(m, "provider"),
			helpers.StrOrDash(m, "model"),
			helpers.IntStr(m["promptTokens"]),
			helpers.IntStr(m["completionTokens"]),
			helpers.IntStr(m["latencyMs"]),
		})
	}
	return w.PrintTable(headers, rows)
}

// --- metrics today ---

var metricsTodayCmd = &cobra.Command{
	Use:   "today",
	Short: "Usage for today so far",
	RunE: func(cmd *cobra.Command, args []string) error {
		c := GetClient(cmd)
		w := GetOutput(cmd)

		now := time.Now()
		midnight := time.Date(now.Year(), now.Month(), now.Day(), 0, 0, 0, 0, now.Location())
		fromStr := midnight.Format("2006-01-02T15:04:05Z07:00")
		toStr := now.Format("2006-01-02T15:04:05Z07:00")

		path := "/api/metrics/usage?from=" + fromStr + "&to=" + toStr
		// Append optional filters.
		extraParams := buildQueryString(cmd, "page", "page-size", "provider", "model")
		if extraParams != "" {
			path += "&" + extraParams
		}

		resp, err := c.Do(cmd.Context(), "GET", path, nil)
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

		return renderUsageResult(w, result)
	},
}

// --- metrics last ---

var metricsLastCmd = &cobra.Command{
	Use:   "last <duration>",
	Short: "Usage for the last duration (e.g. 7d, 24h, 2w)",
	Args:  cobra.ExactArgs(1),
	RunE: func(cmd *cobra.Command, args []string) error {
		c := GetClient(cmd)
		w := GetOutput(cmd)

		d, err := parse.ParseDuration(args[0])
		if err != nil {
			return w.Error("invalid_duration", fmt.Sprintf("invalid duration %q: %s", args[0], err), nil, "use a format like 7d, 24h, 2w, 1m", 1)
		}

		from := time.Now().Add(-d)
		fromStr := from.Format("2006-01-02T15:04:05Z07:00")

		path := "/api/metrics/usage?from=" + fromStr
		// Append optional filters.
		extraParams := buildQueryString(cmd, "to", "page", "page-size", "provider", "model")
		if extraParams != "" {
			path += "&" + extraParams
		}

		resp, err := c.Do(cmd.Context(), "GET", path, nil)
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

		return renderUsageResult(w, result)
	},
}

// --- metrics summary ---

var metricsSummaryCmd = &cobra.Command{
	Use:   "summary",
	Short: "Get time-bucketed usage summary",
	RunE: func(cmd *cobra.Command, args []string) error {
		c := GetClient(cmd)
		w := GetOutput(cmd)

		path := "/api/metrics/summary"
		params := buildQueryString(cmd, "from", "to", "granularity", "group-by")
		if params != "" {
			path += "?" + params
		}

		resp, err := c.Do(cmd.Context(), "GET", path, nil)
		if err != nil {
			return FormatErrorResponse(w, err)
		}
		if resp.StatusCode >= 400 {
			apiErr := client.ParseError(resp)
			return w.Error(apiErr.ErrCode, apiErr.Message, apiErr.Status, apiErr.Hint, apiErr.ExitCode)
		}

		var buckets []map[string]any
		if err := json.Unmarshal(resp.Body, &buckets); err != nil {
			return w.Error("parse_error", "failed to parse response", nil, "", 1)
		}

		headers := []string{"BUCKET", "GROUP", "REQUESTS", "TOKENS", "AVG LATENCY"}
		rows := make([][]string, 0, len(buckets))
		for _, b := range buckets {
			group := "-"
			if v, ok := b["groupBy"]; ok && v != nil {
				group = fmt.Sprintf("%v", v)
			}
			rows = append(rows, []string{
				helpers.StrOrDash(b, "bucket"),
				group,
				helpers.IntStr(b["requests"]),
				helpers.IntStr(b["totalTokens"]),
				helpers.IntStr(b["avgLatencyMs"]),
			})
		}

		// Add a sparkline row for totalTokens across buckets in table mode
		if w.GetFormat() == output.FormatTable && len(buckets) > 1 {
			tokenVals := make([]float64, 0, len(buckets))
			for _, b := range buckets {
				if v, ok := b["totalTokens"]; ok {
					tokenVals = append(tokenVals, float64(costToInt(v)))
				}
			}
			if len(tokenVals) > 0 {
				headers = append(headers, "TREND")
				spark := SparkLine(tokenVals, len(buckets))
				// Pad spark to match row count — add trend to each row
				for i := range rows {
					if i == 0 {
						rows[i] = append(rows[i], spark)
					} else {
						rows[i] = append(rows[i], "")
					}
				}
			}
		}

		return w.PrintTable(headers, rows)
	},
}

// --- metrics models ---

var metricsModelsCmd = &cobra.Command{
	Use:   "models",
	Short: "Get per-model usage summary",
	RunE: func(cmd *cobra.Command, args []string) error {
		c := GetClient(cmd)
		w := GetOutput(cmd)

		path := "/api/metrics/models"
		params := buildQueryString(cmd, "from", "to")
		if params != "" {
			path += "?" + params
		}

		resp, err := c.Do(cmd.Context(), "GET", path, nil)
		if err != nil {
			return FormatErrorResponse(w, err)
		}
		if resp.StatusCode >= 400 {
			apiErr := client.ParseError(resp)
			return w.Error(apiErr.ErrCode, apiErr.Message, apiErr.Status, apiErr.Hint, apiErr.ExitCode)
		}

		var models []map[string]any
		if err := json.Unmarshal(resp.Body, &models); err != nil {
			return w.Error("parse_error", "failed to parse response", nil, "", 1)
		}

		headers := []string{"MODEL", "REQUESTS", "PROMPT TOKENS", "COMPLETION TOKENS", "AVG LATENCY"}
		rows := make([][]string, 0, len(models))
		for _, m := range models {
			rows = append(rows, []string{
				helpers.StrOrDash(m, "model"),
				helpers.IntStr(m["requests"]),
				helpers.IntStr(m["promptTokens"]),
				helpers.IntStr(m["completionTokens"]),
				helpers.IntStr(m["avgLatencyMs"]),
			})
		}
		return w.PrintTable(headers, rows)
	},
}

// --- metrics providers ---

var metricsProvidersCmd = &cobra.Command{
	Use:   "providers",
	Short: "Get per-provider usage summary",
	RunE: func(cmd *cobra.Command, args []string) error {
		c := GetClient(cmd)
		w := GetOutput(cmd)

		path := "/api/metrics/providers"
		params := buildQueryString(cmd, "from", "to")
		if params != "" {
			path += "?" + params
		}

		resp, err := c.Do(cmd.Context(), "GET", path, nil)
		if err != nil {
			return FormatErrorResponse(w, err)
		}
		if resp.StatusCode >= 400 {
			apiErr := client.ParseError(resp)
			return w.Error(apiErr.ErrCode, apiErr.Message, apiErr.Status, apiErr.Hint, apiErr.ExitCode)
		}

		var providers []map[string]any
		if err := json.Unmarshal(resp.Body, &providers); err != nil {
			return w.Error("parse_error", "failed to parse response", nil, "", 1)
		}

		headers := []string{"PROVIDER", "REQUESTS", "TOKENS"}
		rows := make([][]string, 0, len(providers))
		for _, p := range providers {
			rows = append(rows, []string{
				helpers.StrOrDash(p, "provider"),
				helpers.IntStr(p["requests"]),
				helpers.IntStr(p["totalTokens"]),
			})
		}
		return w.PrintTable(headers, rows)
	},
}

// --- metrics totals ---

var metricsTotalsCmd = &cobra.Command{
	Use:   "totals",
	Short: "Get overall usage totals",
	RunE: func(cmd *cobra.Command, args []string) error {
		c := GetClient(cmd)
		w := GetOutput(cmd)

		path := "/api/metrics/totals"
		params := buildQueryString(cmd, "from", "to")
		if params != "" {
			path += "?" + params
		}

		resp, err := c.Do(cmd.Context(), "GET", path, nil)
		if err != nil {
			return FormatErrorResponse(w, err)
		}
		if resp.StatusCode >= 400 {
			apiErr := client.ParseError(resp)
			return w.Error(apiErr.ErrCode, apiErr.Message, apiErr.Status, apiErr.Hint, apiErr.ExitCode)
		}

		var totals map[string]any
		if err := json.Unmarshal(resp.Body, &totals); err != nil {
			return w.Error("parse_error", "failed to parse response", nil, "", 1)
		}

		headers := []string{"METRIC", "VALUE"}
		rows := [][]string{
			{"Requests", helpers.IntStr(totals["totalRequests"])},
			{"Prompt Tokens", helpers.IntStr(totals["promptTokens"])},
			{"Completion Tokens", helpers.IntStr(totals["completionTokens"])},
			{"Avg Latency", helpers.IntStr(totals["avgLatencyMs"])},
		}

		// Add sparkline for requestsPerMinute if available and in table mode
		if w.GetFormat() == output.FormatTable {
			if rpmArr, ok := totals["requestsPerMinute"].([]any); ok && len(rpmArr) > 0 {
				vals := make([]float64, 0, len(rpmArr))
				for _, v := range rpmArr {
					if f, ok := v.(float64); ok {
						vals = append(vals, f)
					}
				}
				if len(vals) > 0 {
					rows = append(rows, []string{"RPM Spark", SparkLine(vals, 0)})
				}
			}
		}

		return w.PrintTable(headers, rows)
	},
}

// --- metrics latency-bands ---

var metricsLatencyBandsCmd = &cobra.Command{
	Use:   "latency-bands",
	Short: "Get latency distribution bands",
	RunE: func(cmd *cobra.Command, args []string) error {
		c := GetClient(cmd)
		w := GetOutput(cmd)

		path := "/api/metrics/latency-bands"
		params := buildQueryString(cmd, "from", "to")
		if params != "" {
			path += "?" + params
		}

		resp, err := c.Do(cmd.Context(), "GET", path, nil)
		if err != nil {
			return FormatErrorResponse(w, err)
		}
		if resp.StatusCode >= 400 {
			apiErr := client.ParseError(resp)
			return w.Error(apiErr.ErrCode, apiErr.Message, apiErr.Status, apiErr.Hint, apiErr.ExitCode)
		}

		var bands []map[string]any
		if err := json.Unmarshal(resp.Body, &bands); err != nil {
			return w.Error("parse_error", "failed to parse response", nil, "", 1)
		}

		headers := []string{"BAND", "COUNT"}
		rows := make([][]string, 0, len(bands))
		for _, b := range bands {
			rows = append(rows, []string{
				helpers.StrOrDash(b, "band"),
				helpers.IntStr(b["count"]),
			})
		}
		return w.PrintTable(headers, rows)
	},
}

// --- metrics api-keys ---

var metricsApiKeysCmd = &cobra.Command{
	Use:   "api-keys",
	Short: "Get per-key usage summary",
	RunE: func(cmd *cobra.Command, args []string) error {
		c := GetClient(cmd)
		w := GetOutput(cmd)

		path := "/api/metrics/api-keys"
		params := buildQueryString(cmd, "from", "to")
		if params != "" {
			path += "?" + params
		}

		resp, err := c.Do(cmd.Context(), "GET", path, nil)
		if err != nil {
			return FormatErrorResponse(w, err)
		}
		if resp.StatusCode >= 400 {
			apiErr := client.ParseError(resp)
			return w.Error(apiErr.ErrCode, apiErr.Message, apiErr.Status, apiErr.Hint, apiErr.ExitCode)
		}

		var keys []map[string]any
		if err := json.Unmarshal(resp.Body, &keys); err != nil {
			return w.Error("parse_error", "failed to parse response", nil, "", 1)
		}

		headers := []string{"KEY", "NAME", "REQUESTS", "TOKENS"}
		rows := make([][]string, 0, len(keys))
		for _, k := range keys {
			rows = append(rows, []string{
				helpers.StrOrDash(k, "keyId"),
				helpers.StrOrDash(k, "name"),
				helpers.IntStr(k["requests"]),
				helpers.IntStr(k["totalTokens"]),
			})
		}
		return w.PrintTable(headers, rows)
	},
}

// --- metrics api-key-usage ---

var metricsApiKeyUsageCmd = &cobra.Command{
	Use:   "api-key-usage <keyId>",
	Short: "Get detailed usage for a specific API key",
	Args:  cobra.ExactArgs(1),
	RunE: func(cmd *cobra.Command, args []string) error {
		c := GetClient(cmd)
		w := GetOutput(cmd)

		path := "/api/metrics/api-keys/" + args[0] + "/usage"
		params := buildQueryString(cmd, "from", "to")
		if params != "" {
			path += "?" + params
		}

		resp, err := c.Do(cmd.Context(), "GET", path, nil)
		if err != nil {
			return FormatErrorResponse(w, err)
		}
		if resp.StatusCode >= 400 {
			apiErr := client.ParseError(resp)
			return w.Error(apiErr.ErrCode, apiErr.Message, apiErr.Status, apiErr.Hint, apiErr.ExitCode)
		}

		var usage map[string]any
		if err := json.Unmarshal(resp.Body, &usage); err != nil {
			return w.Error("parse_error", "failed to parse response", nil, "", 1)
		}
		return w.Print(usage)
	},
}

// --- metrics provider-catalog ---

var metricsProviderCatalogCmd = &cobra.Command{
	Use:   "provider-catalog",
	Short: "List available providers and their kinds",
	RunE: func(cmd *cobra.Command, args []string) error {
		c := GetClient(cmd)
		w := GetOutput(cmd)

		resp, err := c.Do(cmd.Context(), "GET", "/api/metrics/provider-catalog", nil)
		if err != nil {
			return FormatErrorResponse(w, err)
		}
		if resp.StatusCode >= 400 {
			apiErr := client.ParseError(resp)
			return w.Error(apiErr.ErrCode, apiErr.Message, apiErr.Status, apiErr.Hint, apiErr.ExitCode)
		}

		var providers []map[string]any
		if err := json.Unmarshal(resp.Body, &providers); err != nil {
			return w.Error("parse_error", "failed to parse response", nil, "", 1)
		}

		headers := []string{"NAME", "KIND"}
		rows := make([][]string, 0, len(providers))
		for _, p := range providers {
			rows = append(rows, []string{
				helpers.StrOrDash(p, "name"),
				helpers.StrOrDash(p, "kind"),
			})
		}
		return w.PrintTable(headers, rows)
	},
}

// --- metrics purge ---

var metricsPurgeCmd = &cobra.Command{
	Use:   "purge",
	Short: "Purge old usage data",
	RunE: func(cmd *cobra.Command, args []string) error {
		c := GetClient(cmd)
		w := GetOutput(cmd)

		if IsDryRun(cmd) {
			w.DryRun("Would purge old usage data")
			return nil
		}

		confirmed, err := ConfirmOrSkip(cmd, "Purge old usage data? This action cannot be undone.")
		if err != nil {
			return w.Error("confirmation_required", err.Error(), nil, "use --yes to skip confirmation", 1)
		}
		if !confirmed {
			return nil
		}

		path := "/api/metrics/usage/purge"
		if cmd.Flags().Changed("older-days") {
			v, _ := cmd.Flags().GetInt("older-days")
			path += "?olderThanDays=" + strconv.Itoa(v)
		}

		resp, err := c.Do(cmd.Context(), "DELETE", path, nil)
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

// --- metrics cost ---

var metricsCostCmd = &cobra.Command{
	Use:   "cost",
	Short: "Estimate cost of usage based on token rates",
	RunE: func(cmd *cobra.Command, args []string) error {
		c := GetClient(cmd)
		w := GetOutput(cmd)

		// --rates is required.
		if !cmd.Flags().Changed("rates") {
			return w.Error("missing_rates", "--rates flag is required", nil, "provide a JSON object mapping model names to rates", 1)
		}
		ratesJSON, _ := cmd.Flags().GetString("rates")
		var rates map[string]struct {
			Prompt     float64 `json:"prompt"`
			Completion float64 `json:"completion"`
		}
		if err := json.Unmarshal([]byte(ratesJSON), &rates); err != nil {
			return w.Error("invalid_rates", "failed to parse --rates JSON: "+err.Error(), nil, "ensure valid JSON with model rates (USD per 1000 tokens)", 1)
		}

		// Fetch usage.
		path := "/api/metrics/usage"
		params := buildQueryString(cmd, "from", "to", "provider", "model")
		if params != "" {
			path += "?" + params
		}
		resp, err := c.Do(cmd.Context(), "GET", path, nil)
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

		items, _ := result["items"].([]any)
		if items == nil {
			items = []any{}
		}

		// Aggregate by model.
		type modelAgg struct {
			Requests         int
			PromptTokens     int
			CompletionTokens int
			Cost             float64
		}
		aggs := make(map[string]*modelAgg)
		var modelOrder []string
		totalCost := 0.0
		totalPrompt := 0
		totalCompletion := 0
		totalRequests := 0

		for _, item := range items {
			m, ok := item.(map[string]any)
			if !ok {
				continue
			}
			model := helpers.StrOrDash(m, "model")
			if model == "-" {
				model = "(unknown)"
			}
			promptTokens := costToInt(m["promptTokens"])
			completionTokens := costToInt(m["completionTokens"])

			agg, exists := aggs[model]
			if !exists {
				agg = &modelAgg{}
				aggs[model] = agg
				modelOrder = append(modelOrder, model)
			}
			agg.Requests++
			agg.PromptTokens += promptTokens
			agg.CompletionTokens += completionTokens

			// Compute cost if rate is available.
			if rate, ok := rates[model]; ok {
				cost := (float64(promptTokens)*rate.Prompt + float64(completionTokens)*rate.Completion) / 1000.0
				agg.Cost += cost
				totalCost += cost
			}
			totalPrompt += promptTokens
			totalCompletion += completionTokens
			totalRequests++
		}

		// Build JSON output if requested.
		if w.GetFormat() == output.FormatJSON {
			modelCosts := make([]map[string]any, 0, len(modelOrder))
			for _, model := range modelOrder {
				agg := aggs[model]
				entry := map[string]any{
					"model":            model,
					"requests":         agg.Requests,
					"promptTokens":     agg.PromptTokens,
					"completionTokens": agg.CompletionTokens,
					"estimatedCost":    agg.Cost,
				}
				modelCosts = append(modelCosts, entry)
			}
			return w.Print(map[string]any{
				"models": modelCosts,
				"totals": map[string]any{
					"requests":         totalRequests,
					"promptTokens":     totalPrompt,
					"completionTokens": totalCompletion,
					"estimatedCost":    totalCost,
				},
			})
		}

		// Table mode.
		headers := []string{"MODEL", "REQUESTS", "PROMPT TKNS", "COMPL TKNS", "EST. COST"}
		rows := make([][]string, 0, len(modelOrder)+1)
		for _, model := range modelOrder {
			agg := aggs[model]
			rows = append(rows, []string{
				model,
				strconv.Itoa(agg.Requests),
				costFormatTokenCount(agg.PromptTokens),
				costFormatTokenCount(agg.CompletionTokens),
				fmt.Sprintf("$%.2f", agg.Cost),
			})
		}
		// Totals row
		rows = append(rows, []string{
			"TOTAL",
			strconv.Itoa(totalRequests),
			costFormatTokenCount(totalPrompt),
			costFormatTokenCount(totalCompletion),
			fmt.Sprintf("$%.2f", totalCost),
		})
		return w.PrintTable(headers, rows)
	},
}

// costToInt extracts an int from various numeric types.
func costToInt(v any) int {
	if v == nil {
		return 0
	}
	switch val := v.(type) {
	case float64:
		return int(val)
	case int:
		return val
	case json.Number:
		n, _ := val.Int64()
		return int(n)
	default:
		return 0
	}
}

// costFormatTokenCount formats an int with comma separators.
func costFormatTokenCount(n int) string {
	s := strconv.Itoa(n)
	if len(s) <= 3 {
		return s
	}
	result := make([]byte, 0, len(s)+(len(s)-1)/3)
	for i, c := range s {
		if i > 0 && (len(s)-i)%3 == 0 {
			result = append(result, ',')
		}
		result = append(result, byte(c))
	}
	return string(result)
}

func init() {
	// Usage flags
	metricsUsageCmd.Flags().String("from", "", "Start time (ISO 8601)")
	metricsUsageCmd.Flags().String("to", "", "End time (ISO 8601)")
	metricsUsageCmd.Flags().Int("page", 0, "Page number")
	metricsUsageCmd.Flags().Int("page-size", 0, "Page size")
	metricsUsageCmd.Flags().String("provider", "", "Filter by provider")
	metricsUsageCmd.Flags().String("model", "", "Filter by model")

	// Today flags (inherits usage flags for filtering)
	metricsTodayCmd.Flags().Int("page", 0, "Page number")
	metricsTodayCmd.Flags().Int("page-size", 0, "Page size")
	metricsTodayCmd.Flags().String("provider", "", "Filter by provider")
	metricsTodayCmd.Flags().String("model", "", "Filter by model")

	// Last flags
	metricsLastCmd.Flags().String("to", "", "End time (ISO 8601)")
	metricsLastCmd.Flags().Int("page", 0, "Page number")
	metricsLastCmd.Flags().Int("page-size", 0, "Page size")
	metricsLastCmd.Flags().String("provider", "", "Filter by provider")
	metricsLastCmd.Flags().String("model", "", "Filter by model")

	// Summary flags
	metricsSummaryCmd.Flags().String("from", "", "Start time (ISO 8601)")
	metricsSummaryCmd.Flags().String("to", "", "End time (ISO 8601)")
	metricsSummaryCmd.Flags().String("granularity", "", "Time granularity (hour, day, week)")
	metricsSummaryCmd.Flags().String("group-by", "", "Group by field")

	// Models flags
	metricsModelsCmd.Flags().String("from", "", "Start time (ISO 8601)")
	metricsModelsCmd.Flags().String("to", "", "End time (ISO 8601)")

	// Providers flags
	metricsProvidersCmd.Flags().String("from", "", "Start time (ISO 8601)")
	metricsProvidersCmd.Flags().String("to", "", "End time (ISO 8601)")

	// Totals flags
	metricsTotalsCmd.Flags().String("from", "", "Start time (ISO 8601)")
	metricsTotalsCmd.Flags().String("to", "", "End time (ISO 8601)")

	// Latency-bands flags
	metricsLatencyBandsCmd.Flags().String("from", "", "Start time (ISO 8601)")
	metricsLatencyBandsCmd.Flags().String("to", "", "End time (ISO 8601)")

	// API-keys flags
	metricsApiKeysCmd.Flags().String("from", "", "Start time (ISO 8601)")
	metricsApiKeysCmd.Flags().String("to", "", "End time (ISO 8601)")

	// API-key-usage flags
	metricsApiKeyUsageCmd.Flags().String("from", "", "Start time (ISO 8601)")
	metricsApiKeyUsageCmd.Flags().String("to", "", "End time (ISO 8601)")

	// Purge flags
	metricsPurgeCmd.Flags().Int("older-days", 0, "Purge data older than N days")

	// Cost flags
	metricsCostCmd.Flags().String("from", "", "Start time (ISO 8601)")
	metricsCostCmd.Flags().String("to", "", "End time (ISO 8601)")
	metricsCostCmd.Flags().String("provider", "", "Filter by provider")
	metricsCostCmd.Flags().String("model", "", "Filter by model")
	metricsCostCmd.Flags().String("rates", "", "JSON rates per model (USD per 1000 tokens)")

	metricsCmd.AddCommand(metricsUsageCmd)
	metricsCmd.AddCommand(metricsTodayCmd)
	metricsCmd.AddCommand(metricsLastCmd)
	metricsCmd.AddCommand(metricsSummaryCmd)
	metricsCmd.AddCommand(metricsModelsCmd)
	metricsCmd.AddCommand(metricsProvidersCmd)
	metricsCmd.AddCommand(metricsTotalsCmd)
	metricsCmd.AddCommand(metricsLatencyBandsCmd)
	metricsCmd.AddCommand(metricsApiKeysCmd)
	metricsCmd.AddCommand(metricsApiKeyUsageCmd)
	metricsCmd.AddCommand(metricsProviderCatalogCmd)
	metricsCmd.AddCommand(metricsPurgeCmd)
	metricsCmd.AddCommand(metricsCostCmd)

	rootCmd.AddCommand(metricsCmd)
}

// buildQueryString builds a URL query string from optional flags.
// paramNames are the flag names; the query key uses camelCase.
func buildQueryString(cmd *cobra.Command, flags ...string) string {
	paramMap := map[string]string{
		"from":        "from",
		"to":          "to",
		"page":        "page",
		"page-size":   "pageSize",
		"provider":    "provider",
		"model":       "model",
		"granularity": "granularity",
		"group-by":    "groupBy",
	}

	var parts []string
	for _, flag := range flags {
		if cmd.Flags().Changed(flag) {
			val, _ := cmd.Flags().GetString(flag)
			if val == "" {
				// Try int
				if ival, err := cmd.Flags().GetInt(flag); err == nil && ival != 0 {
					val = strconv.Itoa(ival)
				}
			}
			if val != "" {
				key := paramMap[flag]
				if key == "" {
					key = flag
				}
				parts = append(parts, key+"="+val)
			}
		}
	}
	if len(parts) == 0 {
		return ""
	}
	result := parts[0]
	for i := 1; i < len(parts); i++ {
		result += "&" + parts[i]
	}
	return result
}
