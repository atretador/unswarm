package commands

import (
	"encoding/json"
	"errors"
	"os"
	"path/filepath"
	"testing"

	"github.com/spf13/cobra"
	"github.com/unswarm/cli/internal/client"
	"github.com/unswarm/cli/internal/output"
)

// ==================== metrics.go helpers ====================

func TestCostToInt(t *testing.T) {
	cases := []struct {
		name string
		in   any
		want int
	}{
		{"nil", nil, 0},
		{"float64 truncates", float64(3.9), 3},
		{"int", 7, 7},
		{"json.Number", json.Number("42"), 42},
		{"json.Number invalid", json.Number("nope"), 0},
		{"string unsupported", "12", 0},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			if got := costToInt(tc.in); got != tc.want {
				t.Errorf("costToInt(%v) = %d, want %d", tc.in, got, tc.want)
			}
		})
	}
}

func TestCostFormatTokenCount(t *testing.T) {
	cases := []struct {
		in   int
		want string
	}{
		{0, "0"},
		{123, "123"},
		{1234, "1,234"},
		{1234567, "1,234,567"},
	}
	for _, tc := range cases {
		if got := costFormatTokenCount(tc.in); got != tc.want {
			t.Errorf("costFormatTokenCount(%d) = %q, want %q", tc.in, got, tc.want)
		}
	}
}

func TestBuildQueryString(t *testing.T) {
	newCmd := func() *cobra.Command {
		cmd := &cobra.Command{}
		cmd.Flags().String("from", "", "")
		cmd.Flags().String("to", "", "")
		cmd.Flags().Int("page-size", 0, "")
		cmd.Flags().String("provider", "", "")
		cmd.Flags().String("group-by", "", "")
		return cmd
	}

	t.Run("no changed flags yields empty string", func(t *testing.T) {
		if got := buildQueryString(newCmd(), "from", "to", "provider"); got != "" {
			t.Errorf("expected empty query, got %q", got)
		}
	})

	t.Run("maps keys to camelCase and skips empties", func(t *testing.T) {
		cmd := newCmd()
		cmd.Flags().Set("from", "2024-01-01")
		cmd.Flags().Set("to", "")
		cmd.Flags().Set("page-size", "2")
		cmd.Flags().Set("provider", "openai")
		got := buildQueryString(cmd, "from", "to", "page-size", "provider", "group-by")
		want := "from=2024-01-01&pageSize=2&provider=openai"
		if got != want {
			t.Errorf("buildQueryString() = %q, want %q", got, want)
		}
	})

	t.Run("unknown flag name is ignored", func(t *testing.T) {
		cmd := newCmd()
		if got := buildQueryString(cmd, "does-not-exist"); got != "" {
			t.Errorf("expected empty query, got %q", got)
		}
	})
}

// ==================== stats.go helpers ====================

func TestFormatRPM(t *testing.T) {
	cases := []struct {
		name string
		in   any
		want string
	}{
		{"nil", nil, "0"},
		{"all zero", []int64{0, 0, 0}, "0"},
		{"empty", []int64{}, "0"},
		{"average", []int{1, 1, 1, 1}, "1.0 avg (4 total / 4 min)"},
		{"non numeric", "abc", "abc"},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			if got := formatRPM(tc.in); got != tc.want {
				t.Errorf("formatRPM(%v) = %q, want %q", tc.in, got, tc.want)
			}
		})
	}

	t.Run("marshal error falls back to fmt", func(t *testing.T) {
		got := formatRPM(make(chan int))
		if got == "" {
			t.Error("expected non-empty fallback for unmarshalable value")
		}
	})
}

// ==================== prompts.go helpers ====================

func TestResolveUpdateText(t *testing.T) {
	newCmd := func() *cobra.Command {
		cmd := &cobra.Command{}
		cmd.Flags().String("text", "", "")
		cmd.Flags().String("file", "", "")
		cmd.Flags().Bool("stdin", false, "")
		cmd.Flags().Bool("edit", false, "")
		return cmd
	}

	t.Run("text flag", func(t *testing.T) {
		cmd := newCmd()
		cmd.Flags().Set("text", "hello")
		got, err := resolveUpdateText(cmd, nil, "id")
		if err != nil || got != "hello" {
			t.Errorf("resolvedUpdateText = (%q, %v), want (hello, nil)", got, err)
		}
	})

	t.Run("file flag", func(t *testing.T) {
		dir := t.TempDir()
		path := filepath.Join(dir, "prompt.txt")
		if err := os.WriteFile(path, []byte("from-file"), 0o600); err != nil {
			t.Fatal(err)
		}
		cmd := newCmd()
		cmd.Flags().Set("file", path)
		got, err := resolveUpdateText(cmd, nil, "id")
		if err != nil || got != "from-file" {
			t.Errorf("resolvedUpdateText = (%q, %v), want (from-file, nil)", got, err)
		}
	})

	t.Run("missing file errors", func(t *testing.T) {
		cmd := newCmd()
		cmd.Flags().Set("file", filepath.Join(t.TempDir(), "missing.txt"))
		if _, err := resolveUpdateText(cmd, nil, "id"); err == nil {
			t.Error("expected error for missing file")
		}
	})

	t.Run("mutually exclusive sources", func(t *testing.T) {
		cmd := newCmd()
		cmd.Flags().Set("text", "a")
		cmd.Flags().Set("file", "b")
		if _, err := resolveUpdateText(cmd, nil, "id"); err == nil {
			t.Error("expected mutual-exclusion error")
		}
	})

	t.Run("no source returns empty", func(t *testing.T) {
		got, err := resolveUpdateText(newCmd(), nil, "id")
		if err != nil || got != "" {
			t.Errorf("expected empty result, got (%q, %v)", got, err)
		}
	})
}

func TestOpenEditor(t *testing.T) {
	writeScript := func(t *testing.T, body string) string {
		t.Helper()
		dir := t.TempDir()
		path := filepath.Join(dir, "editor.sh")
		script := "#!/bin/sh\n" + body + "\n"
		if err := os.WriteFile(path, []byte(script), 0o700); err != nil {
			t.Fatal(err)
		}
		return path
	}

	t.Run("captures editor output", func(t *testing.T) {
		t.Setenv("EDITOR", writeScript(t, `printf 'edited content' > "$1"`))
		got, err := openEditor("name", "")
		if err != nil || got != "edited content" {
			t.Errorf("openEditor = (%q, %v), want (edited content, nil)", got, err)
		}
	})

	t.Run("prefilled text survives no-op editor", func(t *testing.T) {
		t.Setenv("EDITOR", writeScript(t, "exit 0"))
		got, err := openEditor("name", "existing text")
		if err != nil || got != "existing text" {
			t.Errorf("openEditor = (%q, %v), want (existing text, nil)", got, err)
		}
	})

	t.Run("empty result errors", func(t *testing.T) {
		t.Setenv("EDITOR", writeScript(t, `: > "$1"`))
		if _, err := openEditor("name", "seed"); err == nil {
			t.Error("expected error for empty editor result")
		}
	})

	t.Run("editor failure errors", func(t *testing.T) {
		t.Setenv("EDITOR", filepath.Join(t.TempDir(), "no-such-editor"))
		if _, err := openEditor("name", "seed"); err == nil {
			t.Error("expected error when editor cannot run")
		}
	})
}

// ==================== root.go helpers ====================

func TestFormatErrorResponse(t *testing.T) {
	assertCode := func(t *testing.T, err error, want string) {
		t.Helper()
		var exitErr *output.ExitError
		if !errors.As(err, &exitErr) {
			t.Fatalf("expected *output.ExitError, got %T", err)
		}
		if exitErr.Code != want {
			t.Errorf("exit code = %q, want %q", exitErr.Code, want)
		}
	}

	t.Run("nil error returns nil", func(t *testing.T) {
		w := output.NewWriter(output.FormatJSON, true, false)
		if err := FormatErrorResponse(w, nil); err != nil {
			t.Errorf("expected nil, got %v", err)
		}
	})

	t.Run("unsafe http", func(t *testing.T) {
		w := output.NewWriter(output.FormatJSON, true, false)
		assertCode(t, FormatErrorResponse(w, client.ErrUnsafeHTTP), "unsafe_http")
	})

	t.Run("transport error", func(t *testing.T) {
		w := output.NewWriter(output.FormatJSON, true, false)
		assertCode(t, FormatErrorResponse(w, &client.TransportError{Err: errors.New("boom")}), "transport_error")
	})

	t.Run("missing api key", func(t *testing.T) {
		w := output.NewWriter(output.FormatJSON, true, false)
		assertCode(t, FormatErrorResponse(w, errors.New("no API key configured: nope")), "missing_api_key")
	})

	t.Run("generic error", func(t *testing.T) {
		w := output.NewWriter(output.FormatJSON, true, false)
		assertCode(t, FormatErrorResponse(w, errors.New("something else")), "internal_error")
	})
}
