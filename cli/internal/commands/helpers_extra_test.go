package commands

import (
	"context"
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"

	"github.com/spf13/cobra"
	"github.com/unswarm/cli/internal/client"
)

// ==================== settings.go helpers ====================

func TestJSONify(t *testing.T) {
	t.Run("marshalable value", func(t *testing.T) {
		got := jsonify(map[string]any{"a": 1})
		if got != `{"a":1}` {
			t.Errorf("jsonify() = %q, want %q", got, `{"a":1}`)
		}
	})

	t.Run("unmarshalable value falls back to fmt", func(t *testing.T) {
		got := jsonify(make(chan int))
		if got == "" {
			t.Error("expected fallback string for unmarshalable value")
		}
	})
}

func TestFormatSettingValue(t *testing.T) {
	cases := []struct {
		name string
		in   any
		want string
	}{
		{"map becomes JSON", map[string]any{"k": "v"}, `{"k":"v"}`},
		{"slice becomes JSON", []any{1, 2}, `[1,2]`},
		{"string unchanged", "hello", "hello"},
		{"int unchanged", 42, "42"},
		{"bool unchanged", true, "true"},
		{"nil unchanged", nil, "<nil>"},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			if got := formatSettingValue(tc.in); got != tc.want {
				t.Errorf("formatSettingValue(%v) = %q, want %q", tc.in, got, tc.want)
			}
		})
	}
}

func TestParseValue(t *testing.T) {
	cases := []struct {
		name     string
		raw      string
		existing any
		want     any
	}{
		{"bool true", "true", false, true},
		{"bool yes", "yes", false, true},
		{"bool one", "1", false, true},
		{"bool false", "false", true, false},
		{"bool no", "no", true, false},
		{"bool zero", "0", true, false},
		{"bool unrecognized keeps raw", "maybe", true, "maybe"},
		{"float parses", "3.5", float64(0), float64(3.5)},
		{"float invalid falls through to raw", "abc", float64(0), "abc"},
		{"json.Number preserved", "12", json.Number("0"), json.Number("12")},
		{"untyped true", "true", "unused", true},
		{"untyped false", "no", "unused", false},
		{"untyped number", "7", "unused", float64(7)},
		{"untyped string", "hello", "unused", "hello"},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			if got := parseValue(tc.raw, tc.existing); got != tc.want {
				t.Errorf("parseValue(%q, %T) = %v (%T), want %v (%T)", tc.raw, tc.existing, got, got, tc.want, tc.want)
			}
		})
	}
}

// ==================== configgenerate.go helpers ====================

func TestFilterCatalog(t *testing.T) {
	catalog := []catalogEntry{
		{Name: "openai", Models: []string{"gpt-4", "gpt-3"}, ModelDisplayNames: map[string]string{"gpt-4": "GPT-4"}},
		{Name: "anthropic", Models: []string{"claude-3"}},
		{Name: "local", Models: []string{"llama-7b", "mistral"}},
	}

	t.Run("unrestricted returns all", func(t *testing.T) {
		got := filterCatalog(catalog, accessGrants{})
		if len(got) != len(catalog) {
			t.Fatalf("expected %d entries, got %d", len(catalog), len(got))
		}
	})

	t.Run("provider grant includes whole entry", func(t *testing.T) {
		got := filterCatalog(catalog, accessGrants{Providers: []string{"OPENAI"}})
		if len(got) != 1 || got[0].Name != "openai" {
			t.Fatalf("expected only openai entry, got %+v", got)
		}
		if len(got[0].Models) != 2 {
			t.Errorf("expected all openai models, got %v", got[0].Models)
		}
	})

	t.Run("model-only grant keeps specific models", func(t *testing.T) {
		got := filterCatalog(catalog, accessGrants{Models: []string{"llama-7b", "gpt-4"}})
		if len(got) != 2 {
			t.Fatalf("expected 2 entries, got %d: %+v", len(got), got)
		}
		byName := map[string][]string{}
		for _, e := range got {
			byName[e.Name] = e.Models
		}
		if len(byName["local"]) != 1 || byName["local"][0] != "llama-7b" {
			t.Errorf("expected only llama-7b from local, got %v", byName["local"])
		}
		if len(byName["openai"]) != 1 || byName["openai"][0] != "gpt-4" {
			t.Errorf("expected only gpt-4 from openai, got %v", byName["openai"])
		}
	})

	t.Run("model grant with provider grant includes whole provider entry", func(t *testing.T) {
		got := filterCatalog(catalog, accessGrants{Providers: []string{"anthropic"}, Models: []string{"llama-7b"}})
		byName := map[string][]string{}
		for _, e := range got {
			byName[e.Name] = e.Models
		}
		if len(byName["anthropic"]) != 1 {
			t.Errorf("expected full anthropic entry, got %v", byName["anthropic"])
		}
		// When a provider grant is also present, a matched model grants the
		// whole entry rather than a filtered model list.
		if len(byName["local"]) != 2 {
			t.Errorf("expected full local entry, got %v", byName["local"])
		}
	})

	t.Run("no match returns empty", func(t *testing.T) {
		got := filterCatalog(catalog, accessGrants{Models: []string{"nope"}})
		if len(got) != 0 {
			t.Errorf("expected no entries, got %+v", got)
		}
	})
}

func TestDedupCatalogEntries(t *testing.T) {
	entries := []catalogEntry{
		{Name: "a", Models: []string{"m1", "m2"}},
		{Name: "b", Models: []string{"m2", "m3"}},
		{Name: "c", Models: []string{"m1", "m3", "m4"}},
	}
	got := dedupCatalogEntries(entries)
	want := [][]string{{"m1", "m2"}, {"m3"}, {"m4"}}
	if len(got) != len(want) {
		t.Fatalf("expected %d entries, got %d", len(want), len(got))
	}
	for i := range want {
		if strings.Join(got[i].Models, ",") != strings.Join(want[i], ",") {
			t.Errorf("entry %d models = %v, want %v", i, got[i].Models, want[i])
		}
	}
}

func TestResolveBackendURL(t *testing.T) {
	oldURL := url
	defer func() { url = oldURL }()

	t.Run("explicit url wins", func(t *testing.T) {
		url = "http://explicit.example:1234"
		if got := resolveBackendURL(); got != "http://explicit.example:1234" {
			t.Errorf("resolveBackendURL() = %q", got)
		}
	})

	t.Run("falls back to default when unset", func(t *testing.T) {
		url = ""
		t.Setenv("HOME", t.TempDir())
		if got := resolveBackendURL(); got != "http://localhost:22301" {
			t.Errorf("resolveBackendURL() = %q, want default", got)
		}
	})
}

func TestFetchV1Models(t *testing.T) {
	newCmd := func() *cobra.Command {
		cmd := &cobra.Command{}
		cmd.Flags().Bool("insecure", false, "")
		return cmd
	}

	t.Run("parses models and sends bearer token", func(t *testing.T) {
		var gotAuth string
		srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
			if r.URL.Path != "/v1/models" {
				t.Errorf("unexpected path %s", r.URL.Path)
			}
			gotAuth = r.Header.Get("Authorization")
			w.Header().Set("Content-Type", "application/json")
			w.Write([]byte(`{"data":[{"id":"m1","owned_by":"o1"}]}`))
		}))
		defer srv.Close()

		oldURL := url
		url = srv.URL
		defer func() { url = oldURL }()

		got := fetchV1Models(newCmd(), "secret-token")
		if gotAuth != "Bearer secret-token" {
			t.Errorf("Authorization = %q, want %q", gotAuth, "Bearer secret-token")
		}
		if len(got) != 1 || got[0].ID != "m1" || got[0].OwnedBy != "o1" {
			t.Fatalf("unexpected models: %+v", got)
		}
	})

	t.Run("insecure flag is accepted", func(t *testing.T) {
		srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
			w.Write([]byte(`{"data":[]}`))
		}))
		defer srv.Close()

		oldURL := url
		url = srv.URL
		defer func() { url = oldURL }()

		cmd := newCmd()
		cmd.Flags().Set("insecure", "true")
		if got := fetchV1Models(cmd, "k"); len(got) != 0 {
			t.Errorf("expected no models, got %+v", got)
		}
	})

	t.Run("server error returns nil", func(t *testing.T) {
		srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
			w.WriteHeader(http.StatusInternalServerError)
		}))
		defer srv.Close()

		oldURL := url
		url = srv.URL
		defer func() { url = oldURL }()

		if got := fetchV1Models(newCmd(), "k"); got != nil {
			t.Errorf("expected nil on 500, got %+v", got)
		}
	})

	t.Run("invalid JSON returns nil", func(t *testing.T) {
		srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
			w.Write([]byte("not-json"))
		}))
		defer srv.Close()

		oldURL := url
		url = srv.URL
		defer func() { url = oldURL }()

		if got := fetchV1Models(newCmd(), "k"); got != nil {
			t.Errorf("expected nil on bad JSON, got %+v", got)
		}
	})

	t.Run("unreachable server returns nil", func(t *testing.T) {
		oldURL := url
		url = "http://127.0.0.1:1" // closed port
		defer func() { url = oldURL }()

		if got := fetchV1Models(newCmd(), "k"); got != nil {
			t.Errorf("expected nil on transport error, got %+v", got)
		}
	})
}

// ==================== queue.go helper ====================

func TestRenderQueueSection(t *testing.T) {
	t.Run("non-slice input is ignored", func(t *testing.T) {
		rows := [][]string{{"existing"}}
		got := renderQueueSection(rows, "waiting", "not-a-slice")
		if len(got) != 1 || got[0][0] != "existing" {
			t.Errorf("expected unchanged rows, got %v", got)
		}
	})

	t.Run("empty slice is ignored", func(t *testing.T) {
		rows := [][]string{}
		got := renderQueueSection(rows, "waiting", []any{})
		if len(got) != 0 {
			t.Errorf("expected unchanged rows, got %v", got)
		}
	})

	t.Run("renders header and item rows, skipping non-maps", func(t *testing.T) {
		rows := [][]string{}
		items := []any{
			map[string]any{"id": "i1", "status": "waiting", "target": "agent-1", "priority": "high", "createdAt": "t1"},
			"skip-me",
			map[string]any{"id": "i2"},
		}
		got := renderQueueSection(rows, "waiting", items)
		if len(got) != 3 {
			t.Fatalf("expected header + 2 items = 3 rows, got %d: %v", len(got), got)
		}
		if !strings.Contains(got[0][0], "waiting (3)") {
			t.Errorf("unexpected header row: %v", got[0])
		}
		if got[1][0] != "i1" || got[1][1] != "waiting" || got[1][2] != "agent-1" {
			t.Errorf("unexpected first item row: %v", got[1])
		}
		// Missing fields fall back to dash.
		if got[2][1] != "-" || got[2][2] != "-" {
			t.Errorf("expected dash placeholders for missing fields, got: %v", got[2])
		}
	})
}

// ==================== setup.go helper ====================

func TestFetchCount(t *testing.T) {
	cases := []struct {
		name    string
		handler http.HandlerFunc
		want    int
	}{
		{"counts items", func(w http.ResponseWriter, r *http.Request) {
			w.Write([]byte(`[{"a":1},{"b":2},{"c":3}]`))
		}, 3},
		{"empty list", func(w http.ResponseWriter, r *http.Request) {
			w.Write([]byte(`[]`))
		}, 0},
		{"server error", func(w http.ResponseWriter, r *http.Request) {
			w.WriteHeader(http.StatusInternalServerError)
		}, 0},
		{"invalid json", func(w http.ResponseWriter, r *http.Request) {
			w.Write([]byte("nope"))
		}, 0},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			srv := httptest.NewServer(tc.handler)
			defer srv.Close()
			c := client.New(&client.Config{BaseURL: srv.URL, OutputFmt: "json", Color: false})
			if got := fetchCount(c, context.Background(), "/api/things"); got != tc.want {
				t.Errorf("fetchCount() = %d, want %d", got, tc.want)
			}
		})
	}
}

// ==================== root.go helpers ====================

func TestRequireAPIKey(t *testing.T) {
	t.Run("nil client returns error", func(t *testing.T) {
		cmd := &cobra.Command{}
		ctx := context.WithValue(context.Background(), clientKey, (*client.Client)(nil))
		cmd.SetContext(ctx)
		if err := RequireAPIKey(cmd); err == nil {
			t.Error("expected error for nil client")
		}
	})

	t.Run("configured client succeeds", func(t *testing.T) {
		cmd := &cobra.Command{}
		ctx := context.WithValue(context.Background(), clientKey, client.New(&client.Config{BaseURL: "http://localhost:1"}))
		cmd.SetContext(ctx)
		if err := RequireAPIKey(cmd); err != nil {
			t.Errorf("unexpected error: %v", err)
		}
	})
}

func TestParseAPIError(t *testing.T) {
	t.Run("structured error from body", func(t *testing.T) {
		resp := &client.Response{
			StatusCode: 400,
			Body:       []byte(`{"error":"bad_request","message":"nope"}`),
		}
		got := ParseAPIError(resp)
		if got.ErrCode != "bad_request" || got.Message != "nope" {
			t.Errorf("unexpected APIError: %+v", got)
		}
	})

	t.Run("nil response gets unknown_error", func(t *testing.T) {
		got := ParseAPIError(nil)
		if got.ErrCode != "unknown_error" {
			t.Errorf("expected unknown_error, got %+v", got)
		}
	})
}
