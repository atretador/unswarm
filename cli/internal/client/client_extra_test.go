package client

import (
	"context"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
)

func TestClientOptions(t *testing.T) {
	c := New(&Config{BaseURL: "http://127.0.0.1:1"},
		WithAPIKey("secret"),
		WithOutput("csv"),
		WithNoColor(true),
	)
	if c.apiKey != "secret" {
		t.Errorf("apiKey = %q, want secret", c.apiKey)
	}
	if c.outputFmt != "csv" {
		t.Errorf("outputFmt = %q, want csv", c.outputFmt)
	}
	if !c.noColor {
		t.Error("expected noColor=true")
	}
}

func TestAPIErrorError(t *testing.T) {
	cases := []struct {
		name string
		e    *APIError
		want string
	}{
		{"message wins", &APIError{ErrCode: "code", Message: "boom"}, "boom"},
		{"falls back to code", &APIError{ErrCode: "code"}, "code"},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			if got := tc.e.Error(); got != tc.want {
				t.Errorf("Error() = %q, want %q", got, tc.want)
			}
		})
	}
}

func TestTruncateBody(t *testing.T) {
	short := []byte("short body")
	if got := truncateBody(short); got != "short body" {
		t.Errorf("truncateBody(short) = %q", got)
	}

	long := make([]byte, maxBodyDisplay+100)
	for i := range long {
		long[i] = 'x'
	}
	got := truncateBody(long)
	if !strings.HasSuffix(got, "...(truncated)") {
		t.Errorf("expected truncation suffix, got %q", got[len(got)-20:])
	}
	if want := maxBodyDisplay + len("...(truncated)"); len(got) != want {
		t.Errorf("len(truncateBody(long)) = %d, want %d", len(got), want)
	}
}

func TestParseErrorFallback(t *testing.T) {
	resp := &Response{StatusCode: 418, Body: []byte("teapot")}
	got := ParseError(resp)
	if got.ErrCode != "api_error" {
		t.Errorf("ErrCode = %q, want api_error", got.ErrCode)
	}
	if !strings.Contains(got.Message, "HTTP 418") {
		t.Errorf("Message = %q, want HTTP 418 prefix", got.Message)
	}
	if got.Status == nil || *got.Status != 418 {
		t.Errorf("Status = %v, want 418", got.Status)
	}

	if nilResp := ParseError(nil); nilResp.ErrCode != "unknown_error" {
		t.Errorf("ParseError(nil).ErrCode = %q, want unknown_error", nilResp.ErrCode)
	}
}

func TestDoOrError(t *testing.T) {
	t.Run("returns body on success", func(t *testing.T) {
		srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
			w.Write([]byte(`{"ok":true}`))
		}))
		defer srv.Close()
		c := New(&Config{BaseURL: srv.URL})
		body, err := c.DoOrError(context.Background(), "GET", "/x", nil)
		if err != nil {
			t.Fatalf("unexpected error: %v", err)
		}
		if string(body) != `{"ok":true}` {
			t.Errorf("body = %q", body)
		}
	})

	t.Run("returns APIError on failure", func(t *testing.T) {
		srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
			w.WriteHeader(http.StatusBadRequest)
			w.Write([]byte(`{"error":"bad_request","message":"nope"}`))
		}))
		defer srv.Close()
		c := New(&Config{BaseURL: srv.URL})
		_, err := c.DoOrError(context.Background(), "GET", "/x", nil)
		if err == nil {
			t.Fatal("expected error")
		}
		apiErr, ok := err.(*APIError)
		if !ok {
			t.Fatalf("expected *APIError, got %T", err)
		}
		if apiErr.ErrCode != "bad_request" {
			t.Errorf("ErrCode = %q, want bad_request", apiErr.ErrCode)
		}
	})
}

func TestDoText(t *testing.T) {
	t.Run("returns raw text", func(t *testing.T) {
		var gotKey string
		srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
			gotKey = r.Header.Get("X-Api-Key")
			w.Write([]byte("hello text"))
		}))
		defer srv.Close()
		c := New(&Config{BaseURL: srv.URL}, WithAPIKey("k1"))
		resp, err := c.DoText(context.Background(), "GET", "/text")
		if err != nil {
			t.Fatalf("unexpected error: %v", err)
		}
		if string(resp.Body) != "hello text" {
			t.Errorf("body = %q", resp.Body)
		}
		if gotKey != "k1" {
			t.Errorf("X-Api-Key = %q, want k1", gotKey)
		}
	})

	t.Run("204 yields empty body", func(t *testing.T) {
		srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
			w.WriteHeader(http.StatusNoContent)
		}))
		defer srv.Close()
		c := New(&Config{BaseURL: srv.URL})
		resp, err := c.DoText(context.Background(), "GET", "/text")
		if err != nil {
			t.Fatalf("unexpected error: %v", err)
		}
		if resp.StatusCode != http.StatusNoContent || len(resp.Body) != 0 {
			t.Errorf("resp = %+v", resp)
		}
	})

	t.Run("rejects unsafe http", func(t *testing.T) {
		c := New(&Config{BaseURL: "http://example.com"})
		_, err := c.DoText(context.Background(), "GET", "/text")
		if err == nil || !strings.Contains(err.Error(), "refusing plaintext HTTP") {
			t.Errorf("expected unsafe HTTP error, got: %v", err)
		}
	})
}
