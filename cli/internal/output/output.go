package output

import (
	"encoding/csv"
	"encoding/json"
	"fmt"
	"io"
	"os"
	"strings"
	"text/tabwriter"
)

// Format represents output format
type Format string

const (
	FormatTable Format = "table"
	FormatJSON  Format = "json"
	FormatCSV   Format = "csv"
	FormatRaw   Format = "raw"
)

// Writer handles output formatting
type Writer struct {
	format  Format
	noColor bool
	quiet   bool
	w       io.Writer // stdout
	ew      io.Writer // stderr
}

// NewWriter creates output writer
func NewWriter(format Format, noColor, quiet bool) *Writer {
	return &Writer{
		format:  format,
		noColor: noColor,
		quiet:   quiet,
		w:       os.Stdout,
		ew:      os.Stderr,
	}
}

// Print renders data in the configured format
func (w *Writer) Print(data any) error {
	switch w.format {
	case FormatJSON:
		return w.printJSON(data)
	case FormatCSV:
		return w.printCSV(data)
	case FormatRaw:
		return w.printRaw(data)
	case FormatTable:
		return w.printTable(data)
	default:
		return w.printJSON(data)
	}
}

func (w *Writer) printJSON(data any) error {
	enc := json.NewEncoder(w.w)
	enc.SetIndent("", "  ")
	return enc.Encode(data)
}

func (w *Writer) printCSV(data any) error {
	// Try to extract table data
	type tableData struct {
		Headers []string   `json:"headers"`
		Rows    [][]string `json:"rows"`
	}

	var td tableData
	switch v := data.(type) {
	case tableData:
		td = v
	case *tableData:
		if v != nil {
			td = *v
		}
	case map[string]any:
		if h, ok := v["headers"].([]string); ok {
			td.Headers = h
		}
		if r, ok := v["rows"].([][]string); ok {
			td.Rows = r
		}
	default:
		// Try JSON marshal/unmarshal
		jsonBytes, err := json.Marshal(data)
		if err != nil {
			return fmt.Errorf("failed to marshal data for CSV: %w", err)
		}
		if err := json.Unmarshal(jsonBytes, &td); err != nil {
			return fmt.Errorf("failed to unmarshal data for CSV: %w", err)
		}
	}

	wr := csv.NewWriter(w.w)
	if len(td.Headers) > 0 {
		if err := wr.Write(td.Headers); err != nil {
			return fmt.Errorf("failed to write CSV headers: %w", err)
		}
	}
	for _, row := range td.Rows {
		if err := wr.Write(row); err != nil {
			return fmt.Errorf("failed to write CSV row: %w", err)
		}
	}
	wr.Flush()
	return wr.Error()
}

func (w *Writer) printRaw(data any) error {
	switch v := data.(type) {
	case string:
		fmt.Fprint(w.w, v)
	case []byte:
		w.w.Write(v)
	default:
		fmt.Fprint(w.w, data)
	}
	return nil
}

func (w *Writer) printTable(data any) error {
	type tableData struct {
		Headers []string   `json:"headers"`
		Rows    [][]string `json:"rows"`
	}

	var td tableData
	switch v := data.(type) {
	case tableData:
		td = v
	case *tableData:
		if v != nil {
			td = *v
		}
	case map[string]any:
		if h, ok := v["headers"].([]string); ok {
			td.Headers = h
		}
		if r, ok := v["rows"].([][]string); ok {
			td.Rows = r
		}
	default:
		jsonBytes, err := json.Marshal(data)
		if err != nil {
			return fmt.Errorf("failed to marshal data for table: %w", err)
		}
		if err := json.Unmarshal(jsonBytes, &td); err != nil {
			return fmt.Errorf("failed to unmarshal data for table: %w", err)
		}
	}

	tw := tabwriter.NewWriter(w.w, 0, 0, 2, ' ', 0)

	// Print headers
	if len(td.Headers) > 0 {
		fmt.Fprintln(tw, strings.Join(td.Headers, "\t"))
	}

	// Print rows
	for _, row := range td.Rows {
		fmt.Fprintln(tw, strings.Join(row, "\t"))
	}

	return tw.Flush()
}

// PrintTable renders a table with headers and rows
func (w *Writer) PrintTable(headers []string, rows [][]string) error {
	return w.Print(map[string]any{
		"headers": headers,
		"rows":    rows,
	})
}

// ExitError is returned by Error() to carry the intended exit code
// through Cobra's RunE chain. Cobra prints the error, main.go reads ExitCode.
type ExitError struct {
	Code    string
	Message string
	Status  *int
	Hint    string
	Exit    int
}

func (e *ExitError) Error() string {
	if e.Hint != "" {
		return fmt.Sprintf("%s: %s (hint: %s)", e.Code, e.Message, e.Hint)
	}
	return fmt.Sprintf("%s: %s", e.Code, e.Message)
}

// Error prints error in the correct output format and returns an ExitError
// so that Cobra propagates a non-nil error and main.go can set the exit code.
func (w *Writer) Error(code, message string, status *int, hint string, exitCode int) error {
	if w.format == FormatJSON {
		w.errorJSON(code, message, status, hint, exitCode)
	} else {
		w.errorText(code, message, hint)
	}
	return &ExitError{Code: code, Message: message, Status: status, Hint: hint, Exit: exitCode}
}

func (w *Writer) errorJSON(code, message string, status *int, hint string, exitCode int) error {
	errObj := map[string]any{
		"error":    code,
		"message":  message,
		"status":   status,
		"hint":     hint,
		"exitCode": exitCode,
	}

	enc := json.NewEncoder(w.w)
	enc.SetIndent("", "  ")
	return enc.Encode(errObj)
}

func (w *Writer) errorText(code, message, hint string) error {
	var sb strings.Builder
	sb.WriteString(fmt.Sprintf("Error: %s\n", message))
	if hint != "" {
		sb.WriteString(fmt.Sprintf("Hint: %s\n", hint))
	}
	_, err := fmt.Fprint(w.ew, sb.String())
	return err
}

// DetectFormat returns json if stdout is not a TTY, table otherwise
func DetectFormat() Format {
	// Check if stdout is a terminal
	fi, err := os.Stdout.Stat()
	if err != nil {
		return FormatJSON
	}

	// If not a TTY, use JSON
	if fi.Mode()&os.ModeCharDevice == 0 {
		return FormatJSON
	}

	return FormatTable
}

// statusColor colorizes a status string for table output.
// Only applied in table mode when color is enabled.
func (w *Writer) statusColor(status string) string {
	if w.noColor || w.format != FormatTable {
		return status
	}
	switch strings.ToLower(status) {
	case "ready", "running", "completed", "healthy", "active":
		return "\033[32m" + status + "\033[0m" // green
	case "error", "failed", "unhealthy":
		return "\033[31m" + status + "\033[0m" // red
	case "starting", "pending", "initializing", "loading":
		return "\033[33m" + status + "\033[0m" // yellow
	case "stopped", "disabled":
		return "\033[90m" + status + "\033[0m" // gray
	default:
		return status
	}
}

// StatusRow returns a row with the status column colorized.
// Pass the 0-based index of the status column.
func (w *Writer) StatusRow(row []string, statusIdx int) []string {
	if statusIdx >= len(row) {
		return row
	}
	out := make([]string, len(row))
	copy(out, row)
	out[statusIdx] = w.statusColor(row[statusIdx])
	return out
}

// GetFormat returns the current output format.
func (w *Writer) GetFormat() Format {
	return w.format
}

// DryRun prints what would happen and returns true if in dry-run mode.
func (w *Writer) DryRun(action string) bool {
	if w.quiet {
		return false // quiet doesn't mean dry-run
	}
	fmt.Fprintf(w.ew, "[dry-run] %s\n", action)
	return true
}
