package commands

import (
	"encoding/json"
	"fmt"
	"strconv"
	"strings"

	"github.com/spf13/cobra"
	"github.com/unswarm/cli/internal/client"
	"github.com/unswarm/cli/internal/output"
)

var settingsCmd = &cobra.Command{
	Use:   "settings",
	Short: "Manage settings",
	Long:  `Get and update system settings.`,
}

// --- settings get [key] ---

var settingsGetCmd = &cobra.Command{
	Use:   "get [key]",
	Short: "Get all settings or a single setting value",
	Args:  cobra.MaximumNArgs(1),
	RunE: func(cmd *cobra.Command, args []string) error {
		c := GetClient(cmd)
		w := GetOutput(cmd)

		resp, err := c.Do(cmd.Context(), "GET", "/api/settings", nil)
		if err != nil {
			return FormatErrorResponse(w, err)
		}
		if resp.StatusCode >= 400 {
			apiErr := client.ParseError(resp)
			return w.Error(apiErr.ErrCode, apiErr.Message, apiErr.Status, apiErr.Hint, apiErr.ExitCode)
		}

		var settings map[string]any
		if err := json.Unmarshal(resp.Body, &settings); err != nil {
			return w.Error("parse_error", "failed to parse response", nil, "", 1)
		}

		// Single key mode
		if len(args) == 1 {
			key := args[0]
			val, ok := settings[key]
			if !ok {
				return w.Error("not_found", fmt.Sprintf("setting %q not found", key), nil, "Use 'settings get' to list all settings", 1)
			}
			headers := []string{"KEY", "VALUE"}
			valStr := formatSettingValue(val)
			return w.PrintTable(headers, [][]string{{key, valStr}})
		}

		// All settings mode
		if w.GetFormat() == output.FormatJSON {
			return w.Print(settings)
		}
		headers := []string{"KEY", "VALUE"}
		rows := make([][]string, 0, len(settings))
		for k, v := range settings {
			rows = append(rows, []string{k, formatSettingValue(v)})
		}
		return w.PrintTable(headers, rows)
	},
}

// --- settings update ---

var settingsUpdateCmd = &cobra.Command{
	Use:   "update",
	Short: "Update settings",
	RunE: func(cmd *cobra.Command, args []string) error {
		c := GetClient(cmd)
		w := GetOutput(cmd)

		body := make(map[string]any)

		if cmd.Flags().Changed("request-timeout") {
			v, _ := cmd.Flags().GetFloat64("request-timeout")
			body["requestTimeout"] = v
		}
		if cmd.Flags().Changed("health-check-interval") {
			v, _ := cmd.Flags().GetFloat64("health-check-interval")
			body["healthCheckInterval"] = v
		}
		if cmd.Flags().Changed("auto-shutdown-idle") {
			v, _ := cmd.Flags().GetBool("auto-shutdown-idle")
			body["autoShutdownIdle"] = v
		}
		if cmd.Flags().Changed("idle-timeout") {
			v, _ := cmd.Flags().GetFloat64("idle-timeout")
			body["idleTimeout"] = v
		}
		if cmd.Flags().Changed("log-retention") {
			v, _ := cmd.Flags().GetFloat64("log-retention")
			body["logRetention"] = v
		}
		if cmd.Flags().Changed("enable-benchmarking") {
			v, _ := cmd.Flags().GetBool("enable-benchmarking")
			body["enableBenchmarking"] = v
		}
		if cmd.Flags().Changed("priority-mode") {
			v, _ := cmd.Flags().GetString("priority-mode")
			body["priorityMode"] = v
		}
		if cmd.Flags().Changed("batch-drain") {
			v, _ := cmd.Flags().GetBool("batch-drain")
			body["batchDrain"] = v
		}
		if cmd.Flags().Changed("lazy-stop") {
			v, _ := cmd.Flags().GetBool("lazy-stop")
			body["lazyStop"] = v
		}
		if cmd.Flags().Changed("max-queue-depth") {
			v, _ := cmd.Flags().GetInt("max-queue-depth")
			body["maxQueueDepth"] = v
		}
		if cmd.Flags().Changed("parallel-slot-skip-limit") {
			v, _ := cmd.Flags().GetInt("parallel-slot-skip-limit")
			body["parallelSlotSkipLimit"] = v
		}
		if cmd.Flags().Changed("enable-parallel-slot-skip") {
			v, _ := cmd.Flags().GetBool("enable-parallel-slot-skip")
			body["enableParallelSlotSkip"] = v
		}
		if cmd.Flags().Changed("queue-steps-till-reset") {
			v, _ := cmd.Flags().GetInt("queue-steps-till-reset")
			body["queueStepsTillReset"] = v
		}
		if cmd.Flags().Changed("enable-conversation-affinity") {
			v, _ := cmd.Flags().GetBool("enable-conversation-affinity")
			body["enableConversationAffinity"] = v
		}
		if cmd.Flags().Changed("conversation-dwell-seconds") {
			v, _ := cmd.Flags().GetFloat64("conversation-dwell-seconds")
			body["conversationDwellSeconds"] = v
		}
		if cmd.Flags().Changed("health-check-timeout-seconds") {
			v, _ := cmd.Flags().GetFloat64("health-check-timeout-seconds")
			body["healthCheckTimeoutSeconds"] = v
		}
		if cmd.Flags().Changed("hide-origin-prefix") {
			v, _ := cmd.Flags().GetBool("hide-origin-prefix")
			body["hideOriginPrefix"] = v
		}
		if cmd.Flags().Changed("agent-display-names") {
			v, _ := cmd.Flags().GetString("agent-display-names")
			body["agentDisplayNames"] = v
		}
		if cmd.Flags().Changed("usage-retention-days") {
			v, _ := cmd.Flags().GetInt("usage-retention-days")
			body["usageRetentionDays"] = v
		}
		if cmd.Flags().Changed("router-retry-attempts") {
			v, _ := cmd.Flags().GetInt("router-retry-attempts")
			body["routerRetryAttempts"] = v
		}
		if cmd.Flags().Changed("router-retry-delay-ms") {
			v, _ := cmd.Flags().GetInt("router-retry-delay-ms")
			body["routerRetryDelayMs"] = v
		}

		if len(body) == 0 {
			return w.Error("no_changes", "No settings specified to update", nil, "Use --<flag> to specify settings to change", 1)
		}

		resp, err := c.Do(cmd.Context(), "PUT", "/api/settings", body)
		if err != nil {
			return FormatErrorResponse(w, err)
		}
		if resp.StatusCode >= 400 {
			apiErr := client.ParseError(resp)
			return w.Error(apiErr.ErrCode, apiErr.Message, apiErr.Status, apiErr.Hint, apiErr.ExitCode)
		}

		var settings map[string]any
		if err := json.Unmarshal(resp.Body, &settings); err != nil {
			return w.Error("parse_error", "failed to parse response", nil, "", 1)
		}
		return w.Print(settings)
	},
}

// --- settings set <key> <value> ---

var settingsSetKeyCmd = &cobra.Command{
	Use:   "set <key> <value>",
	Short: "Set a single setting value",
	Args:  cobra.ExactArgs(2),
	RunE: func(cmd *cobra.Command, args []string) error {
		c := GetClient(cmd)
		w := GetOutput(cmd)

		// Fetch current settings
		resp, err := c.Do(cmd.Context(), "GET", "/api/settings", nil)
		if err != nil {
			return FormatErrorResponse(w, err)
		}
		if resp.StatusCode >= 400 {
			apiErr := client.ParseError(resp)
			return w.Error(apiErr.ErrCode, apiErr.Message, apiErr.Status, apiErr.Hint, apiErr.ExitCode)
		}

		var settings map[string]any
		if err := json.Unmarshal(resp.Body, &settings); err != nil {
			return w.Error("parse_error", "failed to parse response", nil, "", 1)
		}

		key := args[0]
		if _, ok := settings[key]; !ok {
			return w.Error("not_found", fmt.Sprintf("setting %q not found", key), nil, "Use 'settings get' to list all settings", 1)
		}

		// Parse value with type coercion based on existing value type
		settings[key] = parseValue(args[1], settings[key])

		// PUT back the full settings object
		resp, err = c.Do(cmd.Context(), "PUT", "/api/settings", settings)
		if err != nil {
			return FormatErrorResponse(w, err)
		}
		if resp.StatusCode >= 400 {
			apiErr := client.ParseError(resp)
			return w.Error(apiErr.ErrCode, apiErr.Message, apiErr.Status, apiErr.Hint, apiErr.ExitCode)
		}

		var updated map[string]any
		if err := json.Unmarshal(resp.Body, &updated); err != nil {
			return w.Error("parse_error", "failed to parse response", nil, "", 1)
		}

		headers := []string{"KEY", "VALUE"}
		return w.PrintTable(headers, [][]string{{key, formatSettingValue(updated[key])}})
	},
}

func init() {
	// Update flags (all optional)
	settingsUpdateCmd.Flags().Float64("request-timeout", 0, "Request timeout in seconds")
	settingsUpdateCmd.Flags().Float64("health-check-interval", 0, "Health check interval in seconds")
	settingsUpdateCmd.Flags().Bool("auto-shutdown-idle", false, "Auto shutdown when idle")
	settingsUpdateCmd.Flags().Float64("idle-timeout", 0, "Idle timeout in seconds")
	settingsUpdateCmd.Flags().Float64("log-retention", 0, "Log retention in seconds")
	settingsUpdateCmd.Flags().Bool("enable-benchmarking", false, "Enable benchmarking")
	settingsUpdateCmd.Flags().String("priority-mode", "", "Priority mode")
	settingsUpdateCmd.Flags().Bool("batch-drain", false, "Batch drain")
	settingsUpdateCmd.Flags().Bool("lazy-stop", false, "Lazy stop")
	settingsUpdateCmd.Flags().Int("max-queue-depth", 0, "Max queue depth")
	settingsUpdateCmd.Flags().Int("parallel-slot-skip-limit", 0, "Parallel slot skip limit")
	settingsUpdateCmd.Flags().Bool("enable-parallel-slot-skip", false, "Enable parallel slot skip")
	settingsUpdateCmd.Flags().Int("queue-steps-till-reset", 0, "Queue steps till reset")
	settingsUpdateCmd.Flags().Bool("enable-conversation-affinity", false, "Enable conversation affinity")
	settingsUpdateCmd.Flags().Float64("conversation-dwell-seconds", 0, "Conversation dwell seconds")
	settingsUpdateCmd.Flags().Float64("health-check-timeout-seconds", 0, "Health check timeout seconds")
	settingsUpdateCmd.Flags().Bool("hide-origin-prefix", false, "Hide origin prefix")
	settingsUpdateCmd.Flags().String("agent-display-names", "", "Agent display names")
	settingsUpdateCmd.Flags().Int("usage-retention-days", 0, "Usage retention days")
	settingsUpdateCmd.Flags().Int("router-retry-attempts", 0, "Router retry attempts")
	settingsUpdateCmd.Flags().Int("router-retry-delay-ms", 0, "Router retry delay in ms")

	settingsCmd.AddCommand(settingsGetCmd)
	settingsCmd.AddCommand(settingsSetKeyCmd)
	settingsCmd.AddCommand(settingsUpdateCmd)

	rootCmd.AddCommand(settingsCmd)
}

// jsonify converts a value to JSON string for display
func jsonify(v any) string {
	b, err := json.Marshal(v)
	if err != nil {
		return fmt.Sprintf("%v", v)
	}
	return string(b)
}

// formatSettingValue converts a setting value to a display string.
// Objects and arrays are shown as compact JSON.
func formatSettingValue(v any) string {
	switch vv := v.(type) {
	case map[string]any, []any:
		return jsonify(vv)
	default:
		return fmt.Sprintf("%v", vv)
	}
}

// parseValue converts a string value to the appropriate type based on the
// existing value's type. Booleans accept true/false/yes/no/1/0.
func parseValue(raw string, existing any) any {
	switch existing.(type) {
	case bool:
		switch strings.ToLower(raw) {
		case "true", "yes", "1":
			return true
		case "false", "no", "0":
			return false
		}
		return raw
	case float64:
		if f, err := strconv.ParseFloat(raw, 64); err == nil {
			return f
		}
	case json.Number:
		return json.Number(raw)
	default:
		// Try boolean first for untyped values
		switch strings.ToLower(raw) {
		case "true", "yes", "1":
			return true
		case "false", "no", "0":
			return false
		}
		if f, err := strconv.ParseFloat(raw, 64); err == nil {
			return f
		}
	}
	return raw
}
