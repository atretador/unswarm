package interact

import (
	"bufio"
	"fmt"
	"io"
	"os"
	"strconv"
	"strings"

	"golang.org/x/term"
)

// Reader/Writer can be injected for testing. Default to os.Stdin/os.Stdout.
var (
	In  io.Reader = os.Stdin
	Out io.Writer = os.Stdout
)

// readLine reads a full line from In, trimming the trailing newline.
func readLine() (string, error) {
	r := bufio.NewReader(In)
	line, err := r.ReadString('\n')
	if err != nil && err != io.EOF {
		return "", err
	}
	return strings.TrimRight(line, "\r\n"), nil
}

// Confirm prompts the user with a yes/no question. Returns true for yes.
// Accepts "y", "yes", "Y", "Yes" as true; everything else (including empty/Enter) returns false.
func Confirm(prompt string) (bool, error) {
	fmt.Fprintf(Out, "%s [y/N] ", prompt)
	line, err := readLine()
	if err != nil {
		return false, err
	}
	trimmed := strings.TrimSpace(line)
	switch strings.ToLower(trimmed) {
	case "y", "yes":
		return true, nil
	default:
		return false, nil
	}
}

// Input prompts the user for text input. Returns the trimmed string.
func Input(prompt string) (string, error) {
	fmt.Fprintf(Out, "%s ", prompt)
	line, err := readLine()
	if err != nil {
		return "", err
	}
	return strings.TrimSpace(line), nil
}

// Password prompts for hidden input using golang.org/x/term.
// If In is an *os.File (real terminal), uses raw-mode password reading.
// Otherwise (e.g. test buffers) falls back to normal line reading.
func Password(prompt string) (string, error) {
	fmt.Fprintf(Out, "%s ", prompt)

	if f, ok := In.(*os.File); ok {
		pw, err := term.ReadPassword(int(f.Fd()))
		fmt.Fprintln(Out) // newline after hidden input
		if err != nil {
			return "", err
		}
		return string(pw), nil
	}

	// Fallback for tests / non-terminal readers.
	line, err := readLine()
	if err != nil {
		return "", err
	}
	return strings.TrimSpace(line), nil
}

// Select shows a numbered list and returns the 0-based index.
// Returns -1 on invalid input (out of range, non-numeric, etc.).
func Select(prompt string, options []string) (int, error) {
	fmt.Fprintf(Out, "%s:\n", prompt)
	for i, opt := range options {
		fmt.Fprintf(Out, "  %d) %s\n", i+1, opt)
	}
	fmt.Fprintf(Out, "Enter number [1-%d]: ", len(options))

	line, err := readLine()
	if err != nil {
		return -1, err
	}
	line = strings.TrimSpace(line)
	n, err := strconv.Atoi(line)
	if err != nil || n < 1 || n > len(options) {
		return -1, nil
	}
	return n - 1, nil
}
