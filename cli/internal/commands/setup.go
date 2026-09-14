package commands

import (
	"context"
	"encoding/json"
	"fmt"
	"os"

	"github.com/spf13/cobra"
	"github.com/unswarm/cli/internal/client"
	"github.com/unswarm/cli/internal/output"
)

var setupCmd = &cobra.Command{
	Use:   "setup",
	Short: "Run the guided setup wizard",
	Long:  `Check connection, show configuration, and display available resources.`,
	RunE: func(cmd *cobra.Command, args []string) error {
		c := GetClient(cmd)
		w := GetOutput(cmd)

		// Step 1: Check connection
		resp, err := c.Do(cmd.Context(), "GET", "/api/stats", nil)
		if err != nil {
			return FormatErrorResponse(w, err)
		}
		if resp.StatusCode >= 400 {
			apiErr := client.ParseError(resp)
			return w.Error(apiErr.ErrCode, apiErr.Message, apiErr.Status, apiErr.Hint, apiErr.ExitCode)
		}

		// Step 2: Show connection info
		cfg, cfgErr := client.LoadConfig()
		url := "unknown"
		if cfgErr == nil && cfg != nil {
			url = cfg.BaseURL
		}

		apiKeyPresent := "no"
		if os.Getenv("UNSWARM_API_KEY") != "" {
			apiKeyPresent = "yes"
		}

		// Step 3: Fetch resource counts
		modelsCount := fetchCount(c, cmd.Context(), "/api/models")
		runtimesCount := fetchCount(c, cmd.Context(), "/api/containers/registered")
		containersCount := fetchCount(c, cmd.Context(), "/api/containers")
		agentsCount := fetchCount(c, cmd.Context(), "/api/agents")

		// Determine output format
		format := output.Format(cmd.Flags().Lookup("output").Value.String())
		if format == "" {
			format = output.DetectFormat()
		}

		// JSON output mode
		if format == output.FormatJSON {
			return w.Print(map[string]any{
				"connection": map[string]any{
					"url":    url,
					"status": "ok",
				},
				"apiKey": map[string]any{
					"configured": apiKeyPresent == "yes",
				},
				"resources": map[string]any{
					"models":     modelsCount,
					"runtimes":   runtimesCount,
					"containers": containersCount,
					"agents":     agentsCount,
				},
			})
		}

		// Table mode: connection info
		fmt.Printf("Connection: OK (%s)\n", url)
		fmt.Printf("API Key:    %s\n", apiKeyPresent)
		fmt.Println()

		// Resources table
		fmt.Println("RESOURCES")
		resHeaders := []string{"TYPE", "COUNT"}
		resRows := [][]string{
			{"Models", fmt.Sprintf("%d", modelsCount)},
			{"Runtimes", fmt.Sprintf("%d", runtimesCount)},
			{"Containers", fmt.Sprintf("%d", containersCount)},
			{"Agents", fmt.Sprintf("%d", agentsCount)},
		}
		_ = w.PrintTable(resHeaders, resRows)

		// Next steps
		fmt.Println()
		fmt.Println("NEXT STEPS")
		fmt.Println("  unswarm models list          List available models")
		fmt.Println("  unswarm runtimes list        List registered runtimes")
		fmt.Println("  unswarm stats                View system statistics")
		fmt.Println("  unswarm health               Dashboard view")
		fmt.Println("  unswarm benchmarks list      View benchmark results")

		return nil
	},
}

// fetchCount returns the number of items in a JSON array response, or 0 on error.
func fetchCount(c *client.Client, ctx context.Context, path string) int {
	resp, err := c.Do(ctx, "GET", path, nil)
	if err != nil {
		return 0
	}
	if resp.StatusCode >= 400 {
		return 0
	}
	var items []json.RawMessage
	if err := json.Unmarshal(resp.Body, &items); err != nil {
		return 0
	}
	return len(items)
}

func init() {
	rootCmd.AddCommand(setupCmd)
}
