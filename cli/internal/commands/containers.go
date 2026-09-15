package commands

import (
	"encoding/json"
	"fmt"
	"os"
	"strings"
	"time"

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

// --- containers create ---

var containersCreateCmd = &cobra.Command{
	Use:   "create",
	Short: "Create a new container from a Docker image",
	Long:  `Create a new container by pulling a Docker image and starting it with the specified parameters.`,
	RunE: func(cmd *cobra.Command, args []string) error {
		c := GetClient(cmd)
		w := GetOutput(cmd)

		image, _ := cmd.Flags().GetString("image")
		if image == "" {
			return w.Error("missing_flag", "--image is required", nil, "specify a Docker image with --image", 1)
		}

		name, _ := cmd.Flags().GetString("name")
		if name == "" {
			// Derive name from image: docker.io/org/image:tag -> image
			name = image
			if idx := strings.LastIndex(name, "/"); idx >= 0 {
				name = name[idx+1:]
			}
			if idx := strings.Index(name, ":"); idx >= 0 {
				name = name[:idx]
			}
		}

		containerName, _ := cmd.Flags().GetString("container-name")
		containerPort, _ := cmd.Flags().GetInt("container-port")
		hostPort, _ := cmd.Flags().GetInt("host-port")
		shmSize, _ := cmd.Flags().GetInt("shm-size")
		ipc, _ := cmd.Flags().GetString("ipc")
		network, _ := cmd.Flags().GetString("network")
		restart, _ := cmd.Flags().GetString("restart")
		agent, _ := cmd.Flags().GetString("agent")
		detach, _ := cmd.Flags().GetBool("detach")
		dryRun, _ := cmd.Flags().GetBool("dry-run")

		devices, _ := cmd.Flags().GetStringArray("device")
		volumes, _ := cmd.Flags().GetStringArray("volume")
		envs, _ := cmd.Flags().GetStringArray("env")
		serverArgs, _ := cmd.Flags().GetStringArray("server-arg")

		// Parse volumes into structured format
		var volumeMounts []map[string]any
		for _, v := range volumes {
			parts := strings.SplitN(v, ":", 3)
			if len(parts) < 2 {
				return w.Error("invalid_volume", fmt.Sprintf("Invalid volume format: %s (expected host:container[:ro])", v), nil, "", 1)
			}
			mount := map[string]any{
				"host":      parts[0],
				"container": parts[1],
				"readonly":  len(parts) > 2 && parts[2] == "ro",
			}
			volumeMounts = append(volumeMounts, mount)
		}

		// Parse env vars into structured format
		var envVars []map[string]any
		for _, e := range envs {
			parts := strings.SplitN(e, "=", 2)
			if len(parts) != 2 {
				return w.Error("invalid_env", fmt.Sprintf("Invalid env format: %s (expected KEY=VALUE)", e), nil, "", 1)
			}
			envVars = append(envVars, map[string]any{"key": parts[0], "value": parts[1]})
		}

		// Build request body
		dockerParams := map[string]any{
			"containerPort": containerPort,
			"shmSizeMb":     shmSize,
			"ipcMode":       ipc,
			"networkMode":   network,
			"restartPolicy": restart,
		}
		if containerName != "" {
			dockerParams["containerName"] = containerName
		}
		if hostPort > 0 {
			dockerParams["hostPort"] = hostPort
		}
		if len(devices) > 0 {
			dockerParams["devices"] = devices
		}
		if len(volumeMounts) > 0 {
			dockerParams["volumes"] = volumeMounts
		}
		if len(envVars) > 0 {
			dockerParams["env"] = envVars
		}
		if len(serverArgs) > 0 {
			dockerParams["serverArgs"] = serverArgs
		}

		body := map[string]any{
			"image":        image,
			"name":         name,
			"dockerParams": dockerParams,
			"agent":        agent,
			"detach":       detach,
		}

		if dryRun {
			reqJSON, _ := json.MarshalIndent(body, "", "  ")
			w.DryRun(fmt.Sprintf("Would create container:\n%s", string(reqJSON)))
			return nil
		}

		resp, err := c.Do(cmd.Context(), "POST", "/api/containers/create", body)
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

		if detach {
			runtimeId := result["runtimeId"]
			return w.Print(map[string]any{
				"message":   "Container creation started in background",
				"runtimeId": runtimeId,
			})
		}

		// Poll for completion
		runtimeId, _ := result["runtimeId"].(string)
		if runtimeId == "" {
			return w.Print(result)
		}

		status, _ := result["status"].(string)
		status = strings.ToLower(status)
		for status != "ready" && status != "error" {
			time.Sleep(2 * time.Second)
			pollResp, err := c.Do(cmd.Context(), "GET", "/api/containers/registered/"+runtimeId, nil)
			if err != nil {
				return FormatErrorResponse(w, err)
			}
			if pollResp.StatusCode >= 400 {
				break
			}
			var pollResult map[string]any
			if err := json.Unmarshal(pollResp.Body, &pollResult); err != nil {
				break
			}
			status, _ = pollResult["status"].(string)
			fmt.Fprintf(os.Stderr, "Status: %s...\n", status)
		}

		// Get final state
		finalResp, err := c.Do(cmd.Context(), "GET", "/api/containers/registered/"+runtimeId, nil)
		if err != nil {
			return FormatErrorResponse(w, err)
		}
		var final map[string]any
		if err := json.Unmarshal(finalResp.Body, &final); err != nil {
			return w.Print(result)
		}
		return w.Print(final)
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

	// Create flags
	containersCreateCmd.Flags().String("image", "", "Docker image to use (required)")
	containersCreateCmd.Flags().String("name", "", "Display name (defaults to image slug)")
	containersCreateCmd.Flags().String("container-name", "", "Docker container name (defaults to unswarm-<name>)")
	containersCreateCmd.Flags().Int("container-port", 8080, "Container internal port")
	containersCreateCmd.Flags().Int("host-port", 0, "Host-mapped port (0 = auto)")
	containersCreateCmd.Flags().StringArray("device", nil, "Device to pass through (repeatable, e.g. --device /dev/kfd)")
	containersCreateCmd.Flags().StringArray("volume", nil, "Volume mount (repeatable, e.g. --volume /data:/models:ro)")
	containersCreateCmd.Flags().StringArray("env", nil, "Environment variable (repeatable, e.g. --env KEY=VALUE)")
	containersCreateCmd.Flags().Int("shm-size", 16384, "Shared memory size in MB")
	containersCreateCmd.Flags().String("ipc", "host", "IPC mode")
	containersCreateCmd.Flags().String("network", "bridge", "Network mode")
	containersCreateCmd.Flags().String("restart", "unless-stopped", "Restart policy")
	containersCreateCmd.Flags().StringArray("server-arg", nil, "llama-server argument (repeatable)")
	containersCreateCmd.Flags().String("agent", "host", "Target agent")
	containersCreateCmd.Flags().Bool("detach", false, "Return immediately without waiting for container to be ready")
	containersCreateCmd.Flags().Bool("dry-run", false, "Show what would be created without actually creating")
	_ = containersCreateCmd.MarkFlagRequired("image")

	containersCmd.AddCommand(containersListCmd)
	containersCmd.AddCommand(containersStartCmd)
	containersCmd.AddCommand(containersStopCmd)
	containersCmd.AddCommand(containersRestartCmd)
	containersCmd.AddCommand(containersCreateCmd)

	rootCmd.AddCommand(containersCmd)
}
