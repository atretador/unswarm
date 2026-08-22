package docker

import (
	"bytes"
	"context"
	"encoding/json"
	"fmt"
	"io"
	"net"
	"net/http"
	"strings"
	"time"

	"unswarm/agent/internal/protocol"
)

// HealthCheck performs a TCP/HTTP check on a local port.
// The command succeeds (ok=true) when the check runs; the result data
// carries the health verdict.
func HealthCheck(ctx context.Context, port int) protocol.CommandResultPayload {
	if port <= 0 {
		return errorResult("invalid port")
	}
	addr := fmt.Sprintf("127.0.0.1:%d", port)

	// TCP reachability is the base check.
	conn, err := net.DialTimeout("tcp", addr, 3*time.Second)
	if err != nil {
		return okResult(map[string]interface{}{
			"healthy": false,
			"port":    port,
			"error":   err.Error(),
		})
	}
	conn.Close()

	// If TCP is up, probe HTTP for a status code (best-effort).
	data := map[string]interface{}{
		"healthy": true,
		"port":    port,
	}
	httpClient := &http.Client{Timeout: 5 * time.Second}
	resp, err := httpClient.Get("http://" + addr + "/") //nolint:gosec
	if err == nil {
		defer resp.Body.Close()
		data["statusCode"] = resp.StatusCode
	}
	return okResult(data)
}

// DiscoverModels queries a local OpenAI-compatible endpoint for available models.
func DiscoverModels(ctx context.Context, port int) protocol.CommandResultPayload {
	if port <= 0 {
		return errorResult("invalid port")
	}
	url := fmt.Sprintf("http://127.0.0.1:%d/v1/models", port)
	httpClient := &http.Client{Timeout: 10 * time.Second}
	resp, err := httpClient.Get(url) //nolint:gosec
	if err != nil {
		return errorResult(fmt.Sprintf("discover models on port %d: %v", port, err))
	}
	defer resp.Body.Close()

	var result map[string]interface{}
	if err := json.NewDecoder(resp.Body).Decode(&result); err != nil {
		return errorResult(fmt.Sprintf("decode models response: %v", err))
	}
	return okResult(result)
}

// ChatCompletion forwards a raw OpenAI chat-completions request body to a local
// OpenAI-compatible endpoint and returns the raw response body. The inference
// timeout is generous (120s) because benchmark/validation prompts can take a while.
// The context is honored: if the backend disconnects/cancels, the HTTP call is
// aborted so the agent's slot frees up promptly.
func ChatCompletion(ctx context.Context, port int, body json.RawMessage) protocol.CommandResultPayload {
	if port <= 0 {
		return errorResult("invalid port")
	}
	if len(body) == 0 {
		return errorResult("empty chat completion body")
	}
	url := fmt.Sprintf("http://127.0.0.1:%d/v1/chat/completions", port)
	httpClient := &http.Client{Timeout: 120 * time.Second}

	req, err := http.NewRequestWithContext(ctx, http.MethodPost, url, bytes.NewReader(body))
	if err != nil {
		return errorResult(fmt.Sprintf("build chat completion request: %v", err))
	}
	req.Header.Set("Content-Type", "application/json")

	resp, err := httpClient.Do(req)
	if err != nil {
		if ctx.Err() != nil {
			return errorResult(fmt.Sprintf("chat completion cancelled: %v", ctx.Err()))
		}
		return errorResult(fmt.Sprintf("chat completion on port %d: %v", port, err))
	}
	defer resp.Body.Close()

	if resp.StatusCode < 200 || resp.StatusCode >= 300 {
		respBody, _ := io.ReadAll(resp.Body)
		return errorResult(fmt.Sprintf("chat completion on port %d returned status %d: %s", port, resp.StatusCode, string(respBody)))
	}

	raw, err := io.ReadAll(resp.Body)
	if err != nil {
		return errorResult(fmt.Sprintf("read chat completion response: %v", err))
	}

	// Return the raw body as a JSON string so the backend can echo it verbatim.
	return okResult(string(raw))
}

// ChatCompletionStream forwards a raw OpenAI chat-completions request body to a
// local OpenAI-compatible endpoint and streams the response body incrementally:
// each raw byte chunk read from the response is passed to emit as soon as it
// arrives (raw reads with an 8KB buffer — no line buffering, so SSE streams and
// arbitrary binary bodies both work). The context is honored: if the backend
// disconnects/cancels, the HTTP call is aborted. A non-2xx status is an error
// carrying the status code and response body.
func ChatCompletionStream(ctx context.Context, port int, jsonBody string, emit func(chunk []byte) error) error {
	if port <= 0 {
		return fmt.Errorf("invalid port")
	}
	if jsonBody == "" {
		return fmt.Errorf("empty chat completion body")
	}
	url := fmt.Sprintf("http://127.0.0.1:%d/v1/chat/completions", port)
	httpClient := &http.Client{Timeout: 120 * time.Second}

	req, err := http.NewRequestWithContext(ctx, http.MethodPost, url, strings.NewReader(jsonBody))
	if err != nil {
		return fmt.Errorf("build chat completion stream request: %w", err)
	}
	req.Header.Set("Content-Type", "application/json")

	resp, err := httpClient.Do(req)
	if err != nil {
		if ctx.Err() != nil {
			return fmt.Errorf("chat completion stream cancelled: %w", ctx.Err())
		}
		return fmt.Errorf("chat completion stream on port %d: %w", port, err)
	}
	defer resp.Body.Close()

	if resp.StatusCode < 200 || resp.StatusCode >= 300 {
		respBody, _ := io.ReadAll(resp.Body)
		return fmt.Errorf("chat completion stream on port %d returned status %d: %s", port, resp.StatusCode, string(respBody))
	}

	buf := make([]byte, 8192)
	for {
		n, readErr := resp.Body.Read(buf)
		if n > 0 {
			chunk := make([]byte, n)
			copy(chunk, buf[:n])
			if err := emit(chunk); err != nil {
				return fmt.Errorf("emit chat completion stream chunk: %w", err)
			}
		}
		if readErr == io.EOF {
			return nil
		}
		if readErr != nil {
			if ctx.Err() != nil {
				return fmt.Errorf("chat completion stream cancelled: %w", ctx.Err())
			}
			return fmt.Errorf("read chat completion stream response: %w", readErr)
		}
	}
}
