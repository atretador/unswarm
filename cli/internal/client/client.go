package client

import (
	"bytes"
	"context"
	"crypto/tls"
	"encoding/json"
	"fmt"
	"io"
	"math"
	"mime/multipart"
	"net"
	"net/http"
	"net/url"
	"os"
	"path/filepath"
	"strings"
	"time"
)

const (
	maxRetries        = 3
	maxBodyDisplay    = 4096
	maxRetryAfter     = 10 * time.Second
	defaultTimeout    = 30 * time.Second
	backoffBase       = 1 * time.Second
)

// Response wraps http.Response with parsed body
type Response struct {
	StatusCode int
	Body       []byte
	Headers    http.Header
	APIError   *APIError // populated by Do() when StatusCode >= 400
}

// APIError represents a structured API error
type APIError struct {
	ErrCode  string `json:"error"`
	Message  string `json:"message"`
	Status   *int   `json:"status"`
	Hint     string `json:"hint"`
	ExitCode int    `json:"exitCode"`
}

// Error implements the error interface for APIError
func (e *APIError) Error() string {
	if e.Message != "" {
		return e.Message
	}
	return e.ErrCode
}

// Client is the HTTP client
type Client struct {
	httpClient *http.Client
	apiKey     string
	baseURL    string
	outputFmt  string
	noColor    bool
	insecure   bool
}

// Option pattern for flags
type Option func(*Client)

// WithAPIKey sets the API key
func WithAPIKey(key string) Option {
	return func(c *Client) {
		c.apiKey = key
	}
}

// WithOutput sets the output format
func WithOutput(f string) Option {
	return func(c *Client) {
		c.outputFmt = f
	}
}

// WithNoColor sets the no-color flag
func WithNoColor(v bool) Option {
	return func(c *Client) {
		c.noColor = v
	}
}

// WithTimeout sets the client timeout
func WithTimeout(d time.Duration) Option {
	return func(c *Client) {
		c.httpClient.Timeout = d
	}
}

// WithInsecure allows plaintext HTTP to non-loopback
func WithInsecure(v bool) Option {
	return func(c *Client) {
		c.insecure = v
	}
}

// New creates a client from config + options
func New(cfg *Config, opts ...Option) *Client {
	c := &Client{
		baseURL:   cfg.BaseURL,
		outputFmt: cfg.OutputFmt,
		noColor:   !cfg.Color,
		httpClient: &http.Client{
			Timeout: defaultTimeout,
			CheckRedirect: func(req *http.Request, via []*http.Request) error {
				// Custom redirect policy handled in Do()
				return http.ErrUseLastResponse
			},
		},
	}

	for _, opt := range opts {
		opt(c)
	}

	return c
}

// isLoopback checks if a host is a loopback address
func isLoopback(host string) bool {
	// Strip port
	h, _, err := net.SplitHostPort(host)
	if err != nil {
		h = host
	}

	// localhost
	if h == "localhost" {
		return true
	}

	// Parse IP
	ip := net.ParseIP(h)
	if ip == nil {
		return false
	}

	return ip.IsLoopback()
}

// validateURLSafety checks that plaintext HTTP is only used for loopback
func (c *Client) validateURLSafety(rawURL string) error {
	u, err := url.Parse(rawURL)
	if err != nil {
		return fmt.Errorf("invalid URL: %w", err)
	}

	if u.Scheme == "http" && !isLoopback(u.Host) && !c.insecure {
		return fmt.Errorf("refusing plaintext HTTP to non-loopback host %s: use --insecure to override or configure HTTPS", u.Host)
	}

	return nil
}

// shouldRetry determines if the request should be retried
func shouldRetry(method string, statusCode int, err error) bool {
	// Never retry mutations
	switch strings.ToUpper(method) {
	case "POST", "PUT", "DELETE", "PATCH":
		return false
	}

	// Transport error
	if err != nil {
		return true
	}

	// Retry on 502, 503, 504
	switch statusCode {
	case 502, 503, 504:
		return true
	}

	return false
}

// parseRetryAfter parses Retry-After header (integer seconds or HTTP-date)
func parseRetryAfter(val string) time.Duration {
	if val == "" {
		return 0
	}

	// Try integer seconds
	if seconds, err := fmt.Sscanf(val, "%d"); err == nil && seconds > 0 {
		d := time.Duration(seconds) * time.Second
		if d > maxRetryAfter {
			return maxRetryAfter
		}
		return d
	}

	// Try HTTP-date
	if t, err := time.Parse(time.RFC1123, val); err == nil {
		d := time.Until(t)
		if d < 0 {
			return 0
		}
		if d > maxRetryAfter {
			return maxRetryAfter
		}
		return d
	}

	return 0
}

// cloneRequest clones an http.Request for retry
func cloneRequest(ctx context.Context, req *http.Request) (*http.Request, error) {
	newReq := req.Clone(ctx)
	newReq.Body = nil
	if req.Body != nil {
		body, err := io.ReadAll(req.Body)
		if err != nil {
			return nil, err
		}
		newReq.Body = io.NopCloser(strings.NewReader(string(body)))
		newReq.GetBody = func() (io.ReadCloser, error) {
			return io.NopCloser(strings.NewReader(string(body))), nil
		}
	}
	return newReq, nil
}

// Do executes an HTTP request with retry, redirect validation, error handling
func (c *Client) Do(ctx context.Context, method, path string, body any) (*Response, error) {
	rawURL := c.baseURL + path

	// Validate URL safety
	if err := c.validateURLSafety(rawURL); err != nil {
		return nil, fmt.Errorf("%w", ErrUnsafeHTTP)
	}

	var bodyReader io.Reader
	if body != nil {
		jsonBytes, err := json.Marshal(body)
		if err != nil {
			return nil, fmt.Errorf("failed to marshal request body: %w", err)
		}
		bodyReader = strings.NewReader(string(jsonBytes))
	}

	req, err := http.NewRequestWithContext(ctx, method, rawURL, bodyReader)
	if err != nil {
		return nil, fmt.Errorf("failed to create request: %w", err)
	}

	req.Header.Set("Content-Type", "application/json")
	req.Header.Set("Accept", "application/json")

	if c.apiKey != "" {
		req.Header.Set("X-Api-Key", c.apiKey)
	}

	// Redirect policy: follow within same host+port, allow HTTP→HTTPS, never HTTPS→HTTP
	c.httpClient.CheckRedirect = func(req *http.Request, via []*http.Request) error {
		if len(via) >= 10 {
			return fmt.Errorf("too many redirects")
		}

		prev := via[len(via)-1]
		prevURL := prev.URL
		newURL := req.URL

		// Same host+port
		if prevURL.Hostname() != newURL.Hostname() || prevURL.Port() != newURL.Port() {
			return fmt.Errorf("redirect to different host blocked: %s -> %s", prevURL.Host, newURL.Host)
		}

		// Never HTTPS→HTTP
		if prevURL.Scheme == "https" && newURL.Scheme == "http" {
			return fmt.Errorf("redirect from HTTPS to HTTP blocked")
		}

		// Forward API key only after same-host validation (already validated above)
		if c.apiKey != "" {
			req.Header.Set("X-Api-Key", c.apiKey)
		}

		return nil
	}

	// If insecure, allow insecure TLS
	if c.insecure {
		c.httpClient.Transport = &http.Transport{
			TLSClientConfig: &tls.Config{InsecureSkipVerify: true},
		}
	}

	var lastErr error
	var lastResp *Response

	for attempt := 0; attempt <= maxRetries; attempt++ {
		if attempt > 0 {
			// Calculate backoff
			backoff := backoffBase * time.Duration(math.Pow(2, float64(attempt-1)))

			// Check for Retry-After header
			if lastResp != nil {
				if retryAfter := lastResp.Headers.Get("Retry-After"); retryAfter != "" {
					if parsed := parseRetryAfter(retryAfter); parsed > 0 {
						backoff = parsed
					}
				}
			}

			// Check deadline
			deadline, ok := ctx.Deadline()
			if ok {
				remaining := time.Until(deadline)
				if remaining < backoff {
					break
				}
			}

			select {
			case <-ctx.Done():
				return nil, ctx.Err()
			case <-time.After(backoff):
			}

			// Clone request for retry
			req, err = cloneRequest(ctx, req)
			if err != nil {
				return nil, fmt.Errorf("failed to clone request: %w", err)
			}
		}

		resp, err := c.httpClient.Do(req)
		if err != nil {
			lastErr = err
			if shouldRetry(method, 0, err) {
				continue
			}
			return nil, &TransportError{Err: err}
		}

		var bodyBytes []byte
		if resp.StatusCode != http.StatusNoContent {
			bodyBytes, err = io.ReadAll(resp.Body)
			resp.Body.Close()
			if err != nil {
				return nil, &TransportError{Err: fmt.Errorf("failed to read response body: %w", err)}
			}
		}

		wrappedResp := &Response{
			StatusCode: resp.StatusCode,
			Body:       bodyBytes,
			Headers:    resp.Header,
		}

		// Populate APIError for error responses so callers can use DoOrError
		if resp.StatusCode >= 400 {
			wrappedResp.APIError = ParseError(wrappedResp)
		}

		lastResp = wrappedResp
		lastErr = nil

		if shouldRetry(method, resp.StatusCode, nil) {
			continue
		}

		return wrappedResp, nil
	}

	// If we exhausted retries due to transport errors
	if lastErr != nil {
		return nil, &TransportError{Err: lastErr}
	}

	return lastResp, nil
}

// DoOrError executes an HTTP request and returns (body, nil) on success,
// or (nil, *APIError) when the server returns status >= 400.
// Transport-level errors are returned as *TransportError.
func (c *Client) DoOrError(ctx context.Context, method, path string, body any) ([]byte, error) {
	resp, err := c.Do(ctx, method, path, body)
	if err != nil {
		return nil, err
	}
	if resp.APIError != nil {
		return nil, resp.APIError
	}
	return resp.Body, nil
}

// ParseError parses an API error from a response
func ParseError(resp *Response) *APIError {
	if resp == nil {
		return &APIError{
			ErrCode:  "unknown_error",
			Message:  "no response received",
			ExitCode: 1,
		}
	}

	var apiErr APIError
	if len(resp.Body) > 0 {
		if err := json.Unmarshal(resp.Body, &apiErr); err == nil && apiErr.ErrCode != "" {
			apiErr.Status = &resp.StatusCode
			return &apiErr
		}
	}

	// Fallback error
	status := resp.StatusCode
	return &APIError{
		ErrCode:  "api_error",
		Message:  fmt.Sprintf("HTTP %d: %s", resp.StatusCode, truncateBody(resp.Body)),
		Status:   &status,
		ExitCode: 1,
	}
}

func truncateBody(body []byte) string {
	if len(body) > maxBodyDisplay {
		return string(body[:maxBodyDisplay]) + "...(truncated)"
	}
	return string(body)
}

// IsTransportError checks if an error is a transport error
func IsTransportError(err error) bool {
	var te *TransportError
	return errorAs(err, &te)
}

func errorAs(err error, target interface{}) bool {
	switch t := target.(type) {
	case **TransportError:
		if te, ok := err.(*TransportError); ok {
			*t = te
			return true
		}
	}
	return false
}

// DoMultipart executes an HTTP request with a multipart file upload.
// fieldName is the form field name for the file, filePath is the local path.
func (c *Client) DoMultipart(ctx context.Context, method, path, fieldName, filePath string) (*Response, error) {
	rawURL := c.baseURL + path

	if err := c.validateURLSafety(rawURL); err != nil {
		return nil, fmt.Errorf("%w", ErrUnsafeHTTP)
	}

	file, err := os.Open(filePath)
	if err != nil {
		return nil, fmt.Errorf("failed to open file: %w", err)
	}
	defer file.Close()

	var buf bytes.Buffer
	writer := multipart.NewWriter(&buf)
	part, err := writer.CreateFormFile(fieldName, filepath.Base(filePath))
	if err != nil {
		return nil, fmt.Errorf("failed to create form file: %w", err)
	}
	if _, err := io.Copy(part, file); err != nil {
		return nil, fmt.Errorf("failed to write file to form: %w", err)
	}
	if err := writer.Close(); err != nil {
		return nil, fmt.Errorf("failed to close multipart writer: %w", err)
	}

	req, err := http.NewRequestWithContext(ctx, method, rawURL, &buf)
	if err != nil {
		return nil, fmt.Errorf("failed to create request: %w", err)
	}

	req.Header.Set("Content-Type", writer.FormDataContentType())
	req.Header.Set("Accept", "application/json")

	if c.apiKey != "" {
		req.Header.Set("X-Api-Key", c.apiKey)
	}

	resp, err := c.httpClient.Do(req)
	if err != nil {
		return nil, &TransportError{Err: err}
	}

	var bodyBytes []byte
	if resp.StatusCode != http.StatusNoContent {
		bodyBytes, err = io.ReadAll(io.LimitReader(resp.Body, maxBodyDisplay+1))
		resp.Body.Close()
		if err != nil {
			return nil, &TransportError{Err: fmt.Errorf("failed to read response body: %w", err)}
		}
		if len(bodyBytes) > maxBodyDisplay {
			bodyBytes = bodyBytes[:maxBodyDisplay]
		}
	}

	return &Response{
		StatusCode: resp.StatusCode,
		Body:       bodyBytes,
		Headers:    resp.Header,
	}, nil
}

// SSEEvent represents a single Server-Sent Event.
type SSEEvent struct {
	Event string // event type (from "event:" field)
	Data  string // payload (from "data:" field, multi-line concatenated with \n)
	ID    string // event ID (from "id:" field)
	Retry int    // reconnection time in ms (from "retry:" field), 0 if not set
}

// SSEReader wraps an http.Response.Body to read SSE events incrementally.
type SSEReader struct {
	resp     *http.Response
	body     io.ReadCloser
	buf      []byte
	event    *SSEEvent // current event being parsed
}

// ReadEvent reads the next SSE event from the stream.
// Returns the parsed event or io.EOF when the stream ends.
func (r *SSEReader) ReadEvent() (*SSEEvent, error) {
	for {
		// Read more data from the body into the buffer
		if len(r.buf) == 0 {
			chunk := make([]byte, 4096)
			n, err := r.body.Read(chunk)
			if n > 0 {
				r.buf = append(r.buf, chunk[:n]...)
			}
			if err == io.EOF && n == 0 {
				// If we have a partial event, flush it
				if r.event != nil {
					ev := r.event
					r.event = nil
					return ev, nil
				}
				return nil, io.EOF
			}
			if err != nil && err != io.EOF {
				return nil, err
			}
		}

		// Find the next newline
		idx := bytes.IndexByte(r.buf, '\n')
		if idx == -1 {
			// No complete line yet; try reading more if buffer is getting large
			if len(r.buf) > 64*1024 {
				return nil, fmt.Errorf("SSE line too long")
			}
			continue
		}

		// Extract the line (strip \r\n or \n)
		line := string(r.buf[:idx])
		if idx+1 <= len(r.buf) {
			r.buf = r.buf[idx+1:]
		} else {
			r.buf = r.buf[:0]
		}
		// Strip trailing \r if present
		line = strings.TrimRight(line, "\r")

		// Empty line = event boundary
		if line == "" {
			if r.event != nil {
				ev := r.event
				r.event = nil
				// Check for [DONE] sentinel
				if ev.Data == "[DONE]" {
					return ev, io.EOF
				}
				return ev, nil
			}
			continue
		}

		// Comment line — skip
		if strings.HasPrefix(line, ":") {
			continue
		}

		// Initialize event if needed
		if r.event == nil {
			r.event = &SSEEvent{}
		}

		// Parse field
		if strings.HasPrefix(line, "event:") {
			r.event.Event = strings.TrimSpace(line[6:])
		} else if strings.HasPrefix(line, "data:") {
			data := strings.TrimSpace(line[5:])
			if r.event.Data != "" {
				r.event.Data += "\n" + data
			} else {
				r.event.Data = data
			}
		} else if strings.HasPrefix(line, "id:") {
			r.event.ID = strings.TrimSpace(line[3:])
		} else if strings.HasPrefix(line, "retry:") {
			val := strings.TrimSpace(line[6:])
			var ms int
			if _, err := fmt.Sscanf(val, "%d", &ms); err == nil {
				r.event.Retry = ms
			}
		}
	}
}

// Response returns the underlying HTTP response (headers, status code).
func (r *SSEReader) Response() *http.Response {
	return r.resp
}

// Close closes the response body.
func (r *SSEReader) Close() error {
	return r.body.Close()
}

// DoSSE makes an HTTP request and returns an SSEReader for streaming responses.
// The caller MUST call Close() on the returned SSEReader when done.
// Unlike Do, this does NOT buffer the response body.
func (c *Client) DoSSE(ctx context.Context, method, path string, body any) (*SSEReader, error) {
	rawURL := c.baseURL + path

	// Validate URL safety
	if err := c.validateURLSafety(rawURL); err != nil {
		return nil, fmt.Errorf("%w", ErrUnsafeHTTP)
	}

	var bodyReader io.Reader
	if body != nil {
		jsonBytes, err := json.Marshal(body)
		if err != nil {
			return nil, fmt.Errorf("failed to marshal request body: %w", err)
		}
		bodyReader = strings.NewReader(string(jsonBytes))
	}

	req, err := http.NewRequestWithContext(ctx, method, rawURL, bodyReader)
	if err != nil {
		return nil, fmt.Errorf("failed to create request: %w", err)
	}

	req.Header.Set("Content-Type", "application/json")
	req.Header.Set("Accept", "text/event-stream")

	if c.apiKey != "" {
		req.Header.Set("X-Api-Key", c.apiKey)
	}

	// Redirect policy: follow within same host+port, allow HTTP→HTTPS, never HTTPS→HTTP
	c.httpClient.CheckRedirect = func(req *http.Request, via []*http.Request) error {
		if len(via) >= 10 {
			return fmt.Errorf("too many redirects")
		}
		prev := via[len(via)-1]
		prevURL := prev.URL
		newURL := req.URL
		if prevURL.Hostname() != newURL.Hostname() || prevURL.Port() != newURL.Port() {
			return fmt.Errorf("redirect to different host blocked: %s -> %s", prevURL.Host, newURL.Host)
		}
		if prevURL.Scheme == "https" && newURL.Scheme == "http" {
			return fmt.Errorf("redirect from HTTPS to HTTP blocked")
		}
		if c.apiKey != "" {
			req.Header.Set("X-Api-Key", c.apiKey)
		}
		return nil
	}

	if c.insecure {
		c.httpClient.Transport = &http.Transport{
			TLSClientConfig: &tls.Config{InsecureSkipVerify: true},
		}
	}

	resp, err := c.httpClient.Do(req)
	if err != nil {
		return nil, &TransportError{Err: err}
	}

	// Check for error responses (4xx, 5xx)
	if resp.StatusCode >= 400 {
		// Read the body to parse error, then close
		var bodyBytes []byte
		bodyBytes, err = io.ReadAll(resp.Body)
		resp.Body.Close()
		if err != nil {
			return nil, &TransportError{Err: fmt.Errorf("failed to read error response: %w", err)}
		}
		wrappedResp := &Response{
			StatusCode: resp.StatusCode,
			Body:       bodyBytes,
			Headers:    resp.Header,
		}
		return nil, ParseError(wrappedResp)
	}

	return &SSEReader{
		resp: resp,
		body: resp.Body,
		buf:  make([]byte, 0, 4096),
	}, nil
}

// DoText executes an HTTP request expecting a text/plain response (no JSON parsing).
func (c *Client) DoText(ctx context.Context, method, path string) (*Response, error) {
	rawURL := c.baseURL + path

	if err := c.validateURLSafety(rawURL); err != nil {
		return nil, fmt.Errorf("%w", ErrUnsafeHTTP)
	}

	req, err := http.NewRequestWithContext(ctx, method, rawURL, nil)
	if err != nil {
		return nil, fmt.Errorf("failed to create request: %w", err)
	}

	req.Header.Set("Accept", "*/*")

	if c.apiKey != "" {
		req.Header.Set("X-Api-Key", c.apiKey)
	}

	resp, err := c.httpClient.Do(req)
	if err != nil {
		return nil, &TransportError{Err: err}
	}

	var bodyBytes []byte
	if resp.StatusCode != http.StatusNoContent {
		bodyBytes, err = io.ReadAll(resp.Body)
		resp.Body.Close()
		if err != nil {
			return nil, &TransportError{Err: fmt.Errorf("failed to read response body: %w", err)}
		}
	}

	return &Response{
		StatusCode: resp.StatusCode,
		Body:       bodyBytes,
		Headers:    resp.Header,
	}, nil
}
