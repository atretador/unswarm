package commands

import (
	"encoding/json"
	"fmt"
	"os"
	"time"

	"github.com/spf13/cobra"
	"github.com/unswarm/cli/internal/client"
)

var (
	configContextName string
)

var configCmd = &cobra.Command{
	Use:   "config",
	Short: "Manage CLI configuration",
	Long:  `Initialize, view, and manage CLI configuration settings including multi-context support.`,
}

var configInitCmd = &cobra.Command{
	Use:   "init",
	Short: "Initialize configuration file",
	Long:  `Create the configuration file at ~/.config/unswarm/cli.yaml.`,
	RunE: func(cmd *cobra.Command, args []string) error {
		w := GetOutput(cmd)

		// Check if config already exists
		path := client.ConfigPath()
		if _, err := os.Stat(path); err == nil {
			// File exists, check --yes flag
			if !yes {
				fmt.Print("Configuration file already exists. Overwrite? [y/N] ")
				var response string
				fmt.Scanln(&response)
				if response != "y" && response != "Y" && response != "yes" {
					fmt.Println("Aborted.")
					return nil
				}
			}
		}

		// Create config with provided flags
		cfg := &client.Config{
			BaseURL:   "http://localhost:22301",
			OutputFmt: "table",
			Color:     true,
		}

		// Apply flag overrides
		if url != "" {
			cfg.BaseURL = url
		}
		if outputFmt != "" {
			cfg.OutputFmt = outputFmt
		}
		if noColor {
			cfg.Color = false
		}

		// Determine context name
		ctxName := configContextName
		if ctxName == "" {
			ctxName = "default"
		}

		// Save config under context
		if err := client.SaveConfigWithContext(cfg, ctxName); err != nil {
			return FormatErrorResponse(w, err)
		}

		// Also set as active context if creating "default" or first context
		cf, err := client.LoadConfigFile()
		if err == nil {
			if cf.ActiveContext == "" || ctxName == "default" {
				cf.ActiveContext = ctxName
				_ = client.SaveConfigFile(cf)
			}
		}

		w.Print(map[string]any{
			"message": "Configuration initialized",
			"path":    path,
			"context": ctxName,
		})

		return nil
	},
}

var configShowCmd = &cobra.Command{
	Use:   "show",
	Short: "Show current configuration",
	Long:  `Display the current CLI configuration settings (URL, output format, color).`,
	RunE: func(cmd *cobra.Command, args []string) error {
		w := GetOutput(cmd)

		cfg, err := client.LoadConfig()
		if err != nil {
			return FormatErrorResponse(w, err)
		}

		// Apply flag overrides for display
		if url != "" {
			cfg.BaseURL = url
		}
		if outputFmt != "" {
			cfg.OutputFmt = outputFmt
		}
		if noColor {
			cfg.Color = false
		}

		// Show config (NO API key)
		w.Print(map[string]any{
			"url":    cfg.BaseURL,
			"output": cfg.OutputFmt,
			"color":  cfg.Color,
		})

		return nil
	},
}

var configGetCmd = &cobra.Command{
	Use:   "get <key>",
	Short: "Get a single config value",
	Long: `Read and print a single config key from the active context.

Valid keys: url, output, color`,
	Args: cobra.ExactArgs(1),
	RunE: func(cmd *cobra.Command, args []string) error {
		w := GetOutput(cmd)

		key := args[0]
		validKeys := map[string]bool{"url": true, "output": true, "color": true}
		if !validKeys[key] {
			return w.Error("invalid_config_key", fmt.Sprintf("invalid config key %q", key), nil, "valid keys: url, output, color", 1)
		}

		cfg, err := client.LoadConfig()
		if err != nil {
			return FormatErrorResponse(w, err)
		}

		var value any
		switch key {
		case "url":
			value = cfg.BaseURL
		case "output":
			value = cfg.OutputFmt
		case "color":
			value = cfg.Color
		}

		w.Print(map[string]any{
			"key":   key,
			"value": value,
		})

		return nil
	},
}

var configSetCmd = &cobra.Command{
	Use:   "set <key> <value>",
	Short: "Set a single config value",
	Long: `Write a single config key to the active context.

Valid keys: url, output, color`,
	Args: cobra.ExactArgs(2),
	RunE: func(cmd *cobra.Command, args []string) error {
		w := GetOutput(cmd)

		key := args[0]
		value := args[1]
		validKeys := map[string]bool{"url": true, "output": true, "color": true}
		if !validKeys[key] {
			return w.Error("invalid_config_key", fmt.Sprintf("invalid config key %q", key), nil, "valid keys: url, output, color", 1)
		}

		// Load current config
		cf, err := client.LoadConfigFile()
		if err != nil {
			return FormatErrorResponse(w, err)
		}

		activeCtx := cf.ActiveContext
		if activeCtx == "" {
			activeCtx = "default"
		}

		cfg, ok := cf.Contexts[activeCtx]
		if !ok {
			// Create the context with defaults
			cfg = &client.Config{
				BaseURL:   "http://localhost:22301",
				OutputFmt: "table",
				Color:     true,
			}
			cf.Contexts[activeCtx] = cfg
		}

		// Apply the value
		switch key {
		case "url":
			cfg.BaseURL = value
		case "output":
			cfg.OutputFmt = value
		case "color":
			switch value {
			case "true", "yes", "1":
				cfg.Color = true
			case "false", "no", "0":
				cfg.Color = false
			default:
				return w.Error("invalid_color_value", fmt.Sprintf("invalid color value %q", value), nil, "use: true, false, yes, no, 1, 0", 1)
			}
		}

		cf.Contexts[activeCtx] = cfg
		if err := client.SaveConfigFile(cf); err != nil {
			return FormatErrorResponse(w, err)
		}

		w.Print(map[string]any{
			"message": fmt.Sprintf("Set %s = %v", key, value),
			"context": activeCtx,
		})

		return nil
	},
}

var configContextsCmd = &cobra.Command{
	Use:   "contexts",
	Short: "List all configuration contexts",
	Long:  `Display all named contexts with the active context marked by *.`,
	RunE: func(cmd *cobra.Command, args []string) error {
		w := GetOutput(cmd)

		cf, err := client.LoadConfigFile()
		if err != nil {
			return FormatErrorResponse(w, err)
		}

		type contextEntry struct {
			Name    string `json:"name"`
			Active  bool   `json:"active"`
			URL     string `json:"url"`
			Output  string `json:"output"`
			Color   bool   `json:"color"`
		}

		var entries []contextEntry
		for name, cfg := range cf.Contexts {
			entries = append(entries, contextEntry{
				Name:   name,
				Active: name == cf.ActiveContext,
				URL:    cfg.BaseURL,
				Output: cfg.OutputFmt,
				Color:  cfg.Color,
			})
		}

		w.Print(map[string]any{
			"active-context": cf.ActiveContext,
			"contexts":       entries,
		})

		return nil
	},
}

var configUseCmd = &cobra.Command{
	Use:   "use <context>",
	Short: "Switch active context",
	Long:  `Set the active context to the named context.`,
	Args:  cobra.ExactArgs(1),
	RunE: func(cmd *cobra.Command, args []string) error {
		w := GetOutput(cmd)
		ctxName := args[0]

		cf, err := client.LoadConfigFile()
		if err != nil {
			return FormatErrorResponse(w, err)
		}

		if _, ok := cf.Contexts[ctxName]; !ok {
			return w.Error("context_not_found", fmt.Sprintf("context %q not found", ctxName), nil, "use 'config contexts' to list available contexts", 1)
		}

		cf.ActiveContext = ctxName
		if err := client.SaveConfigFile(cf); err != nil {
			return FormatErrorResponse(w, err)
		}

		cfg := cf.Contexts[ctxName]
		w.Print(map[string]any{
			"message": fmt.Sprintf("Switched to context %q", ctxName),
			"context": ctxName,
			"url":     cfg.BaseURL,
		})

		return nil
	},
}

var configTestCmd = &cobra.Command{
	Use:   "test",
	Short: "Test connection to the server",
	Long:  `Hit the server to verify connectivity, show connection status, server version, and latency.`,
	RunE: func(cmd *cobra.Command, args []string) error {
		w := GetOutput(cmd)
		c := GetClient(cmd)

		start := time.Now()
		resp, err := c.Do(cmd.Context(), "GET", "/api/stats", nil)
		latency := time.Since(start)

		if err != nil {
			w.Print(map[string]any{
				"status":  "error",
				"message": err.Error(),
				"latency": latency.String(),
			})
			return nil
		}

		result := map[string]any{
			"status":  "ok",
			"code":    resp.StatusCode,
			"latency": latency.String(),
		}

		// Try to extract version from response
		var stats map[string]any
		if err := json.Unmarshal(resp.Body, &stats); err == nil {
			if v, ok := stats["version"]; ok {
				result["version"] = v
			}
		}

		w.Print(result)
		return nil
	},
}

func init() {
	configCmd.AddCommand(configInitCmd)
	configCmd.AddCommand(configShowCmd)
	configCmd.AddCommand(configGetCmd)
	configCmd.AddCommand(configSetCmd)
	configCmd.AddCommand(configContextsCmd)
	configCmd.AddCommand(configUseCmd)
	configCmd.AddCommand(configTestCmd)

	// Add flags to config init
	configInitCmd.Flags().StringVar(&url, "url", "", "Default API server URL")
	configInitCmd.Flags().StringVar(&outputFmt, "output", "", "Default output format")
	configInitCmd.Flags().BoolVar(&noColor, "no-color", false, "Disable colored output")
	configInitCmd.Flags().StringVar(&configContextName, "context", "", "Context name (default: \"default\")")

	rootCmd.AddCommand(configCmd)
}
