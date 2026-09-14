package commands

import (
	"encoding/json"
	"fmt"

	"github.com/spf13/cobra"
	"github.com/unswarm/cli/internal/client"
	"github.com/unswarm/cli/internal/helpers"
	"github.com/unswarm/cli/internal/resolve"
)

var apikeysCmd = &cobra.Command{
	Use:   "apikeys",
	Short: "Manage API keys",
	Long:  `List, create, revoke, rotate, and manage API keys and their access/permissions.`,
}

// --- apikeys list ---

var apikeysListCmd = &cobra.Command{
	Use:   "list",
	Short: "List all API keys",
	RunE: func(cmd *cobra.Command, args []string) error {
		c := GetClient(cmd)
		w := GetOutput(cmd)

		resp, err := c.Do(cmd.Context(), "GET", "/api/api-keys", nil)
		if err != nil {
			return FormatErrorResponse(w, err)
		}
		if resp.StatusCode >= 400 {
			apiErr := client.ParseError(resp)
			return w.Error(apiErr.ErrCode, apiErr.Message, apiErr.Status, apiErr.Hint, apiErr.ExitCode)
		}

		var keys []map[string]any
		if err := json.Unmarshal(resp.Body, &keys); err != nil {
			return w.Error("parse_error", "failed to parse response", nil, "", 1)
		}

		headers := []string{"ID", "NAME", "PREFIX", "SCOPE", "ACTIVE", "CREATED"}
		rows := make([][]string, 0, len(keys))
		for _, k := range keys {
			rows = append(rows, []string{
				helpers.StrOrDash(k, "id"),
				helpers.StrOrDash(k, "name"),
				helpers.StrOrDash(k, "keyPrefix"),
				helpers.StrOrDash(k, "scope"),
				fmt.Sprintf("%v", k["isActive"]),
				helpers.StrOrDash(k, "createdAt"),
			})
		}
		return w.PrintTable(headers, rows)
	},
}

// --- apikeys get ---

var apikeysGetCmd = &cobra.Command{
	Use:   "get <name-or-id>",
	Short: "Get an API key by name or ID",
	Args:  cobra.ExactArgs(1),
	RunE: func(cmd *cobra.Command, args []string) error {
		c := GetClient(cmd)
		w := GetOutput(cmd)

		resolver := resolve.New(newResolveAdapter(c), "/api/api-keys", "name")
		resolvedID, err := resolver.Resolve(cmd.Context(), args[0])
		if err != nil {
			return w.Error("not_found", err.Error(), nil, "use 'apikeys list' to see available API keys", 1)
		}

		resp, err := c.Do(cmd.Context(), "GET", "/api/api-keys/"+resolvedID, nil)
		if err != nil {
			return FormatErrorResponse(w, err)
		}
		if resp.StatusCode >= 400 {
			apiErr := client.ParseError(resp)
			return w.Error(apiErr.ErrCode, apiErr.Message, apiErr.Status, apiErr.Hint, apiErr.ExitCode)
		}

		var key map[string]any
		if err := json.Unmarshal(resp.Body, &key); err != nil {
			return w.Error("parse_error", "failed to parse response", nil, "", 1)
		}
		return w.Print(key)
	},
}

// --- apikeys create ---

var apikeysCreateCmd = &cobra.Command{
	Use:   "create",
	Short: "Create a new inference API key",
	RunE: func(cmd *cobra.Command, args []string) error {
		c := GetClient(cmd)
		w := GetOutput(cmd)

		name, _ := cmd.Flags().GetString("name")

		body := map[string]any{
			"name": name,
		}

		resp, err := c.Do(cmd.Context(), "POST", "/api/api-keys", body)
		if err != nil {
			return FormatErrorResponse(w, err)
		}
		if resp.StatusCode >= 400 {
			apiErr := client.ParseError(resp)
			return w.Error(apiErr.ErrCode, apiErr.Message, apiErr.Status, apiErr.Hint, apiErr.ExitCode)
		}

		var key map[string]any
		if err := json.Unmarshal(resp.Body, &key); err != nil {
			return w.Error("parse_error", "failed to parse response", nil, "", 1)
		}
		return w.Print(key)
	},
}

// --- apikeys create-agent ---

var apikeysCreateAgentCmd = &cobra.Command{
	Use:   "create-agent",
	Short: "Create a new agent API key",
	RunE: func(cmd *cobra.Command, args []string) error {
		c := GetClient(cmd)
		w := GetOutput(cmd)

		name, _ := cmd.Flags().GetString("name")
		boundAgent, _ := cmd.Flags().GetString("bound-agent")

		body := map[string]any{
			"name": name,
		}
		if boundAgent != "" {
			body["boundAgentName"] = boundAgent
		}

		resp, err := c.Do(cmd.Context(), "POST", "/api/api-keys/agent", body)
		if err != nil {
			return FormatErrorResponse(w, err)
		}
		if resp.StatusCode >= 400 {
			apiErr := client.ParseError(resp)
			return w.Error(apiErr.ErrCode, apiErr.Message, apiErr.Status, apiErr.Hint, apiErr.ExitCode)
		}

		var key map[string]any
		if err := json.Unmarshal(resp.Body, &key); err != nil {
			return w.Error("parse_error", "failed to parse response", nil, "", 1)
		}
		return w.Print(key)
	},
}

// --- apikeys create-control-plane ---

var apikeysCreateControlPlaneCmd = &cobra.Command{
	Use:   "create-control-plane",
	Short: "Create a new control-plane API key",
	RunE: func(cmd *cobra.Command, args []string) error {
		c := GetClient(cmd)
		w := GetOutput(cmd)

		name, _ := cmd.Flags().GetString("name")
		permissionsJSON, _ := cmd.Flags().GetString("permissions")

		var permissions map[string]any
		if err := json.Unmarshal([]byte(permissionsJSON), &permissions); err != nil {
			return w.Error("invalid_permissions", "failed to parse permissions JSON", nil, "provide permissions as a JSON object", 1)
		}

		body := map[string]any{
			"name":        name,
			"permissions": permissions,
		}

		resp, err := c.Do(cmd.Context(), "POST", "/api/api-keys/control-plane", body)
		if err != nil {
			return FormatErrorResponse(w, err)
		}
		if resp.StatusCode >= 400 {
			apiErr := client.ParseError(resp)
			return w.Error(apiErr.ErrCode, apiErr.Message, apiErr.Status, apiErr.Hint, apiErr.ExitCode)
		}

		var key map[string]any
		if err := json.Unmarshal(resp.Body, &key); err != nil {
			return w.Error("parse_error", "failed to parse response", nil, "", 1)
		}
		return w.Print(key)
	},
}

// --- apikeys revoke ---

var apikeysRevokeCmd = &cobra.Command{
	Use:   "revoke <name-or-id>",
	Short: "Revoke an API key",
	Args:  cobra.ExactArgs(1),
	RunE: func(cmd *cobra.Command, args []string) error {
		c := GetClient(cmd)
		w := GetOutput(cmd)

		resolver := resolve.New(newResolveAdapter(c), "/api/api-keys", "name")
		resolvedID, err := resolver.Resolve(cmd.Context(), args[0])
		if err != nil {
			return w.Error("not_found", err.Error(), nil, "use 'apikeys list' to see available API keys", 1)
		}

		confirmed, err := ConfirmOrSkip(cmd, fmt.Sprintf("Revoke API key '%s'? This cannot be undone.", args[0]))
		if err != nil {
			return w.Error("confirmation_required", err.Error(), nil, "use --yes to skip confirmation", 1)
		}
		if !confirmed {
			return nil
		}

		resp, err := c.Do(cmd.Context(), "DELETE", "/api/api-keys/"+resolvedID, nil)
		if err != nil {
			return FormatErrorResponse(w, err)
		}
		if resp.StatusCode >= 400 {
			apiErr := client.ParseError(resp)
			return w.Error(apiErr.ErrCode, apiErr.Message, apiErr.Status, apiErr.Hint, apiErr.ExitCode)
		}

		return w.Print(map[string]any{"message": fmt.Sprintf("Revoked API key %s", resolvedID)})
	},
}

// --- apikeys rotate ---

var apikeysRotateCmd = &cobra.Command{
	Use:   "rotate <name-or-id>",
	Short: "Rotate an API key",
	Args:  cobra.ExactArgs(1),
	RunE: func(cmd *cobra.Command, args []string) error {
		c := GetClient(cmd)
		w := GetOutput(cmd)

		resolver := resolve.New(newResolveAdapter(c), "/api/api-keys", "name")
		resolvedID, err := resolver.Resolve(cmd.Context(), args[0])
		if err != nil {
			return w.Error("not_found", err.Error(), nil, "use 'apikeys list' to see available API keys", 1)
		}

		confirmed, err := ConfirmOrSkip(cmd, fmt.Sprintf("Rotate API key '%s'? The old key will be invalidated.", args[0]))
		if err != nil {
			return w.Error("confirmation_required", err.Error(), nil, "use --yes to skip confirmation", 1)
		}
		if !confirmed {
			return nil
		}

		resp, err := c.Do(cmd.Context(), "POST", "/api/api-keys/"+resolvedID+"/rotate", nil)
		if err != nil {
			return FormatErrorResponse(w, err)
		}
		if resp.StatusCode >= 400 {
			apiErr := client.ParseError(resp)
			return w.Error(apiErr.ErrCode, apiErr.Message, apiErr.Status, apiErr.Hint, apiErr.ExitCode)
		}

		var key map[string]any
		if err := json.Unmarshal(resp.Body, &key); err != nil {
			return w.Error("parse_error", "failed to parse response", nil, "", 1)
		}
		return w.Print(key)
	},
}

// --- apikeys get-access ---

var apikeysGetAccessCmd = &cobra.Command{
	Use:   "get-access <name-or-id>",
	Short: "Get access settings for an API key",
	Args:  cobra.ExactArgs(1),
	RunE: func(cmd *cobra.Command, args []string) error {
		c := GetClient(cmd)
		w := GetOutput(cmd)

		resolver := resolve.New(newResolveAdapter(c), "/api/api-keys", "name")
		resolvedID, err := resolver.Resolve(cmd.Context(), args[0])
		if err != nil {
			return w.Error("not_found", err.Error(), nil, "use 'apikeys list' to see available API keys", 1)
		}

		resp, err := c.Do(cmd.Context(), "GET", "/api/api-keys/"+resolvedID+"/access", nil)
		if err != nil {
			return FormatErrorResponse(w, err)
		}
		if resp.StatusCode >= 400 {
			apiErr := client.ParseError(resp)
			return w.Error(apiErr.ErrCode, apiErr.Message, apiErr.Status, apiErr.Hint, apiErr.ExitCode)
		}

		var access map[string]any
		if err := json.Unmarshal(resp.Body, &access); err != nil {
			return w.Error("parse_error", "failed to parse response", nil, "", 1)
		}
		return w.Print(access)
	},
}

// --- apikeys set-access ---

var apikeysSetAccessCmd = &cobra.Command{
	Use:   "set-access <name-or-id>",
	Short: "Set access settings for an API key",
	Args:  cobra.ExactArgs(1),
	RunE: func(cmd *cobra.Command, args []string) error {
		c := GetClient(cmd)
		w := GetOutput(cmd)

		resolver := resolve.New(newResolveAdapter(c), "/api/api-keys", "name")
		resolvedID, err := resolver.Resolve(cmd.Context(), args[0])
		if err != nil {
			return w.Error("not_found", err.Error(), nil, "use 'apikeys list' to see available API keys", 1)
		}

		providersJSON, _ := cmd.Flags().GetString("providers")
		modelsJSON, _ := cmd.Flags().GetString("models")

		var providers []any
		if providersJSON != "" {
			if err := json.Unmarshal([]byte(providersJSON), &providers); err != nil {
				return w.Error("invalid_providers", "failed to parse providers JSON", nil, "provide providers as a JSON array", 1)
			}
		}

		var models []any
		if modelsJSON != "" {
			if err := json.Unmarshal([]byte(modelsJSON), &models); err != nil {
				return w.Error("invalid_models", "failed to parse models JSON", nil, "provide models as a JSON array", 1)
			}
		}

		body := map[string]any{
			"providers": providers,
			"models":    models,
		}

		resp, err := c.Do(cmd.Context(), "PUT", "/api/api-keys/"+resolvedID+"/access", body)
		if err != nil {
			return FormatErrorResponse(w, err)
		}
		if resp.StatusCode >= 400 {
			apiErr := client.ParseError(resp)
			return w.Error(apiErr.ErrCode, apiErr.Message, apiErr.Status, apiErr.Hint, apiErr.ExitCode)
		}

		var access map[string]any
		if err := json.Unmarshal(resp.Body, &access); err != nil {
			return w.Error("parse_error", "failed to parse response", nil, "", 1)
		}
		return w.Print(access)
	},
}

// --- apikeys get-permissions ---

var apikeysGetPermissionsCmd = &cobra.Command{
	Use:   "get-permissions <name-or-id>",
	Short: "Get permissions for an API key",
	Args:  cobra.ExactArgs(1),
	RunE: func(cmd *cobra.Command, args []string) error {
		c := GetClient(cmd)
		w := GetOutput(cmd)

		resolver := resolve.New(newResolveAdapter(c), "/api/api-keys", "name")
		resolvedID, err := resolver.Resolve(cmd.Context(), args[0])
		if err != nil {
			return w.Error("not_found", err.Error(), nil, "use 'apikeys list' to see available API keys", 1)
		}

		resp, err := c.Do(cmd.Context(), "GET", "/api/api-keys/"+resolvedID+"/permissions", nil)
		if err != nil {
			return FormatErrorResponse(w, err)
		}
		if resp.StatusCode >= 400 {
			apiErr := client.ParseError(resp)
			return w.Error(apiErr.ErrCode, apiErr.Message, apiErr.Status, apiErr.Hint, apiErr.ExitCode)
		}

		var perms map[string]any
		if err := json.Unmarshal(resp.Body, &perms); err != nil {
			return w.Error("parse_error", "failed to parse response", nil, "", 1)
		}
		return w.Print(perms)
	},
}

// --- apikeys set-permissions ---

var apikeysSetPermissionsCmd = &cobra.Command{
	Use:   "set-permissions <name-or-id>",
	Short: "Set permissions for an API key",
	Args:  cobra.ExactArgs(1),
	RunE: func(cmd *cobra.Command, args []string) error {
		c := GetClient(cmd)
		w := GetOutput(cmd)

		resolver := resolve.New(newResolveAdapter(c), "/api/api-keys", "name")
		resolvedID, err := resolver.Resolve(cmd.Context(), args[0])
		if err != nil {
			return w.Error("not_found", err.Error(), nil, "use 'apikeys list' to see available API keys", 1)
		}

		permissionsJSON, _ := cmd.Flags().GetString("permissions")

		var permissions map[string]any
		if err := json.Unmarshal([]byte(permissionsJSON), &permissions); err != nil {
			return w.Error("invalid_permissions", "failed to parse permissions JSON", nil, "provide permissions as a JSON object", 1)
		}

		body := map[string]any{
			"permissions": permissions,
		}

		resp, err := c.Do(cmd.Context(), "PUT", "/api/api-keys/"+resolvedID+"/permissions", body)
		if err != nil {
			return FormatErrorResponse(w, err)
		}
		if resp.StatusCode >= 400 {
			apiErr := client.ParseError(resp)
			return w.Error(apiErr.ErrCode, apiErr.Message, apiErr.Status, apiErr.Hint, apiErr.ExitCode)
		}

		var perms map[string]any
		if err := json.Unmarshal(resp.Body, &perms); err != nil {
			return w.Error("parse_error", "failed to parse response", nil, "", 1)
		}
		return w.Print(perms)
	},
}

func init() {
	// Create flags
	apikeysCreateCmd.Flags().String("name", "", "Key name")

	// Create-agent flags
	apikeysCreateAgentCmd.Flags().String("name", "", "Key name")
	apikeysCreateAgentCmd.Flags().String("bound-agent", "", "Bound agent name (optional)")

	// Create-control-plane flags
	apikeysCreateControlPlaneCmd.Flags().String("name", "", "Key name")
	apikeysCreateControlPlaneCmd.Flags().String("permissions", "", "Permissions JSON (e.g. {\"domain\": \"rw\"})")

	// Set-access flags
	apikeysSetAccessCmd.Flags().String("providers", "[]", "Providers JSON array")
	apikeysSetAccessCmd.Flags().String("models", "[]", "Models JSON array")

	// Set-permissions flags
	apikeysSetPermissionsCmd.Flags().String("permissions", "", "Permissions JSON (e.g. {\"domain\": \"rw\"})")

	apikeysCmd.AddCommand(apikeysListCmd)
	apikeysCmd.AddCommand(apikeysGetCmd)
	apikeysCmd.AddCommand(apikeysCreateCmd)
	apikeysCmd.AddCommand(apikeysCreateAgentCmd)
	apikeysCmd.AddCommand(apikeysCreateControlPlaneCmd)
	apikeysCmd.AddCommand(apikeysRevokeCmd)
	apikeysCmd.AddCommand(apikeysRotateCmd)
	apikeysCmd.AddCommand(apikeysGetAccessCmd)
	apikeysCmd.AddCommand(apikeysSetAccessCmd)
	apikeysCmd.AddCommand(apikeysGetPermissionsCmd)
	apikeysCmd.AddCommand(apikeysSetPermissionsCmd)

	rootCmd.AddCommand(apikeysCmd)
}
