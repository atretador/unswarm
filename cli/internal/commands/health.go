package commands

import (
	"encoding/json"
	"fmt"
	"os"
	"os/signal"
	"strings"
	"sync"
	"syscall"
	"time"

	"github.com/spf13/cobra"
	"github.com/unswarm/cli/internal/client"
	"github.com/unswarm/cli/internal/helpers"
	"github.com/unswarm/cli/internal/output"
	"github.com/unswarm/cli/internal/parse"
)

var healthCmd = &cobra.Command{
	Use:   "health",
	Short: "Show system health dashboard",
	Long:  `Display a comprehensive overview of system health including runtimes, queue, and summary statistics.`,
	RunE: func(cmd *cobra.Command, args []string) error {
		// Determine output format
		var format output.Format
		if f := cmd.Flags().Lookup("output"); f != nil {
			format = output.Format(f.Value.String())
		}
		if format == "" {
			format = output.DetectFormat()
		}

		watch, _ := cmd.Flags().GetBool("watch")
		summary, _ := cmd.Flags().GetBool("summary")
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
				return renderHealthDashboard(cmd, format, watch)
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

		if summary {
			return renderHealthSummary(cmd, format)
		}

		return renderHealthDashboard(cmd, format, false)
	},
}

const clearScreen = "\033[H\033[2J"

func renderHealthDashboard(cmd *cobra.Command, format output.Format, watch bool) error {
	c := GetClient(cmd)
	w := GetOutput(cmd)

	// Clear screen on watch mode
	if watch {
		fmt.Fprint(cmd.OutOrStdout(), clearScreen)
	}

	// Fetch all endpoints in parallel
	type fetchResult struct {
		data json.RawMessage
		err  error
	}

	var mu sync.Mutex
	var wg sync.WaitGroup

	endpoints := map[string]string{
		"runtimes":   "/api/containers/registered",
		"containers": "/api/containers",
		"stats":      "/api/stats",
		"queue":      "/api/queue/snapshot",
	}

	results := make(map[string]fetchResult)
	for name, path := range endpoints {
		wg.Add(1)
		go func(name, path string) {
			defer wg.Done()
			resp, err := c.Do(cmd.Context(), "GET", path, nil)
			if err != nil {
				mu.Lock()
				results[name] = fetchResult{err: err}
				mu.Unlock()
				return
			}
			if resp.StatusCode >= 400 {
				apiErr := client.ParseError(resp)
				mu.Lock()
				results[name] = fetchResult{err: fmt.Errorf("%s: %s", apiErr.ErrCode, apiErr.Message)}
				mu.Unlock()
				return
			}
			mu.Lock()
			results[name] = fetchResult{data: resp.Body}
			mu.Unlock()
		}(name, path)
	}
	wg.Wait()

	// Parse results (errors are stored for display, not fatal)
	var runtimes []map[string]any
	var containers []map[string]any
	var stats map[string]any
	var queue map[string]any

	if r, ok := results["runtimes"]; ok && r.err == nil && r.data != nil {
		_ = json.Unmarshal(r.data, &runtimes)
	}
	if r, ok := results["containers"]; ok && r.err == nil && r.data != nil {
		_ = json.Unmarshal(r.data, &containers)
	}
	if r, ok := results["stats"]; ok && r.err == nil && r.data != nil {
		_ = json.Unmarshal(r.data, &stats)
	}
	if r, ok := results["queue"]; ok && r.err == nil && r.data != nil {
		_ = json.Unmarshal(r.data, &queue)
	}

	// If in JSON mode, output the full structured data
	if format == output.FormatJSON {
		outputData := map[string]any{
			"runtimes":   runtimes,
			"containers": containers,
			"stats":      stats,
			"queue":      queue,
		}
		// Add any fetch errors
		errs := make(map[string]string)
		for name, r := range results {
			if r.err != nil {
				errs[name] = r.err.Error()
			}
		}
		if len(errs) > 0 {
			outputData["fetchErrors"] = errs
		}
		return w.Print(outputData)
	}

	// --- Table mode: multi-section output ---

	// Build system health rows: group containers by runtimeId
	runtimeStatus := make(map[string]string) // id -> status
	runtimeName := make(map[string]string)   // id -> displayName
	for _, r := range runtimes {
		id := helpers.StrOrDash(r, "id")
		runtimeStatus[id] = helpers.StrOrDash(r, "status")
		runtimeName[id] = helpers.StrOrDash(r, "displayName")
	}

	type runtimeHealth struct {
		name    string
		status  string
		running int
		stopped int
		total   int
	}
	runtimeMap := make(map[string]*runtimeHealth)
	// Initialize from runtimes list
	for id, name := range runtimeName {
		healthStatus := "unknown"
		if s, ok := runtimeStatus[id]; ok && s != "-" {
			healthStatus = s
		}
		runtimeMap[id] = &runtimeHealth{
			name:   name,
			status: healthStatus,
		}
	}

	// Count containers per runtime
	for _, ct := range containers {
		rtID := helpers.StrOrDash(ct, "runtimeId")
		if rtID == "-" {
			// Try alternate field names
			rtID = helpers.StrOrDash(ct, "runtime")
		}
		if rtID == "-" || rtID == "" {
			continue
		}
		h, ok := runtimeMap[rtID]
		if !ok {
			runtimeMap[rtID] = &runtimeHealth{name: rtID, status: "unknown"}
			h = runtimeMap[rtID]
		}
		h.total++
		status := strings.ToLower(helpers.StrOrDash(ct, "status"))
		if status == "running" || status == "ready" {
			h.running++
		} else {
			h.stopped++
		}
	}

	// System Health table
	fmt.Println("SYSTEM HEALTH")
	healthHeaders := []string{"STATUS", "RUNTIME", "RUNNING", "STOPPED", "TOTAL"}
	healthRows := make([][]string, 0, len(runtimeMap))
	for _, h := range runtimeMap {
		statusDisplay := h.status
		if statusDisplay == "unknown" {
			statusDisplay = "unavailable"
		}
		row := []string{
			statusDisplay,
			h.name,
			fmt.Sprintf("%d", h.running),
			fmt.Sprintf("%d", h.stopped),
			fmt.Sprintf("%d", h.total),
		}
		healthRows = append(healthRows, w.StatusRow(row, 0))
	}
	if len(healthRows) == 0 {
		healthRows = append(healthRows, []string{"-", "-", "-", "-", "-"})
	}
	_ = w.PrintTable(healthHeaders, healthRows)

	// Queue table
	fmt.Println()
	fmt.Println("QUEUE")
	queueHeaders := []string{"DEPTH", "PENDING", "HOLDING", "PROCESSING"}
	queueRows := [][]string{
		{
			helpers.IntStr(queue["depth"]),
			helpers.IntStr(queue["pending"]),
			helpers.IntStr(queue["holding"]),
			helpers.IntStr(queue["processing"]),
		},
	}
	_ = w.PrintTable(queueHeaders, queueRows)

	// Summary table
	fmt.Println()
	fmt.Println("SUMMARY")
	totalRequests := "-"
	if v, ok := stats["totalRequests"]; ok && v != nil {
		totalRequests = helpers.IntStr(v)
	}
	containersRunning := "-"
	if v, ok := stats["containersRunning"]; ok && v != nil {
		containersRunning = helpers.IntStr(v)
	}
	queueDepth := "-"
	if v, ok := stats["queueDepth"]; ok && v != nil {
		queueDepth = helpers.IntStr(v)
	}
	avgLatency := "-"
	if v, ok := stats["avgLatencyMs"]; ok && v != nil {
		avgLatency = helpers.IntStr(v)
	}

	summaryHeaders := []string{"METRIC", "VALUE"}
	summaryRows := [][]string{
		{"Runtimes", fmt.Sprintf("%d", len(runtimes))},
		{"Containers Running", containersRunning},
		{"Total Requests", totalRequests},
		{"Queue Depth", queueDepth},
		{"Avg Latency (ms)", avgLatency},
	}
	return w.PrintTable(summaryHeaders, summaryRows)
}

// formatWithCommas formats an integer with comma separators for thousands.
func formatWithCommas(n int) string {
	if n < 1000 {
		return fmt.Sprintf("%d", n)
	}
	return formatWithCommas(n/1000) + fmt.Sprintf(",%03d", n%1000)
}

// renderHealthSummary prints a single-line health summary.
func renderHealthSummary(cmd *cobra.Command, format output.Format) error {
	c := GetClient(cmd)
	w := GetOutput(cmd)

	// Fetch all endpoints in parallel
	type fetchResult struct {
		data json.RawMessage
		err  error
	}

	var mu sync.Mutex
	var wg sync.WaitGroup

	endpoints := map[string]string{
		"runtimes":   "/api/containers/registered",
		"containers": "/api/containers",
		"stats":      "/api/stats",
		"queue":      "/api/queue/snapshot",
	}

	results := make(map[string]fetchResult)
	for name, path := range endpoints {
		wg.Add(1)
		go func(name, path string) {
			defer wg.Done()
			resp, err := c.Do(cmd.Context(), "GET", path, nil)
			if err != nil {
				mu.Lock()
				results[name] = fetchResult{err: err}
				mu.Unlock()
				return
			}
			if resp.StatusCode >= 400 {
				apiErr := client.ParseError(resp)
				mu.Lock()
				results[name] = fetchResult{err: fmt.Errorf("%s: %s", apiErr.ErrCode, apiErr.Message)}
				mu.Unlock()
				return
			}
			mu.Lock()
			results[name] = fetchResult{data: resp.Body}
			mu.Unlock()
		}(name, path)
	}
	wg.Wait()

	// Parse results
	var runtimes []map[string]any
	var containers []map[string]any
	var stats map[string]any
	var queue map[string]any

	if r, ok := results["runtimes"]; ok && r.err == nil && r.data != nil {
		_ = json.Unmarshal(r.data, &runtimes)
	}
	if r, ok := results["containers"]; ok && r.err == nil && r.data != nil {
		_ = json.Unmarshal(r.data, &containers)
	}
	if r, ok := results["stats"]; ok && r.err == nil && r.data != nil {
		_ = json.Unmarshal(r.data, &stats)
	}
	if r, ok := results["queue"]; ok && r.err == nil && r.data != nil {
		_ = json.Unmarshal(r.data, &queue)
	}

	// Count runtimes by status
	totalRuntimes := len(runtimes)
	runtimesOK := 0
	runtimesErr := 0
	for _, r := range runtimes {
		status := strings.ToLower(helpers.StrOrDash(r, "status"))
		if status == "ready" || status == "running" {
			runtimesOK++
		} else {
			runtimesErr++
		}
	}

	// Count containers by status
	totalContainers := len(containers)
	containersRunning := 0
	containersStopped := 0
	for _, ct := range containers {
		status := strings.ToLower(helpers.StrOrDash(ct, "status"))
		if status == "running" || status == "ready" {
			containersRunning++
		} else {
			containersStopped++
		}
	}

	// Queue metrics
	queueDepth := helpers.IntOrDash(queue, "depth")
	queuePending := helpers.IntOrDash(queue, "pending")

	// Stats metrics
	avgLatency := "-"
	if v, ok := stats["avgLatencyMs"]; ok && v != nil {
		avgLatency = helpers.IntStr(v) + "ms"
	}
	rpm := "-"
	if v, ok := stats["requestsPerMinute"]; ok && v != nil {
		if fv, ok := v.(float64); ok {
			rpm = fmt.Sprintf("%.1f", fv)
		}
	}

	if format == output.FormatJSON {
		outputData := map[string]any{
			"runtimesTotal":    totalRuntimes,
			"runtimesOK":       runtimesOK,
			"runtimesErr":      runtimesErr,
			"containersTotal":  totalContainers,
			"containersRunning": containersRunning,
			"containersStopped": containersStopped,
			"queueDepth":       queueDepth,
			"queuePending":     queuePending,
			"avgLatency":       avgLatency,
			"rpm":              rpm,
			"summary": fmt.Sprintf("Runtimes: %s (%s ok, %s err) | Containers: %s (%s running, %s stopped) | Queue: depth=%s pending=%s | Latency: %s | RPM: %s",
				formatWithCommas(totalRuntimes), formatWithCommas(runtimesOK), formatWithCommas(runtimesErr),
				formatWithCommas(totalContainers), formatWithCommas(containersRunning), formatWithCommas(containersStopped),
				queueDepth, queuePending, avgLatency, rpm),
		}
		// Add any fetch errors
		errs := make(map[string]string)
		for name, r := range results {
			if r.err != nil {
				errs[name] = r.err.Error()
			}
		}
		if len(errs) > 0 {
			outputData["fetchErrors"] = errs
		}
		return w.Print(outputData)
	}

	// Table/CSV mode: single line
	line := fmt.Sprintf("Runtimes: %s (%s ok, %s err) | Containers: %s (%s running, %s stopped) | Queue: depth=%s pending=%s | Latency: %s | RPM: %s",
		formatWithCommas(totalRuntimes), formatWithCommas(runtimesOK), formatWithCommas(runtimesErr),
		formatWithCommas(totalContainers), formatWithCommas(containersRunning), formatWithCommas(containersStopped),
		queueDepth, queuePending, avgLatency, rpm)

	// Colorize: green for the runtimes/containers OK counts, red for errors/stopped
	if !noColor {
		// Highlight the entire line based on whether there are errors
		if runtimesErr > 0 || containersStopped > 0 {
			line = "\033[33m" + line + "\033[0m" // yellow for warnings
		} else {
			line = "\033[32m" + line + "\033[0m" // green for all OK
		}
	}

	fmt.Fprintln(cmd.OutOrStdout(), line)
	return nil
}

func init() {
	healthCmd.Flags().Bool("watch", false, "Auto-refresh every 5s (min 2s)")
	healthCmd.Flags().String("interval", "", "Refresh interval for --watch (e.g. 5s, 10s)")
	healthCmd.Flags().Bool("summary", false, "Print a single-line health summary")
	healthCmd.MarkFlagsMutuallyExclusive("watch", "summary")
	rootCmd.AddCommand(healthCmd)
}
