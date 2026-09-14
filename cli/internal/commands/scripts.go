package commands

import (
	"encoding/json"
	"fmt"
	"io"
	"os"
	"path/filepath"

	"github.com/spf13/cobra"
	"github.com/unswarm/cli/internal/client"
	"github.com/unswarm/cli/internal/helpers"
	"github.com/unswarm/cli/internal/output"
)

var scriptsCmd = &cobra.Command{
	Use:   "scripts",
	Short: "Manage scripts",
	Long:  `List, upload, view, and delete scripts.`,
}

// --- scripts list ---

var scriptsListCmd = &cobra.Command{
	Use:   "list",
	Short: "List all scripts",
	RunE: func(cmd *cobra.Command, args []string) error {
		c := GetClient(cmd)
		w := GetOutput(cmd)

		resp, err := c.Do(cmd.Context(), "GET", "/api/scripts", nil)
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

		headers := []string{"NAME", "SIZE", "MODIFIED"}
		rows := make([][]string, 0, len(scripts))
		for _, s := range scripts {
			sizeBytes := "-"
			if v, ok := s["sizeBytes"]; ok && v != nil {
				sizeBytes = helpers.IntStr(v)
			}
			rows = append(rows, []string{
				helpers.StrOrDash(s, "name"),
				sizeBytes,
				helpers.StrOrDash(s, "lastModified"),
			})
		}
		return w.PrintTable(headers, rows)
	},
}

// --- scripts upload ---

var scriptsUploadCmd = &cobra.Command{
	Use:   "upload [file]",
	Short: "Upload a script file (from path or stdin)",
	Long:  `Upload a script file. Use a file path argument or --stdin to read from stdin.`,
	Args:  cobra.MaximumNArgs(1),
	RunE: func(cmd *cobra.Command, args []string) error {
		c := GetClient(cmd)
		w := GetOutput(cmd)

		useStdin, _ := cmd.Flags().GetBool("stdin")
		filename, _ := cmd.Flags().GetString("filename")

		if useStdin {
			return doStdinUpload(cmd, c, w, "/api/scripts/upload", filename)
		}

		if len(args) == 0 {
			return w.Error("missing_argument", "file path required (or use --stdin)", nil, "provide a file path or use --stdin flag", 1)
		}

		filePath := args[0]
		if _, err := filepath.Abs(filePath); err != nil {
			return w.Error("invalid_path", "invalid file path", nil, "", 1)
		}

		resp, err := c.DoMultipart(cmd.Context(), "POST", "/api/scripts/upload", "file", filePath)
		if err != nil {
			return FormatErrorResponse(w, err)
		}
		if resp.StatusCode >= 400 {
			apiErr := client.ParseError(resp)
			return w.Error(apiErr.ErrCode, apiErr.Message, apiErr.Status, apiErr.Hint, apiErr.ExitCode)
		}

		var result any
		if err := json.Unmarshal(resp.Body, &result); err != nil {
			return w.Error("parse_error", "failed to parse response", nil, "", 1)
		}
		return w.Print(result)
	},
}

// stdinReader is the reader used for stdin. Overridden in tests.
var stdinReader io.Reader

// doStdinUpload reads from stdin, writes to a temp file, and uploads via multipart.
func doStdinUpload(cmd *cobra.Command, c *client.Client, w *output.Writer, uploadPath, filename string) error {
	reader := stdinReader
	if reader == nil {
		reader = os.Stdin
	}

	data, err := io.ReadAll(reader)
	if err != nil {
		return w.Error("stdin_read_error", "failed to read from stdin", nil, "", 1)
	}
	if len(data) == 0 {
		return w.Error("empty_input", "no data received from stdin", nil, "pipe data to stdin or use --file", 1)
	}

	if filename == "" {
		filename = "script.sh"
	}

	tmpFile, err := os.CreateTemp("", "upload-*.tmp")
	if err != nil {
		return w.Error("temp_file_error", "failed to create temporary file", nil, "", 1)
	}
	defer os.Remove(tmpFile.Name())

	if _, err := tmpFile.Write(data); err != nil {
		tmpFile.Close()
		return w.Error("temp_write_error", "failed to write temporary file", nil, "", 1)
	}
	tmpFile.Close()

	// Use DoMultipart with the temp file, passing the desired filename via the filePath
	// The multipart writer uses filepath.Base(filePath) as the filename, so we
	// need to rename the temp file to include the desired name.
	renamePath := filepath.Join(filepath.Dir(tmpFile.Name()), filename)
	if err := os.Rename(tmpFile.Name(), renamePath); err != nil {
		return w.Error("rename_error", "failed to rename temporary file", nil, "", 1)
	}
	defer os.Remove(renamePath)

	resp, err := c.DoMultipart(cmd.Context(), "POST", uploadPath, "file", renamePath)
	if err != nil {
		return FormatErrorResponse(w, err)
	}
	if resp.StatusCode >= 400 {
		apiErr := client.ParseError(resp)
		return w.Error(apiErr.ErrCode, apiErr.Message, apiErr.Status, apiErr.Hint, apiErr.ExitCode)
	}

	var result any
	if err := json.Unmarshal(resp.Body, &result); err != nil {
		return w.Error("parse_error", "failed to parse response", nil, "", 1)
	}
	return w.Print(result)
}

// --- scripts content ---

var scriptsContentCmd = &cobra.Command{
	Use:   "content <fileName>",
	Short: "Get script content",
	Args:  cobra.ExactArgs(1),
	RunE: func(cmd *cobra.Command, args []string) error {
		c := GetClient(cmd)
		w := GetOutput(cmd)

		resp, err := c.DoText(cmd.Context(), "GET", "/api/scripts/"+args[0]+"/content")
		if err != nil {
			return FormatErrorResponse(w, err)
		}
		if resp.StatusCode >= 400 {
			apiErr := client.ParseError(resp)
			return w.Error(apiErr.ErrCode, apiErr.Message, apiErr.Status, apiErr.Hint, apiErr.ExitCode)
		}

		return w.Print(string(resp.Body))
	},
}

// --- scripts delete ---

var scriptsDeleteCmd = &cobra.Command{
	Use:   "delete <fileName>",
	Short: "Delete a script",
	Args:  cobra.ExactArgs(1),
	RunE: func(cmd *cobra.Command, args []string) error {
		c := GetClient(cmd)
		w := GetOutput(cmd)

		if IsDryRun(cmd) {
			w.DryRun(fmt.Sprintf("Would delete script %s", args[0]))
			return nil
		}

		confirmed, err := ConfirmOrSkip(cmd, fmt.Sprintf("Delete script %s?", args[0]))
		if err != nil {
			return w.Error("confirmation_required", err.Error(), nil, "use --yes to skip confirmation", 1)
		}
		if !confirmed {
			return nil
		}

		resp, err := c.Do(cmd.Context(), "DELETE", "/api/scripts/"+args[0], nil)
		if err != nil {
			return FormatErrorResponse(w, err)
		}
		if resp.StatusCode >= 400 {
			apiErr := client.ParseError(resp)
			return w.Error(apiErr.ErrCode, apiErr.Message, apiErr.Status, apiErr.Hint, apiErr.ExitCode)
		}

		return w.Print(map[string]any{"message": fmt.Sprintf("Deleted script %s", args[0])})
	},
}

// --- scripts upload-agent ---

var scriptsUploadAgentCmd = &cobra.Command{
	Use:   "upload-agent <agentName> <file>",
	Short: "Upload a script to an agent",
	Args:  cobra.ExactArgs(2),
	RunE: func(cmd *cobra.Command, args []string) error {
		c := GetClient(cmd)
		w := GetOutput(cmd)

		agentName := args[0]
		filePath := args[1]
		if _, err := filepath.Abs(filePath); err != nil {
			return w.Error("invalid_path", "invalid file path", nil, "", 1)
		}

		path := "/api/scripts/agent/" + agentName + "/upload"
		resp, err := c.DoMultipart(cmd.Context(), "POST", path, "file", filePath)
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

// --- scripts content-agent ---

var scriptsContentAgentCmd = &cobra.Command{
	Use:   "content-agent <agentName> <fileName>",
	Short: "Get script content from an agent",
	Args:  cobra.ExactArgs(2),
	RunE: func(cmd *cobra.Command, args []string) error {
		c := GetClient(cmd)
		w := GetOutput(cmd)

		path := "/api/scripts/agent/" + args[0] + "/" + args[1] + "/content"
		resp, err := c.DoText(cmd.Context(), "GET", path)
		if err != nil {
			return FormatErrorResponse(w, err)
		}
		if resp.StatusCode >= 400 {
			apiErr := client.ParseError(resp)
			return w.Error(apiErr.ErrCode, apiErr.Message, apiErr.Status, apiErr.Hint, apiErr.ExitCode)
		}

		return w.Print(string(resp.Body))
	},
}

func init() {
	scriptsUploadCmd.Flags().Bool("stdin", false, "Read script content from stdin")
	scriptsUploadCmd.Flags().String("filename", "", "Filename for stdin upload (default: script.sh)")

	scriptsCmd.AddCommand(scriptsListCmd)
	scriptsCmd.AddCommand(scriptsUploadCmd)
	scriptsCmd.AddCommand(scriptsContentCmd)
	scriptsCmd.AddCommand(scriptsDeleteCmd)
	scriptsCmd.AddCommand(scriptsUploadAgentCmd)
	scriptsCmd.AddCommand(scriptsContentAgentCmd)

	rootCmd.AddCommand(scriptsCmd)
}
