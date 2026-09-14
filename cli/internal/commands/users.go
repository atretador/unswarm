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

var usersCmd = &cobra.Command{
	Use:   "users",
	Short: "Manage users",
	Long:  `List, create, reset passwords, and delete users.`,
}

// --- users list ---

var usersListCmd = &cobra.Command{
	Use:   "list",
	Short: "List all users",
	RunE: func(cmd *cobra.Command, args []string) error {
		c := GetClient(cmd)
		w := GetOutput(cmd)

		resp, err := c.Do(cmd.Context(), "GET", "/api/users", nil)
		if err != nil {
			return FormatErrorResponse(w, err)
		}
		if resp.StatusCode >= 400 {
			apiErr := client.ParseError(resp)
			return w.Error(apiErr.ErrCode, apiErr.Message, apiErr.Status, apiErr.Hint, apiErr.ExitCode)
		}

		var users []map[string]any
		if err := json.Unmarshal(resp.Body, &users); err != nil {
			return w.Error("parse_error", "failed to parse response", nil, "", 1)
		}

		headers := []string{"ID", "USERNAME", "TEMP PASSWORD"}
		rows := make([][]string, 0, len(users))
		for _, u := range users {
			rows = append(rows, []string{
				helpers.StrOrDash(u, "id"),
				helpers.StrOrDash(u, "username"),
				fmt.Sprintf("%v", u["isTempPassword"]),
			})
		}
		return w.PrintTable(headers, rows)
	},
}

// --- users create ---

var usersCreateCmd = &cobra.Command{
	Use:   "create",
	Short: "Create a new user",
	RunE: func(cmd *cobra.Command, args []string) error {
		c := GetClient(cmd)
		w := GetOutput(cmd)

		username, _ := cmd.Flags().GetString("username")
		password, _ := cmd.Flags().GetString("password")

		// Interactive password prompt if not provided via flag
		if password == "" {
			if IsQuiet(cmd) {
				return w.Error("password_required", "password is required", nil, "use --password flag or run interactively", 1)
			}
			var err error
			password, err = interact.Password("Enter password:")
			if err != nil {
				return w.Error("input_error", err.Error(), nil, "", 1)
			}
			if password == "" {
				return w.Error("password_required", "password cannot be empty", nil, "", 1)
			}
		}

		body := map[string]any{
			"username": username,
			"password": password,
		}

		resp, err := c.Do(cmd.Context(), "POST", "/api/users", body)
		if err != nil {
			return FormatErrorResponse(w, err)
		}
		if resp.StatusCode >= 400 {
			apiErr := client.ParseError(resp)
			return w.Error(apiErr.ErrCode, apiErr.Message, apiErr.Status, apiErr.Hint, apiErr.ExitCode)
		}

		var user map[string]any
		if err := json.Unmarshal(resp.Body, &user); err != nil {
			return w.Error("parse_error", "failed to parse response", nil, "", 1)
		}
		return w.Print(user)
	},
}

// --- users reset-password ---

var usersResetPasswordCmd = &cobra.Command{
	Use:   "reset-password <name-or-id>",
	Short: "Reset a user's password",
	Args:  cobra.ExactArgs(1),
	RunE: func(cmd *cobra.Command, args []string) error {
		c := GetClient(cmd)
		w := GetOutput(cmd)

		resolver := resolve.New(newResolveAdapter(c), "/api/users", "username")
		resolvedID, err := resolver.Resolve(cmd.Context(), args[0])
		if err != nil {
			return w.Error("not_found", err.Error(), nil, "use 'users list' to see available users", 1)
		}

		newPassword, _ := cmd.Flags().GetString("new-password")

		// Interactive password prompt if not provided via flag
		if newPassword == "" {
			if IsQuiet(cmd) {
				return w.Error("password_required", "new password is required", nil, "use --new-password flag or run interactively", 1)
			}
			var err error
			newPassword, err = interact.Password("Enter new password:")
			if err != nil {
				return w.Error("input_error", err.Error(), nil, "", 1)
			}
			if newPassword == "" {
				return w.Error("password_required", "password cannot be empty", nil, "", 1)
			}
		}

		body := map[string]any{
			"newPassword": newPassword,
		}

		resp, err := c.Do(cmd.Context(), "POST", "/api/users/"+resolvedID+"/reset-password", body)
		if err != nil {
			return FormatErrorResponse(w, err)
		}
		if resp.StatusCode >= 400 {
			apiErr := client.ParseError(resp)
			return w.Error(apiErr.ErrCode, apiErr.Message, apiErr.Status, apiErr.Hint, apiErr.ExitCode)
		}

		return w.Print(map[string]any{"message": fmt.Sprintf("Password reset for user %s", resolvedID)})
	},
}

// --- users delete ---

var usersDeleteCmd = &cobra.Command{
	Use:   "delete <name-or-id>",
	Short: "Delete a user",
	Args:  cobra.ExactArgs(1),
	RunE: func(cmd *cobra.Command, args []string) error {
		c := GetClient(cmd)
		w := GetOutput(cmd)

		resolver := resolve.New(newResolveAdapter(c), "/api/users", "username")
		resolvedID, err := resolver.Resolve(cmd.Context(), args[0])
		if err != nil {
			return w.Error("not_found", err.Error(), nil, "use 'users list' to see available users", 1)
		}

		if IsDryRun(cmd) {
			w.DryRun(fmt.Sprintf("Would delete user %s", resolvedID))
			return nil
		}

		confirmed, err := ConfirmOrSkip(cmd, fmt.Sprintf("Delete user %s?", resolvedID))
		if err != nil {
			return w.Error("confirmation_required", err.Error(), nil, "use --yes to skip confirmation", 1)
		}
		if !confirmed {
			return nil
		}

		resp, err := c.Do(cmd.Context(), "DELETE", "/api/users/"+resolvedID, nil)
		if err != nil {
			return FormatErrorResponse(w, err)
		}
		if resp.StatusCode >= 400 {
			apiErr := client.ParseError(resp)
			return w.Error(apiErr.ErrCode, apiErr.Message, apiErr.Status, apiErr.Hint, apiErr.ExitCode)
		}

		return w.Print(map[string]any{"message": fmt.Sprintf("Deleted user %s", resolvedID)})
	},
}

func init() {
	// Create flags
	usersCreateCmd.Flags().String("username", "", "Username")
	usersCreateCmd.Flags().String("password", "", "Password")

	// Reset password flags
	usersResetPasswordCmd.Flags().String("new-password", "", "New password")

	usersCmd.AddCommand(usersListCmd)
	usersCmd.AddCommand(usersCreateCmd)
	usersCmd.AddCommand(usersResetPasswordCmd)
	usersCmd.AddCommand(usersDeleteCmd)

	rootCmd.AddCommand(usersCmd)
}
