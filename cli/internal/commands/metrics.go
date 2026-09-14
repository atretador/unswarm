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
