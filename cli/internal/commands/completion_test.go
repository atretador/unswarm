package commands

import (
	"bytes"
	"io"
	"os"
	"strings"
	"testing"
)

func TestCompletionCommandRegistered(t *testing.T) {
	cmds := rootCmd.Commands()
	names := make(map[string]bool)
	for _, c := range cmds {
		names[c.Name()] = true
	}
	if !names["completion"] {
		t.Error("expected 'completion' subcommand to be registered on root command")
	}
}

func TestCompletionBash(t *testing.T) {
	r, w, _ := os.Pipe()
	old := os.Stdout
	os.Stdout = w

	err := completionCmd.RunE(completionCmd, []string{"bash"})
	w.Close()
	os.Stdout = old

	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	var buf bytes.Buffer
	io.Copy(&buf, r)
	out := buf.String()
	if len(out) == 0 {
		t.Error("expected non-empty bash completion output")
	}
	if !strings.Contains(out, "bash") && !strings.Contains(out, "complete") {
		t.Errorf("expected bash completion content, got prefix: %s", out[:min(len(out), 200)])
	}
}

func TestCompletionZsh(t *testing.T) {
	r, w, _ := os.Pipe()
	old := os.Stdout
	os.Stdout = w

	err := completionCmd.RunE(completionCmd, []string{"zsh"})
	w.Close()
	os.Stdout = old

	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	var buf bytes.Buffer
	io.Copy(&buf, r)
	out := buf.String()
	if len(out) == 0 {
		t.Error("expected non-empty zsh completion output")
	}
}

func TestCompletionFish(t *testing.T) {
	r, w, _ := os.Pipe()
	old := os.Stdout
	os.Stdout = w

	err := completionCmd.RunE(completionCmd, []string{"fish"})
	w.Close()
	os.Stdout = old

	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	var buf bytes.Buffer
	io.Copy(&buf, r)
	out := buf.String()
	if len(out) == 0 {
		t.Error("expected non-empty fish completion output")
	}
}

func TestCompletionPowershell(t *testing.T) {
	r, w, _ := os.Pipe()
	old := os.Stdout
	os.Stdout = w

	err := completionCmd.RunE(completionCmd, []string{"powershell"})
	w.Close()
	os.Stdout = old

	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	var buf bytes.Buffer
	io.Copy(&buf, r)
	out := buf.String()
	if len(out) == 0 {
		t.Error("expected non-empty powershell completion output")
	}
}

func TestCompletionInvalidArg(t *testing.T) {
	err := completionCmd.Args(completionCmd, []string{"invalid"})
	if err == nil {
		t.Error("expected error for invalid shell argument")
	}
}

func TestCompletionMissingArg(t *testing.T) {
	err := completionCmd.Args(completionCmd, []string{})
	if err == nil {
		t.Error("expected error for missing shell argument")
	}
}

func min(a, b int) int {
	if a < b {
		return a
	}
	return b
}
