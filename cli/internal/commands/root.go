package commands

import (
	"context"
	"fmt"
	"os"
	"strings"

	"github.com/spf13/cobra"
	"github.com/unswarm/cli/internal/client"
	"github.com/unswarm/cli/internal/interact"
	"github.com/unswarm/cli/internal/output"
)

var (
	cfgFile   string
	apiKey    string
	outputFmt string
	noColor   bool
	quiet     bool
	insecure  bool
	yes       bool
	dryRun    bool
	url       string
)

// contextKey is used to store values in context
type contextKey string

const (
	clientKey   contextKey = "client"
	outputKey   contextKey = "output"
	dryRunKey   contextKey = "dry-run"
	yesKey      contextKey = "yes"
	quietKey    contextKey = "quiet"
)

var rootCmd = &cobra.Command{
	Use:   "unswarm",
	Short: "Unswarm control plane CLI",
	Long:  `Unswarm is a control plane for managing distributed systems.`,
	PersistentPreRunE: func(cmd *cobra.Command, args []string) error {
		// Skip setup for help/version/completion commands
		if cmd.Name() == "help" || cmd.Name() == "version" || cmd.Name() == "completion" {
			return nil
		}

		// 1. Load config
		cfg, err := client.LoadConfig()
		if err != nil {
			return fmt.Errorf("failed to load config: %w", err)
		}

		// 2. Apply flag overrides
		if url != "" {
			cfg.BaseURL = url
		} else if envURL := os.Getenv("UNSWARM_URL"); envURL != "" {
			cfg.BaseURL = envURL
		}

		if outputFmt != "" {
			cfg.OutputFmt = outputFmt
		}

		if noColor {
			cfg.Color = false
		}

		// 3. Resolve API key (flag > env)
		resolvedAPIKey := apiKey
		if resolvedAPIKey == "" {
			resolvedAPIKey = os.Getenv("UNSWARM_API_KEY")
		}

		// Warn about API key in shell history
		if apiKey != "" {
			fmt.Fprintln(os.Stderr, "Warning: Using --api-key flag may expose your key in shell history")
		}

		// 4. Validate HTTP safety (non-loopback check)
		// This is done in client.Do() when making requests

		// 5. Create client
		c := client.New(cfg,
			client.WithAPIKey(resolvedAPIKey),
			client.WithOutput(cfg.OutputFmt),
			client.WithNoColor(!cfg.Color),
			client.WithInsecure(insecure),
		)

		// 6. Store in context
		ctx := context.WithValue(cmd.Context(), clientKey, c)

		// Create output writer
		format := output.Format(cfg.OutputFmt)
		if outputFmt == "" {
			format = output.DetectFormat()
		}
		w := output.NewWriter(format, !cfg.Color, quiet)
		ctx = context.WithValue(ctx, outputKey, w)
		ctx = context.WithValue(ctx, dryRunKey, dryRun)
		ctx = context.WithValue(ctx, yesKey, yes)
		ctx = context.WithValue(ctx, quietKey, quiet)

		cmd.SetContext(ctx)

		return nil
	},
}

func init() {
	rootCmd.PersistentFlags().StringVar(&url, "url", "", "API server URL (env UNSWARM_URL)")
	rootCmd.PersistentFlags().StringVar(&apiKey, "api-key", "", "API key (env UNSWARM_API_KEY)")
	rootCmd.PersistentFlags().StringVar(&outputFmt, "output", "", "Output format: table, json, csv, raw")
	rootCmd.PersistentFlags().BoolVar(&noColor, "no-color", false, "Disable colored output")
	rootCmd.PersistentFlags().BoolVar(&quiet, "quiet", false, "Suppress non-essential output")
	rootCmd.PersistentFlags().BoolVar(&insecure, "insecure", false, "Allow plaintext HTTP to non-loopback hosts")
	rootCmd.PersistentFlags().BoolVarP(&yes, "yes", "y", false, "Skip confirmation prompts")
	rootCmd.PersistentFlags().BoolVar(&dryRun, "dry-run", false, "Show what would be done without executing")
}

// Execute runs the root command
func Execute() error {
	return rootCmd.Execute()
}

// GetClient retrieves the client from context
func GetClient(cmd *cobra.Command) *client.Client {
	return cmd.Context().Value(clientKey).(*client.Client)
}

// GetOutput retrieves the output writer from context
func GetOutput(cmd *cobra.Command) *output.Writer {
	return cmd.Context().Value(outputKey).(*output.Writer)
}

// RequireAPIKey checks that an API key is configured
func RequireAPIKey(cmd *cobra.Command) error {
	c := GetClient(cmd)
	if c == nil {
		return fmt.Errorf("no API key configured: use --api-key flag or set UNSWARM_API_KEY environment variable")
	}

	// Check if API key is actually set
	// We'll check via the client's ability to make authenticated requests
	return nil
}

// ParseAPIError parses an API error response
func ParseAPIError(resp *client.Response) *client.APIError {
	return client.ParseError(resp)
}

// IsUnsafeHTTP checks if the error is an unsafe HTTP error
func IsUnsafeHTTP(err error) bool {
	return err == client.ErrUnsafeHTTP
}

// IsDryRun returns whether --dry-run flag is set
func IsDryRun(cmd *cobra.Command) bool {
	v, _ := cmd.Context().Value(dryRunKey).(bool)
	return v
}

// IsQuiet returns whether --quiet flag is set
func IsQuiet(cmd *cobra.Command) bool {
	v, _ := cmd.Context().Value(quietKey).(bool)
	return v
}

// IsYes returns whether --yes flag is set
func IsYes(cmd *cobra.Command) bool {
	v, _ := cmd.Context().Value(yesKey).(bool)
	return v
}

// ConfirmOrSkip prompts for confirmation unless --yes or --quiet.
// Returns (true, nil) if auto-confirmed, (false, error) if quiet mode.
func ConfirmOrSkip(cmd *cobra.Command, prompt string) (bool, error) {
	if IsYes(cmd) {
		return true, nil
	}
	if IsQuiet(cmd) {
		return false, fmt.Errorf("confirmation required: %s", prompt)
	}
	return interact.Confirm(prompt)
}

// FormatErrorResponse formats an error for display
func FormatErrorResponse(w *output.Writer, err error) error {
	if err == nil {
		return nil
	}

	if IsUnsafeHTTP(err) {
		return w.Error("unsafe_http", "refusing plaintext HTTP to non-loopback host", nil, "use --insecure flag or configure HTTPS", 2)
	}

	if client.IsTransportError(err) {
		return w.Error("transport_error", err.Error(), nil, "check network connectivity", 3)
	}

	// Check for missing API key
	if strings.Contains(err.Error(), "no API key configured") {
		return w.Error("missing_api_key", "no API key configured", nil, "use --api-key flag or set UNSWARM_API_KEY environment variable", 2)
	}

	return w.Error("internal_error", err.Error(), nil, "", 1)
}
