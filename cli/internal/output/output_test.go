package output

import (
	"bytes"
	"encoding/json"
	"errors"
	"strings"
	"testing"
)

func TestTableFormatting(t *testing.T) {
	var buf bytes.Buffer
	w := &Writer{
		format:  FormatTable,
		noColor: true,
		quiet:   false,
		w:       &buf,
		ew:      &buf,
	}

	headers := []string{"Name", "Status", "Age"}
	rows := [][]string{
		{"Alice", "Active", "30"},
		{"Bob", "Inactive", "25"},
	}

	err := w.PrintTable(headers, rows)
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}

	output := buf.String()
	if !strings.Contains(output, "Name") {
		t.Error("expected header 'Name' in output")
	}
	if !strings.Contains(output, "Alice") {
		t.Error("expected 'Alice' in output")
	}
	if !strings.Contains(output, "Bob") {
		t.Error("expected 'Bob' in output")
	}
}

func TestJSONOutput(t *testing.T) {
	var buf bytes.Buffer
	w := &Writer{
		format:  FormatJSON,
		noColor: true,
		quiet:   false,
		w:       &buf,
		ew:      &buf,
	}

	data := map[string]any{
		"name":   "test",
		"status": "ok",
	}

	err := w.Print(data)
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}

	output := buf.String()
	var parsed map[string]any
	if err := json.Unmarshal([]byte(output), &parsed); err != nil {
		t.Fatalf("failed to parse JSON output: %v", err)
	}

	if parsed["name"] != "test" {
		t.Errorf("expected name 'test', got '%v'", parsed["name"])
	}
	if parsed["status"] != "ok" {
		t.Errorf("expected status 'ok', got '%v'", parsed["status"])
	}
}

func TestCSVOutput(t *testing.T) {
	var buf bytes.Buffer
	w := &Writer{
		format:  FormatCSV,
		noColor: true,
		quiet:   false,
		w:       &buf,
		ew:      &buf,
	}

	data := map[string]any{
		"headers": []string{"Name", "Status"},
		"rows":    [][]string{{"Alice", "Active"}, {"Bob", "Inactive"}},
	}

	err := w.Print(data)
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}

	output := buf.String()
	lines := strings.Split(strings.TrimSpace(output), "\n")
	if len(lines) != 3 {
		t.Fatalf("expected 3 lines (header + 2 rows), got %d", len(lines))
	}

	if lines[0] != "Name,Status" {
		t.Errorf("expected header 'Name,Status', got '%s'", lines[0])
	}
	if lines[1] != "Alice,Active" {
		t.Errorf("expected row 'Alice,Active', got '%s'", lines[1])
	}
	if lines[2] != "Bob,Inactive" {
		t.Errorf("expected row 'Bob,Inactive', got '%s'", lines[2])
	}
}

func TestRawOutput(t *testing.T) {
	var buf bytes.Buffer
	w := &Writer{
		format:  FormatRaw,
		noColor: true,
		quiet:   false,
		w:       &buf,
		ew:      &buf,
	}

	err := w.Print("hello world")
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}

	output := buf.String()
	if output != "hello world" {
		t.Errorf("expected 'hello world', got '%s'", output)
	}
}

func TestErrorFormattingTable(t *testing.T) {
	var buf bytes.Buffer
	w := &Writer{
		format:  FormatTable,
		noColor: true,
		quiet:   false,
		w:       &buf,
		ew:      &buf,
	}

	err := w.Error("unauthorized", "invalid API key", intPtr(401), "check your API key", 1)
	// w.Error() now returns *ExitError (non-nil) — this is expected for error cases
	var exitErr *ExitError
	if !errors.As(err, &exitErr) {
		t.Fatalf("expected *ExitError, got %T: %v", err, err)
	}

	output := buf.String()
	if !strings.Contains(output, "Error: invalid API key") {
		t.Errorf("expected error message in output, got: %s", output)
	}
	if !strings.Contains(output, "Hint: check your API key") {
		t.Errorf("expected hint in output, got: %s", output)
	}
}

func TestErrorFormattingJSON(t *testing.T) {
	var buf bytes.Buffer
	w := &Writer{
		format:  FormatJSON,
		noColor: true,
		quiet:   false,
		w:       &buf,
		ew:      &buf,
	}

	err := w.Error("unauthorized", "invalid API key", intPtr(401), "check your API key", 1)
	// w.Error() now returns *ExitError (non-nil) — this is expected for error cases
	var exitErr *ExitError
	if !errors.As(err, &exitErr) {
		t.Fatalf("expected *ExitError, got %T: %v", err, err)
	}

	output := buf.String()
	var parsed map[string]any
	if err := json.Unmarshal([]byte(output), &parsed); err != nil {
		t.Fatalf("failed to parse JSON error: %v", err)
	}

	if parsed["error"] != "unauthorized" {
		t.Errorf("expected error 'unauthorized', got '%v'", parsed["error"])
	}
	if parsed["message"] != "invalid API key" {
		t.Errorf("expected message 'invalid API key', got '%v'", parsed["message"])
	}
	if parsed["hint"] != "check your API key" {
		t.Errorf("expected hint 'check your API key', got '%v'", parsed["hint"])
	}
	if parsed["exitCode"] != float64(1) {
		t.Errorf("expected exitCode 1, got '%v'", parsed["exitCode"])
	}
}

func TestQuietMode(t *testing.T) {
	var buf bytes.Buffer
	w := &Writer{
		format:  FormatTable,
		noColor: true,
		quiet:   true,
		w:       &buf,
		ew:      &buf,
	}

	// Quiet mode should still print data
	data := map[string]any{
		"headers": []string{"Name"},
		"rows":    [][]string{{"test"}},
	}
	err := w.Print(data)
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}

	output := buf.String()
	if !strings.Contains(output, "Name") || !strings.Contains(output, "test") {
		t.Errorf("expected table output with headers and rows, got '%s'", output)
	}
}

func TestDetectFormat(t *testing.T) {
	// Note: DetectFormat depends on os.Stdout.Stat() which may not
	// work as expected in test environments. This is a basic test.
	format := DetectFormat()
	if format != FormatTable && format != FormatJSON {
		t.Errorf("unexpected format: %s", format)
	}
}

func intPtr(i int) *int {
	return &i
}
