package commands

import (
	"bytes"
	"crypto/tls"
	"encoding/json"
	"fmt"
	"net/http"
	"os"
	"path/filepath"
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

// openCodeModalities represents the input modalities block in opencode.jsonc.
type openCodeModalities struct {
	Input []string `json:"input"`
}

// openCodeModel represents a model block in opencode.jsonc.
type openCodeModel struct {
	Name  string `json:"name"`
	Limit struct {
		Context int `json:"context"`
		Output  int `json:"output"`
	} `json:"limit"`
	Modalities openCodeModalities `json:"modalities"`
}

// piModelEntry represents a model entry in PI's models.json.
type piModelEntry struct {
	ID            string   `json:"id"`
	Name          string   `json:"name"`
	ContextWindow int      `json:"contextWindow"`
	MaxTokens     int      `json:"maxTokens"`
	Input         []string `json:"input,omitempty"`
}

// inputModalityOrder is the canonical ordering of input modalities supported
// by the Unswarm wire contract.
var inputModalityOrder = []string{"text", "image", "video", "audio", "pdf"}

// piInputModalities is the set of input modalities accepted by the PI agent.
var piInputModalities = map[string]bool{"text": true, "image": true}

// openCodeInputModalities is the set of input modalities accepted by opencode.
var openCodeInputModalities = map[string]bool{
	"text": true, "image": true, "video": true, "audio": true, "pdf": true,
}

// filterInputModalities returns the subset of a model's modalities present in
// allowed, in canonical order, and always including "text".
func filterInputModalities(mods []string, allowed map[string]bool) []string {
	present := make(map[string]bool, len(mods))
	for _, m := range mods {
		present[strings.ToLower(strings.TrimSpace(m))] = true
	}
	result := make([]string, 0, len(allowed))
	for _, m := range inputModalityOrder {
		if allowed[m] && present[m] {
			result = append(result, m)
		}
	}
	if !present["text"] {
		result = append([]string{"text"}, result...)
	}
	return result
}

// modelInputModalities resolves the input modalities for a model from the
// /v1/models Unswarm metadata, filtered to allowed. Missing or empty metadata
// defaults to text-only.
func modelInputModalities(m v1ModelData, allowed map[string]bool) []string {
	if m.Unswarm == nil {
		return filterInputModalities(nil, allowed)
	}
	return filterInputModalities(m.Unswarm.InputModalities, allowed)
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

	// 2b. Verify the existing target file is mergeable BEFORE rotating.
	// Rotation invalidates the old secret and cannot be undone, so refusing
	// here (rather than during the write) is the only safe ordering.
	if err := preflightConfigTarget(kind, target); err != nil {
		return w.Error(
			"invalid_config",
			err.Error(),
			nil,
			"fix or remove the existing config file, then re-run (no key was rotated)",
			1,
		)
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
	ContextWindow   int      `json:"contextWindow"`
	MaxOutputTokens int      `json:"maxOutputTokens"`
	InputModalities []string `json:"inputModalities"`
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
// It is used by tests to read back a generated file; merging itself operates
// on the raw document so unknown keys are never dropped (see
// mergeProviderIntoConfig).
type openCodeConfig struct {
	Provider map[string]openCodeProvider `json:"provider"`
}

type openCodeProvider struct {
	Name    string                   `json:"name"`
	API     string                   `json:"api"`
	Options openCodeOptions          `json:"options"`
	Models  map[string]openCodeModel `json:"models"`
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
			Modalities: openCodeModalities{
				Input: modelInputModalities(m, openCodeInputModalities),
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

	// Read existing file, preserving every top-level key and every sibling
	// provider. A missing file yields an empty document (fresh write).
	existingData, err := os.ReadFile(targetPath)
	if err != nil && !os.IsNotExist(err) {
		return fmt.Errorf("failed to read existing config %s: %w", targetPath, err)
	}

	merged, err := mergeProviderIntoConfig(existingData, "provider", "unswarm", provider)
	if err != nil {
		return fmt.Errorf("refusing to overwrite %s: %w", targetPath, err)
	}

	if dryRun {
		fmt.Printf("Would write to: %s\n", targetPath)
		fmt.Println(string(merged))
		return nil
	}

	// Backup existing file
	if err := backupFile(targetPath); err != nil {
		return err
	}

	if err := writeFileAtomically(targetPath, merged); err != nil {
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
	BaseURL string         `json:"baseUrl"`
	API     string         `json:"api"`
	APIKey  string         `json:"apiKey"`
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
			Input:         modelInputModalities(m, piInputModalities),
		})
	}

	provider := piProvider{
		BaseURL: backendURL + "/v1",
		API:     "openai-completions",
		APIKey:  secret,
		Models:  models,
	}

	// Read existing file, preserving every top-level key and every sibling
	// provider. A missing file yields an empty document (fresh write).
	existingData, err := os.ReadFile(targetPath)
	if err != nil && !os.IsNotExist(err) {
		return fmt.Errorf("failed to read existing config %s: %w", targetPath, err)
	}

	merged, err := mergeProviderIntoConfig(existingData, "providers", "unswarm", provider)
	if err != nil {
		return fmt.Errorf("refusing to overwrite %s: %w", targetPath, err)
	}

	if dryRun {
		fmt.Printf("Would write to: %s\n", targetPath)
		fmt.Println(string(merged))
		return nil
	}

	// Backup existing file
	if err := backupFile(targetPath); err != nil {
		return err
	}

	if err := writeFileAtomically(targetPath, merged); err != nil {
		return err
	}

	return w.Print(map[string]any{
		"message": fmt.Sprintf("Wrote Unswarm provider config for %d models", len(models)),
		"path":    targetPath,
		"scope":   target,
	})
}

// stripJSONComments removes // and /* */ comments from JSONC content.
//
// Unlike a plain regular expression, this is string-aware: a "//" or "/*"
// that occurs inside a JSON string literal (for example "http://localhost"
// or "https://opencode.ai/config.json") is part of the value and must be
// preserved. Stripping those produced invalid JSON, which the old callers
// silently ignored — overwriting the user's config with a stub.
func stripJSONComments(s string) string {
	var b strings.Builder
	b.Grow(len(s))

	inString := false
	escaped := false

	for i := 0; i < len(s); i++ {
		c := s[i]

		if inString {
			b.WriteByte(c)
			switch {
			case escaped:
				escaped = false
			case c == '\\':
				escaped = true
			case c == '"':
				inString = false
			}
			continue
		}

		if c == '"' {
			inString = true
			b.WriteByte(c)
			continue
		}

		if c == '/' && i+1 < len(s) {
			switch s[i+1] {
			case '/':
				// Line comment: drop through end of line, keep the newline.
				for i < len(s) && s[i] != '\n' {
					i++
				}
				if i < len(s) {
					b.WriteByte('\n')
				}
				continue
			case '*':
				// Block comment: drop through the closing marker.
				i += 2
				for i+1 < len(s) && !(s[i] == '*' && s[i+1] == '/') {
					i++
				}
				i++ // land on '/' so the loop's i++ moves past it
				continue
			}
		}

		b.WriteByte(c)
	}

	return b.String()
}

// decodeJSONObject parses a JSON object, returning its keys in document order
// plus each key's raw value. Preserving order keeps diffs of the user's config
// minimal instead of alphabetising their file on every regeneration.
func decodeJSONObject(s string) ([]string, map[string]json.RawMessage, error) {
	dec := json.NewDecoder(strings.NewReader(s))

	tok, err := dec.Token()
	if err != nil {
		return nil, nil, err
	}
	if delim, ok := tok.(json.Delim); !ok || delim != '{' {
		return nil, nil, fmt.Errorf("expected a JSON object, got %v", tok)
	}

	keys := make([]string, 0, 8)
	vals := make(map[string]json.RawMessage, 8)

	for dec.More() {
		tok, err := dec.Token()
		if err != nil {
			return nil, nil, err
		}
		key, ok := tok.(string)
		if !ok {
			return nil, nil, fmt.Errorf("expected an object key, got %v", tok)
		}

		var raw json.RawMessage
		if err := dec.Decode(&raw); err != nil {
			return nil, nil, err
		}

		if _, seen := vals[key]; !seen {
			keys = append(keys, key)
		}
		vals[key] = raw
	}

	// Consume the closing '}'.
	if _, err := dec.Token(); err != nil {
		return nil, nil, err
	}

	return keys, vals, nil
}

// buildCompactObject encodes an ordered object as compact JSON.
func buildCompactObject(keys []string, vals map[string]json.RawMessage) ([]byte, error) {
	var buf bytes.Buffer
	buf.WriteByte('{')

	for i, k := range keys {
		v, ok := vals[k]
		if !ok {
			continue
		}
		if i > 0 {
			buf.WriteByte(',')
		}

		keyJSON, err := marshalJSONNoEscape(k)
		if err != nil {
			return nil, err
		}
		buf.Write(keyJSON)
		buf.WriteByte(':')

		// json.Compact validates the value while preserving its key order and
		// leaving string contents (including "http://") untouched.
		if err := json.Compact(&buf, v); err != nil {
			return nil, fmt.Errorf("value for key %q is not valid JSON: %w", k, err)
		}
	}

	buf.WriteByte('}')
	return buf.Bytes(), nil
}

// marshalJSONNoEscape encodes v without HTML-escaping <, > and & so that URL
// and prompt strings in the config stay readable.
func marshalJSONNoEscape(v any) ([]byte, error) {
	var buf bytes.Buffer
	enc := json.NewEncoder(&buf)
	enc.SetEscapeHTML(false)
	if err := enc.Encode(v); err != nil {
		return nil, err
	}
	return bytes.TrimRight(buf.Bytes(), "\n"), nil
}

// parseConfigDocument validates that an existing config file can be safely
// merged: it must be a JSON object whose provider block (when present) is also
// an object. Returns the ordered top-level keys/values.
func parseConfigDocument(existing []byte, topLevelKey string) ([]string, map[string]json.RawMessage, error) {
	stripped := strings.TrimSpace(stripJSONComments(string(existing)))
	if stripped == "" {
		// Nothing to preserve (missing, empty, or comments-only file).
		return []string{}, map[string]json.RawMessage{}, nil
	}

	topKeys, topVals, err := decodeJSONObject(stripped)
	if err != nil {
		return nil, nil, fmt.Errorf("existing config is not a valid JSON object: %w", err)
	}

	if raw, ok := topVals[topLevelKey]; ok && string(raw) != "null" {
		if _, _, err := decodeJSONObject(string(raw)); err != nil {
			return nil, nil, fmt.Errorf("existing %q block is not a valid JSON object: %w", topLevelKey, err)
		}
	}

	return topKeys, topVals, nil
}

// mergeProviderIntoConfig upserts providerName under topLevelKey into the
// existing document while preserving every other key — top level and sibling
// providers alike — along with their document order.
//
// The caller must validate with parseConfigDocument first (see
// preflightConfigTarget) so an unparseable file is rejected before any
// irreversible side effect such as an API key rotation.
func mergeProviderIntoConfig(existing []byte, topLevelKey, providerName string, provider any) ([]byte, error) {
	providerRaw, err := marshalJSONNoEscape(provider)
	if err != nil {
		return nil, fmt.Errorf("failed to encode %s provider block: %w", providerName, err)
	}

	topKeys, topVals, err := parseConfigDocument(existing, topLevelKey)
	if err != nil {
		return nil, err
	}

	providerKeys := []string{}
	providerVals := map[string]json.RawMessage{}

	if raw, ok := topVals[topLevelKey]; ok && string(raw) != "null" {
		providerKeys, providerVals, err = decodeJSONObject(string(raw))
		if err != nil {
			return nil, fmt.Errorf("existing %q block is not a valid JSON object: %w", topLevelKey, err)
		}
	} else {
		topKeys = append(topKeys, topLevelKey)
	}

	if _, exists := providerVals[providerName]; !exists {
		providerKeys = append(providerKeys, providerName)
	}
	providerVals[providerName] = providerRaw

	providerObj, err := buildCompactObject(providerKeys, providerVals)
	if err != nil {
		return nil, err
	}
	topVals[topLevelKey] = providerObj

	compact, err := buildCompactObject(topKeys, topVals)
	if err != nil {
		return nil, err
	}

	var out bytes.Buffer
	if err := json.Indent(&out, compact, "", "  "); err != nil {
		return nil, fmt.Errorf("failed to format merged config: %w", err)
	}
	out.WriteByte('\n')

	return out.Bytes(), nil
}

// preflightConfigTarget verifies an existing target file is mergeable.
// Callers run this BEFORE rotating an API key: rotation cannot be undone, so
// a config we cannot parse must fail the command rather than leave the user
// with an invalidated key and no updated file.
func preflightConfigTarget(kind, target string) error {
	targetPath, err := resolveTargetPath(kind, target)
	if err != nil {
		return err
	}

	existing, err := os.ReadFile(targetPath)
	if err != nil {
		if os.IsNotExist(err) {
			return nil // fresh install, nothing to preserve
		}
		return fmt.Errorf("failed to read existing config %s: %w", targetPath, err)
	}

	_, _, err = parseConfigDocument(existing, providerTopLevelKey(kind))
	return err
}

// providerTopLevelKey returns the document key that holds providers for a
// given agent kind.
func providerTopLevelKey(kind string) string {
	if kind == "pi" {
		return "providers"
	}
	return "provider"
}
