package commands

import (
	"encoding/json"
	"fmt"
	"strings"

	"github.com/spf13/cobra"
	"github.com/unswarm/cli/internal/client"
	"github.com/unswarm/cli/internal/helpers"
	"github.com/unswarm/cli/internal/output"
)

var queueCmd = &cobra.Command{
	Use:   "queue",
	Short: "Manage the inference queue",
	Long:  `View queue snapshot, cancel items, and release holds.`,
}

// --- queue snapshot ---

// renderQueueSection adds a section header and item rows to the given rows slice.
func renderQueueSection(rows [][]string, section string, itemsRaw any) [][]string {
	items, ok := itemsRaw.([]any)
	if !ok || len(items) == 0 {
		return rows
	}
	rows = append(rows, []string{fmt.Sprintf("=== %s (%d) ===", section, len(items)), "", "", "", ""})
	for _, item := range items {
		m, ok := item.(map[string]any)
		if !ok {
			continue
		}
		rows = append(rows, []string{
			helpers.StrOrDash(m, "id"),
			helpers.StrOrDash(m, "status"),
			helpers.StrOrDash(m, "target"),
			helpers.StrOrDash(m, "priority"),
			helpers.StrOrDash(m, "createdAt"),
		})
	}
	return rows
}

var queueSnapshotCmd = &cobra.Command{
	Use:   "snapshot",
	Short: "Get a snapshot of the queue status",
	RunE: func(cmd *cobra.Command, args []string) error {
		c := GetClient(cmd)
		w := GetOutput(cmd)

		resp, err := c.Do(cmd.Context(), "GET", "/api/queue/snapshot", nil)
		if err != nil {
			return FormatErrorResponse(w, err)
		}
		if resp.StatusCode >= 400 {
			apiErr := client.ParseError(resp)
			return w.Error(apiErr.ErrCode, apiErr.Message, apiErr.Status, apiErr.Hint, apiErr.ExitCode)
		}

		var snapshot map[string]any
		if err := json.Unmarshal(resp.Body, &snapshot); err != nil {
			return w.Error("parse_error", "failed to parse response", nil, "", 1)
		}

		// JSON mode: print raw snapshot
		if w.GetFormat() != output.FormatTable {
			return w.Print(snapshot)
		}

		// Table mode: build sectioned summary
		headers := []string{"ITEM", "STATUS", "TARGET", "PRIORITY", "ADDED"}
		rows := make([][]string, 0)
		rows = renderQueueSection(rows, "PROCESSING", snapshot["processing"])
		rows = renderQueueSection(rows, "WAITING", snapshot["waiting"])
		rows = renderQueueSection(rows, "RECENT COMPLETED", snapshot["recentCompleted"])

		if len(rows) == 0 {
			rows = append(rows, []string{"(empty queue)", "", "", "", ""})
		}

		return w.PrintTable(headers, rows)
	},
}

// --- queue cancel ---

var queueCancelCmd = &cobra.Command{
	Use:   "cancel <itemId>",
	Short: "Cancel a queued item",
	Args:  cobra.ExactArgs(1),
	RunE: func(cmd *cobra.Command, args []string) error {
		c := GetClient(cmd)
		w := GetOutput(cmd)

		resp, err := c.Do(cmd.Context(), "DELETE", "/api/queue/"+args[0], nil)
		if err != nil {
			return FormatErrorResponse(w, err)
		}
		if resp.StatusCode >= 400 {
			apiErr := client.ParseError(resp)
			return w.Error(apiErr.ErrCode, apiErr.Message, apiErr.Status, apiErr.Hint, apiErr.ExitCode)
		}

		var result map[string]any
		if err := json.Unmarshal(resp.Body, &result); err != nil {
			return w.Print(map[string]any{"message": fmt.Sprintf("Cancelled item %s", args[0])})
		}
		return w.Print(result)
	},
}

// --- queue release-hold ---

var queueReleaseHoldCmd = &cobra.Command{
	Use:   "release-hold <targetId>",
	Short: "Release a conversation hold",
	Args:  cobra.ExactArgs(1),
	RunE: func(cmd *cobra.Command, args []string) error {
		c := GetClient(cmd)
		w := GetOutput(cmd)

		resp, err := c.Do(cmd.Context(), "POST", "/api/queue/targets/"+args[0]+"/hold/release", nil)
		if err != nil {
			return FormatErrorResponse(w, err)
		}
		if resp.StatusCode >= 400 {
			apiErr := client.ParseError(resp)
			return w.Error(apiErr.ErrCode, apiErr.Message, apiErr.Status, apiErr.Hint, apiErr.ExitCode)
		}

		var result map[string]any
		if err := json.Unmarshal(resp.Body, &result); err != nil {
			return w.Print(map[string]any{"message": fmt.Sprintf("Released hold for target %s", args[0])})
		}
		return w.Print(result)
	},
}

// --- queue list ---

var queueListCmd = &cobra.Command{
	Use:   "list",
	Short: "List queued items",
	Long:  `List items in the inference queue. Optionally filter by status.`,
	RunE: func(cmd *cobra.Command, args []string) error {
		c := GetClient(cmd)
		w := GetOutput(cmd)

		resp, err := c.Do(cmd.Context(), "GET", "/api/queue/snapshot", nil)
		if err != nil {
			return FormatErrorResponse(w, err)
		}
		if resp.StatusCode >= 400 {
			apiErr := client.ParseError(resp)
			return w.Error(apiErr.ErrCode, apiErr.Message, apiErr.Status, apiErr.Hint, apiErr.ExitCode)
		}

		var snapshot map[string]any
		if err := json.Unmarshal(resp.Body, &snapshot); err != nil {
			return w.Error("parse_error", "failed to parse response", nil, "", 1)
		}

		// Flatten processing + waiting into one list, tagging each with its source
		type taggedItem struct {
			source string
			item   map[string]any
		}
		var all []taggedItem
		for _, src := range []struct {
			key    string
			prefix string
		}{
			{"processing", "processing"},
			{"waiting", "waiting"},
		} {
			arr, _ := snapshot[src.key].([]any)
			for _, raw := range arr {
				if m, ok := raw.(map[string]any); ok {
					all = append(all, taggedItem{source: src.prefix, item: m})
				}
			}
		}

		// Apply --status filter (matches the source tag)
		statusFilter, _ := cmd.Flags().GetString("status")
		if statusFilter != "" {
			filtered := make([]taggedItem, 0, len(all))
			for _, ti := range all {
				if strings.EqualFold(ti.source, statusFilter) {
					filtered = append(filtered, ti)
				}
			}
			all = filtered
		}

		// Render table
		headers := []string{"ID", "STATUS", "TARGET", "PRIORITY", "ADDED"}
		rows := make([][]string, 0, len(all))
		for _, ti := range all {
			m := ti.item
			rows = append(rows, []string{
				helpers.StrOrDash(m, "id"),
				ti.source,
				helpers.StrOrDash(m, "target"),
				helpers.StrOrDash(m, "priority"),
				helpers.StrOrDash(m, "createdAt"),
			})
		}
		return w.PrintTable(headers, rows)
	},
}

func init() {
	queueListCmd.Flags().String("status", "", "Filter by item status (e.g. waiting, processing)")

	queueCmd.AddCommand(queueListCmd)
	queueCmd.AddCommand(queueSnapshotCmd)
	queueCmd.AddCommand(queueCancelCmd)
	queueCmd.AddCommand(queueReleaseHoldCmd)

	rootCmd.AddCommand(queueCmd)
}
