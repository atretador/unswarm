package commands

import (
	"encoding/json"
	"fmt"

	"github.com/spf13/cobra"
	"github.com/unswarm/cli/internal/client"
	"github.com/unswarm/cli/internal/helpers"
)

var agentsCmd = &cobra.Command{
	Use:   "agents",
	Short: "Manage agents",
	Long:  `List agents and view their containers, scripts, and available scripts.`,
}

// --- agents list ---

var agentsListCmd = &cobra.Command{
	Use:   "list",
	Short: "List all agents",
	RunE: func(cmd *cobra.Command, args []string) error {
		c := GetClient(cmd)
		w := GetOutput(cmd)

		resp, err := c.Do(cmd.Context(), "GET", "/api/agents", nil)
		if err != nil {
			return FormatErrorResponse(w, err)
		}
		if resp.StatusCode >= 400 {
			apiErr := client.ParseError(resp)
			return w.Error(apiErr.ErrCode, apiErr.Message, apiErr.Status, apiErr.Hint, apiErr.ExitCode)
		}

		var agents []map[string]any
		if err := json.Unmarshal(resp.Body, &agents); err != nil {
			return w.Error("parse_error", "failed to parse response", nil, "", 1)
		}

		headers := []string{"NAME", "STATUS", "HOSTNAME", "GPU", "RAM", "CONTAINERS"}
		rows := make([][]string, 0, len(agents))
		for _, a := range agents {
			status := "Disconnected"
			if connected, ok := a["isConnected"].(bool); ok && connected {
				status = "Connected"
			}

			containers := "-"
			if cList, ok := a["containers"].([]any); ok {
				containers = fmt.Sprintf("%d", len(cList))
			}

			ram := "-"
			if ramVal, ok := a["totalMemoryMb"]; ok && ramVal != nil {
				ram = fmt.Sprintf("%v", ramVal)
			}

			rows = append(rows, []string{
				helpers.StrOrDash(a, "name"),
				status,
				helpers.StrOrDash(a, "hostname"),
				helpers.StrOrDash(a, "gpuInfo"),
				ram,
				containers,
			})
		}
		return w.PrintTable(headers, rows)
	},
}

// --- agents containers ---

var agentsContainersCmd = &cobra.Command{
	Use:   "containers <name>",
	Short: "List containers for an agent",
	Args:  cobra.ExactArgs(1),
	RunE: func(cmd *cobra.Command, args []string) error {
		c := GetClient(cmd)
		w := GetOutput(cmd)

		resp, err := c.Do(cmd.Context(), "GET", "/api/agents/"+args[0]+"/containers", nil)
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

		headers := []string{"NAME", "STATUS", "IMAGE", "PORT"}
		rows := make([][]string, 0, len(containers))
		for _, c := range containers {
			port := "-"
			if p, ok := c["port"]; ok && p != nil {
				port = fmt.Sprintf("%v", p)
			}
			rows = append(rows, []string{
				helpers.StrOrDash(c, "containerId"),
				helpers.StrOrDash(c, "status"),
				helpers.StrOrDash(c, "modelName"),
				port,
			})
		}
		return w.PrintTable(headers, rows)
	},
}

// --- agents scripts ---

var agentsScriptsCmd = &cobra.Command{
	Use:   "scripts <name>",
	Short: "List scripts running on an agent",
	Args:  cobra.ExactArgs(1),
	RunE: func(cmd *cobra.Command, args []string) error {
		c := GetClient(cmd)
		w := GetOutput(cmd)

		resp, err := c.Do(cmd.Context(), "GET", "/api/agents/"+args[0]+"/scripts", nil)
		if err != nil {
			return FormatErrorResponse(w, err)
		}
		if resp.StatusCode >= 400 {
			apiErr := client.ParseError(resp)
			return w.Error(apiErr.ErrCode, apiErr.Message, apiErr.Status, apiErr.Hint, apiErr.ExitCode)
		}

		var scripts []map[string]any
		if err := json.Unmarshal(resp.Body, &scripts); err != nil {
			return w.Error("parse_error", "failed to parse response", nil, "", 1)
		}

		headers := []string{"PATH", "STATUS"}
		rows := make([][]string, 0, len(scripts))
		for _, s := range scripts {
			rows = append(rows, []string{
				helpers.StrOrDash(s, "path"),
				helpers.StrOrDash(s, "status"),
			})
		}
		return w.PrintTable(headers, rows)
	},
}

// --- agents scripts-available ---

var agentsScriptsAvailableCmd = &cobra.Command{
	Use:   "scripts-available <name>",
	Short: "List available scripts for an agent",
	Args:  cobra.ExactArgs(1),
	RunE: func(cmd *cobra.Command, args []string) error {
		c := GetClient(cmd)
		w := GetOutput(cmd)

		resp, err := c.Do(cmd.Context(), "GET", "/api/agents/"+args[0]+"/scripts/available", nil)
		if err != nil {
			return FormatErrorResponse(w, err)
		}
		if resp.StatusCode >= 400 {
			apiErr := client.ParseError(resp)
			return w.Error(apiErr.ErrCode, apiErr.Message, apiErr.Status, apiErr.Hint, apiErr.ExitCode)
		}

		var scripts []map[string]any
		if err := json.Unmarshal(resp.Body, &scripts); err != nil {
			return w.Error("parse_error", "failed to parse response", nil, "", 1)
		}

		headers := []string{"NAME", "PATH"}
		rows := make([][]string, 0, len(scripts))
		for _, s := range scripts {
			rows = append(rows, []string{
				helpers.StrOrDash(s, "name"),
				helpers.StrOrDash(s, "path"),
			})
		}
		return w.PrintTable(headers, rows)
	},
}

func init() {
	agentsCmd.AddCommand(agentsListCmd)
	agentsCmd.AddCommand(agentsContainersCmd)
	agentsCmd.AddCommand(agentsScriptsCmd)
	agentsCmd.AddCommand(agentsScriptsAvailableCmd)

	rootCmd.AddCommand(agentsCmd)
}
