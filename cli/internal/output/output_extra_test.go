package output

import (
	"bytes"
	"os"
	"strings"
	"testing"
)

// newTestWriter builds a Writer whose output goes to the supplied buffers.
func newTestWriter(format Format, noColor bool, out, errOut *bytes.Buffer) *Writer {
	return &Writer{
		format:  format,
		noColor: noColor,
		quiet:   false,
		w:       out,
		ew:      errOut,
	}
}

func TestNewWriterWiresStdStreams(t *testing.T) {
	w := NewWriter(FormatJSON, true, false)
	if w.GetFormat() != FormatJSON {
		t.Errorf("GetFormat() = %q, want %q", w.GetFormat(), FormatJSON)
	}
	if w.w != os.Stdout {
		t.Error("expected writer to target os.Stdout")
	}
	if w.ew != os.Stderr {
		t.Error("expected error writer to target os.Stderr")
	}
	if !w.noColor {
		t.Error("expected noColor to be propagated")
	}
	if w.quiet {
		t.Error("expected quiet=false to be propagated")
	}
}

func TestGetFormatTableDriven(t *testing.T) {
	for _, format := range []Format{FormatTable, FormatJSON, FormatCSV, FormatRaw, Format("bogus")} {
		t.Run(string(format), func(t *testing.T) {
			w := newTestWriter(format, true, &bytes.Buffer{}, &bytes.Buffer{})
			if got := w.GetFormat(); got != format {
				t.Errorf("GetFormat() = %q, want %q", got, format)
			}
		})
	}
}

func TestPrintUnknownFormatFallsBackToJSON(t *testing.T) {
	var buf bytes.Buffer
	w := newTestWriter(Format("bogus"), true, &buf, &buf)

	if err := w.Print(map[string]any{"name": "x"}); err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if !strings.Contains(buf.String(), `"name": "x"`) {
		t.Errorf("expected JSON fallback output, got: %q", buf.String())
	}
}

func TestPrintRawVariants(t *testing.T) {
	cases := []struct {
		name string
		data any
		want string
	}{
		{"string", "hello world", "hello world"},
		{"bytes", []byte("raw-bytes"), "raw-bytes"},
		{"default int", 42, "42"},
		{"default struct", struct{ A int }{A: 7}, "{7}"},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			var buf bytes.Buffer
			w := newTestWriter(FormatRaw, true, &buf, &buf)
			if err := w.Print(tc.data); err != nil {
				t.Fatalf("unexpected error: %v", err)
			}
			if got := buf.String(); got != tc.want {
				t.Errorf("Print(%v) = %q, want %q", tc.data, got, tc.want)
			}
		})
	}
}

func TestPrintCSVFromStructRoundTrip(t *testing.T) {
	type csvTable struct {
		Headers []string   `json:"headers"`
		Rows    [][]string `json:"rows"`
	}
	var buf bytes.Buffer
	w := newTestWriter(FormatCSV, true, &buf, &buf)
	data := csvTable{
		Headers: []string{"A", "B"},
		Rows:    [][]string{{"1", "2"}, {"3", "4"}},
	}
	if err := w.Print(data); err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	lines := strings.Split(strings.TrimSpace(buf.String()), "\n")
	want := []string{"A,B", "1,2", "3,4"}
	if len(lines) != len(want) {
		t.Fatalf("expected %d lines, got %d: %q", len(want), len(lines), buf.String())
	}
	for i := range want {
		if lines[i] != want[i] {
			t.Errorf("line %d = %q, want %q", i, lines[i], want[i])
		}
	}
}

func TestPrintCSVRowsWithoutHeaders(t *testing.T) {
	var buf bytes.Buffer
	w := newTestWriter(FormatCSV, true, &buf, &buf)
	if err := w.Print(map[string]any{
		"rows": [][]string{{"only", "row"}},
	}); err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	got := strings.TrimSpace(buf.String())
	if got != "only,row" {
		t.Errorf("expected row without header, got %q", got)
	}
}

func TestPrintCSVMarshalError(t *testing.T) {
	var buf bytes.Buffer
	w := newTestWriter(FormatCSV, true, &buf, &buf)
	err := w.Print(make(chan int))
	if err == nil {
		t.Fatal("expected error marshaling unsupported type for CSV")
	}
	if !strings.Contains(err.Error(), "CSV") {
		t.Errorf("expected CSV error, got: %v", err)
	}
}

func TestPrintTableKeyValueMap(t *testing.T) {
	var buf bytes.Buffer
	w := newTestWriter(FormatTable, true, &buf, &buf)
	if err := w.Print(map[string]any{"status": "ready", "name": "alpha"}); err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	out := buf.String()
	if !strings.Contains(out, "KEY") || !strings.Contains(out, "VALUE") {
		t.Errorf("expected KEY/VALUE headers, got: %q", out)
	}
	// keys must be sorted: name before status
	nameIdx := strings.Index(out, "name")
	statusIdx := strings.Index(out, "status")
	if nameIdx == -1 || statusIdx == -1 || nameIdx > statusIdx {
		t.Errorf("expected sorted key rows, got: %q", out)
	}
}

func TestPrintTableStructRoundTrip(t *testing.T) {
	var buf bytes.Buffer
	w := newTestWriter(FormatTable, true, &buf, &buf)
	if err := w.Print(struct {
		Name string `json:"name"`
	}{Name: "beta"}); err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	out := buf.String()
	if !strings.Contains(out, "KEY") || !strings.Contains(out, "name") || !strings.Contains(out, "beta") {
		t.Errorf("expected struct key-value table, got: %q", out)
	}
}

func TestPrintTableMarshalError(t *testing.T) {
	var buf bytes.Buffer
	w := newTestWriter(FormatTable, true, &buf, &buf)
	err := w.Print(make(chan int))
	if err == nil {
		t.Fatal("expected error marshaling unsupported type for table")
	}
	if !strings.Contains(err.Error(), "table") {
		t.Errorf("expected table error, got: %v", err)
	}
}

func TestStatusColorMatrix(t *testing.T) {
	cases := []struct {
		name    string
		format  Format
		noColor bool
		status  string
		want    string
	}{
		{"green ready", FormatTable, false, "ready", "\033[32mready\033[0m"},
		{"green running", FormatTable, false, "running", "\033[32mrunning\033[0m"},
		{"green completed", FormatTable, false, "completed", "\033[32mcompleted\033[0m"},
		{"green healthy", FormatTable, false, "healthy", "\033[32mhealthy\033[0m"},
		{"green active", FormatTable, false, "active", "\033[32mactive\033[0m"},
		{"red error", FormatTable, false, "error", "\033[31merror\033[0m"},
		{"red failed", FormatTable, false, "failed", "\033[31mfailed\033[0m"},
		{"red unhealthy", FormatTable, false, "unhealthy", "\033[31munhealthy\033[0m"},
		{"yellow starting", FormatTable, false, "starting", "\033[33mstarting\033[0m"},
		{"yellow pending", FormatTable, false, "pending", "\033[33mpending\033[0m"},
		{"yellow initializing", FormatTable, false, "initializing", "\033[33minitializing\033[0m"},
		{"yellow loading", FormatTable, false, "loading", "\033[33mloading\033[0m"},
		{"gray stopped", FormatTable, false, "stopped", "\033[90mstopped\033[0m"},
		{"gray disabled", FormatTable, false, "disabled", "\033[90mdisabled\033[0m"},
		{"unknown unchanged", FormatTable, false, "weird", "weird"},
		{"case-insensitive", FormatTable, false, "READY", "\033[32mREADY\033[0m"},
		{"noColor wins", FormatTable, true, "ready", "ready"},
		{"non-table format", FormatJSON, false, "ready", "ready"},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			w := newTestWriter(tc.format, tc.noColor, &bytes.Buffer{}, &bytes.Buffer{})
			if got := w.statusColor(tc.status); got != tc.want {
				t.Errorf("statusColor(%q) = %q, want %q", tc.status, got, tc.want)
			}
		})
	}
}

func TestStatusRow(t *testing.T) {
	t.Run("colorizes in place safely", func(t *testing.T) {
		w := newTestWriter(FormatTable, false, &bytes.Buffer{}, &bytes.Buffer{})
		row := []string{"svc", "ready", "1"}
		got := w.StatusRow(row, 1)
		if got[1] != "\033[32mready\033[0m" {
			t.Errorf("expected colorized status, got %q", got[1])
		}
		// original row must not be mutated
		if row[1] != "ready" {
			t.Errorf("original row mutated: %q", row[1])
		}
	})

	t.Run("out of range index returns row unchanged", func(t *testing.T) {
		w := newTestWriter(FormatTable, false, &bytes.Buffer{}, &bytes.Buffer{})
		row := []string{"svc", "ready"}
		got := w.StatusRow(row, 5)
		if len(got) != 2 || got[1] != "ready" {
			t.Errorf("expected unchanged row, got %v", got)
		}
	})
}

func TestExitErrorMessage(t *testing.T) {
	cases := []struct {
		name string
		e    *ExitError
		want string
	}{
		{"with hint", &ExitError{Code: "unauthorized", Message: "bad key", Hint: "set a key"}, "unauthorized: bad key (hint: set a key)"},
		{"without hint", &ExitError{Code: "not_found", Message: "missing"}, "not_found: missing"},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			if got := tc.e.Error(); got != tc.want {
				t.Errorf("Error() = %q, want %q", got, tc.want)
			}
		})
	}
}

func TestDryRun(t *testing.T) {
	t.Run("active dry-run prints and returns true", func(t *testing.T) {
		var errBuf bytes.Buffer
		w := newTestWriter(FormatTable, true, &bytes.Buffer{}, &errBuf)
		if !w.DryRun("delete model") {
			t.Error("expected DryRun to return true")
		}
		if got := errBuf.String(); got != "[dry-run] delete model\n" {
			t.Errorf("unexpected dry-run output: %q", got)
		}
	})

	t.Run("quiet suppresses dry-run", func(t *testing.T) {
		var errBuf bytes.Buffer
		w := &Writer{format: FormatTable, quiet: true, w: &bytes.Buffer{}, ew: &errBuf}
		if w.DryRun("delete model") {
			t.Error("expected DryRun to return false in quiet mode")
		}
		if errBuf.Len() != 0 {
			t.Errorf("expected no dry-run output, got: %q", errBuf.String())
		}
	})
}

func TestDetectFormatStatErrorFallsBackToJSON(t *testing.T) {
	f, err := os.CreateTemp(t.TempDir(), "closed-stdout")
	if err != nil {
		t.Fatalf("failed to create temp file: %v", err)
	}
	if err := f.Close(); err != nil {
		t.Fatalf("failed to close temp file: %v", err)
	}

	old := os.Stdout
	os.Stdout = f
	defer func() { os.Stdout = old }()

	if got := DetectFormat(); got != FormatJSON {
		t.Errorf("DetectFormat() on stat error = %q, want %q", got, FormatJSON)
	}
}
