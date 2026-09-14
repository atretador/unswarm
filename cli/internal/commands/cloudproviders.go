package commands

import (
	"encoding/json"
	"fmt"
	"strings"
	"time"

	"github.com/spf13/cobra"
	"github.com/unswarm/cli/internal/client"
	"github.com/unswarm/cli/internal/helpers"
	"github.com/unswarm/cli/internal/interact"
	"github.com/unswarm/cli/internal/resolve"
)

var cloudProvidersCmd = &cobra.Command{
	Use:   "cloud-providers",
	Short: "Manage cloud providers",
	Long:  `List, create, update, delete, and manage cloud providers and their models.`,
}

// --- cloud-providers list ---

var cloudProvidersListCmd = &cobra.Command{
	Use:   "list",
	Short: "List all cloud providers",
	RunE: func(cmd *cobra.Command, args []string) error {
		c := GetClient(cmd)
		w := GetOutput(cmd)

		resp, err := c.Do(cmd.Context(), "GET", "/api/cloudproviders", nil)
		if err != nil {
			return FormatErrorResponse(w, err)
		}
		if resp.StatusCode >= 400 {
			apiErr := client.ParseError(resp)
			return w.Error(apiErr.ErrCode, apiErr.Message, apiErr.Status, apiErr.Hint, apiErr.ExitCode)
		}

		var providers []map[string]any
		if err := json.Unmarshal(resp.Body, &providers); err != nil {
			return w.Error("parse_error", "failed to parse response", nil, "", 1)
		}

		headers := []string{"NAME", "BASE URL", "MODELS", "AUTH TYPE", "UPDATED"}
		rows := make([][]string, 0, len(providers))
		for _, p := range providers {
			authType := "-"
			if v, ok := p["authType"]; ok && v != nil {
				switch val := v.(type) {
				case float64:
					if val == 0 {
						authType = "apikey"
					} else if val == 1 {
						authType = "oauth"
					} else {
						authType = fmt.Sprintf("%d", int(val))
					}
				default:
					authType = fmt.Sprintf("%v", v)
				}
			}
			rows = append(rows, []string{
				helpers.StrOrDash(p, "name"),
				helpers.StrOrDash(p, "baseUrl"),
				helpers.IntStr(p["modelCount"]),
				authType,
				helpers.StrOrDash(p, "updatedAt"),
			})
		}
		return w.PrintTable(headers, rows)
	},
}

// --- cloud-providers get ---

var cloudProvidersGetCmd = &cobra.Command{
	Use:   "get <name-or-id>",
	Short: "Get a cloud provider by name or ID",
	Args:  cobra.ExactArgs(1),
	RunE: func(cmd *cobra.Command, args []string) error {
		c := GetClient(cmd)
		w := GetOutput(cmd)

		resolver := resolve.New(newResolveAdapter(c), "/api/cloudproviders", "name")
		resolvedID, err := resolver.Resolve(cmd.Context(), args[0])
		if err != nil {
			return w.Error("not_found", err.Error(), nil, "use 'cloud-providers list' to see available providers", 1)
		}

		resp, err := c.Do(cmd.Context(), "GET", "/api/cloudproviders/"+resolvedID, nil)
		if err != nil {
			return FormatErrorResponse(w, err)
		}
		if resp.StatusCode >= 400 {
			apiErr := client.ParseError(resp)
			return w.Error(apiErr.ErrCode, apiErr.Message, apiErr.Status, apiErr.Hint, apiErr.ExitCode)
		}

		var provider map[string]any
		if err := json.Unmarshal(resp.Body, &provider); err != nil {
			return w.Error("parse_error", "failed to parse response", nil, "", 1)
		}
		return w.Print(provider)
	},
}

// --- cloud-providers create ---

var cloudProvidersCreateCmd = &cobra.Command{
	Use:   "create",
	Short: "Create a new cloud provider",
	RunE: func(cmd *cobra.Command, args []string) error {
		c := GetClient(cmd)
		w := GetOutput(cmd)

		name, _ := cmd.Flags().GetString("name")
		baseURL, _ := cmd.Flags().GetString("base-url")
		authTypeStr, _ := cmd.Flags().GetString("auth-type")

		authType, err := parseAuthType(authTypeStr)
		if err != nil {
			return w.Error("invalid_auth_type", err.Error(), nil, "use 'apikey' or 'oauth'", 1)
		}

		body := map[string]any{
			"name":     name,
			"baseUrl":  baseURL,
			"authType": authType,
		}

		if cmd.Flags().Changed("api-key") {
			v, _ := cmd.Flags().GetString("api-key")
			body["apiKey"] = v
		}

		resp, err := c.Do(cmd.Context(), "POST", "/api/cloudproviders", body)
		if err != nil {
			return FormatErrorResponse(w, err)
		}
		if resp.StatusCode >= 400 {
			apiErr := client.ParseError(resp)
			return w.Error(apiErr.ErrCode, apiErr.Message, apiErr.Status, apiErr.Hint, apiErr.ExitCode)
		}

		var provider map[string]any
		if err := json.Unmarshal(resp.Body, &provider); err != nil {
			return w.Error("parse_error", "failed to parse response", nil, "", 1)
		}
		return w.Print(provider)
	},
}

// --- cloud-providers update ---

var cloudProvidersUpdateCmd = &cobra.Command{
	Use:   "update <name-or-id>",
	Short: "Update a cloud provider",
	Args:  cobra.ExactArgs(1),
	RunE: func(cmd *cobra.Command, args []string) error {
		c := GetClient(cmd)
		w := GetOutput(cmd)

		resolver := resolve.New(newResolveAdapter(c), "/api/cloudproviders", "name")
		resolvedID, err := resolver.Resolve(cmd.Context(), args[0])
		if err != nil {
			return w.Error("not_found", err.Error(), nil, "use 'cloud-providers list' to see available providers", 1)
		}

		body := make(map[string]any)
		if cmd.Flags().Changed("base-url") {
			v, _ := cmd.Flags().GetString("base-url")
			body["baseUrl"] = v
		}
		if cmd.Flags().Changed("api-key") {
			v, _ := cmd.Flags().GetString("api-key")
			body["apiKey"] = v
		}
		if cmd.Flags().Changed("api-key-hint") {
			v, _ := cmd.Flags().GetString("api-key-hint")
			body["apiKeyHint"] = v
		}

		resp, err := c.Do(cmd.Context(), "PUT", "/api/cloudproviders/"+resolvedID, body)
		if err != nil {
			return FormatErrorResponse(w, err)
		}
		if resp.StatusCode >= 400 {
			apiErr := client.ParseError(resp)
			return w.Error(apiErr.ErrCode, apiErr.Message, apiErr.Status, apiErr.Hint, apiErr.ExitCode)
		}

		var provider map[string]any
		if err := json.Unmarshal(resp.Body, &provider); err != nil {
			return w.Error("parse_error", "failed to parse response", nil, "", 1)
		}
		return w.Print(provider)
	},
}

// --- cloud-providers delete ---

var cloudProvidersDeleteCmd = &cobra.Command{
	Use:   "delete <name-or-id>",
	Short: "Delete a cloud provider",
	Args:  cobra.ExactArgs(1),
	RunE: func(cmd *cobra.Command, args []string) error {
		c := GetClient(cmd)
		w := GetOutput(cmd)

		resolver := resolve.New(newResolveAdapter(c), "/api/cloudproviders", "name")
		resolvedID, err := resolver.Resolve(cmd.Context(), args[0])
		if err != nil {
			return w.Error("not_found", err.Error(), nil, "use 'cloud-providers list' to see available providers", 1)
		}

		if IsDryRun(cmd) {
			w.DryRun(fmt.Sprintf("Would delete cloud provider %s", resolvedID))
			return nil
		}

		confirmed, err := ConfirmOrSkip(cmd, fmt.Sprintf("Delete cloud provider %s?", resolvedID))
		if err != nil {
			return w.Error("confirmation_required", err.Error(), nil, "use --yes to skip confirmation", 1)
		}
		if !confirmed {
			return nil
		}

		resp, err := c.Do(cmd.Context(), "DELETE", "/api/cloudproviders/"+resolvedID, nil)
		if err != nil {
			return FormatErrorResponse(w, err)
		}
		if resp.StatusCode >= 400 {
			apiErr := client.ParseError(resp)
			return w.Error(apiErr.ErrCode, apiErr.Message, apiErr.Status, apiErr.Hint, apiErr.ExitCode)
		}

		return w.Print(map[string]any{"message": fmt.Sprintf("Deleted cloud provider %s", resolvedID)})
	},
}

// --- cloud-providers fetch-models ---

var cloudProvidersFetchModelsCmd = &cobra.Command{
	Use:   "fetch-models <name-or-id>",
	Short: "Fetch available models from a cloud provider",
	Args:  cobra.ExactArgs(1),
	RunE: func(cmd *cobra.Command, args []string) error {
		c := GetClient(cmd)
		w := GetOutput(cmd)

		resolver := resolve.New(newResolveAdapter(c), "/api/cloudproviders", "name")
		resolvedID, err := resolver.Resolve(cmd.Context(), args[0])
		if err != nil {
			return w.Error("not_found", err.Error(), nil, "use 'cloud-providers list' to see available providers", 1)
		}

		resp, err := c.Do(cmd.Context(), "POST", "/api/cloudproviders/"+resolvedID+"/fetch-models", nil)
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

// --- cloud-providers test-and-fetch ---

var cloudProvidersTestAndFetchCmd = &cobra.Command{
	Use:   "test-and-fetch",
	Short: "Test a cloud provider connection and fetch models",
	RunE: func(cmd *cobra.Command, args []string) error {
		c := GetClient(cmd)
		w := GetOutput(cmd)

		baseURL, _ := cmd.Flags().GetString("base-url")
		apiKey, _ := cmd.Flags().GetString("api-key")

		body := map[string]any{
			"baseUrl": baseURL,
			"apiKey":  apiKey,
		}

		resp, err := c.Do(cmd.Context(), "POST", "/api/cloudproviders/test-and-fetch", body)
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

// --- cloud-providers save-models ---

var cloudProvidersSaveModelsCmd = &cobra.Command{
	Use:   "save-models <name-or-id>",
	Short: "Save model IDs for a cloud provider",
	Args:  cobra.ExactArgs(1),
	RunE: func(cmd *cobra.Command, args []string) error {
		c := GetClient(cmd)
		w := GetOutput(cmd)

		resolver := resolve.New(newResolveAdapter(c), "/api/cloudproviders", "name")
		resolvedID, err := resolver.Resolve(cmd.Context(), args[0])
		if err != nil {
			return w.Error("not_found", err.Error(), nil, "use 'cloud-providers list' to see available providers", 1)
		}

		var modelIDs []any

		if cmd.Flags().Changed("model-ids") {
			modelIDsJSON, _ := cmd.Flags().GetString("model-ids")
			if err := json.Unmarshal([]byte(modelIDsJSON), &modelIDs); err != nil {
				return w.Error("invalid_model_ids", "failed to parse model IDs JSON", nil, "provide model IDs as a JSON array", 1)
			}
		} else {
			// Interactive model selection: fetch available models from the provider
			resp, err := c.Do(cmd.Context(), "POST", "/api/cloudproviders/"+resolvedID+"/fetch-models", nil)
			if err != nil {
				return FormatErrorResponse(w, err)
			}
			if resp.StatusCode >= 400 {
				apiErr := client.ParseError(resp)
				return w.Error(apiErr.ErrCode, apiErr.Message, apiErr.Status, apiErr.Hint, apiErr.ExitCode)
			}

			var result map[string]any
			if err := json.Unmarshal(resp.Body, &result); err != nil {
				return w.Error("parse_error", "failed to parse fetch-models response", nil, "", 1)
			}

			// Extract model list from the response
			models, ok := result["models"].([]any)
			if !ok || len(models) == 0 {
				return w.Error("no_models", "no models available from this provider", nil, "try 'cloud-providers fetch-models' first", 1)
			}

			// Build display options for selection
			options := make([]string, 0, len(models))
			for _, m := range models {
				if mm, ok := m.(map[string]any); ok {
					name := helpers.StrOrDash(mm, "name")
					if name == "-" {
						name = helpers.StrOrDash(mm, "id")
					}
					options = append(options, name)
				}
			}
			if len(options) == 0 {
				return w.Error("no_models", "no valid models found in response", nil, "", 1)
			}

			fmt.Fprintf(interact.Out, "Available models for %s:\n", resolvedID)
			idx, err := interact.Select("Select a model", options)
			if err != nil {
				return w.Error("selection_error", "failed to read selection", nil, "", 1)
			}
			if idx < 0 {
				return w.Error("selection_error", "invalid selection", nil, "", 1)
			}

			// Extract the ID of the selected model
			selectedModel := models[idx]
			if mm, ok := selectedModel.(map[string]any); ok {
				if id, ok := mm["id"].(string); ok {
					modelIDs = []any{id}
				}
			}
			if len(modelIDs) == 0 {
				return w.Error("selection_error", "could not extract model ID from selection", nil, "", 1)
			}
		}

		body := map[string]any{
			"modelIds": modelIDs,
		}

		resp, err := c.Do(cmd.Context(), "PUT", "/api/cloudproviders/"+resolvedID+"/models", body)
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

// --- cloud-providers oauth ---

var cloudProvidersOauthCmd = &cobra.Command{
	Use:   "oauth <name-or-id>",
	Short: "Authenticate with a cloud provider (interactive device code flow)",
	Args:  cobra.ExactArgs(1),
	RunE: func(cmd *cobra.Command, args []string) error {
		c := GetClient(cmd)
		w := GetOutput(cmd)

		resolver := resolve.New(newResolveAdapter(c), "/api/cloudproviders", "name")
		resolvedID, err := resolver.Resolve(cmd.Context(), args[0])
		if err != nil {
			return w.Error("not_found", err.Error(), nil, "use 'cloud-providers list' to see available providers", 1)
		}

		// Step 1: Start OAuth device code flow
		startResp, err := c.Do(cmd.Context(), "POST", "/api/cloudproviders/"+resolvedID+"/oauth/start", nil)
		if err != nil {
			return FormatErrorResponse(w, err)
		}
		if startResp.StatusCode >= 400 {
			apiErr := client.ParseError(startResp)
			return w.Error(apiErr.ErrCode, apiErr.Message, apiErr.Status, apiErr.Hint, apiErr.ExitCode)
		}

		var startResult map[string]any
		if err := json.Unmarshal(startResp.Body, &startResult); err != nil {
			return w.Error("parse_error", "failed to parse oauth start response", nil, "", 1)
		}

		// Step 2: Display user code and verification URL
		userCode, _ := startResult["userCode"].(string)
		verificationURL, _ := startResult["verificationUrl"].(string)
		if verificationURL == "" {
			verificationURL, _ = startResult["verification_url"].(string)
		}

		if userCode != "" {
			fmt.Fprintf(interact.Out, "\nTo authenticate, visit: %s\n", verificationURL)
			fmt.Fprintf(interact.Out, "Enter code: %s\n\n", userCode)
		}

		// Step 3: Poll until authenticated or timeout
		deviceAuthID, _ := startResult["deviceAuthId"].(string)
		if deviceAuthID == "" {
			deviceAuthID, _ = startResult["device_auth_id"].(string)
		}

		timeout := 5 * time.Minute
		pollInterval := 5 * time.Second
		deadline := time.Now().Add(timeout)

		fmt.Fprint(interact.Out, "Waiting for authentication... ")

		for time.Now().Before(deadline) {
			pollBody := map[string]any{
				"deviceAuthId": deviceAuthID,
				"userCode":     userCode,
			}

			pollResp, err := c.Do(cmd.Context(), "POST", "/api/cloudproviders/"+resolvedID+"/oauth/poll", pollBody)
			if err != nil {
				fmt.Fprintln(interact.Out)
				return FormatErrorResponse(w, err)
			}

			if pollResp.StatusCode >= 400 {
				fmt.Fprintln(interact.Out)
				apiErr := client.ParseError(pollResp)
				return w.Error(apiErr.ErrCode, apiErr.Message, apiErr.Status, apiErr.Hint, apiErr.ExitCode)
			}

			var pollResult map[string]any
			if err := json.Unmarshal(pollResp.Body, &pollResult); err != nil {
				fmt.Fprintln(interact.Out)
				return w.Error("parse_error", "failed to parse poll response", nil, "", 1)
			}

			status, _ := pollResult["status"].(string)
			switch strings.ToLower(status) {
			case "authenticated", "success", "complete":
				fmt.Fprintln(interact.Out, "done!")
				fmt.Fprintf(interact.Out, "Successfully authenticated with %s\n", resolvedID)
				return nil
			case "error", "failed", "expired":
				fmt.Fprintln(interact.Out, "failed!")
				msg, _ := pollResult["message"].(string)
				if msg == "" {
					msg = "authentication failed"
				}
				return w.Error("oauth_failed", msg, nil, "try running the oauth command again", 1)
			}

			// Still pending — wait and try again
			time.Sleep(pollInterval)
			fmt.Fprint(interact.Out, ".")
		}

		fmt.Fprintln(interact.Out, "\nTimed out waiting for authentication (5 minutes)")
		return w.Error("oauth_timeout", "authentication timed out", nil, "try running the oauth command again", 1)
	},
}

// --- cloud-providers oauth-start (scripting) ---

var cloudProvidersOauthStartCmd = &cobra.Command{
	Use:   "oauth-start <name-or-id>",
	Short: "Start OAuth device code flow for a cloud provider",
	Args:  cobra.ExactArgs(1),
	RunE: func(cmd *cobra.Command, args []string) error {
		c := GetClient(cmd)
		w := GetOutput(cmd)

		resolver := resolve.New(newResolveAdapter(c), "/api/cloudproviders", "name")
		resolvedID, err := resolver.Resolve(cmd.Context(), args[0])
		if err != nil {
			return w.Error("not_found", err.Error(), nil, "use 'cloud-providers list' to see available providers", 1)
		}

		resp, err := c.Do(cmd.Context(), "POST", "/api/cloudproviders/"+resolvedID+"/oauth/start", nil)
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

// --- cloud-providers oauth-poll (scripting) ---

var cloudProvidersOauthPollCmd = &cobra.Command{
	Use:   "oauth-poll <name-or-id>",
	Short: "Poll OAuth device code flow status",
	Args:  cobra.ExactArgs(1),
	RunE: func(cmd *cobra.Command, args []string) error {
		c := GetClient(cmd)
		w := GetOutput(cmd)

		resolver := resolve.New(newResolveAdapter(c), "/api/cloudproviders", "name")
		resolvedID, err := resolver.Resolve(cmd.Context(), args[0])
		if err != nil {
			return w.Error("not_found", err.Error(), nil, "use 'cloud-providers list' to see available providers", 1)
		}

		deviceAuthID, _ := cmd.Flags().GetString("device-auth-id")
		userCode, _ := cmd.Flags().GetString("user-code")

		body := map[string]any{
			"deviceAuthId": deviceAuthID,
			"userCode":     userCode,
		}

		resp, err := c.Do(cmd.Context(), "POST", "/api/cloudproviders/"+resolvedID+"/oauth/poll", body)
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

// --- cloud-providers oauth-refresh (scripting) ---

var cloudProvidersOauthRefreshCmd = &cobra.Command{
	Use:   "oauth-refresh <name-or-id>",
	Short: "Refresh OAuth token for a cloud provider",
	Args:  cobra.ExactArgs(1),
	RunE: func(cmd *cobra.Command, args []string) error {
		c := GetClient(cmd)
		w := GetOutput(cmd)

		resolver := resolve.New(newResolveAdapter(c), "/api/cloudproviders", "name")
		resolvedID, err := resolver.Resolve(cmd.Context(), args[0])
		if err != nil {
			return w.Error("not_found", err.Error(), nil, "use 'cloud-providers list' to see available providers", 1)
		}

		resp, err := c.Do(cmd.Context(), "POST", "/api/cloudproviders/"+resolvedID+"/oauth/refresh", nil)
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

func init() {
	// Create flags
	cloudProvidersCreateCmd.Flags().String("name", "", "Provider name")
	cloudProvidersCreateCmd.Flags().String("base-url", "", "Base URL")
	cloudProvidersCreateCmd.Flags().String("api-key", "", "API key (optional)")
	cloudProvidersCreateCmd.Flags().String("auth-type", "apikey", "Auth type: apikey or oauth")
	_ = cloudProvidersCreateCmd.MarkFlagRequired("name")
	_ = cloudProvidersCreateCmd.MarkFlagRequired("base-url")

	// Update flags
	cloudProvidersUpdateCmd.Flags().String("base-url", "", "Base URL")
	cloudProvidersUpdateCmd.Flags().String("api-key", "", "API key")
	cloudProvidersUpdateCmd.Flags().String("api-key-hint", "", "API key hint")

	// Test-and-fetch flags
	cloudProvidersTestAndFetchCmd.Flags().String("base-url", "", "Base URL to test")
	cloudProvidersTestAndFetchCmd.Flags().String("api-key", "", "API key to test")
	_ = cloudProvidersTestAndFetchCmd.MarkFlagRequired("base-url")
	_ = cloudProvidersTestAndFetchCmd.MarkFlagRequired("api-key")

	// Save-models flags (model-ids is optional; when omitted, interactive selection is used)
	cloudProvidersSaveModelsCmd.Flags().String("model-ids", "", "Model IDs as JSON array (omit for interactive selection)")

	// OAuth-poll flags
	cloudProvidersOauthPollCmd.Flags().String("device-auth-id", "", "Device auth ID")
	cloudProvidersOauthPollCmd.Flags().String("user-code", "", "User code")
	_ = cloudProvidersOauthPollCmd.MarkFlagRequired("device-auth-id")
	_ = cloudProvidersOauthPollCmd.MarkFlagRequired("user-code")

	cloudProvidersCmd.AddCommand(cloudProvidersListCmd)
	cloudProvidersCmd.AddCommand(cloudProvidersGetCmd)
	cloudProvidersCmd.AddCommand(cloudProvidersCreateCmd)
	cloudProvidersCmd.AddCommand(cloudProvidersUpdateCmd)
	cloudProvidersCmd.AddCommand(cloudProvidersDeleteCmd)
	cloudProvidersCmd.AddCommand(cloudProvidersFetchModelsCmd)
	cloudProvidersCmd.AddCommand(cloudProvidersTestAndFetchCmd)
	cloudProvidersCmd.AddCommand(cloudProvidersSaveModelsCmd)
	cloudProvidersCmd.AddCommand(cloudProvidersOauthCmd)
	cloudProvidersCmd.AddCommand(cloudProvidersOauthStartCmd)
	cloudProvidersCmd.AddCommand(cloudProvidersOauthPollCmd)
	cloudProvidersCmd.AddCommand(cloudProvidersOauthRefreshCmd)

	rootCmd.AddCommand(cloudProvidersCmd)
}

// parseAuthType parses auth type from string to int.
// Accepts: "apikey", "api-key", "api_key", "0" → 0; "oauth", "chatgpt", "1" → 1.
func parseAuthType(s string) (int, error) {
	s = strings.TrimSpace(s)
	switch strings.ToLower(s) {
	case "0", "apikey", "api_key", "api-key":
		return 0, nil
	case "1", "oauth", "chatgpt":
		return 1, nil
	default:
		return 0, fmt.Errorf("invalid auth type %q: use 'apikey' or 'oauth'", s)
	}
}
