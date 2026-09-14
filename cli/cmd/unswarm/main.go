package main

import (
	"errors"
	"os"

	"github.com/unswarm/cli/internal/commands"
	"github.com/unswarm/cli/internal/output"
)

func main() {
	if err := commands.Execute(); err != nil {
		var exitErr *output.ExitError
		if errors.As(err, &exitErr) {
			os.Exit(exitErr.Exit)
		}
		// Non-ExitError errors (e.g. from ConfirmOrSkip, PersistentPreRunE)
		// were already printed by Cobra. Exit with 1.
		os.Exit(1)
	}
}
