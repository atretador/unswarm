package commands

import (
	"encoding/json"
	"fmt"
	"strings"

	"github.com/spf13/cobra"
	"github.com/unswarm/cli/internal/client"
	"github.com/unswarm/cli/internal/helpers"
)

var providerModelCatalogCmd = &cobra.Command{
	Use:   "provider-model-catalog",
	Short: "List providers and their models",
	Long:  `Display the union of all providers and their available models.`,
	RunE: func(cmd *cobra.Command, args []string) error {
		c := GetClient(cmd)
		w := GetOutput(cmd)

		resp, err := c.Do(cmd.Context(), "GET", "/api/provider-model-catalog", nil)
		if err != nil {
			return FormatErrorResponse(w, err)
		}
		if resp.StatusCode >= 400 {
			apiErr := client.ParseError(resp)
			return w.Error(apiErr.ErrCode, apiErr.Message, apiErr.Status, apiErr.Hint, apiErr.ExitCode)
		}

		var entries []map[string]any
		if err := json.Unmarshal(resp.Body, &entries); err != nil {
			return w.Error("parse_error", "failed to parse response", nil, "", 1)
		}

		headers := []string{"NAME", "KIND", "MODELS"}
		rows := make([][]string, 0, len(entries))
		for _, e := range entries {
			models := "-"
			if m, ok := e["models"].([]any); ok && len(m) > 0 {
				names := make([]string, 0, len(m))
				for _, item := range m {
					names = append(names, fmt.Sprintf("%v", item))
				}
				models = joinStrings(names, ", ")
			}
			rows = append(rows, []string{
				helpers.StrOrDash(e, "name"),
				helpers.StrOrDash(e, "kind"),
				models,
			})
		}
		return w.PrintTable(headers, rows)
	},
}

func init() {
	rootCmd.AddCommand(providerModelCatalogCmd)
}

// joinStrings joins a slice of strings with a separator
func joinStrings(ss []string, sep string) string {
	return strings.Join(ss, sep)
}
