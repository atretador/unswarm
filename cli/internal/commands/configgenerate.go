package commands

import (
	"crypto/tls"
	"encoding/json"
	"fmt"
	"net/http"
	"os"
	"path/filepath"
	"regexp"
	"strings"

	"github.com/spf13/cobra"
	"github.com/unswarm/cli/internal/client"
	"github.com/unswarm/cli/internal/output"
	"github.com/unswarm/cli/internal/resolve"
)

var configGenerateOpenCodeCmd = &cobra.Command{
	Use:   "generate-opencode",
	Short: "Generate opencode.jsonc provider config for Unswarm",
	Long:  `Rotate an inference API key and write a provider block into opencode.jsonc for use with the Unswarm backend.`,
	RunE: func(cmd *cobra.Command, args []string) error {
		return runConfigGenerate(cmd, "opencode")
	},
}

var configGeneratePiCmd = &cobra.Command{
	Use:   "generate-pi",
	Short: "Generate PI agent models.json provider config for Unswarm",
	Long:  `Rotate an inference API key and write a provider block into ~/.pi/agent/models.json for use with the Unswarm backend.`,
	RunE: func(cmd *cobra.Command, args []string) error {
		return runConfigGenerate(cmd, "pi")
	},
}

// catalogEntry represents a single entry from the provider-model-catalog.
type catalogEntry struct {
	Name              string            `json:"name"`
	Kind              string            `json:"kind"`
	Models            []string          `json:"models"`
	ModelDisplayNames map[string]string `json:"modelDisplayNames"`
}

// accessGrants represents the access grants for an API key.
type accessGrants struct {
	Providers []string `json:"providers"`
	Models    []string `json:"models"`
}

// openCodeModel represents a model block in opencode.jsonc.
type openCodeModel struct {
	Name  string `json:"name"`
	Limit struct {
		Context int `json:"context"`
		Output  int `json:"output"`
	} `json:"limit"`
}

// piModelEntry represents a model entry in PI's models.json.
type piModelEntry struct {
	ID            string `json:"id"`
	Name          string `json:"name"`
	ContextWindow int    `json:"contextWindow"`
	MaxTokens     int    `json:"maxTokens"`
}

func init() {
	configGenerateOpenCodeCmd.Flags().String("key", "", "Inference-scope API key name (required)")
	configGenerateOpenCodeCmd.Flags().String("target", "global", "Config scope: global or project")

	configGeneratePiCmd.Flags().String("key", "", "Inference-scope API key name (required)")
	configGeneratePiCmd.Flags().String("target", "global", "Config scope: global or project")

	_ = configGenerateOpenCodeCmd.MarkFlagRequired("key")
	_ = configGeneratePiCmd.MarkFlagRequired("key")

	configCmd.AddCommand(configGenerateOpenCodeCmd)
	configCmd.AddCommand(configGeneratePiCmd)
}

// runConfigGenerate implements the shared flow for both generate-opencode and generate-pi.
func runConfigGenerate(cmd *cobra.Command, kind string) error {
	c := GetClient(cmd)
	w := GetOutput(cmd)

	apiKeyName, _ := cmd.Flags().GetString("key")
	target, _ := cmd.Flags().GetString("target")

	// 1. Resolve key name → ID
	resolver := resolve.New(newResolveAdapter(c), "/api/api-keys", "name")
	keyID, err := resolver.Resolve(cmd.Context(), apiKeyName)
	if err != nil {
		return w.Error("not_found", err.Error(), nil, "use 'apikeys list' to see available API keys", 1)
	}

	// 2. Fetch key detail — verify scope is "inference"
	resp, err := c.Do(cmd.Context(), "GET", "/api/api-keys/"+keyID, nil)
	if err != nil {
		return FormatErrorResponse(w, err)
	}
	if resp.StatusCode >= 400 {
		apiErr := client.ParseError(resp)
		return w.Error(apiErr.ErrCode, apiErr.Message, apiErr.Status, apiErr.Hint, apiErr.ExitCode)
	}

	var keyDetail map[string]interface{}
	if err := json.Unmarshal(resp.Body, &keyDetail); err != nil {
		return w.Error("parse_error", "failed to parse API key detail", nil, "", 1)
	}

	scope, _ := keyDetail["scope"].(string)
	if strings.ToLower(scope) != "inference" {
		return w.Error("invalid_scope", fmt.Sprintf("only inference-scope API keys are supported (got %q)", scope), nil, "create an inference-scope key with 'apikeys create'", 1)
	}

	// 3. Confirm rotation
	if !dryRun {
		confirmed, err := ConfirmOrSkip(cmd, fmt.Sprintf("Inference key '%s' will be rotated to obtain a secret for config generation. This invalidates the previous key. Continue?", apiKeyName))
		if err != nil {
			return w.Error("confirmation_required", err.Error(), nil, "use --yes to skip confirmation", 1)
		}
		if !confirmed {
			return nil
		}
	}

	// 4. Rotate key (skip in dry-run)
	var newSecret string
	if dryRun {
		newSecret = "DRY_RUN_SECRET"
	} else {
		resp, err = c.Do(cmd.Context(), "POST", "/api/api-keys/"+keyID+"/rotate", nil)
		if err != nil {
			return FormatErrorResponse(w, err)
		}
		if resp.StatusCode >= 400 {
			apiErr := client.ParseError(resp)
			return w.Error(apiErr.ErrCode, apiErr.Message, apiErr.Status, apiErr.Hint, apiErr.ExitCode)
		}

		var rotateResult map[string]interface{}
		if err := json.Unmarshal(resp.Body, &rotateResult); err != nil {
			return w.Error("parse_error", "failed to parse rotate response", nil, "", 1)
		}
		newSecret, _ = rotateResult["secret"].(string)
		if newSecret == "" {
			return w.Error("missing_secret", "rotate response did not contain a secret", nil, "", 1)
		}
	}

	// 5. Fetch models from /v1/models (source of truth for what the key can access)
	var v1Models []v1ModelData
	if !dryRun {
		v1Models = fetchV1Models(cmd, newSecret)
	} else {
		// In dry-run, we can't call /v1/models (no real secret), so fall back to catalog
		resp, err = c.Do(cmd.Context(), "GET", "/api/provider-model-catalog", nil)
		if err != nil {
			return FormatErrorResponse(w, err)
		}
		if resp.StatusCode >= 400 {
			apiErr := client.ParseError(resp)
			return w.Error(apiErr.ErrCode, apiErr.Message, apiErr.Status, apiErr.Hint, apiErr.ExitCode)
		}
		var catalog []catalogEntry
		if err := json.Unmarshal(resp.Body, &catalog); err != nil {
			return w.Error("parse_error", "failed to parse provider-model-catalog", nil, "", 1)
		}
		// Convert catalog entries to v1ModelData for dry-run
		for _, entry := range catalog {
			if strings.EqualFold(entry.Kind, "router") {
				v1Models = append(v1Models, v1ModelData{ID: "router/" + entry.Name, OwnedBy: "router"})
			} else {
				for _, modelID := range entry.Models {
					v1Models = append(v1Models, v1ModelData{ID: modelID, OwnedBy: entry.Name})
				}
			}
		}
	}

	// 7. Optionally fetch catalog for display names
	displayNameMap := make(map[string]string)
	resp, err = c.Do(cmd.Context(), "GET", "/api/provider-model-catalog", nil)
	if err == nil && resp.StatusCode < 400 {
		var catalog []catalogEntry
		if err := json.Unmarshal(resp.Body, &catalog); err == nil {
			for _, entry := range catalog {
				if entry.ModelDisplayNames != nil {
					for k, v := range entry.ModelDisplayNames {
						displayNameMap[k] = v
					}
				}
			}
		}
	}

	// 8. Determine backend URL
	backendURL := resolveBackendURL()

	// 9. Build and write config file
	switch kind {
	case "opencode":
		return writeOpenCodeConfig(cmd, w, target, backendURL, newSecret, v1Models, displayNameMap)
	case "pi":
		return writePiConfig(cmd, w, target, backendURL, newSecret, v1Models, displayNameMap)
	default:
		return fmt.Errorf("unknown config kind: %s", kind)
	}
}

// filterCatalog filters catalog entries based on access grants.
// If both providers and models are empty → unrestricted → return all.
func filterCatalog(catalog []catalogEntry, grants accessGrants) []catalogEntry {
	if len(grants.Providers) == 0 && len(grants.Models) == 0 {
		return catalog
	}

	providerSet := make(map[string]bool, len(grants.Providers))
	for _, p := range grants.Providers {
		providerSet[strings.ToLower(p)] = true
	}

	modelSet := make(map[string]bool, len(grants.Models))
	for _, m := range grants.Models {
		modelSet[m] = true
	}

	var result []catalogEntry
	for _, entry := range catalog {
		providerMatch := providerSet[strings.ToLower(entry.Name)]

		if providerMatch {
			// Provider is in grants → include all models from this entry
			result = append(result, entry)
			continue
		}

		// Check if any model from this entry is in the models grant
		var matchedModels []string
		for _, modelID := range entry.Models {
			if modelSet[modelID] {
				matchedModels = append(matchedModels, modelID)
			}
		}

		if len(matchedModels) > 0 {
			// If providers is empty but models has entries, include only specific models
			if len(grants.Providers) == 0 {
				result = append(result, catalogEntry{
					Name:              entry.Name,
					Kind:              entry.Kind,
					Models:            matchedModels,
					ModelDisplayNames: entry.ModelDisplayNames,
				})
			} else {
				result = append(result, entry)
			}
		}
	}

	return result
}

// dedupCatalogEntries removes duplicate model IDs across all catalog entries.
// Models appear in multiple router profiles; this keeps only the first occurrence.
func dedupCatalogEntries(entries []catalogEntry) []catalogEntry {
	seen := make(map[string]bool)
	for i := range entries {
		var uniq []string
		for _, m := range entries[i].Models {
			if !seen[m] {
				seen[m] = true
				uniq = append(uniq, m)
			}
		}
		entries[i].Models = uniq
	}
	return entries
}

// resolveBackendURL determines the backend URL from config/flags.
func resolveBackendURL() string {
	if url != "" {
		return url
	}
	cfg, err := client.LoadConfig()
	if err == nil && cfg.BaseURL != "" {
		return cfg.BaseURL
	}
	return "http://localhost:22301"
}

// v1ModelData represents a single model from the /v1/models endpoint.
type v1ModelData struct {
	ID      string              `json:"id"`
	OwnedBy string              `json:"owned_by"`
	Unswarm *v1ModelUnswarmInfo `json:"unswarm"`
}

// v1ModelUnswarmInfo holds Unswarm-specific metadata from /v1/models.
type v1ModelUnswarmInfo struct {
	ContextWindow   int `json:"contextWindow"`
	MaxOutputTokens int `json:"maxOutputTokens"`
}

// fetchV1Models calls GET /v1/models to get the full model list the key can access.
func fetchV1Models(cmd *cobra.Command, secret string) []v1ModelData {
	baseURL := resolveBackendURL()
	modelURL := baseURL + "/v1/models"

	req, err := http.NewRequest("GET", modelURL, nil)
	if err != nil {
		return nil
	}
	req.Header.Set("Authorization", "Bearer "+secret)

	insecure, _ := cmd.Flags().GetBool("insecure")
	transport := &http.Transport{}
	if insecure {
		transport.TLSClientConfig = &tls.Config{InsecureSkipVerify: true}
	}
	httpClient := &http.Client{Transport: transport}

	resp, err := httpClient.Do(req)
	if err != nil {
		return nil
	}
	defer resp.Body.Close()

	if resp.StatusCode >= 400 {
		return nil
	}

	var body struct {
		Data []v1ModelData `json:"data"`
	}
	if err := json.NewDecoder(resp.Body).Decode(&body); err != nil {
		return nil
	}

	return body.Data
}

// resolveTargetPath returns the target file path for the given kind and scope.
func resolveTargetPath(kind, target string) (string, error) {
	switch kind {
	case "opencode":
		if target == "project" {
			return "opencode.jsonc", nil
		}
		home, err := os.UserHomeDir()
		if err != nil {
			return "", fmt.Errorf("cannot determine home directory: %w", err)
		}
		return filepath.Join(home, ".config", "opencode", "opencode.jsonc"), nil
	case "pi":
		home, err := os.UserHomeDir()
		if err != nil {
			return "", fmt.Errorf("cannot determine home directory: %w", err)
		}
		return filepath.Join(home, ".pi", "agent", "models.json"), nil
	default:
		return "", fmt.Errorf("unknown config kind: %s", kind)
	}
}

// backupFile creates a .bak copy of a file if it exists.
func backupFile(path string) error {
	data, err := os.ReadFile(path)
	if err != nil {
		if os.IsNotExist(err) {
			return nil // nothing to back up
		}
		return fmt.Errorf("failed to read file for backup: %w", err)
	}
	return os.WriteFile(path+".bak", data, 0644)
}

// writeFileAtomically writes data to path, creating parent dirs as needed.
func writeFileAtomically(path string, data []byte) error {
	dir := filepath.Dir(path)
	if err := os.MkdirAll(dir, 0755); err != nil {
		return fmt.Errorf("failed to create directory %s: %w", dir, err)
	}
	return os.WriteFile(path, data, 0644)
}

// --- opencode.jsonc ---

// openCodeConfig represents the top-level structure of opencode.jsonc.
type openCodeConfig struct {
	Provider map[string]openCodeProvider `json:"provider"`
}

type openCodeProvider struct {
	Name    string                    `json:"name"`
	API     string                    `json:"api"`
	Options openCodeOptions           `json:"options"`
	Models  map[string]openCodeModel  `json:"models"`
}

type openCodeOptions struct {
	BaseURL string `json:"baseURL"`
	APIKey  string `json:"apiKey"`
}

func writeOpenCodeConfig(_ *cobra.Command, w *output.Writer, target, backendURL, secret string, v1Models []v1ModelData, displayNames map[string]string) error {
	targetPath, err := resolveTargetPath("opencode", target)
	if err != nil {
		return err
	}

	// Build the models map directly from /v1/models
	models := make(map[string]openCodeModel)
	for _, m := range v1Models {
		name := m.ID
		if dn, ok := displayNames[m.ID]; ok && dn != "" {
			name = dn
		} else if strings.HasPrefix(m.ID, "router/") {
			name = strings.TrimPrefix(m.ID, "router/")
		}
		entry := openCodeModel{
			Name: name,
			Limit: struct {
				Context int `json:"context"`
				Output  int `json:"output"`
			}{
				Context: 131072,
				Output:  32768,
			},
		}
		if m.Unswarm != nil {
			if m.Unswarm.ContextWindow > 0 {
				entry.Limit.Context = m.Unswarm.ContextWindow
			}
			if m.Unswarm.MaxOutputTokens > 0 {
				entry.Limit.Output = m.Unswarm.MaxOutputTokens
			}
		}
		models[m.ID] = entry
	}

	provider := openCodeProvider{
		Name: "Unswarm",
		API:  "openai",
		Options: openCodeOptions{
			BaseURL: backendURL + "/v1",
			APIKey:  secret,
		},
		Models: models,
	}

	// Read existing file
	existingConfig := &openCodeConfig{
		Provider: make(map[string]openCodeProvider),
	}

	existingData, err := os.ReadFile(targetPath)
	if err == nil {
		// Strip comments before parsing JSON
		stripped := stripJSONComments(string(existingData))
		_ = json.Unmarshal([]byte(stripped), existingConfig)
	}

	// Upsert
	if existingConfig.Provider == nil {
		existingConfig.Provider = make(map[string]openCodeProvider)
	}
	existingConfig.Provider["unswarm"] = provider

	if dryRun {
		prettyJSON, _ := json.MarshalIndent(existingConfig, "", "  ")
		fmt.Printf("Would write to: %s\n", targetPath)
		fmt.Println(string(prettyJSON))
		return nil
	}

	// Backup existing file
	if err := backupFile(targetPath); err != nil {
		return err
	}

	data, err := json.MarshalIndent(existingConfig, "", "  ")
	if err != nil {
		return err
	}

	if err := writeFileAtomically(targetPath, data); err != nil {
		return err
	}

	return w.Print(map[string]any{
		"message": fmt.Sprintf("Wrote Unswarm provider config for %d models", len(models)),
		"path":    targetPath,
		"scope":   target,
	})
}

// --- PI models.json ---

// piConfig represents the top-level structure of PI's models.json.
type piConfig struct {
	Providers map[string]piProvider `json:"providers"`
}

type piProvider struct {
	BaseURL string        `json:"baseUrl"`
	API     string        `json:"api"`
	APIKey  string        `json:"apiKey"`
	Models  []piModelEntry `json:"models"`
}

func writePiConfig(_ *cobra.Command, w *output.Writer, target, backendURL, secret string, v1Models []v1ModelData, displayNames map[string]string) error {
	targetPath, err := resolveTargetPath("pi", target)
	if err != nil {
		return err
	}

	// Build the models list directly from /v1/models
	var models []piModelEntry
	for _, m := range v1Models {
		name := m.ID
		if dn, ok := displayNames[m.ID]; ok && dn != "" {
			name = dn
		} else if strings.HasPrefix(m.ID, "router/") {
			name = strings.TrimPrefix(m.ID, "router/")
		}
		cw := 131072
		if m.Unswarm != nil && m.Unswarm.ContextWindow > 0 {
			cw = m.Unswarm.ContextWindow
		}
		models = append(models, piModelEntry{
			ID:            m.ID,
			Name:          name,
			ContextWindow: cw,
			MaxTokens:     32768,
		})
	}

	provider := piProvider{
		BaseURL: backendURL + "/v1",
		API:     "openai-completions",
		APIKey:  secret,
		Models:  models,
	}

	// Read existing file
	existingConfig := &piConfig{
		Providers: make(map[string]piProvider),
	}

	existingData, err := os.ReadFile(targetPath)
	if err == nil {
		stripped := stripJSONComments(string(existingData))
		_ = json.Unmarshal([]byte(stripped), existingConfig)
	}

	// Upsert
	if existingConfig.Providers == nil {
		existingConfig.Providers = make(map[string]piProvider)
	}
	existingConfig.Providers["unswarm"] = provider

	if dryRun {
		prettyJSON, _ := json.MarshalIndent(existingConfig, "", "  ")
		fmt.Printf("Would write to: %s\n", targetPath)
		fmt.Println(string(prettyJSON))
		return nil
	}

	// Backup existing file
	if err := backupFile(targetPath); err != nil {
		return err
	}

	data, err := json.MarshalIndent(existingConfig, "", "  ")
	if err != nil {
		return err
	}

	if err := writeFileAtomically(targetPath, data); err != nil {
		return err
	}

	return w.Print(map[string]any{
		"message": fmt.Sprintf("Wrote Unswarm provider config for %d models", len(models)),
		"path":    targetPath,
		"scope":   target,
	})
}

// stripJSONComments removes // and /* */ comments from JSONC content.
// This is a simple approach for stripping comments before JSON parsing.
var (
	lineCommentRe  = regexp.MustCompile(`//[^\n]*`)
	blockCommentRe = regexp.MustCompile(`/\*[\s\S]*?\*/`)
)

func stripJSONComments(s string) string {
	s = blockCommentRe.ReplaceAllString(s, "")
	s = lineCommentRe.ReplaceAllString(s, "")
	return s
}
