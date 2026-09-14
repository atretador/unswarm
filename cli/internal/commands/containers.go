package commands

import (
	"encoding/json"
	"fmt"
	"strings"

	"github.com/spf13/cobra"
	"github.com/unswarm/cli/internal/client"
	"github.com/unswarm/cli/internal/helpers"
	"github.com/unswarm/cli/internal/resolve"
)

var containersCmd = &cobra.Command{
	Use:   "containers",
	Short: "Manage containers",
	Long:  `List, start, stop, and restart model containers.`,
}

// --- containers list ---

var containersListCmd = &cobra.Command{
	Use:   "list",
	Short: "List all containers",
	RunE: func(cmd *cobra.Command, args []string) error {
		c := GetClient(cmd)
		w := GetOutput(cmd)

		resp, err := c.Do(cmd.Context(), "GET", "/api/containers", nil)
		if err != nil {
			return FormatErrorResponse(w, err)
		}
		if resp.StatusCode >= 400 {
			apiErr := client.ParseError(resp)
			return w.Error(apiErr.ErrCode, apiErr.Message, apiErr.Status, apiErr.Hint, apiErr.ExitCode)
		}

		var containers []map[string]any
		if err := json.Unmarshal(resp.Body, &containers); err != nil {
			return w.Error("parse_error", "failed to parse response", nil, "", 1)
		}

		// Client-side filtering
		statusFilter, _ := cmd.Flags().GetString("status")
		modelFilter, _ := cmd.Flags().GetString("model")
		filtered := containers[:0]
		for _, ct := range containers {
			if statusFilter != "" {
				s := helpers.StrOrDash(ct, "status")
				if !strings.EqualFold(s, statusFilter) {
					continue
				}
			}
			if modelFilter != "" {
				m := helpers.StrOrDash(ct, "modelName")
				if !strings.Contains(strings.ToLower(m), strings.ToLower(modelFilter)) {
					continue
				}
			}
			filtered = append(filtered, ct)
		}
		containers = filtered

		headers := []string{"ID", "MODEL", "STATUS", "PORT", "CPU%", "MEM MB"}
		rows := make([][]string, 0, len(containers))
		for _, ct := range containers {
			id := helpers.StrOrDash(ct, "id")
			if len(id) > 12 {
				id = id[:12]
			}
			port := "-"
			if p, ok := ct["port"]; ok && p != nil {
				port = fmt.Sprintf("%v", p)
			}
			rows = append(rows, []string{
				id,
				helpers.StrOrDash(ct, "modelName"),
				helpers.StrOrDash(ct, "status"),
				port,
				helpers.FloatOrDash(ct, "cpuPercent"),
				helpers.IntOrDash(ct, "memoryMb"),
			})
		}
		return w.PrintTable(headers, rows)
	},
}

// --- containers start ---

var containersStartCmd = &cobra.Command{
	Use:   "start",
	Short: "Start a container for a model",
	RunE: func(cmd *cobra.Command, args []string) error {
		c := GetClient(cmd)
		w := GetOutput(cmd)

		modelRaw, _ := cmd.Flags().GetString("model")
		if modelRaw == "" {
			modelRaw, _ = cmd.Flags().GetString("model-id")
		}

		resolver := resolve.New(newResolveAdapter(c), "/api/models", "name")
		modelID, err := resolver.Resolve(cmd.Context(), modelRaw)
		if err != nil {
			return w.Error("not_found", err.Error(), nil, "use 'models list' to see available models", 1)
		}

		if IsDryRun(cmd) {
			w.DryRun(fmt.Sprintf("Would start container for model %s", modelID))
			return nil
		}

		body := map[string]any{
			"modelId": modelID,
		}

		resp, err := c.Do(cmd.Context(), "POST", "/api/containers/start", body)
		if err != nil {
			return FormatErrorResponse(w, err)
		}
		if resp.StatusCode >= 400 {
			apiErr := client.ParseError(resp)
			return w.Error(apiErr.ErrCode, apiErr.Message, apiErr.Status, apiErr.Hint, apiErr.ExitCode)
		}

		var container map[string]any
		if err := json.Unmarshal(resp.Body, &container); err != nil {
			return w.Error("parse_error", "failed to parse response", nil, "", 1)
		}
		return w.Print(container)
	},
}

// --- containers stop ---

var containersStopCmd = &cobra.Command{
	Use:   "stop <name-or-id>",
	Short: "Stop a container",
	Args:  cobra.ExactArgs(1),
	RunE: func(cmd *cobra.Command, args []string) error {
		c := GetClient(cmd)
		w := GetOutput(cmd)

		resolver := resolve.New(newResolveAdapter(c), "/api/containers", "modelName")
		resolvedID, err := resolver.Resolve(cmd.Context(), args[0])
		if err != nil {
			return w.Error("not_found", err.Error(), nil, "use 'containers list' to see available containers", 1)
		}

		if IsDryRun(cmd) {
			w.DryRun(fmt.Sprintf("Would stop container %s", resolvedID))
			return nil
		}

		confirmed, err := ConfirmOrSkip(cmd, fmt.Sprintf("Stop container %s?", resolvedID))
		if err != nil {
			return err
		}
		if !confirmed {
			return nil
		}

		resp, err := c.Do(cmd.Context(), "POST", "/api/containers/"+resolvedID+"/stop", nil)
		if err != nil {
			return FormatErrorResponse(w, err)
		}
		if resp.StatusCode >= 400 {
			apiErr := client.ParseError(resp)
			return w.Error(apiErr.ErrCode, apiErr.Message, apiErr.Status, apiErr.Hint, apiErr.ExitCode)
		}

		return w.Print(map[string]any{"message": fmt.Sprintf("Stopped container %s", resolvedID)})
	},
}

// --- containers restart ---

var containersRestartCmd = &cobra.Command{
	Use:   "restart <name-or-id>",
	Short: "Restart a container",
	Args:  cobra.ExactArgs(1),
	RunE: func(cmd *cobra.Command, args []string) error {
		c := GetClient(cmd)
		w := GetOutput(cmd)

		resolver := resolve.New(newResolveAdapter(c), "/api/containers", "modelName")
		resolvedID, err := resolver.Resolve(cmd.Context(), args[0])
		if err != nil {
			return w.Error("not_found", err.Error(), nil, "use 'containers list' to see available containers", 1)
		}

		if IsDryRun(cmd) {
			w.DryRun(fmt.Sprintf("Would restart container %s", resolvedID))
			return nil
		}

		confirmed, err := ConfirmOrSkip(cmd, fmt.Sprintf("Restart container %s?", resolvedID))
		if err != nil {
			return err
		}
		if !confirmed {
			return nil
		}

		resp, err := c.Do(cmd.Context(), "POST", "/api/containers/"+resolvedID+"/restart", nil)
		if err != nil {
			return FormatErrorResponse(w, err)
		}
		if resp.StatusCode >= 400 {
			apiErr := client.ParseError(resp)
			return w.Error(apiErr.ErrCode, apiErr.Message, apiErr.Status, apiErr.Hint, apiErr.ExitCode)
		}

		if resp.StatusCode == 204 {
			return w.Print(map[string]any{"message": fmt.Sprintf("Restarted container %s", resolvedID)})
		}
		var container map[string]any
		if err := json.Unmarshal(resp.Body, &container); err != nil {
			return w.Print(map[string]any{"message": fmt.Sprintf("Restarted container %s", resolvedID)})
		}
		return w.Print(container)
	},
}

func init() {
	// List filters
	containersListCmd.Flags().String("status", "", "Filter by status (running, stopped, etc.)")
	containersListCmd.Flags().String("model", "", "Filter by model name (substring match)")

	// Start flags
	containersStartCmd.Flags().String("model", "", "Model name or ID to start a container for")
	containersStartCmd.Flags().String("model-id", "", "Model ID to start a container for (deprecated: use --model)")
	_ = containersStartCmd.MarkFlagRequired("model")
	_ = containersStartCmd.Flags().MarkHidden("model-id")

	containersCmd.AddCommand(containersListCmd)
	containersCmd.AddCommand(containersStartCmd)
	containersCmd.AddCommand(containersStopCmd)
	containersCmd.AddCommand(containersRestartCmd)

	rootCmd.AddCommand(containersCmd)
}
