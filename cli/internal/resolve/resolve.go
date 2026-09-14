package resolve

import (
	"context"
	"encoding/json"
	"fmt"
	"strings"
)

// Client is the interface needed for API calls. Matches *client.Client.Do.
type Client interface {
	Do(ctx context.Context, method, path string, body any) (*Response, error)
}

// Response wraps the HTTP response. Matches client.Response fields used here.
type Response struct {
	StatusCode int
	Body       []byte
}

// resource holds a single resolved entry.
type resource struct {
	ID   string
	Name string
}

// Resolver resolves names/IDs to canonical IDs for a specific resource type.
type Resolver struct {
	client   Client
	listPath string // API endpoint returning JSON array, e.g. "/api/models"
	nameField string // JSON field to match against, e.g. "displayName"
	resources []resource
	loaded    bool
}

// New creates a Resolver for a specific resource type.
// listPath is the API endpoint that returns all resources (e.g., "/api/models").
// nameField is the JSON field to match against (e.g., "displayName", "name").
func New(client Client, listPath, nameField string) *Resolver {
	return &Resolver{
		client:    client,
		listPath:  listPath,
		nameField: nameField,
	}
}

// isID returns true if input looks like a hex ID (32+ hex chars,
// with optional dashes as separators).
func isID(input string) bool {
	// Strip dashes (used as separators in some ID formats)
	candidate := strings.ReplaceAll(input, "-", "")

	if len(candidate) < 32 {
		return false
	}

	for _, c := range candidate {
		if !((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F')) {
			return false
		}
	}
	return true
}

// load fetches the resource list from the API and caches it.
func (r *Resolver) load(ctx context.Context) error {
	if r.loaded {
		return nil
	}

	resp, err := r.client.Do(ctx, "GET", r.listPath, nil)
	if err != nil {
		return fmt.Errorf("failed to list resources: %w", err)
	}
	if resp.StatusCode >= 400 {
		return fmt.Errorf("failed to list resources: HTTP %d", resp.StatusCode)
	}

	// Parse as []map[string]any — same pattern as the commands package.
	var items []map[string]any
	if err := json.Unmarshal(resp.Body, &items); err != nil {
		return fmt.Errorf("failed to parse resource list: %w", err)
	}

	r.resources = make([]resource, 0, len(items))
	for _, item := range items {
		id, _ := item["id"].(string)
		if id == "" {
			continue
		}
		name, _ := item[r.nameField].(string)
		// Fall back to "name" field if the requested field is empty.
		if name == "" {
			if fallback, ok := item["name"].(string); ok {
				name = fallback
			}
		}
		r.resources = append(r.resources, resource{ID: id, Name: name})
	}

	r.loaded = true
	return nil
}

// Resolve takes user input and returns the canonical ID.
//
// 1. Empty input → error
// 2. Looks like a hex ID (32+ hex chars) → return as-is
// 3. Exact name match → return its ID
// 4. Single partial/substring match → return its ID
// 5. Multiple partial matches → error with suggestions
// 6. No matches → error
func (r *Resolver) Resolve(ctx context.Context, input string) (string, error) {
	input = strings.TrimSpace(input)
	if input == "" {
		return "", fmt.Errorf("no resource specified")
	}

	// If input looks like an ID, return as-is.
	if isID(input) {
		return input, nil
	}

	// Load the full list.
	if err := r.load(ctx); err != nil {
		return "", err
	}

	// Phase 1: exact match (case-insensitive).
	target := strings.ToLower(input)
	for _, res := range r.resources {
		if strings.ToLower(res.Name) == target {
			return res.ID, nil
		}
	}

	// Phase 2: substring / partial match (case-insensitive).
	var matches []resource
	for _, res := range r.resources {
		if strings.Contains(strings.ToLower(res.Name), target) {
			matches = append(matches, res)
		}
	}

	switch len(matches) {
	case 0:
		return "", fmt.Errorf("no resource named %q found", input)
	case 1:
		return matches[0].ID, nil
	default:
		names := make([]string, 0, len(matches))
		for _, m := range matches {
			names = append(names, m.Name)
		}
		return "", fmt.Errorf("ambiguous name %q — did you mean: %s", input, strings.Join(names, ", "))
	}
}

// ResolveOptional returns empty string if input is empty (for optional params).
func (r *Resolver) ResolveOptional(ctx context.Context, input string) (string, error) {
	input = strings.TrimSpace(input)
	if input == "" {
		return "", nil
	}
	return r.Resolve(ctx, input)
}

// ClearCache removes cached data (for testing or long-running commands).
func (r *Resolver) ClearCache() {
	r.resources = nil
	r.loaded = false
}
