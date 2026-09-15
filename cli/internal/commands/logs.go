package commands

import (
	"encoding/json"
	"fmt"
	"os"
	"os/signal"
	"strconv"
	"strings"
	"syscall"
	"time"

	"github.com/spf13/cobra"
	"github.com/unswarm/cli/internal/client"
	"github.com/unswarm/cli/internal/helpers"
)

var logsCmd = &cobra.Command{
	Use:   "logs",
	Short: "View logs",
	Long:  `Query and list log entries from the system.`,
}

// --- logs list ---

var logsListCmd = &cobra.Command{
	Use:   "list",
	Short: "List log entries",
	RunE: func(cmd *cobra.Command, args []string) error {
		c := GetClient(cmd)
		w := GetOutput(cmd)

		path := "/api/logs"
		params := buildQueryString(cmd, "source", "level", "limit", "since")
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

		var logs []map[string]any
		if err := json.Unmarshal(resp.Body, &logs); err != nil {
			return w.Error("parse_error", "failed to parse response", nil, "", 1)
		}

		headers := []string{"TIME", "LEVEL", "SOURCE", "MESSAGE"}
		rows := make([][]string, 0, len(logs))
		for _, l := range logs {
			msg := helpers.StrOrDash(l, "message")
			// Truncate long messages for table view
			if len(msg) > 120 {
				msg = msg[:117] + "..."
			}
			rows = append(rows, []string{
				helpers.StrOrDash(l, "timestamp"),
				helpers.StrOrDash(l, "level"),
				helpers.StrOrDash(l, "source"),
				msg,
			})
		}
		return w.PrintTable(headers, rows)
	},
}

// --- logs follow ---

var logsFollowCmd = &cobra.Command{
	Use:   "follow",
	Short: "Stream log entries via SSE with polling fallback",
	Long: `Connect to the server's SSE stream for real-time log entries.

Falls back to polling if the SSE endpoint is unavailable.
Press Ctrl+C to stop.`,
	RunE: func(cmd *cobra.Command, args []string) error {
		c := GetClient(cmd)

		// Get flags
		limit, _ := cmd.Flags().GetInt("limit")
		intervalSec, _ := cmd.Flags().GetInt("interval")
		if limit <= 0 {
			limit = 20
		}
		if intervalSec < 1 {
			intervalSec = 2
		}
		interval := time.Duration(intervalSec) * time.Second

		outputFmt := cmd.Flags().Lookup("output")
		isJSON := outputFmt != nil && outputFmt.Value.String() == "json"

		// Signal handling for clean exit
		sigCh := make(chan os.Signal, 1)
		signal.Notify(sigCh, syscall.SIGINT, syscall.SIGTERM)
		defer signal.Stop(sigCh)

		// Try SSE first
		ssePath := "/api/logs/stream"
		sseParams := buildQueryString(cmd, "source", "level", "since")
		if sseParams != "" {
			ssePath += "?" + sseParams
		}

		reader, err := c.DoSSE(cmd.Context(), "GET", ssePath, nil)
		if err == nil {
			defer reader.Close()
			if !isJSON {
				fmt.Fprintf(os.Stderr, "Following logs via SSE (Ctrl+C to stop)...\n")
			}

			for {
				select {
				case <-cmd.Context().Done():
					return nil
				case <-sigCh:
					if !isJSON {
						fmt.Fprintf(os.Stderr, "\nStopped following logs.\n")
					}
					return nil
				default:
				}

				event, readErr := reader.ReadEvent()
				if readErr != nil {
					// Context cancellation is expected
					if cmd.Context().Err() != nil {
						return nil
					}
					// Fall through to polling fallback
					break
				}

				var entry map[string]any
				if err := json.Unmarshal([]byte(event.Data), &entry); err != nil {
					continue
				}

				if isJSON {
					line, _ := json.Marshal(entry)
					fmt.Println(string(line))
				} else {
					ts := helpers.StrOrDash(entry, "timestamp")
					level := strings.ToUpper(helpers.StrOrDash(entry, "level"))
					source := helpers.StrOrDash(entry, "source")
					msg := helpers.StrOrDash(entry, "message")

					line := fmt.Sprintf("[%s] [%s] [%s] %s", ts, level, source, msg)
					fmt.Println(colorizeLogLine(line, level))
				}
			}
		}

		// SSE failed — fall back to polling
		return logsFollowPoll(cmd, c, sigCh, isJSON, limit, interval)
	},
}

// logsFollowPoll implements the polling-based fallback for logs follow.
func logsFollowPoll(cmd *cobra.Command, c *client.Client, sigCh chan os.Signal, isJSON bool, limit int, interval time.Duration) error {
	// Seen entries tracker for deduplication (key: timestamp+source+message)
	seen := make(map[string]bool)

	if !isJSON {
		fmt.Fprintf(os.Stderr, "Following logs via polling (Ctrl+C to stop, interval: %s)...\n", interval)
	}

	firstFetch := true
	fetchLimit := limit

	for {
		// Check for cancellation
		select {
		case <-cmd.Context().Done():
			return nil
		case <-sigCh:
			if !isJSON {
				fmt.Fprintf(os.Stderr, "\nStopped following logs.\n")
			}
			return nil
		default:
		}

		// Build request path
		path := "/api/logs"
		params := buildQueryString(cmd, "source", "level", "since")
		if params != "" {
			path += "?" + params + "&limit=" + strconv.Itoa(fetchLimit)
		} else {
			path += "?limit=" + strconv.Itoa(fetchLimit)
		}

		resp, err := c.Do(cmd.Context(), "GET", path, nil)
		if err != nil {
			// On error, wait and retry
			select {
			case <-time.After(interval):
				continue
			case <-cmd.Context().Done():
				return nil
			case <-sigCh:
				if !isJSON {
					fmt.Fprintf(os.Stderr, "\nStopped following logs.\n")
				}
				return nil
			}
		}
		if resp.StatusCode >= 400 {
			select {
			case <-time.After(interval):
				continue
			case <-cmd.Context().Done():
				return nil
			case <-sigCh:
				if !isJSON {
					fmt.Fprintf(os.Stderr, "\nStopped following logs.\n")
				}
				return nil
			}
		}

		var logs []map[string]any
		if err := json.Unmarshal(resp.Body, &logs); err != nil {
			select {
			case <-time.After(interval):
				continue
			case <-cmd.Context().Done():
				return nil
			case <-sigCh:
				if !isJSON {
					fmt.Fprintf(os.Stderr, "\nStopped following logs.\n")
				}
				return nil
			}
		}

		// Output new entries
		for _, l := range logs {
			key := helpers.StrOrDash(l, "timestamp") + "|" + helpers.StrOrDash(l, "source") + "|" + helpers.StrOrDash(l, "message")
			if seen[key] {
				continue
			}
			seen[key] = true

			if isJSON {
				line, _ := json.Marshal(l)
				fmt.Println(string(line))
			} else {
				ts := helpers.StrOrDash(l, "timestamp")
				level := strings.ToUpper(helpers.StrOrDash(l, "level"))
				source := helpers.StrOrDash(l, "source")
				msg := helpers.StrOrDash(l, "message")

				line := fmt.Sprintf("[%s] [%s] [%s] %s", ts, level, source, msg)
				fmt.Println(colorizeLogLine(line, level))
			}
		}

		// After first fetch, use smaller limit for polling
		if firstFetch {
			firstFetch = false
			fetchLimit = 10
		}

		// Wait before next poll
		select {
		case <-time.After(interval):
		case <-cmd.Context().Done():
			return nil
		case <-sigCh:
			if !isJSON {
				fmt.Fprintf(os.Stderr, "\nStopped following logs.\n")
			}
			return nil
		}
	}
}

// colorizeLogLine applies ANSI color codes to a log line based on level.
// Respects the global noColor flag.
func colorizeLogLine(line, level string) string {
	if noColor {
		return line
	}
	switch level {
	case "ERROR", "FATAL", "PANIC":
		return "\033[31m" + line + "\033[0m" // red
	case "WARN", "WARNING":
		return "\033[33m" + line + "\033[0m" // yellow
	default:
		return line
	}
}

// --- logs search ---

var logsSearchCmd = &cobra.Command{
	Use:   "search <query>",
	Short: "Search log entries by text",
	Long: `Fetch recent log entries and filter by case-insensitive substring match.

Searches across all log fields (timestamp, level, source, message).`,
	Args: cobra.ExactArgs(1),
	RunE: func(cmd *cobra.Command, args []string) error {
		c := GetClient(cmd)
		w := GetOutput(cmd)

		query := strings.ToLower(args[0])

		// Fetch logs with optional filters
		limit, _ := cmd.Flags().GetInt("limit")
		if limit <= 0 {
			limit = 1000
		}

		path := "/api/logs"
		params := buildQueryString(cmd, "source", "level")
		if params != "" {
			path += "?" + params + "&limit=" + strconv.Itoa(limit)
		} else {
			path += "?limit=" + strconv.Itoa(limit)
		}

		resp, err := c.Do(cmd.Context(), "GET", path, nil)
		if err != nil {
			return FormatErrorResponse(w, err)
		}
		if resp.StatusCode >= 400 {
			apiErr := client.ParseError(resp)
			return w.Error(apiErr.ErrCode, apiErr.Message, apiErr.Status, apiErr.Hint, apiErr.ExitCode)
		}

		var logs []map[string]any
		if err := json.Unmarshal(resp.Body, &logs); err != nil {
			return w.Error("parse_error", "failed to parse response", nil, "", 1)
		}

		// Client-side filter: case-insensitive substring match on any field
		var filtered []map[string]any
		for _, l := range logs {
			for _, v := range l {
				if s, ok := v.(string); ok {
					if strings.Contains(strings.ToLower(s), query) {
						filtered = append(filtered, l)
						break
					}
				}
			}
		}

		if len(filtered) == 0 {
			return w.Error("no_matches", fmt.Sprintf("no matching logs for %q", args[0]), nil, "try a different query or broaden filters", 0)
		}

		headers := []string{"TIME", "LEVEL", "SOURCE", "MESSAGE"}
		rows := make([][]string, 0, len(filtered))
		for _, l := range filtered {
			msg := helpers.StrOrDash(l, "message")
			if len(msg) > 120 {
				msg = msg[:117] + "..."
			}
			rows = append(rows, []string{
				helpers.StrOrDash(l, "timestamp"),
				helpers.StrOrDash(l, "level"),
				helpers.StrOrDash(l, "source"),
				msg,
			})
		}
		return w.PrintTable(headers, rows)
	},
}

// --- logs last ---

var logsLastCmd = &cobra.Command{
	Use:   "last [n]",
	Short: "Show the last N log entries",
	Long:  `Shortcut for "logs list --limit N". Defaults to 20 entries.`,
	Args:  cobra.MaximumNArgs(1),
	RunE: func(cmd *cobra.Command, args []string) error {
		c := GetClient(cmd)
		w := GetOutput(cmd)

		limit := 20
		if len(args) > 0 {
			n, err := strconv.Atoi(args[0])
			if err != nil || n <= 0 {
				return fmt.Errorf("invalid number: %s", args[0])
			}
			limit = n
		}

		path := "/api/logs?limit=" + strconv.Itoa(limit)

		resp, err := c.Do(cmd.Context(), "GET", path, nil)
		if err != nil {
			return FormatErrorResponse(w, err)
		}
		if resp.StatusCode >= 400 {
			apiErr := client.ParseError(resp)
			return w.Error(apiErr.ErrCode, apiErr.Message, apiErr.Status, apiErr.Hint, apiErr.ExitCode)
		}

		var logs []map[string]any
		if err := json.Unmarshal(resp.Body, &logs); err != nil {
			return w.Error("parse_error", "failed to parse response", nil, "", 1)
		}

		headers := []string{"TIME", "LEVEL", "SOURCE", "MESSAGE"}
		rows := make([][]string, 0, len(logs))
		for _, l := range logs {
			msg := helpers.StrOrDash(l, "message")
			if len(msg) > 120 {
				msg = msg[:117] + "..."
			}
			rows = append(rows, []string{
				helpers.StrOrDash(l, "timestamp"),
				helpers.StrOrDash(l, "level"),
				helpers.StrOrDash(l, "source"),
				msg,
			})
		}
		return w.PrintTable(headers, rows)
	},
}

func init() {
	// List flags
	logsListCmd.Flags().String("source", "", "Filter by source")
	logsListCmd.Flags().String("level", "", "Filter by level (info, warn, error, debug)")
	logsListCmd.Flags().Int("limit", 100, "Max log entries to return")
	logsListCmd.Flags().String("since", "", "Show logs since this time (ISO 8601)")

	// Follow flags
	logsFollowCmd.Flags().Int("limit", 20, "Initial number of entries to show")
	logsFollowCmd.Flags().Int("interval", 2, "Poll interval in seconds (min: 1)")
	logsFollowCmd.Flags().String("source", "", "Filter by source")
	logsFollowCmd.Flags().String("level", "", "Filter by level (info, warn, error, debug)")
	logsFollowCmd.Flags().String("since", "", "Show logs since this time (ISO 8601)")

	// Search flags
	logsSearchCmd.Flags().Int("limit", 1000, "Max entries to fetch before filtering")
	logsSearchCmd.Flags().String("source", "", "Filter by source")
	logsSearchCmd.Flags().String("level", "", "Filter by level (info, warn, error, debug)")

	logsCmd.AddCommand(logsListCmd)
	logsCmd.AddCommand(logsFollowCmd)
	logsCmd.AddCommand(logsSearchCmd)
	logsCmd.AddCommand(logsLastCmd)

	rootCmd.AddCommand(logsCmd)
}
