package interact

import (
	"bytes"
	"strings"
	"testing"
)

// setTestIO replaces In/Out with buffers and returns a cleanup function.
func setTestIO(input string) (*bytes.Buffer, *bytes.Buffer, func()) {
	var inBuf bytes.Buffer
	var outBuf bytes.Buffer
	inBuf.WriteString(input)

	origIn, origOut := In, Out
	In = &inBuf
	Out = &outBuf

	cleanup := func() {
		In = origIn
		Out = origOut
	}
	return &inBuf, &outBuf, cleanup
}

// --- Confirm tests ---

func TestConfirm_YesVariants(t *testing.T) {
	for _, input := range []string{"y\n", "Y\n", "yes\n", "Yes\n", "YES\n"} {
		t.Run(input, func(t *testing.T) {
			_, _, cleanup := setTestIO(input)
			defer cleanup()

			got, err := Confirm("Continue?")
			if err != nil {
				t.Fatalf("unexpected error: %v", err)
			}
			if !got {
				t.Errorf("Confirm with input %q should return true", strings.TrimSpace(input))
			}
		})
	}
}

func TestConfirm_NoVariants(t *testing.T) {
	for _, input := range []string{"n\n", "N\n", "no\n", "No\n", "\n", "maybe\n", "yep\n"} {
		t.Run(strings.TrimSpace(input)+"_input", func(t *testing.T) {
			_, _, cleanup := setTestIO(input)
			defer cleanup()

			got, err := Confirm("Continue?")
			if err != nil {
				t.Fatalf("unexpected error: %v", err)
			}
			if got {
				t.Errorf("Confirm with input %q should return false", strings.TrimSpace(input))
			}
		})
	}
}

func TestConfirm_PromptWritten(t *testing.T) {
	_, outBuf, cleanup := setTestIO("y\n")
	defer cleanup()

	Confirm("Proceed?")
	if !strings.Contains(outBuf.String(), "Proceed? [y/N] ") {
		t.Errorf("expected prompt in output, got: %q", outBuf.String())
	}
}

// --- Input tests ---

func TestInput_ReturnsTrimmedString(t *testing.T) {
	_, _, cleanup := setTestIO("  hello world  \n")
	defer cleanup()

	got, err := Input("Name:")
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if got != "hello world" {
		t.Errorf("Input() = %q, want %q", got, "hello world")
	}
}

func TestInput_EmptyString(t *testing.T) {
	_, _, cleanup := setTestIO("\n")
	defer cleanup()

	got, err := Input("Name:")
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if got != "" {
		t.Errorf("Input() = %q, want empty string", got)
	}
}

func TestInput_PromptWritten(t *testing.T) {
	_, outBuf, cleanup := setTestIO("x\n")
	defer cleanup()

	Input("Enter value:")
	if !strings.Contains(outBuf.String(), "Enter value: ") {
		t.Errorf("expected prompt in output, got: %q", outBuf.String())
	}
}

// --- Password tests ---

func TestPassword_FallbackReadsLine(t *testing.T) {
	_, _, cleanup := setTestIO("secret123\n")
	defer cleanup()

	got, err := Password("Enter password:")
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if got != "secret123" {
		t.Errorf("Password() = %q, want %q", got, "secret123")
	}
}

func TestPassword_PromptWritten(t *testing.T) {
	_, outBuf, cleanup := setTestIO("pw\n")
	defer cleanup()

	Password("Secret:")
	if !strings.Contains(outBuf.String(), "Secret: ") {
		t.Errorf("expected prompt in output, got: %q", outBuf.String())
	}
}

func TestPassword_Trimmed(t *testing.T) {
	_, _, cleanup := setTestIO("  spaced  \n")
	defer cleanup()

	got, err := Password("Password:")
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if got != "spaced" {
		t.Errorf("Password() = %q, want %q", got, "spaced")
	}
}

// --- Select tests ---

func TestSelect_ValidChoice(t *testing.T) {
	_, _, cleanup := setTestIO("2\n")
	defer cleanup()

	idx, err := Select("Pick one:", []string{"alpha", "beta", "gamma"})
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if idx != 1 {
		t.Errorf("Select() = %d, want 1", idx)
	}
}

func TestSelect_FirstOption(t *testing.T) {
	_, _, cleanup := setTestIO("1\n")
	defer cleanup()

	idx, err := Select("Pick:", []string{"a", "b", "c"})
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if idx != 0 {
		t.Errorf("Select() = %d, want 0", idx)
	}
}

func TestSelect_LastOption(t *testing.T) {
	_, _, cleanup := setTestIO("3\n")
	defer cleanup()

	idx, err := Select("Pick:", []string{"a", "b", "c"})
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if idx != 2 {
		t.Errorf("Select() = %d, want 2", idx)
	}
}

func TestSelect_InvalidHigh(t *testing.T) {
	_, _, cleanup := setTestIO("99\n")
	defer cleanup()

	idx, err := Select("Pick:", []string{"a", "b"})
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if idx != -1 {
		t.Errorf("Select() = %d, want -1 for out-of-range", idx)
	}
}

func TestSelect_InvalidZero(t *testing.T) {
	_, _, cleanup := setTestIO("0\n")
	defer cleanup()

	idx, err := Select("Pick:", []string{"a", "b"})
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if idx != -1 {
		t.Errorf("Select() = %d, want -1 for zero", idx)
	}
}

func TestSelect_InvalidNonNumeric(t *testing.T) {
	_, _, cleanup := setTestIO("abc\n")
	defer cleanup()

	idx, err := Select("Pick:", []string{"a", "b"})
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if idx != -1 {
		t.Errorf("Select() = %d, want -1 for non-numeric", idx)
	}
}

func TestSelect_NegativeNumber(t *testing.T) {
	_, _, cleanup := setTestIO("-1\n")
	defer cleanup()

	idx, err := Select("Pick:", []string{"a", "b"})
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if idx != -1 {
		t.Errorf("Select() = %d, want -1 for negative", idx)
	}
}

func TestSelect_PromptWritten(t *testing.T) {
	_, outBuf, cleanup := setTestIO("1\n")
	defer cleanup()

	Select("Choose:", []string{"opt1", "opt2"})
	output := outBuf.String()
	if !strings.Contains(output, "Choose:") {
		t.Errorf("expected prompt in output, got: %q", output)
	}
	if !strings.Contains(output, "1) opt1") || !strings.Contains(output, "2) opt2") {
		t.Errorf("expected numbered options in output, got: %q", output)
	}
}
