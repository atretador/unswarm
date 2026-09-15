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

// --- agents stats ---

var agentsStatsCmd = &cobra.Command{
	Use:   "stats <name-or-id>",
	Short: "Show live telemetry stats for an agent",
	Args:  cobra.ExactArgs(1),
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

		// Find the agent by name (case-insensitive).
		var found map[string]any
		target := strings.ToLower(args[0])
		for _, a := range agents {
			name, _ := a["name"].(string)
			if strings.EqualFold(name, target) {
				found = a
				break
			}
		}
		if found == nil {
			return w.Error("agent_not_found", fmt.Sprintf("agent %q not found", args[0]), nil, "use 'agents list' to see available agents", 1)
		}

		telemetry, ok := found["telemetry"].(map[string]any)
		if !ok || telemetry == nil {
			return w.Error("no_telemetry", "no telemetry data available for this agent", nil, "", 1)
		}

		if w.GetFormat() == output.FormatJSON {
			return w.Print(telemetry)
		}

		// Table mode: host stats
		host, _ := telemetry["host"].(map[string]any)
		gpus, _ := telemetry["gpus"].([]any)
		containers, _ := telemetry["containers"].(map[string]any)

		headers := []string{"RESOURCE", "USED", "TOTAL", "PERCENT"}
		rows := make([][]string, 0)

		// CPU
		cpuPercent := "-"
		if host != nil {
			if v, ok := host["cpuPercent"].(float64); ok {
				cpuPercent = fmt.Sprintf("%.1f%%", v)
			}
		}
		cpuTotal := "-"
		if host != nil {
			if v, ok := host["cpuTotal"].(float64); ok {
				cpuTotal = fmt.Sprintf("%.0f cores", v)
			}
		}
		rows = append(rows, []string{"CPU", "-", cpuTotal, cpuPercent})

		// RAM
		ramUsed := "-"
		ramTotal := "-"
		ramPercent := "-"
		if host != nil {
			if v, ok := host["ramUsedMb"].(float64); ok {
				ramUsed = fmt.Sprintf("%.1f GB", v/1024)
			}
			if v, ok := host["ramTotalMb"].(float64); ok {
				ramTotal = fmt.Sprintf("%.1f GB", v/1024)
			}
			if v, ok := host["ramPercent"].(float64); ok {
				ramPercent = fmt.Sprintf("%.1f%%", v)
			}
		}
		rows = append(rows, []string{"RAM", ramUsed, ramTotal, ramPercent})

		// GPUs
		for _, gpuRaw := range gpus {
			gpu, ok := gpuRaw.(map[string]any)
			if !ok {
				continue
			}
			idx := 0
			if v, ok := gpu["index"].(float64); ok {
				idx = int(v)
			}
			name := helpers.StrOrDash(gpu, "name")
			gpuLabel := fmt.Sprintf("GPU %d (%s)", idx, name)

			corePercent := "-"
			if v, ok := gpu["corePercent"].(float64); ok {
				corePercent = fmt.Sprintf("%.1f%%", v)
			}
			rows = append(rows, []string{gpuLabel, "-", "-", corePercent})

			// GPU VRAM
			vramUsed := "-"
			vramTotal := "-"
			vramPercent := "-"
			if v, ok := gpu["memoryUsedMb"].(float64); ok {
				vramUsed = fmt.Sprintf("%.1f GB", v/1024)
			}
			if v, ok := gpu["memoryTotalMb"].(float64); ok {
				vramTotal = fmt.Sprintf("%.1f GB", v/1024)
			}
			if v, ok := gpu["memoryPercent"].(float64); ok {
				vramPercent = fmt.Sprintf("%.1f%%", v)
			}
			rows = append(rows, []string{fmt.Sprintf("GPU %d VRAM", idx), vramUsed, vramTotal, vramPercent})
		}

		if err := w.PrintTable(headers, rows); err != nil {
			return err
		}

		// Container stats
		if len(containers) > 0 {
			ctHeaders := []string{"CONTAINER", "CPU", "RAM", "RAM%"}
			ctRows := make([][]string, 0, len(containers))
			for ctID, ctRaw := range containers {
				ct, ok := ctRaw.(map[string]any)
				if !ok {
					continue
				}
				ctCPU := "-"
				if v, ok := ct["cpuPercent"].(float64); ok {
					ctCPU = fmt.Sprintf("%.1f%%", v)
				}
				ctRAM := "-"
				if v, ok := ct["ramUsedMb"].(float64); ok {
					ctRAM = fmt.Sprintf("%.1f GB", v/1024)
				}
				ctRAMPercent := "-"
				if v, ok := ct["ramPercent"].(float64); ok {
					ctRAMPercent = fmt.Sprintf("%.1f%%", v)
				}
				ctRows = append(ctRows, []string{ctID, ctCPU, ctRAM, ctRAMPercent})
			}
			return w.PrintTable(ctHeaders, ctRows)
		}

		return nil
	},
}

func init() {
	agentsCmd.AddCommand(agentsListCmd)
	agentsCmd.AddCommand(agentsContainersCmd)
	agentsCmd.AddCommand(agentsScriptsCmd)
	agentsCmd.AddCommand(agentsScriptsAvailableCmd)
	agentsCmd.AddCommand(agentsStatsCmd)

	rootCmd.AddCommand(agentsCmd)
}
