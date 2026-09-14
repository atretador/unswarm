package commands

import (
	"encoding/json"
	"fmt"

	"github.com/spf13/cobra"
	"github.com/unswarm/cli/internal/client"
	"github.com/unswarm/cli/internal/helpers"
	"github.com/unswarm/cli/internal/interact"
	"github.com/unswarm/cli/internal/resolve"
)

var routerProfilesCmd = &cobra.Command{
	Use:   "router-profiles",
	Short: "Manage router profiles",
	Long:  `List, create, update, delete, and manage router profiles.`,
}

// --- router-profiles list ---

var routerProfilesListCmd = &cobra.Command{
	Use:   "list",
	Short: "List all router profiles",
	RunE: func(cmd *cobra.Command, args []string) error {
		c := GetClient(cmd)
		w := GetOutput(cmd)

		resp, err := c.Do(cmd.Context(), "GET", "/api/router-profiles", nil)
		if err != nil {
			return FormatErrorResponse(w, err)
		}
		if resp.StatusCode >= 400 {
			apiErr := client.ParseError(resp)
			return w.Error(apiErr.ErrCode, apiErr.Message, apiErr.Status, apiErr.Hint, apiErr.ExitCode)
		}

		var profiles []map[string]any
		if err := json.Unmarshal(resp.Body, &profiles); err != nil {
			return w.Error("parse_error", "failed to parse response", nil, "", 1)
		}

		headers := []string{"NAME", "MODE", "ENTRIES", "ACTIVE"}
		rows := make([][]string, 0, len(profiles))
		for _, p := range profiles {
			entryCount := "-"
			if entries, ok := p["entries"].([]any); ok {
				entryCount = fmt.Sprintf("%d", len(entries))
			}
			activeModel := "-"
			if v, ok := p["activeModelId"]; ok && v != nil {
				activeModel = fmt.Sprintf("%v", v)
			}
			rows = append(rows, []string{
				helpers.StrOrDash(p, "name"),
				helpers.StrOrDash(p, "mode"),
				entryCount,
				activeModel,
			})
		}
		return w.PrintTable(headers, rows)
	},
}

// --- router-profiles get ---

var routerProfilesGetCmd = &cobra.Command{
	Use:   "get <name-or-id>",
	Short: "Get a router profile by name or ID",
	Args:  cobra.ExactArgs(1),
	RunE: func(cmd *cobra.Command, args []string) error {
		c := GetClient(cmd)
		w := GetOutput(cmd)

		resolver := resolve.New(newResolveAdapter(c), "/api/router-profiles", "name")
		resolvedID, err := resolver.Resolve(cmd.Context(), args[0])
		if err != nil {
			return w.Error("not_found", err.Error(), nil, "use 'router-profiles list' to see available profiles", 1)
		}

		resp, err := c.Do(cmd.Context(), "GET", "/api/router-profiles/"+resolvedID, nil)
		if err != nil {
			return FormatErrorResponse(w, err)
		}
		if resp.StatusCode >= 400 {
			apiErr := client.ParseError(resp)
			return w.Error(apiErr.ErrCode, apiErr.Message, apiErr.Status, apiErr.Hint, apiErr.ExitCode)
		}

		var profile map[string]any
		if err := json.Unmarshal(resp.Body, &profile); err != nil {
			return w.Error("parse_error", "failed to parse response", nil, "", 1)
		}
		return w.Print(profile)
	},
}

// --- router-profiles create ---

var routerProfilesCreateCmd = &cobra.Command{
	Use:   "create",
	Short: "Create a new router profile",
	RunE: func(cmd *cobra.Command, args []string) error {
		c := GetClient(cmd)
		w := GetOutput(cmd)

		profileName, _ := cmd.Flags().GetString("name")
		if !cmd.Flags().Changed("name") {
			name, err := interact.Input("Profile name:")
			if err != nil || name == "" {
				return fmt.Errorf("profile name is required")
			}
			profileName = name
		}

		mode, _ := cmd.Flags().GetString("mode")
		if !cmd.Flags().Changed("mode") {
			modeVal, err := interact.Input("Mode (auto/manual) [auto]:")
			if err != nil {
				return fmt.Errorf("mode is required")
			}
			if modeVal == "" {
				modeVal = "auto"
			}
			mode = modeVal
		}

		entriesJSON, _ := cmd.Flags().GetString("entries")

		var entries []any
		if err := json.Unmarshal([]byte(entriesJSON), &entries); err != nil {
			return w.Error("invalid_entries", "failed to parse entries JSON", nil, "provide entries as a JSON array of {modelId, priority, isEnabled} objects", 1)
		}

		body := map[string]any{
			"name":    profileName,
			"mode":    mode,
			"entries": entries,
		}

		resp, err := c.Do(cmd.Context(), "POST", "/api/router-profiles", body)
		if err != nil {
			return FormatErrorResponse(w, err)
		}
		if resp.StatusCode >= 400 {
			apiErr := client.ParseError(resp)
			return w.Error(apiErr.ErrCode, apiErr.Message, apiErr.Status, apiErr.Hint, apiErr.ExitCode)
		}

		var profile map[string]any
		if err := json.Unmarshal(resp.Body, &profile); err != nil {
			return w.Error("parse_error", "failed to parse response", nil, "", 1)
		}
		return w.Print(profile)
	},
}

// --- router-profiles update ---

var routerProfilesUpdateCmd = &cobra.Command{
	Use:   "update <name-or-id>",
	Short: "Update a router profile",
	Args:  cobra.ExactArgs(1),
	RunE: func(cmd *cobra.Command, args []string) error {
		c := GetClient(cmd)
		w := GetOutput(cmd)

		resolver := resolve.New(newResolveAdapter(c), "/api/router-profiles", "name")
		resolvedID, err := resolver.Resolve(cmd.Context(), args[0])
		if err != nil {
			return w.Error("not_found", err.Error(), nil, "use 'router-profiles list' to see available profiles", 1)
		}

		body := make(map[string]any)
		if cmd.Flags().Changed("name") {
			v, _ := cmd.Flags().GetString("name")
			body["name"] = v
		}
		if cmd.Flags().Changed("mode") {
			v, _ := cmd.Flags().GetString("mode")
			body["mode"] = v
		}
		if cmd.Flags().Changed("entries") {
			v, _ := cmd.Flags().GetString("entries")
			var entries []any
			if err := json.Unmarshal([]byte(v), &entries); err != nil {
				return w.Error("invalid_entries", "failed to parse entries JSON", nil, "provide entries as a JSON array", 1)
			}
			body["entries"] = entries
		}

		resp, err := c.Do(cmd.Context(), "PUT", "/api/router-profiles/"+resolvedID, body)
		if err != nil {
			return FormatErrorResponse(w, err)
		}
		if resp.StatusCode >= 400 {
			apiErr := client.ParseError(resp)
			return w.Error(apiErr.ErrCode, apiErr.Message, apiErr.Status, apiErr.Hint, apiErr.ExitCode)
		}

		var profile map[string]any
		if err := json.Unmarshal(resp.Body, &profile); err != nil {
			return w.Error("parse_error", "failed to parse response", nil, "", 1)
		}
		return w.Print(profile)
	},
}

// --- router-profiles delete ---

var routerProfilesDeleteCmd = &cobra.Command{
	Use:   "delete <name-or-id>",
	Short: "Delete a router profile",
	Args:  cobra.ExactArgs(1),
	RunE: func(cmd *cobra.Command, args []string) error {
		c := GetClient(cmd)
		w := GetOutput(cmd)

		resolver := resolve.New(newResolveAdapter(c), "/api/router-profiles", "name")
		resolvedID, err := resolver.Resolve(cmd.Context(), args[0])
		if err != nil {
			return w.Error("not_found", err.Error(), nil, "use 'router-profiles list' to see available profiles", 1)
		}

		if IsDryRun(cmd) {
			w.DryRun(fmt.Sprintf("Would delete router profile %s", resolvedID))
			return nil
		}

		confirmed, err := ConfirmOrSkip(cmd, fmt.Sprintf("Delete router profile %s?", resolvedID))
		if err != nil {
			return err
		}
		if !confirmed {
			return nil
		}

		resp, err := c.Do(cmd.Context(), "DELETE", "/api/router-profiles/"+resolvedID, nil)
		if err != nil {
			return FormatErrorResponse(w, err)
		}
		if resp.StatusCode >= 400 {
			apiErr := client.ParseError(resp)
			return w.Error(apiErr.ErrCode, apiErr.Message, apiErr.Status, apiErr.Hint, apiErr.ExitCode)
		}

		return w.Print(map[string]any{"message": fmt.Sprintf("Deleted router profile %s", resolvedID)})
	},
}

// --- router-profiles add-entry ---

var routerProfilesAddEntryCmd = &cobra.Command{
	Use:   "add-entry <profile-name-or-id>",
	Short: "Add an entry to a router profile",
	Args:  cobra.ExactArgs(1),
	RunE: func(cmd *cobra.Command, args []string) error {
		c := GetClient(cmd)
		w := GetOutput(cmd)

		// Resolve profile
		profileResolver := resolve.New(newResolveAdapter(c), "/api/router-profiles", "name")
		profileID, err := profileResolver.Resolve(cmd.Context(), args[0])
		if err != nil {
			return w.Error("not_found", err.Error(), nil, "use 'router-profiles list' to see available profiles", 1)
		}

		// Resolve model
		modelInput, _ := cmd.Flags().GetString("model")
		if modelInput == "" {
			return w.Error("missing_flag", "--model is required", nil, "provide a model name or ID", 1)
		}
		modelResolver := resolve.New(newResolveAdapter(c), "/api/models", "displayName")
		modelID, err := modelResolver.Resolve(cmd.Context(), modelInput)
		if err != nil {
			return w.Error("not_found", fmt.Sprintf("model: %s", err.Error()), nil, "use 'models list' to see available models", 1)
		}

		// Resolve optional runtime
		runtimeInput, _ := cmd.Flags().GetString("runtime")
		var runtimeID string
		if runtimeInput != "" {
			runtimeResolver := resolve.New(newResolveAdapter(c), "/api/containers/registered", "displayName")
			runtimeID, err = runtimeResolver.Resolve(cmd.Context(), runtimeInput)
			if err != nil {
				return w.Error("not_found", fmt.Sprintf("runtime: %s", err.Error()), nil, "use 'runtimes list' to see available runtimes", 1)
			}
		}

		priority, _ := cmd.Flags().GetInt("priority")
		enabled, _ := cmd.Flags().GetBool("enabled")

		// Fetch current profile
		resp, err := c.Do(cmd.Context(), "GET", "/api/router-profiles/"+profileID, nil)
		if err != nil {
			return FormatErrorResponse(w, err)
		}
		if resp.StatusCode >= 400 {
			apiErr := client.ParseError(resp)
			return w.Error(apiErr.ErrCode, apiErr.Message, apiErr.Status, apiErr.Hint, apiErr.ExitCode)
		}

		var profile map[string]any
		if err := json.Unmarshal(resp.Body, &profile); err != nil {
			return w.Error("parse_error", "failed to parse response", nil, "", 1)
		}

		// Get existing entries
		var entries []map[string]any
		if e, ok := profile["entries"].([]any); ok {
			for _, entry := range e {
				if m, ok := entry.(map[string]any); ok {
					entries = append(entries, m)
				}
			}
		}

		// Build new entry
		newEntry := map[string]any{
			"modelId":   modelID,
			"priority":  priority,
			"isEnabled": enabled,
		}
		if runtimeID != "" {
			newEntry["runtimeId"] = runtimeID
		}

		entries = append(entries, newEntry)

		// Dry run
		if IsDryRun(cmd) {
			w.DryRun(fmt.Sprintf("Would add entry (model=%s, runtime=%s, priority=%d, enabled=%v) to profile %s",
				modelID, helpers.StrOrDash(newEntry, "runtimeId"), priority, enabled, profileID))
			return nil
		}

		// PUT updated profile (backend requires "name")
		updateBody := map[string]any{
			"name":    profile["name"],
			"entries": entries,
		}

		resp, err = c.Do(cmd.Context(), "PUT", "/api/router-profiles/"+profileID, updateBody)
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
		return w.Print(updated)
	},
}

// --- router-profiles remove-entry ---

var routerProfilesRemoveEntryCmd = &cobra.Command{
	Use:   "remove-entry <profile-name-or-id>",
	Short: "Remove an entry from a router profile",
	Args:  cobra.ExactArgs(1),
	RunE: func(cmd *cobra.Command, args []string) error {
		c := GetClient(cmd)
		w := GetOutput(cmd)

		// Resolve profile
		profileResolver := resolve.New(newResolveAdapter(c), "/api/router-profiles", "name")
		profileID, err := profileResolver.Resolve(cmd.Context(), args[0])
		if err != nil {
			return w.Error("not_found", err.Error(), nil, "use 'router-profiles list' to see available profiles", 1)
		}

		// Resolve model
		modelInput, _ := cmd.Flags().GetString("model")
		if modelInput == "" {
			return w.Error("missing_flag", "--model is required", nil, "provide a model name or ID", 1)
		}
		modelResolver := resolve.New(newResolveAdapter(c), "/api/models", "displayName")
		modelID, err := modelResolver.Resolve(cmd.Context(), modelInput)
		if err != nil {
			return w.Error("not_found", fmt.Sprintf("model: %s", err.Error()), nil, "use 'models list' to see available models", 1)
		}

		// Resolve optional runtime
		runtimeInput, _ := cmd.Flags().GetString("runtime")
		var runtimeID string
		if runtimeInput != "" {
			runtimeResolver := resolve.New(newResolveAdapter(c), "/api/containers/registered", "displayName")
			runtimeID, err = runtimeResolver.Resolve(cmd.Context(), runtimeInput)
			if err != nil {
				return w.Error("not_found", fmt.Sprintf("runtime: %s", err.Error()), nil, "use 'runtimes list' to see available runtimes", 1)
			}
		}

		// Fetch current profile
		resp, err := c.Do(cmd.Context(), "GET", "/api/router-profiles/"+profileID, nil)
		if err != nil {
			return FormatErrorResponse(w, err)
		}
		if resp.StatusCode >= 400 {
			apiErr := client.ParseError(resp)
			return w.Error(apiErr.ErrCode, apiErr.Message, apiErr.Status, apiErr.Hint, apiErr.ExitCode)
		}

		var profile map[string]any
		if err := json.Unmarshal(resp.Body, &profile); err != nil {
			return w.Error("parse_error", "failed to parse response", nil, "", 1)
		}

		// Get existing entries and find matching ones
		var entries []map[string]any
		if e, ok := profile["entries"].([]any); ok {
			for _, entry := range e {
				if m, ok := entry.(map[string]any); ok {
					entries = append(entries, m)
				}
			}
		}

		var matchIndices []int
		for i, entry := range entries {
			entryModelID, _ := entry["modelId"].(string)
			if entryModelID != modelID {
				continue
			}
			if runtimeID != "" {
				entryRuntimeID, _ := entry["runtimeId"].(string)
				if entryRuntimeID != runtimeID {
					continue
				}
			}
			matchIndices = append(matchIndices, i)
		}

		if len(matchIndices) == 0 {
			return w.Error("not_found", "no matching entry found", nil, "check the model and runtime names", 1)
		}

		// Determine which entry to remove
		var removeIdx int
		if len(matchIndices) == 1 {
			removeIdx = matchIndices[0]
		} else {
			// Multiple matches — let user select
			options := make([]string, 0, len(matchIndices))
			for _, idx := range matchIndices {
				e := entries[idx]
				desc := fmt.Sprintf("model=%v", e["modelId"])
				if rid, ok := e["runtimeId"]; ok && rid != nil && rid != "" {
					desc += fmt.Sprintf(", runtime=%v", rid)
				}
				if pri, ok := e["priority"]; ok {
					desc += fmt.Sprintf(", priority=%v", pri)
				}
				if en, ok := e["isEnabled"]; ok {
					desc += fmt.Sprintf(", enabled=%v", en)
				}
				options = append(options, desc)
			}
			sel, err := interact.Select("Multiple entries match — select one to remove", options)
			if err != nil || sel < 0 {
				return fmt.Errorf("selection cancelled or invalid")
			}
			removeIdx = matchIndices[sel]
		}

		// Dry run
		if IsDryRun(cmd) {
			w.DryRun(fmt.Sprintf("Would remove entry (model=%v) from profile %s", entries[removeIdx]["modelId"], profileID))
			return nil
		}

		// Remove the entry
		entries = append(entries[:removeIdx], entries[removeIdx+1:]...)

		// PUT updated profile (backend requires "name")
		updateBody := map[string]any{
			"name":    profile["name"],
			"entries": entries,
		}

		resp, err = c.Do(cmd.Context(), "PUT", "/api/router-profiles/"+profileID, updateBody)
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
		return w.Print(updated)
	},
}

// --- router-profiles set-active-entry ---

var routerProfilesSetActiveEntryCmd = &cobra.Command{
	Use:   "set-active-entry <name-or-id>",
	Short: "Set the active model entry in a router profile",
	Args:  cobra.ExactArgs(1),
	RunE: func(cmd *cobra.Command, args []string) error {
		c := GetClient(cmd)
		w := GetOutput(cmd)

		resolver := resolve.New(newResolveAdapter(c), "/api/router-profiles", "name")
		resolvedID, err := resolver.Resolve(cmd.Context(), args[0])
		if err != nil {
			return w.Error("not_found", err.Error(), nil, "use 'router-profiles list' to see available profiles", 1)
		}

		modelID, _ := cmd.Flags().GetString("model-id")

		body := map[string]any{
			"activeModelId": modelID,
		}

		resp, err := c.Do(cmd.Context(), "PATCH", "/api/router-profiles/"+resolvedID+"/active-entry", body)
		if err != nil {
			return FormatErrorResponse(w, err)
		}
		if resp.StatusCode >= 400 {
			apiErr := client.ParseError(resp)
			return w.Error(apiErr.ErrCode, apiErr.Message, apiErr.Status, apiErr.Hint, apiErr.ExitCode)
		}

		var profile map[string]any
		if err := json.Unmarshal(resp.Body, &profile); err != nil {
			return w.Error("parse_error", "failed to parse response", nil, "", 1)
		}
		return w.Print(profile)
	},
}

// --- router-profiles set-thinking-effort ---

var routerProfilesSetThinkingEffortCmd = &cobra.Command{
	Use:   "set-thinking-effort <name-or-id>",
	Short: "Set thinking effort override for a model in a router profile",
	Args:  cobra.ExactArgs(1),
	RunE: func(cmd *cobra.Command, args []string) error {
		c := GetClient(cmd)
		w := GetOutput(cmd)

		resolver := resolve.New(newResolveAdapter(c), "/api/router-profiles", "name")
		resolvedID, err := resolver.Resolve(cmd.Context(), args[0])
		if err != nil {
			return w.Error("not_found", err.Error(), nil, "use 'router-profiles list' to see available profiles", 1)
		}

		modelID, _ := cmd.Flags().GetString("model-id")
		effort, _ := cmd.Flags().GetString("effort")

		body := map[string]any{
			"modelId":              modelID,
			"thinkingEffortOverride": effort,
		}

		resp, err := c.Do(cmd.Context(), "PATCH", "/api/router-profiles/"+resolvedID+"/thinking-effort", body)
		if err != nil {
			return FormatErrorResponse(w, err)
		}
		if resp.StatusCode >= 400 {
			apiErr := client.ParseError(resp)
			return w.Error(apiErr.ErrCode, apiErr.Message, apiErr.Status, apiErr.Hint, apiErr.ExitCode)
		}

		var profile map[string]any
		if err := json.Unmarshal(resp.Body, &profile); err != nil {
			return w.Error("parse_error", "failed to parse response", nil, "", 1)
		}
		return w.Print(profile)
	},
}

// --- router-profiles status ---

var routerProfilesStatusCmd = &cobra.Command{
	Use:   "status",
	Short: "Show router profiles status",
	RunE: func(cmd *cobra.Command, args []string) error {
		c := GetClient(cmd)
		w := GetOutput(cmd)

		resp, err := c.Do(cmd.Context(), "GET", "/api/router-profiles/status", nil)
		if err != nil {
			return FormatErrorResponse(w, err)
		}
		if resp.StatusCode >= 400 {
			apiErr := client.ParseError(resp)
			return w.Error(apiErr.ErrCode, apiErr.Message, apiErr.Status, apiErr.Hint, apiErr.ExitCode)
		}

		var status map[string]any
		if err := json.Unmarshal(resp.Body, &status); err != nil {
			return w.Error("parse_error", "failed to parse response", nil, "", 1)
		}

		headers := []string{"PROFILE", "ACTIVE REQUESTS"}
		rows := make([][]string, 0, len(status))
		for name, count := range status {
			rows = append(rows, []string{
				name,
				helpers.IntStr(count),
			})
		}
		return w.PrintTable(headers, rows)
	},
}

func init() {
	// Create flags
	routerProfilesCreateCmd.Flags().String("name", "", "Profile name")
	routerProfilesCreateCmd.Flags().String("mode", "", "Mode: auto or manual")
	routerProfilesCreateCmd.Flags().String("entries", "[]", "Entries as JSON array")
	_ = routerProfilesCreateCmd.MarkFlagRequired("mode")
	_ = routerProfilesCreateCmd.MarkFlagRequired("entries")

	// Update flags
	routerProfilesUpdateCmd.Flags().String("name", "", "Profile name")
	routerProfilesUpdateCmd.Flags().String("mode", "", "Mode: auto or manual")
	routerProfilesUpdateCmd.Flags().String("entries", "", "Entries as JSON array")

	// Add-entry flags
	routerProfilesAddEntryCmd.Flags().String("model", "", "Model name or ID")
	routerProfilesAddEntryCmd.Flags().String("runtime", "", "Runtime name or ID (optional)")
	routerProfilesAddEntryCmd.Flags().Int("priority", 1, "Entry priority")
	routerProfilesAddEntryCmd.Flags().Bool("enabled", true, "Enable this entry")
	_ = routerProfilesAddEntryCmd.MarkFlagRequired("model")

	// Remove-entry flags
	routerProfilesRemoveEntryCmd.Flags().String("model", "", "Model name or ID to match")
	routerProfilesRemoveEntryCmd.Flags().String("runtime", "", "Runtime name or ID to match (optional)")
	_ = routerProfilesRemoveEntryCmd.MarkFlagRequired("model")

	// Set-active-entry flags
	routerProfilesSetActiveEntryCmd.Flags().String("model-id", "", "Model ID to set as active")
	_ = routerProfilesSetActiveEntryCmd.MarkFlagRequired("model-id")

	// Set-thinking-effort flags
	routerProfilesSetThinkingEffortCmd.Flags().String("model-id", "", "Model ID")
	routerProfilesSetThinkingEffortCmd.Flags().String("effort", "", "Thinking effort override")
	_ = routerProfilesSetThinkingEffortCmd.MarkFlagRequired("model-id")
	_ = routerProfilesSetThinkingEffortCmd.MarkFlagRequired("effort")

	routerProfilesCmd.AddCommand(routerProfilesListCmd)
	routerProfilesCmd.AddCommand(routerProfilesGetCmd)
	routerProfilesCmd.AddCommand(routerProfilesCreateCmd)
	routerProfilesCmd.AddCommand(routerProfilesUpdateCmd)
	routerProfilesCmd.AddCommand(routerProfilesDeleteCmd)
	routerProfilesCmd.AddCommand(routerProfilesAddEntryCmd)
	routerProfilesCmd.AddCommand(routerProfilesRemoveEntryCmd)
	routerProfilesCmd.AddCommand(routerProfilesSetActiveEntryCmd)
	routerProfilesCmd.AddCommand(routerProfilesSetThinkingEffortCmd)
	routerProfilesCmd.AddCommand(routerProfilesStatusCmd)

	rootCmd.AddCommand(routerProfilesCmd)
}
