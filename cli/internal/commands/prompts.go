package commands

import (
	"encoding/json"
	"fmt"
	"io"
	"os"
	"os/exec"
	"strconv"
	"strings"

	"github.com/spf13/cobra"
	"github.com/unswarm/cli/internal/client"
	"github.com/unswarm/cli/internal/helpers"
	"github.com/unswarm/cli/internal/resolve"
)

var promptsCmd = &cobra.Command{
	Use:   "prompts",
	Short: "Manage prompts",
	Long:  `List, create, update, delete, and manage prompts and their versions.`,
}

// --- prompts list ---

var promptsListCmd = &cobra.Command{
	Use:   "list",
	Short: "List all prompts",
	RunE: func(cmd *cobra.Command, args []string) error {
		c := GetClient(cmd)
		w := GetOutput(cmd)

		resp, err := c.Do(cmd.Context(), "GET", "/api/prompts", nil)
		if err != nil {
			return FormatErrorResponse(w, err)
		}
		if resp.StatusCode >= 400 {
			apiErr := client.ParseError(resp)
			return w.Error(apiErr.ErrCode, apiErr.Message, apiErr.Status, apiErr.Hint, apiErr.ExitCode)
		}

		var prompts []map[string]any
		if err := json.Unmarshal(resp.Body, &prompts); err != nil {
			return w.Error("parse_error", "failed to parse response", nil, "", 1)
		}

		headers := []string{"NAME", "DEFAULT", "VERSIONS", "UPDATED"}
		rows := make([][]string, 0, len(prompts))
		for _, p := range prompts {
			rows = append(rows, []string{
				helpers.StrOrDash(p, "name"),
				helpers.BoolStr(p["isDefault"]),
				helpers.IntStr(p["currentVersion"]),
				helpers.StrOrDash(p, "updatedAt"),
			})
		}
		return w.PrintTable(headers, rows)
	},
}

// --- prompts get ---

var promptsGetCmd = &cobra.Command{
	Use:   "get <name-or-id>",
	Short: "Get a prompt by name or ID",
	Args:  cobra.ExactArgs(1),
	RunE: func(cmd *cobra.Command, args []string) error {
		c := GetClient(cmd)
		w := GetOutput(cmd)

		resolver := resolve.New(newResolveAdapter(c), "/api/prompts", "name")
		resolvedID, err := resolver.Resolve(cmd.Context(), args[0])
		if err != nil {
			return w.Error("not_found", err.Error(), nil, "use 'prompts list' to see available prompts", 1)
		}

		resp, err := c.Do(cmd.Context(), "GET", "/api/prompts/"+resolvedID, nil)
		if err != nil {
			return FormatErrorResponse(w, err)
		}
		if resp.StatusCode >= 400 {
			apiErr := client.ParseError(resp)
			return w.Error(apiErr.ErrCode, apiErr.Message, apiErr.Status, apiErr.Hint, apiErr.ExitCode)
		}

		var prompt map[string]any
		if err := json.Unmarshal(resp.Body, &prompt); err != nil {
			return w.Error("parse_error", "failed to parse response", nil, "", 1)
		}
		return w.Print(prompt)
	},
}

// --- prompts create ---

// resolvePromptText determines the prompt text from flags (--text, --file, --stdin, --edit).
// Returns an error if more than one source is specified or no name is provided.
func resolvePromptText(cmd *cobra.Command, name string) (string, error) {
	text, _ := cmd.Flags().GetString("text")
	filePath, _ := cmd.Flags().GetString("file")
	useStdin, _ := cmd.Flags().GetBool("stdin")
	useEdit, _ := cmd.Flags().GetBool("edit")

	// Count how many sources are specified
	sources := 0
	if text != "" {
		sources++
	}
	if filePath != "" {
		sources++
	}
	if useStdin {
		sources++
	}
	if useEdit {
		sources++
	}

	if sources > 1 {
		return "", fmt.Errorf("--text, --file, --stdin, and --edit are mutually exclusive")
	}

	switch {
	case text != "":
		return text, nil
	case filePath != "":
		data, err := os.ReadFile(filePath)
		if err != nil {
			return "", fmt.Errorf("failed to read file %s: %w", filePath, err)
		}
		return string(data), nil
	case useStdin:
		data, err := io.ReadAll(os.Stdin)
		if err != nil {
			return "", fmt.Errorf("failed to read stdin: %w", err)
		}
		return string(data), nil
	case useEdit:
		return openEditor(name, "")
	default:
		return "", fmt.Errorf("no prompt text provided: use --text, --file, --stdin, or --edit")
	}
}

// openEditor opens the user's editor with the given content and returns the result.
func openEditor(name, existingText string) (string, error) {
	editor := os.Getenv("EDITOR")
	if editor == "" {
		editor = "vi"
	}

	tmpFile, err := os.CreateTemp("", "prompt-*.txt")
	if err != nil {
		return "", fmt.Errorf("failed to create temp file: %w", err)
	}
	defer os.Remove(tmpFile.Name())

	if existingText != "" {
		if _, err := tmpFile.WriteString(existingText); err != nil {
			tmpFile.Close()
			return "", fmt.Errorf("failed to write temp file: %w", err)
		}
	}
	tmpFile.Close()

	cmd := exec.Command(editor, tmpFile.Name())
	cmd.Stdin = os.Stdin
	cmd.Stdout = os.Stdout
	if err := cmd.Run(); err != nil {
		return "", fmt.Errorf("editor failed: %w", err)
	}

	data, err := os.ReadFile(tmpFile.Name())
	if err != nil {
		return "", fmt.Errorf("failed to read edited file: %w", err)
	}

	result := strings.TrimSpace(string(data))
	if result == "" {
		return "", fmt.Errorf("prompt text cannot be empty")
	}
	return result, nil
}

var promptsCreateCmd = &cobra.Command{
	Use:   "create",
	Short: "Create a new prompt",
	RunE: func(cmd *cobra.Command, args []string) error {
		c := GetClient(cmd)
		w := GetOutput(cmd)

		name, _ := cmd.Flags().GetString("name")
		text, err := resolvePromptText(cmd, name)
		if err != nil {
			return w.Error("invalid_input", err.Error(), nil, "use --text, --file, --stdin, or --edit to provide prompt content", 1)
		}

		body := map[string]any{
			"name": name,
			"text": text,
		}
		if cmd.Flags().Changed("max-tokens") {
			v, _ := cmd.Flags().GetInt("max-tokens")
			body["maxTokens"] = v
		}

		resp, err := c.Do(cmd.Context(), "POST", "/api/prompts", body)
		if err != nil {
			return FormatErrorResponse(w, err)
		}
		if resp.StatusCode >= 400 {
			apiErr := client.ParseError(resp)
			return w.Error(apiErr.ErrCode, apiErr.Message, apiErr.Status, apiErr.Hint, apiErr.ExitCode)
		}

		var prompt map[string]any
		if err := json.Unmarshal(resp.Body, &prompt); err != nil {
			return w.Error("parse_error", "failed to parse response", nil, "", 1)
		}
		return w.Print(prompt)
	},
}

// --- prompts update ---

// resolveUpdateText determines the text for update from flags (--text, --file, --stdin, --edit).
// For --edit, it fetches the current text first and opens it in the editor.
func resolveUpdateText(cmd *cobra.Command, c *client.Client, resolvedID string) (string, error) {
	text, _ := cmd.Flags().GetString("text")
	filePath, _ := cmd.Flags().GetString("file")
	useStdin, _ := cmd.Flags().GetBool("stdin")
	useEdit, _ := cmd.Flags().GetBool("edit")

	// Count how many sources are specified
	sources := 0
	if text != "" {
		sources++
	}
	if filePath != "" {
		sources++
	}
	if useStdin {
		sources++
	}
	if useEdit {
		sources++
	}

	if sources > 1 {
		return "", fmt.Errorf("--text, --file, --stdin, and --edit are mutually exclusive")
	}

	switch {
	case text != "":
		return text, nil
	case filePath != "":
		data, err := os.ReadFile(filePath)
		if err != nil {
			return "", fmt.Errorf("failed to read file %s: %w", filePath, err)
		}
		return string(data), nil
	case useStdin:
		data, err := io.ReadAll(os.Stdin)
		if err != nil {
			return "", fmt.Errorf("failed to read stdin: %w", err)
		}
		return string(data), nil
	case useEdit:
		// Fetch current text for the editor
		resp, err := c.Do(cmd.Context(), "GET", "/api/prompts/"+resolvedID, nil)
		if err != nil {
			return "", fmt.Errorf("failed to fetch current prompt: %w", err)
		}
		if resp.StatusCode >= 400 {
			return "", fmt.Errorf("failed to fetch current prompt (status %d)", resp.StatusCode)
		}
		var current map[string]any
		if err := json.Unmarshal(resp.Body, &current); err != nil {
			return "", fmt.Errorf("failed to parse current prompt: %w", err)
		}
		existingText, _ := current["text"].(string)
		return openEditor(resolvedID, existingText)
	default:
		return "", nil // No text change requested
	}
}

var promptsUpdateCmd = &cobra.Command{
	Use:   "update <name-or-id>",
	Short: "Update a prompt",
	Args:  cobra.ExactArgs(1),
	RunE: func(cmd *cobra.Command, args []string) error {
		c := GetClient(cmd)
		w := GetOutput(cmd)

		resolver := resolve.New(newResolveAdapter(c), "/api/prompts", "name")
		resolvedID, err := resolver.Resolve(cmd.Context(), args[0])
		if err != nil {
			return w.Error("not_found", err.Error(), nil, "use 'prompts list' to see available prompts", 1)
		}

		body := make(map[string]any)
		if cmd.Flags().Changed("name") {
			v, _ := cmd.Flags().GetString("name")
			body["name"] = v
		}

		// Check if any text source is specified
		if cmd.Flags().Changed("text") || cmd.Flags().Changed("file") || cmd.Flags().Changed("stdin") || cmd.Flags().Changed("edit") {
			text, err := resolveUpdateText(cmd, c, resolvedID)
			if err != nil {
				return w.Error("invalid_input", err.Error(), nil, "use --text, --file, --stdin, or --edit to provide prompt content", 1)
			}
			body["text"] = text
		}

		if cmd.Flags().Changed("max-tokens") {
			v, _ := cmd.Flags().GetInt("max-tokens")
			body["maxTokens"] = v
		}

		resp, err := c.Do(cmd.Context(), "PUT", "/api/prompts/"+resolvedID, body)
		if err != nil {
			return FormatErrorResponse(w, err)
		}
		if resp.StatusCode >= 400 {
			apiErr := client.ParseError(resp)
			return w.Error(apiErr.ErrCode, apiErr.Message, apiErr.Status, apiErr.Hint, apiErr.ExitCode)
		}

		var prompt map[string]any
		if err := json.Unmarshal(resp.Body, &prompt); err != nil {
			return w.Error("parse_error", "failed to parse response", nil, "", 1)
		}
		return w.Print(prompt)
	},
}

// --- prompts delete ---

var promptsDeleteCmd = &cobra.Command{
	Use:   "delete <name-or-id>",
	Short: "Delete a prompt",
	Args:  cobra.ExactArgs(1),
	RunE: func(cmd *cobra.Command, args []string) error {
		c := GetClient(cmd)
		w := GetOutput(cmd)

		resolver := resolve.New(newResolveAdapter(c), "/api/prompts", "name")
		resolvedID, err := resolver.Resolve(cmd.Context(), args[0])
		if err != nil {
			return w.Error("not_found", err.Error(), nil, "use 'prompts list' to see available prompts", 1)
		}

		if IsDryRun(cmd) {
			w.DryRun(fmt.Sprintf("Would delete prompt %s", resolvedID))
			return nil
		}

		confirmed, err := ConfirmOrSkip(cmd, fmt.Sprintf("Delete prompt %s?", resolvedID))
		if err != nil {
			return w.Error("confirmation_required", err.Error(), nil, "use --yes to skip confirmation", 1)
		}
		if !confirmed {
			return nil
		}

		resp, err := c.Do(cmd.Context(), "DELETE", "/api/prompts/"+resolvedID, nil)
		if err != nil {
			return FormatErrorResponse(w, err)
		}
		if resp.StatusCode >= 400 {
			apiErr := client.ParseError(resp)
			return w.Error(apiErr.ErrCode, apiErr.Message, apiErr.Status, apiErr.Hint, apiErr.ExitCode)
		}

		return w.Print(map[string]any{"message": fmt.Sprintf("Deleted prompt %s", resolvedID)})
	},
}

// --- prompts set-default ---

var promptsSetDefaultCmd = &cobra.Command{
	Use:   "set-default <name-or-id>",
	Short: "Set a prompt as the default",
	Args:  cobra.ExactArgs(1),
	RunE: func(cmd *cobra.Command, args []string) error {
		c := GetClient(cmd)
		w := GetOutput(cmd)

		resolver := resolve.New(newResolveAdapter(c), "/api/prompts", "name")
		resolvedID, err := resolver.Resolve(cmd.Context(), args[0])
		if err != nil {
			return w.Error("not_found", err.Error(), nil, "use 'prompts list' to see available prompts", 1)
		}

		resp, err := c.Do(cmd.Context(), "POST", "/api/prompts/"+resolvedID+"/default", nil)
		if err != nil {
			return FormatErrorResponse(w, err)
		}
		if resp.StatusCode >= 400 {
			apiErr := client.ParseError(resp)
			return w.Error(apiErr.ErrCode, apiErr.Message, apiErr.Status, apiErr.Hint, apiErr.ExitCode)
		}

		var prompt map[string]any
		if err := json.Unmarshal(resp.Body, &prompt); err != nil {
			return w.Error("parse_error", "failed to parse response", nil, "", 1)
		}
		return w.Print(prompt)
	},
}

// --- prompts versions ---

var promptsVersionsCmd = &cobra.Command{
	Use:   "versions <name-or-id>",
	Short: "List versions of a prompt",
	Args:  cobra.ExactArgs(1),
	RunE: func(cmd *cobra.Command, args []string) error {
		c := GetClient(cmd)
		w := GetOutput(cmd)

		resolver := resolve.New(newResolveAdapter(c), "/api/prompts", "name")
		resolvedID, err := resolver.Resolve(cmd.Context(), args[0])
		if err != nil {
			return w.Error("not_found", err.Error(), nil, "use 'prompts list' to see available prompts", 1)
		}

		resp, err := c.Do(cmd.Context(), "GET", "/api/prompts/"+resolvedID+"/versions", nil)
		if err != nil {
			return FormatErrorResponse(w, err)
		}
		if resp.StatusCode >= 400 {
			apiErr := client.ParseError(resp)
			return w.Error(apiErr.ErrCode, apiErr.Message, apiErr.Status, apiErr.Hint, apiErr.ExitCode)
		}

		var versions []map[string]any
		if err := json.Unmarshal(resp.Body, &versions); err != nil {
			return w.Error("parse_error", "failed to parse response", nil, "", 1)
		}

		headers := []string{"VERSION", "CREATED"}
		rows := make([][]string, 0, len(versions))
		for _, v := range versions {
			rows = append(rows, []string{
				helpers.IntStr(v["version"]),
				helpers.StrOrDash(v, "createdAt"),
			})
		}
		return w.PrintTable(headers, rows)
	},
}

// --- prompts version ---

var promptsVersionCmd = &cobra.Command{
	Use:   "version <name-or-id> <version>",
	Short: "Get a specific version of a prompt",
	Args:  cobra.ExactArgs(2),
	RunE: func(cmd *cobra.Command, args []string) error {
		c := GetClient(cmd)
		w := GetOutput(cmd)

		resolver := resolve.New(newResolveAdapter(c), "/api/prompts", "name")
		resolvedID, err := resolver.Resolve(cmd.Context(), args[0])
		if err != nil {
			return w.Error("not_found", err.Error(), nil, "use 'prompts list' to see available prompts", 1)
		}

		path := "/api/prompts/" + resolvedID + "/versions/" + args[1]
		resp, err := c.Do(cmd.Context(), "GET", path, nil)
		if err != nil {
			return FormatErrorResponse(w, err)
		}
		if resp.StatusCode >= 400 {
			apiErr := client.ParseError(resp)
			return w.Error(apiErr.ErrCode, apiErr.Message, apiErr.Status, apiErr.Hint, apiErr.ExitCode)
		}

		var version map[string]any
		if err := json.Unmarshal(resp.Body, &version); err != nil {
			return w.Error("parse_error", "failed to parse response", nil, "", 1)
		}
		return w.Print(version)
	},
}

// --- prompts diff ---

var promptsDiffCmd = &cobra.Command{
	Use:   "diff <name-or-id>",
	Short: "Diff two versions of a prompt",
	Long:  `Compare two versions of a prompt and show the differences in unified diff format.`,
	Args:  cobra.ExactArgs(1),
	RunE: func(cmd *cobra.Command, args []string) error {
		c := GetClient(cmd)
		w := GetOutput(cmd)

		resolver := resolve.New(newResolveAdapter(c), "/api/prompts", "name")
		resolvedID, err := resolver.Resolve(cmd.Context(), args[0])
		if err != nil {
			return w.Error("not_found", err.Error(), nil, "use 'prompts list' to see available prompts", 1)
		}

		fromVersion, _ := cmd.Flags().GetInt("from")
		toVersion, _ := cmd.Flags().GetInt("to")

		// Fetch both versions
		fromPath := "/api/prompts/" + resolvedID + "/versions/" + strconv.Itoa(fromVersion)
		toPath := "/api/prompts/" + resolvedID + "/versions/" + strconv.Itoa(toVersion)

		fromResp, err := c.Do(cmd.Context(), "GET", fromPath, nil)
		if err != nil {
			return FormatErrorResponse(w, err)
		}
		if fromResp.StatusCode >= 400 {
			apiErr := client.ParseError(fromResp)
			return w.Error(apiErr.ErrCode, apiErr.Message, apiErr.Status, apiErr.Hint, apiErr.ExitCode)
		}

		toResp, err := c.Do(cmd.Context(), "GET", toPath, nil)
		if err != nil {
			return FormatErrorResponse(w, err)
		}
		if toResp.StatusCode >= 400 {
			apiErr := client.ParseError(toResp)
			return w.Error(apiErr.ErrCode, apiErr.Message, apiErr.Status, apiErr.Hint, apiErr.ExitCode)
		}

		var fromVersionData, toVersionData map[string]any
		if err := json.Unmarshal(fromResp.Body, &fromVersionData); err != nil {
			return w.Error("parse_error", "failed to parse from version response", nil, "", 1)
		}
		if err := json.Unmarshal(toResp.Body, &toVersionData); err != nil {
			return w.Error("parse_error", "failed to parse to version response", nil, "", 1)
		}

		fromText, _ := fromVersionData["text"].(string)
		toText, _ := toVersionData["text"].(string)

		outputFmt, _ := cmd.Flags().GetString("output")
		if outputFmt == "json" {
			diffResult := DiffOutput{
				FromVersion: fromVersion,
				ToVersion:   toVersion,
				DiffText:    FormatDiff(fromVersion, toVersion, fromText, toText),
			}
			return w.Print(diffResult)
		}

		fmt.Println(FormatDiff(fromVersion, toVersion, fromText, toText))
		return nil
	},
}

// --- prompts rollback ---

var promptsRollbackCmd = &cobra.Command{
	Use:   "rollback <name-or-id>",
	Short: "Rollback a prompt to a specific version",
	Args:  cobra.ExactArgs(1),
	RunE: func(cmd *cobra.Command, args []string) error {
		c := GetClient(cmd)
		w := GetOutput(cmd)

		resolver := resolve.New(newResolveAdapter(c), "/api/prompts", "name")
		resolvedID, err := resolver.Resolve(cmd.Context(), args[0])
		if err != nil {
			return w.Error("not_found", err.Error(), nil, "use 'prompts list' to see available prompts", 1)
		}

		version, _ := cmd.Flags().GetInt("version")

		body := map[string]any{
			"version": version,
		}

		resp, err := c.Do(cmd.Context(), "POST", "/api/prompts/"+resolvedID+"/rollback", body)
		if err != nil {
			return FormatErrorResponse(w, err)
		}
		if resp.StatusCode >= 400 {
			apiErr := client.ParseError(resp)
			return w.Error(apiErr.ErrCode, apiErr.Message, apiErr.Status, apiErr.Hint, apiErr.ExitCode)
		}

		var prompt map[string]any
		if err := json.Unmarshal(resp.Body, &prompt); err != nil {
			return w.Error("parse_error", "failed to parse response", nil, "", 1)
		}
		return w.Print(prompt)
	},
}

func init() {
	// Create flags
	promptsCreateCmd.Flags().String("name", "", "Prompt name")
	promptsCreateCmd.Flags().String("text", "", "Prompt text")
	promptsCreateCmd.Flags().String("file", "", "Read prompt text from file")
	promptsCreateCmd.Flags().Bool("stdin", false, "Read prompt text from stdin")
	promptsCreateCmd.Flags().Bool("edit", false, "Open editor to create/edit prompt text")
	promptsCreateCmd.Flags().Int("max-tokens", 0, "Max tokens")
	_ = promptsCreateCmd.MarkFlagRequired("name")

	// Update flags (all optional)
	promptsUpdateCmd.Flags().String("name", "", "Prompt name")
	promptsUpdateCmd.Flags().String("text", "", "Prompt text")
	promptsUpdateCmd.Flags().String("file", "", "Read prompt text from file")
	promptsUpdateCmd.Flags().Bool("stdin", false, "Read prompt text from stdin")
	promptsUpdateCmd.Flags().Bool("edit", false, "Open editor to edit prompt text")
	promptsUpdateCmd.Flags().Int("max-tokens", 0, "Max tokens")

	// Diff flags
	promptsDiffCmd.Flags().Int("from", 0, "Source version number")
	promptsDiffCmd.Flags().Int("to", 0, "Target version number")
	promptsDiffCmd.Flags().String("output", "", "Output format: json (for machine-readable output)")
	_ = promptsDiffCmd.MarkFlagRequired("from")
	_ = promptsDiffCmd.MarkFlagRequired("to")

	// Rollback flags
	promptsRollbackCmd.Flags().Int("version", 0, "Version to rollback to")
	_ = promptsRollbackCmd.MarkFlagRequired("version")

	promptsCmd.AddCommand(promptsListCmd)
	promptsCmd.AddCommand(promptsGetCmd)
	promptsCmd.AddCommand(promptsCreateCmd)
	promptsCmd.AddCommand(promptsUpdateCmd)
	promptsCmd.AddCommand(promptsDeleteCmd)
	promptsCmd.AddCommand(promptsSetDefaultCmd)
	promptsCmd.AddCommand(promptsVersionsCmd)
	promptsCmd.AddCommand(promptsVersionCmd)
	promptsCmd.AddCommand(promptsDiffCmd)
	promptsCmd.AddCommand(promptsRollbackCmd)

	rootCmd.AddCommand(promptsCmd)
}

