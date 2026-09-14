package commands

import (
	"encoding/json"
	"fmt"
	"sort"
	"strconv"
	"strings"

	"github.com/spf13/cobra"
	"github.com/unswarm/cli/internal/client"
	"github.com/unswarm/cli/internal/interact"
	"github.com/unswarm/cli/internal/output"
	"github.com/unswarm/cli/internal/resolve"
)

var runtimesCmd = &cobra.Command{
	Use:   "runtimes",
	Short: "Manage runtimes",
	Long:  `List, register, update, delete, start, stop, and manage runtimes.`,
}

// --- runtimes list ---

var runtimesListCmd = &cobra.Command{
	Use:   "list",
	Short: "List all runtimes",
	RunE: func(cmd *cobra.Command, args []string) error {
		c := GetClient(cmd)
		w := GetOutput(cmd)

		resp, err := c.Do(cmd.Context(), "GET", "/api/containers/registered", nil)
		if err != nil {
			return FormatErrorResponse(w, err)
		}
		if resp.StatusCode >= 400 {
			apiErr := client.ParseError(resp)
			return w.Error(apiErr.ErrCode, apiErr.Message, apiErr.Status, apiErr.Hint, apiErr.ExitCode)
		}

		var runtimes []Runtime
		if err := json.Unmarshal(resp.Body, &runtimes); err != nil {
			return w.Error("parse_error", "failed to parse response", nil, "", 1)
		}

		// Client-side filtering
		statusFilter, _ := cmd.Flags().GetString("status")
		agentFilter, _ := cmd.Flags().GetString("agent")
		filtered := runtimes[:0]
		for _, r := range runtimes {
			if statusFilter != "" {
				if !strings.EqualFold(r.Status, statusFilter) {
					continue
				}
			}
			if agentFilter != "" {
				if !strings.EqualFold(r.Agent, agentFilter) {
					continue
				}
			}
			filtered = append(filtered, r)
		}
		runtimes = filtered

		headers := []string{"ID", "NAME", "IMAGE", "STATUS", "PORT", "AGENT"}
		rows := make([][]string, 0, len(runtimes))
		for _, r := range runtimes {
			port := "-"
			if r.MappedPort != 0 {
				port = strconv.Itoa(r.MappedPort)
			} else if r.ContainerPort != 0 {
				port = strconv.Itoa(r.ContainerPort)
			}
			rows = append(rows, []string{
				dashIfEmpty(r.ID),
				dashIfEmpty(r.DisplayName),
				dashIfEmpty(r.Image),
				dashIfEmpty(r.Status),
				port,
				dashIfEmpty(r.Agent),
			})
		}
		return w.PrintTable(headers, rows)
	},
}

// --- runtimes get ---

var runtimesGetCmd = &cobra.Command{
	Use:   "get <name-or-id>",
	Short: "Get a runtime by name or ID",
	Args:  cobra.ExactArgs(1),
	RunE: func(cmd *cobra.Command, args []string) error {
		c := GetClient(cmd)
		w := GetOutput(cmd)

		resolver := resolve.New(newResolveAdapter(c), "/api/containers/registered", "displayName")
		resolvedID, err := resolver.Resolve(cmd.Context(), args[0])
		if err != nil {
			return w.Error("not_found", err.Error(), nil, "use 'runtimes list' to see available runtimes", 1)
		}

		resp, err := c.Do(cmd.Context(), "GET", "/api/containers/registered/"+resolvedID, nil)
		if err != nil {
			return FormatErrorResponse(w, err)
		}
		if resp.StatusCode >= 400 {
			apiErr := client.ParseError(resp)
			return w.Error(apiErr.ErrCode, apiErr.Message, apiErr.Status, apiErr.Hint, apiErr.ExitCode)
		}

		var runtime Runtime
		if err := json.Unmarshal(resp.Body, &runtime); err != nil {
			return w.Error("parse_error", "failed to parse response", nil, "", 1)
		}
		return w.Print(runtime)
	},
}

// --- runtimes register ---

// detectInteractive returns true if at least one of the key flags was explicitly set.
func detectInteractiveRegister(cmd *cobra.Command) bool {
	// If any flag was explicitly changed by the user, we're in scripting mode.
	for _, name := range []string{"name", "image", "container-port", "mapped-port", "kind", "launcher-path", "agent", "can-run-along-with", "max-concurrent"} {
		if cmd.Flags().Changed(name) {
			return false
		}
	}
	return true
}

var runtimesRegisterCmd = &cobra.Command{
	Use:   "register",
	Short: "Register a new runtime",
	RunE: func(cmd *cobra.Command, args []string) error {
		c := GetClient(cmd)
		w := GetOutput(cmd)

		// If no flags were provided, enter interactive wizard mode.
		if detectInteractiveRegister(cmd) {
			if IsQuiet(cmd) {
				return w.Error("missing_flags", "interactive mode requires a terminal; use --name and --image flags", nil, "run: unswarm runtimes register --name NAME --image IMAGE", 1)
			}
			return runInteractiveRegister(cmd, c, w)
		}

		// Scripting mode — exact same behaviour as before.
		return runFlagsRegister(cmd, c, w)
	},
}

// runInteractiveRegister prompts the user for every field, confirms, then registers.
func runInteractiveRegister(cmd *cobra.Command, c *client.Client, w *output.Writer) error {
	// --- prompts ---
	displayName, err := interact.Input("Runtime name:")
	if err != nil || displayName == "" {
		return fmt.Errorf("runtime name is required")
	}

	image, err := interact.Input("Container image:")
	if err != nil || image == "" {
		return fmt.Errorf("container image is required")
	}

	containerPortStr, err := interact.Input("Container port (default: same as image):")
	if err != nil {
		return fmt.Errorf("invalid container port: %w", err)
	}
	var containerPort int
	if containerPortStr != "" {
		containerPort, err = strconv.Atoi(containerPortStr)
		if err != nil {
			return fmt.Errorf("invalid container port: %w", err)
		}
	}

	mappedPortStr, err := interact.Input("Mapped host port (default: auto):")
	if err != nil {
		return fmt.Errorf("invalid mapped port: %w", err)
	}
	var mappedPort *int
	if mappedPortStr != "" {
		mp, err := strconv.Atoi(mappedPortStr)
		if err != nil {
			return fmt.Errorf("invalid mapped port: %w", err)
		}
		mappedPort = &mp
	}

	kindIdx, err := interact.Select("Runtime kind", []string{"docker", "local"})
	if err != nil || kindIdx < 0 {
		return fmt.Errorf("runtime kind is required (select 1 or 2)")
	}
	runtimeKind := []string{"docker", "local"}[kindIdx]

	agent, err := interact.Input("Agent name [host]:")
	if err != nil {
		return fmt.Errorf("invalid agent: %w", err)
	}
	if agent == "" {
		agent = "host"
	}

	maxConcurrentStr, err := interact.Input("Max concurrent inferences [1]:")
	if err != nil {
		return fmt.Errorf("invalid max concurrent: %w", err)
	}
	maxConcurrent := 1
	if maxConcurrentStr != "" {
		maxConcurrent, err = strconv.Atoi(maxConcurrentStr)
		if err != nil {
			return fmt.Errorf("invalid max concurrent: %w", err)
		}
	}

	// --- summary ---
	portDisplay := "(auto)"
	if containerPort > 0 {
		portDisplay = strconv.Itoa(containerPort)
	}
	mappedDisplay := "(auto)"
	if mappedPort != nil {
		mappedDisplay = strconv.Itoa(*mappedPort)
	}
	summary := fmt.Sprintf("Register runtime:\n  Name:             %s\n  Image:            %s\n  Container port:   %s\n  Mapped port:      %s\n  Kind:             %s\n  Agent:            %s\n  Max concurrent:   %d\nProceed?",
		displayName, image, portDisplay, mappedDisplay, runtimeKind, agent, maxConcurrent)

	confirmed, err := ConfirmOrSkip(cmd, summary)
	if err != nil {
		return err
	}
	if !confirmed {
		return nil
	}

	// --- build body & POST ---
	body := map[string]any{
		"displayName":             displayName,
		"image":                   image,
		"agent":                   agent,
		"canRunAlongWith":         []string{},
		"maxConcurrentInferences": maxConcurrent,
	}
	if containerPort > 0 {
		body["containerPort"] = containerPort
	}
	if mappedPort != nil {
		body["mappedPort"] = *mappedPort
	}
	if runtimeKind != "" {
		body["runtimeKind"] = runtimeKind
	}

	return doRegister(cmd, c, w, body)
}

// runFlagsRegister handles the original flag-based registration path.
func runFlagsRegister(cmd *cobra.Command, c *client.Client, w *output.Writer) error {
	displayName, _ := cmd.Flags().GetString("name")
	image, _ := cmd.Flags().GetString("image")
	containerPort, _ := cmd.Flags().GetInt("container-port")
	mappedPort, _ := cmd.Flags().GetInt("mapped-port")
	runtimeKind, _ := cmd.Flags().GetString("kind")
	launcherPath, _ := cmd.Flags().GetString("launcher-path")
	agent, _ := cmd.Flags().GetString("agent")
	canRunAlongWith, _ := cmd.Flags().GetStringSlice("can-run-along-with")
	maxConcurrent, _ := cmd.Flags().GetInt("max-concurrent")

	body := map[string]any{
		"displayName":             displayName,
		"image":                   image,
		"containerPort":           containerPort,
		"agent":                   agent,
		"canRunAlongWith":         canRunAlongWith,
		"maxConcurrentInferences": maxConcurrent,
	}
	if cmd.Flags().Changed("mapped-port") {
		body["mappedPort"] = mappedPort
	}
	if runtimeKind != "" {
		body["runtimeKind"] = runtimeKind
	}
	if launcherPath != "" {
		body["launcherPath"] = launcherPath
	}

	return doRegister(cmd, c, w, body)
}

// doRegister makes the POST and handles the response.
func doRegister(cmd *cobra.Command, c *client.Client, w *output.Writer, body map[string]any) error {
	resp, err := c.Do(cmd.Context(), "POST", "/api/containers/register", body)
	if err != nil {
		return FormatErrorResponse(w, err)
	}
	if resp.StatusCode >= 400 {
		apiErr := client.ParseError(resp)
		return w.Error(apiErr.ErrCode, apiErr.Message, apiErr.Status, apiErr.Hint, apiErr.ExitCode)
	}

	var runtime map[string]any
	if err := json.Unmarshal(resp.Body, &runtime); err != nil {
		return w.Error("parse_error", "failed to parse response", nil, "", 1)
	}
	return w.Print(runtime)
}

// --- runtimes update ---

var runtimesUpdateCmd = &cobra.Command{
	Use:   "update <name-or-id>",
	Short: "Update a runtime",
	Args:  cobra.ExactArgs(1),
	RunE: func(cmd *cobra.Command, args []string) error {
		c := GetClient(cmd)
		w := GetOutput(cmd)

		resolver := resolve.New(newResolveAdapter(c), "/api/containers/registered", "displayName")
		resolvedID, err := resolver.Resolve(cmd.Context(), args[0])
		if err != nil {
			return w.Error("not_found", err.Error(), nil, "use 'runtimes list' to see available runtimes", 1)
		}

		body := make(map[string]any)
		if cmd.Flags().Changed("name") {
			v, _ := cmd.Flags().GetString("name")
			body["displayName"] = v
		}
		if cmd.Flags().Changed("image") {
			v, _ := cmd.Flags().GetString("image")
			body["image"] = v
		}
		if cmd.Flags().Changed("container-port") {
			v, _ := cmd.Flags().GetInt("container-port")
			body["containerPort"] = v
		}
		if cmd.Flags().Changed("mapped-port") {
			v, _ := cmd.Flags().GetInt("mapped-port")
			body["mappedPort"] = v
		}
		if cmd.Flags().Changed("kind") {
			v, _ := cmd.Flags().GetString("kind")
			body["runtimeKind"] = v
		}
		if cmd.Flags().Changed("launcher-path") {
			v, _ := cmd.Flags().GetString("launcher-path")
			body["launcherPath"] = v
		}
		if cmd.Flags().Changed("agent") {
			v, _ := cmd.Flags().GetString("agent")
			body["agent"] = v
		}
		if cmd.Flags().Changed("can-run-along-with") {
			v, _ := cmd.Flags().GetStringSlice("can-run-along-with")
			body["canRunAlongWith"] = v
		}
		if cmd.Flags().Changed("max-concurrent") {
			v, _ := cmd.Flags().GetInt("max-concurrent")
			body["maxConcurrentInferences"] = v
		}

		resp, err := c.Do(cmd.Context(), "PUT", "/api/containers/registered/"+resolvedID, body)
		if err != nil {
			return FormatErrorResponse(w, err)
		}
		if resp.StatusCode >= 400 {
			apiErr := client.ParseError(resp)
			return w.Error(apiErr.ErrCode, apiErr.Message, apiErr.Status, apiErr.Hint, apiErr.ExitCode)
		}

		var runtime map[string]any
		if err := json.Unmarshal(resp.Body, &runtime); err != nil {
			return w.Error("parse_error", "failed to parse response", nil, "", 1)
		}
		return w.Print(runtime)
	},
}

// --- runtimes delete ---

var runtimesDeleteCmd = &cobra.Command{
	Use:   "delete <name-or-id>",
	Short: "Delete a runtime",
	Args:  cobra.ExactArgs(1),
	RunE: func(cmd *cobra.Command, args []string) error {
		c := GetClient(cmd)
		w := GetOutput(cmd)

		resolver := resolve.New(newResolveAdapter(c), "/api/containers/registered", "displayName")
		resolvedID, err := resolver.Resolve(cmd.Context(), args[0])
		if err != nil {
			return w.Error("not_found", err.Error(), nil, "use 'runtimes list' to see available runtimes", 1)
		}

		if IsDryRun(cmd) {
			w.DryRun(fmt.Sprintf("Would delete runtime %s", resolvedID))
			return nil
		}

		confirmed, err := ConfirmOrSkip(cmd, fmt.Sprintf("Delete runtime %s?", resolvedID))
		if err != nil {
			return err
		}
		if !confirmed {
			return nil
		}

		deleteModels, _ := cmd.Flags().GetBool("delete-models")
		path := "/api/containers/registered/" + resolvedID
		if deleteModels {
			path += "?deleteModels=true"
		}

		resp, err := c.Do(cmd.Context(), "DELETE", path, nil)
		if err != nil {
			return FormatErrorResponse(w, err)
		}
		if resp.StatusCode >= 400 {
			apiErr := client.ParseError(resp)
			return w.Error(apiErr.ErrCode, apiErr.Message, apiErr.Status, apiErr.Hint, apiErr.ExitCode)
		}

		return w.Print(map[string]any{"message": fmt.Sprintf("Deleted runtime %s", resolvedID)})
	},
}

// --- runtimes start ---

var runtimesStartCmd = &cobra.Command{
	Use:   "start <name-or-id>",
	Short: "Start a runtime",
	Args:  cobra.ExactArgs(1),
	RunE: func(cmd *cobra.Command, args []string) error {
		c := GetClient(cmd)
		w := GetOutput(cmd)

		resolver := resolve.New(newResolveAdapter(c), "/api/containers/registered", "displayName")
		resolvedID, err := resolver.Resolve(cmd.Context(), args[0])
		if err != nil {
			return w.Error("not_found", err.Error(), nil, "use 'runtimes list' to see available runtimes", 1)
		}

		if IsDryRun(cmd) {
			w.DryRun(fmt.Sprintf("Would start runtime %s", resolvedID))
			return nil
		}

		resp, err := c.Do(cmd.Context(), "POST", "/api/containers/registered/"+resolvedID+"/start", nil)
		if err != nil {
			return FormatErrorResponse(w, err)
		}
		if resp.StatusCode >= 400 {
			apiErr := client.ParseError(resp)
			return w.Error(apiErr.ErrCode, apiErr.Message, apiErr.Status, apiErr.Hint, apiErr.ExitCode)
		}

		if resp.StatusCode == 204 {
			return w.Print(map[string]any{"message": fmt.Sprintf("Started runtime %s", resolvedID)})
		}
		var runtime map[string]any
		if err := json.Unmarshal(resp.Body, &runtime); err != nil {
			return w.Print(map[string]any{"message": fmt.Sprintf("Started runtime %s", resolvedID)})
		}
		return w.Print(runtime)
	},
}

// --- runtimes stop ---

var runtimesStopCmd = &cobra.Command{
	Use:   "stop <name-or-id>",
	Short: "Stop a runtime",
	Args:  cobra.ExactArgs(1),
	RunE: func(cmd *cobra.Command, args []string) error {
		c := GetClient(cmd)
		w := GetOutput(cmd)

		resolver := resolve.New(newResolveAdapter(c), "/api/containers/registered", "displayName")
		resolvedID, err := resolver.Resolve(cmd.Context(), args[0])
		if err != nil {
			return w.Error("not_found", err.Error(), nil, "use 'runtimes list' to see available runtimes", 1)
		}

		if IsDryRun(cmd) {
			w.DryRun(fmt.Sprintf("Would stop runtime %s", resolvedID))
			return nil
		}

		resp, err := c.Do(cmd.Context(), "POST", "/api/containers/registered/"+resolvedID+"/stop", nil)
		if err != nil {
			return FormatErrorResponse(w, err)
		}
		if resp.StatusCode >= 400 {
			apiErr := client.ParseError(resp)
			return w.Error(apiErr.ErrCode, apiErr.Message, apiErr.Status, apiErr.Hint, apiErr.ExitCode)
		}

		return w.Print(map[string]any{"message": fmt.Sprintf("Stopped runtime %s", resolvedID)})
	},
}

// --- runtimes rediscover ---

var runtimesRediscoverCmd = &cobra.Command{
	Use:   "rediscover <name-or-id>",
	Short: "Rediscover models on a runtime",
	Args:  cobra.ExactArgs(1),
	RunE: func(cmd *cobra.Command, args []string) error {
		c := GetClient(cmd)
		w := GetOutput(cmd)

		resolver := resolve.New(newResolveAdapter(c), "/api/containers/registered", "displayName")
		resolvedID, err := resolver.Resolve(cmd.Context(), args[0])
		if err != nil {
			return w.Error("not_found", err.Error(), nil, "use 'runtimes list' to see available runtimes", 1)
		}

		resp, err := c.Do(cmd.Context(), "POST", "/api/containers/registered/"+resolvedID+"/rediscover", nil)
		if err != nil {
			return FormatErrorResponse(w, err)
		}
		if resp.StatusCode >= 400 {
			apiErr := client.ParseError(resp)
			return w.Error(apiErr.ErrCode, apiErr.Message, apiErr.Status, apiErr.Hint, apiErr.ExitCode)
		}

		if resp.StatusCode == 204 {
			return w.Print(map[string]any{"message": fmt.Sprintf("Rediscovery started for runtime %s", resolvedID)})
		}
		var result map[string]any
		if err := json.Unmarshal(resp.Body, &result); err != nil {
			return w.Print(map[string]any{"message": fmt.Sprintf("Rediscovery started for runtime %s", resolvedID)})
		}
		return w.Print(result)
	},
}

// --- runtimes healthcheck ---

var runtimesHealthcheckCmd = &cobra.Command{
	Use:   "healthcheck <name-or-id>",
	Short: "Run health check on a runtime",
	Args:  cobra.ExactArgs(1),
	RunE: func(cmd *cobra.Command, args []string) error {
		c := GetClient(cmd)
		w := GetOutput(cmd)

		resolver := resolve.New(newResolveAdapter(c), "/api/containers/registered", "displayName")
		resolvedID, err := resolver.Resolve(cmd.Context(), args[0])
		if err != nil {
			return w.Error("not_found", err.Error(), nil, "use 'runtimes list' to see available runtimes", 1)
		}

		resp, err := c.Do(cmd.Context(), "POST", "/api/containers/registered/"+resolvedID+"/healthcheck", nil)
		if err != nil {
			return FormatErrorResponse(w, err)
		}
		if resp.StatusCode >= 400 {
			apiErr := client.ParseError(resp)
			return w.Error(apiErr.ErrCode, apiErr.Message, apiErr.Status, apiErr.Hint, apiErr.ExitCode)
		}

		if resp.StatusCode == 204 {
			return w.Print(map[string]any{"message": fmt.Sprintf("Health check passed for runtime %s", resolvedID)})
		}
		var result map[string]any
		if err := json.Unmarshal(resp.Body, &result); err != nil {
			return w.Print(map[string]any{"message": fmt.Sprintf("Health check passed for runtime %s", resolvedID)})
		}
		return w.Print(result)
	},
}

// --- runtimes set-concurrency ---

var runtimesSetConcurrencyCmd = &cobra.Command{
	Use:   "set-concurrency <name-or-id>",
	Short: "Set concurrency settings for a runtime",
	Args:  cobra.ExactArgs(1),
	RunE: func(cmd *cobra.Command, args []string) error {
		c := GetClient(cmd)
		w := GetOutput(cmd)

		resolver := resolve.New(newResolveAdapter(c), "/api/containers/registered", "displayName")
		resolvedID, err := resolver.Resolve(cmd.Context(), args[0])
		if err != nil {
			return w.Error("not_found", err.Error(), nil, "use 'runtimes list' to see available runtimes", 1)
		}

		body := make(map[string]any)
		if cmd.Flags().Changed("can-run-along-with") {
			v, _ := cmd.Flags().GetStringSlice("can-run-along-with")
			body["canRunAlongWith"] = v
		}
		if cmd.Flags().Changed("max-concurrent") {
			v, _ := cmd.Flags().GetInt("max-concurrent")
			body["maxConcurrentInferences"] = v
		}

		resp, err := c.Do(cmd.Context(), "PUT", "/api/containers/registered/"+resolvedID+"/concurrency", body)
		if err != nil {
			return FormatErrorResponse(w, err)
		}
		if resp.StatusCode >= 400 {
			apiErr := client.ParseError(resp)
			return w.Error(apiErr.ErrCode, apiErr.Message, apiErr.Status, apiErr.Hint, apiErr.ExitCode)
		}

		var runtime map[string]any
		if err := json.Unmarshal(resp.Body, &runtime); err != nil {
			return w.Print(map[string]any{"message": fmt.Sprintf("Concurrency updated for runtime %s", resolvedID)})
		}
		return w.Print(runtime)
	},
}

// --- runtimes toggle-concurrency ---

var runtimesToggleConcurrencyCmd = &cobra.Command{
	Use:   "toggle-concurrency",
	Short: "Toggle concurrency between two runtimes",
	RunE: func(cmd *cobra.Command, args []string) error {
		c := GetClient(cmd)
		w := GetOutput(cmd)

		resolver := resolve.New(newResolveAdapter(c), "/api/containers/registered", "displayName")

		runtimeARaw, _ := cmd.Flags().GetString("runtime-a")
		runtimeBRaw, _ := cmd.Flags().GetString("runtime-b")

		runtimeA, err := resolver.Resolve(cmd.Context(), runtimeARaw)
		if err != nil {
			return w.Error("not_found", fmt.Sprintf("runtime-a: %s", err.Error()), nil, "use 'runtimes list' to see available runtimes", 1)
		}
		runtimeB, err := resolver.Resolve(cmd.Context(), runtimeBRaw)
		if err != nil {
			return w.Error("not_found", fmt.Sprintf("runtime-b: %s", err.Error()), nil, "use 'runtimes list' to see available runtimes", 1)
		}

		canRun, _ := cmd.Flags().GetBool("can-run")

		body := map[string]any{
			"runtimeAId":      runtimeA,
			"runtimeBId":      runtimeB,
			"canRunAlongWith": canRun,
		}

		resp, err := c.Do(cmd.Context(), "POST", "/api/containers/registered/concurrency", body)
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

// --- runtimes status ---

var runtimesStatusCmd = &cobra.Command{
	Use:   "status",
	Short: "Show status overview of all runtimes",
	RunE: func(cmd *cobra.Command, args []string) error {
		c := GetClient(cmd)
		w := GetOutput(cmd)

		resp, err := c.Do(cmd.Context(), "GET", "/api/containers/registered", nil)
		if err != nil {
			return FormatErrorResponse(w, err)
		}
		if resp.StatusCode >= 400 {
			apiErr := client.ParseError(resp)
			return w.Error(apiErr.ErrCode, apiErr.Message, apiErr.Status, apiErr.Hint, apiErr.ExitCode)
		}

		var runtimes []Runtime
		if err := json.Unmarshal(resp.Body, &runtimes); err != nil {
			return w.Error("parse_error", "failed to parse response", nil, "", 1)
		}

		// Status priority: lower = appears first
		statusOrder := map[string]int{
			"error":    0,
			"starting": 1,
			"running":  2,
			"ready":    3,
			"stopped":  4,
			"disabled": 5,
		}

		// Sort by status: errors first, then by priority
		sort.SliceStable(runtimes, func(i, j int) bool {
			si, oki := statusOrder[strings.ToLower(runtimes[i].Status)]
			sj, okj := statusOrder[strings.ToLower(runtimes[j].Status)]
			if !oki {
				si = 99
			}
			if !okj {
				sj = 99
			}
			return si < sj
		})

		headers := []string{"NAME", "STATUS", "PORT", "AGENT", "MODELS"}
		rows := make([][]string, 0, len(runtimes))
		for _, r := range runtimes {
			port := "-"
			if r.MappedPort != 0 {
				port = strconv.Itoa(r.MappedPort)
			} else if r.ContainerPort != 0 {
				port = strconv.Itoa(r.ContainerPort)
			}
			models := "-"
			if len(r.Models) > 0 {
				models = strconv.Itoa(len(r.Models))
			}
			rows = append(rows, []string{
				dashIfEmpty(r.DisplayName),
				dashIfEmpty(r.Status),
				port,
				dashIfEmpty(r.Agent),
				models,
			})
		}

		// Colorize the status column (index 1)
		for i := range rows {
			rows[i] = w.StatusRow(rows[i], 1)
		}

		return w.PrintTable(headers, rows)
	},
}

func init() {
	// Register flags
	runtimesRegisterCmd.Flags().String("name", "", "Runtime display name")
	runtimesRegisterCmd.Flags().String("image", "", "Container image")
	runtimesRegisterCmd.Flags().Int("container-port", 8080, "Container port")
	runtimesRegisterCmd.Flags().Int("mapped-port", 0, "Mapped host port (0 for auto)")
	runtimesRegisterCmd.Flags().String("kind", "", "Runtime kind: container|script")
	runtimesRegisterCmd.Flags().String("launcher-path", "", "Launcher script path (for script kind)")
	runtimesRegisterCmd.Flags().String("agent", "host", "Agent to run on")
	runtimesRegisterCmd.Flags().StringSlice("can-run-along-with", nil, "Runtime IDs this can run alongside")
	runtimesRegisterCmd.Flags().Int("max-concurrent", 1, "Max concurrent inferences")

	// Update flags
	runtimesUpdateCmd.Flags().String("name", "", "Runtime display name")
	runtimesUpdateCmd.Flags().String("image", "", "Container image")
	runtimesUpdateCmd.Flags().Int("container-port", 0, "Container port")
	runtimesUpdateCmd.Flags().Int("mapped-port", 0, "Mapped host port")
	runtimesUpdateCmd.Flags().String("kind", "", "Runtime kind")
	runtimesUpdateCmd.Flags().String("launcher-path", "", "Launcher script path")
	runtimesUpdateCmd.Flags().String("agent", "", "Agent name")
	runtimesUpdateCmd.Flags().StringSlice("can-run-along-with", nil, "Runtime IDs this can run alongside")
	runtimesUpdateCmd.Flags().Int("max-concurrent", 0, "Max concurrent inferences")

	// Delete flags
	runtimesDeleteCmd.Flags().Bool("delete-models", false, "Also delete models associated with this runtime")

	// List filters
	runtimesListCmd.Flags().String("status", "", "Filter by status (ready, error, starting)")
	runtimesListCmd.Flags().String("agent", "", "Filter by agent name")

	// Set-concurrency flags
	runtimesSetConcurrencyCmd.Flags().StringSlice("can-run-along-with", nil, "Runtime IDs this can run alongside")
	runtimesSetConcurrencyCmd.Flags().Int("max-concurrent", 0, "Max concurrent inferences")

	// Toggle-concurrency flags
	runtimesToggleConcurrencyCmd.Flags().String("runtime-a", "", "First runtime name or ID")
	runtimesToggleConcurrencyCmd.Flags().String("runtime-b", "", "Second runtime name or ID")
	runtimesToggleConcurrencyCmd.Flags().Bool("can-run", true, "Whether they can run alongside each other")
	_ = runtimesToggleConcurrencyCmd.MarkFlagRequired("runtime-a")
	_ = runtimesToggleConcurrencyCmd.MarkFlagRequired("runtime-b")

	runtimesCmd.AddCommand(runtimesListCmd)
	runtimesCmd.AddCommand(runtimesGetCmd)
	runtimesCmd.AddCommand(runtimesRegisterCmd)
	runtimesCmd.AddCommand(runtimesUpdateCmd)
	runtimesCmd.AddCommand(runtimesDeleteCmd)
	runtimesCmd.AddCommand(runtimesStartCmd)
	runtimesCmd.AddCommand(runtimesStopCmd)
	runtimesCmd.AddCommand(runtimesRediscoverCmd)
	runtimesCmd.AddCommand(runtimesHealthcheckCmd)
	runtimesCmd.AddCommand(runtimesSetConcurrencyCmd)
	runtimesCmd.AddCommand(runtimesToggleConcurrencyCmd)
	runtimesCmd.AddCommand(runtimesStatusCmd)

	rootCmd.AddCommand(runtimesCmd)
}
