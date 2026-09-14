package commands

import (
	"encoding/json"
	"fmt"
	"os"
	"os/signal"
	"syscall"
	"time"

	"github.com/spf13/cobra"
	"github.com/unswarm/cli/internal/client"
	"github.com/unswarm/cli/internal/helpers"
	"github.com/unswarm/cli/internal/parse"
)

var statsCmd = &cobra.Command{
	Use:   "stats",
	Short: "Show system statistics",
	Long:  `Display current system statistics including request counts, latency, and more.`,
	RunE: func(cmd *cobra.Command, args []string) error {
		watch, _ := cmd.Flags().GetBool("watch")
		intervalStr, _ := cmd.Flags().GetString("interval")

		if watch {
			interval := 5 * time.Second
			if intervalStr != "" {
				var err error
				interval, err = parse.ParseDuration(intervalStr)
				if err != nil {
					return fmt.Errorf("invalid interval: %w", err)
				}
				if interval < 2*time.Second {
					return fmt.Errorf("interval must be at least 2s")
				}
			}

			sigCh := make(chan os.Signal, 1)
			signal.Notify(sigCh, syscall.SIGINT, syscall.SIGTERM)
			defer signal.Stop(sigCh)

			ticker := time.NewTicker(interval)
			defer ticker.Stop()

			renderFunc := func() error {
				return renderStatsDashboard(cmd, true)
			}

			// First render (clears screen on subsequent)
			if err := renderFunc(); err != nil {
				return err
			}

			for {
				select {
				case <-sigCh:
					fmt.Fprintln(os.Stderr)
					return nil
				case <-ticker.C:
					_ = renderFunc()
				}
			}
		}

		return renderStatsDashboard(cmd, false)
	},
}

func renderStatsDashboard(cmd *cobra.Command, watch bool) error {
	c := GetClient(cmd)
	w := GetOutput(cmd)

	// Clear screen on watch mode
	if watch {
		fmt.Fprint(cmd.OutOrStdout(), clearScreen)
	}

	resp, err := c.Do(cmd.Context(), "GET", "/api/stats", nil)
	if err != nil {
		return FormatErrorResponse(w, err)
	}
	if resp.StatusCode >= 400 {
		apiErr := client.ParseError(resp)
		return w.Error(apiErr.ErrCode, apiErr.Message, apiErr.Status, apiErr.Hint, apiErr.ExitCode)
	}

	var stats map[string]any
	if err := json.Unmarshal(resp.Body, &stats); err != nil {
		return w.Error("parse_error", "failed to parse response", nil, "", 1)
	}

	headers := []string{"METRIC", "VALUE"}
	rows := [][]string{
		{"Total Requests", helpers.IntStr(stats["totalRequests"])},
		{"Active Requests", helpers.IntStr(stats["activeRequests"])},
		{"Avg Latency (ms)", helpers.IntStr(stats["avgLatencyMs"])},
		{"Total Tokens Processed", helpers.IntStr(stats["totalTokensProcessed"])},
		{"Models Loaded", helpers.IntStr(stats["modelsLoaded"])},
		{"Containers Running", helpers.IntStr(stats["containersRunning"])},
		{"Queue Depth", helpers.IntStr(stats["queueDepth"])},
		{"Requests/min", fmt.Sprintf("%v", stats["requestsPerMinute"])},
		{"Errors (24h)", helpers.IntStr(stats["errorsLast24h"])},
		{"Switch Count", helpers.IntStr(stats["switchCount"])},
	}
	return w.PrintTable(headers, rows)
}

func init() {
	statsCmd.Flags().Bool("watch", false, "Auto-refresh every 5s (min 2s)")
	statsCmd.Flags().String("interval", "", "Refresh interval for --watch (e.g. 5s, 10s)")
	rootCmd.AddCommand(statsCmd)
}
