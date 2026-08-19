package docker

import (
	"context"
	"encoding/json"
	"fmt"
	"net"
	"net/http"
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
