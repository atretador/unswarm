package docker

import (
	"bytes"
	"context"
	"encoding/binary"
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"

	"github.com/docker/docker/api/types"
	"github.com/docker/docker/client"
	"github.com/docker/go-connections/nat"

	"unswarm/agent/internal/protocol"
)

// newFakeDockerHandler spins up an httptest server impersonating the Docker
// HTTP API and returns a Handler wired to it. This exercises the real Docker
// SDK request/response paths deterministically, with no Docker daemon.
func newFakeDockerHandler(t *testing.T, fn http.HandlerFunc) (*Handler, func()) {
	t.Helper()
	srv := httptest.NewServer(fn)
	c, err := client.NewClientWithOpts(
		client.WithHost("tcp://"+strings.TrimPrefix(srv.URL, "http://")),
		client.WithVersion("1.45"),
	)
	if err != nil {
		srv.Close()
		t.Fatalf("docker client: %v", err)
	}
	h := &Handler{client: c, socket: "fake", createBindAddress: "127.0.0.1"}
	return h, srv.Close
}

func writeJSON(t *testing.T, w http.ResponseWriter, status int, v interface{}) {
	t.Helper()
	w.Header().Set("Content-Type", "application/json")
	w.WriteHeader(status)
	if v != nil {
		if err := json.NewEncoder(w).Encode(v); err != nil {
			t.Errorf("encode response: %v", err)
		}
	}
}

// dockerLogFrame builds a Docker multiplexed stream frame (8-byte header +
// payload) as expected by stdcopy.StdCopy for non-TTY containers.
func dockerLogFrame(stream byte, payload string) []byte {
	hdr := make([]byte, 8)
	hdr[0] = stream
	binary.BigEndian.PutUint32(hdr[4:], uint32(len(payload)))
	return append(hdr, []byte(payload)...)
}

// ---------------------------------------------------------------------------
// Lifecycle methods
// ---------------------------------------------------------------------------

func TestHandlerLifecycleSuccess(t *testing.T) {
	h, closeFn := newFakeDockerHandler(t, func(w http.ResponseWriter, r *http.Request) {
		switch {
		case r.Method == http.MethodPost && strings.HasSuffix(r.URL.Path, "/start"):
			w.WriteHeader(http.StatusNoContent)
		case r.Method == http.MethodPost && strings.HasSuffix(r.URL.Path, "/stop"),
			r.Method == http.MethodPost && strings.HasSuffix(r.URL.Path, "/restart"):
			w.WriteHeader(http.StatusNoContent)
		case r.Method == http.MethodDelete:
			w.WriteHeader(http.StatusNoContent)
		default:
			http.NotFound(w, r)
		}
	})
	defer closeFn()
	ctx := context.Background()

	if res := h.StartContainer(ctx, "c1"); !res.OK {
		t.Errorf("StartContainer failed: %v", res.Error)
	}
	if res := h.StopContainer(ctx, "c1"); !res.OK {
		t.Errorf("StopContainer failed: %v", res.Error)
	}
	if res := h.RestartContainer(ctx, "c1"); !res.OK {
		t.Errorf("RestartContainer failed: %v", res.Error)
	}
	if res := h.RemoveContainer(ctx, "c1"); !res.OK {
		t.Errorf("RemoveContainer failed: %v", res.Error)
	}
}

func TestHandlerLifecycleNotFound(t *testing.T) {
	h, closeFn := newFakeDockerHandler(t, func(w http.ResponseWriter, r *http.Request) {
		writeJSON(t, w, http.StatusNotFound, map[string]string{"message": "No such container: c1"})
	})
	defer closeFn()
	ctx := context.Background()

	for _, res := range []protocol.CommandResultPayload{
		h.StartContainer(ctx, "c1"),
		h.StopContainer(ctx, "c1"),
		h.RestartContainer(ctx, "c1"),
		h.RemoveContainer(ctx, "c1"),
	} {
		if res.OK {
			t.Fatal("expected failure for a missing container")
		}
		if res.Error == nil || !strings.Contains(*res.Error, "not found") {
			t.Fatalf("expected not-found error, got %v", res.Error)
		}
	}
}

func TestSetCreateBindAddressAndCreateHostIP(t *testing.T) {
	h := &Handler{}
	if got := h.createHostIP(); got != "127.0.0.1" {
		t.Errorf("default createHostIP = %q, want 127.0.0.1", got)
	}
	// Empty/whitespace is ignored, preserving the safe default.
	h.SetCreateBindAddress("   ")
	if got := h.createHostIP(); got != "127.0.0.1" {
		t.Errorf("after empty override createHostIP = %q, want 127.0.0.1", got)
	}
	h.SetCreateBindAddress(" 10.0.0.7 ")
	if got := h.createHostIP(); got != "10.0.0.7" {
		t.Errorf("createHostIP = %q, want 10.0.0.7", got)
	}
	if got := h.Socket(); got != "" {
		t.Errorf("Socket() = %q, want empty", got)
	}
}

// ---------------------------------------------------------------------------
// InspectContainer
// ---------------------------------------------------------------------------

func TestInspectContainerSuccess(t *testing.T) {
	h, closeFn := newFakeDockerHandler(t, func(w http.ResponseWriter, r *http.Request) {
		writeJSON(t, w, http.StatusOK, map[string]interface{}{
			"Id":   "abcdef1234567890",
			"Name": "/mycontainer",
			"State": map[string]interface{}{
				"Status":     "running",
				"Running":    true,
				"StartedAt":  "2020-01-01T00:00:00Z",
				"FinishedAt": "0001-01-01T00:00:00Z",
				"Health":     map[string]interface{}{"Status": "healthy"},
			},
			"Config": map[string]interface{}{"Image": "alpine:latest"},
			"NetworkSettings": map[string]interface{}{
				"Ports": map[string]interface{}{
					"80/tcp": []map[string]string{{"HostIp": "0.0.0.0", "HostPort": "8080"}},
				},
			},
		})
	})
	defer closeFn()

	res := h.InspectContainer(context.Background(), "mycontainer")
	if !res.OK {
		t.Fatalf("InspectContainer failed: %v", res.Error)
	}
	data := res.Data.(map[string]interface{})
	if data["id"] != "abcdef1234567890" {
		t.Errorf("id = %v", data["id"])
	}
	if data["name"] != "mycontainer" {
		t.Errorf("name = %v, want mycontainer", data["name"])
	}
	if data["health"] != "healthy" {
		t.Errorf("health = %v, want healthy", data["health"])
	}
	if _, ok := data["ports"]; !ok {
		t.Error("expected ports in inspect data")
	}
}

func TestInspectContainerError(t *testing.T) {
	h, closeFn := newFakeDockerHandler(t, func(w http.ResponseWriter, r *http.Request) {
		writeJSON(t, w, http.StatusNotFound, map[string]string{"message": "No such container"})
	})
	defer closeFn()

	res := h.InspectContainer(context.Background(), "missing")
	if res.OK {
		t.Fatal("expected failure")
	}
	if res.Error == nil || !strings.Contains(*res.Error, "not found") {
		t.Errorf("expected not-found, got %v", res.Error)
	}
}

// ---------------------------------------------------------------------------
// ListContainers
// ---------------------------------------------------------------------------

func TestListContainersSuccess(t *testing.T) {
	h, closeFn := newFakeDockerHandler(t, func(w http.ResponseWriter, r *http.Request) {
		writeJSON(t, w, http.StatusOK, []types.Container{
			{
				ID:     "abcdef1234567890",
				Names:  []string{"/mycontainer"},
				Image:  "alpine:latest",
				State:  "running",
				Status: "Up 5 minutes",
				Ports: []types.Port{
					{PrivatePort: 80, PublicPort: 8080, Type: "tcp"},
				},
			},
		})
	})
	defer closeFn()

	res := h.ListContainers(context.Background())
	if !res.OK {
		t.Fatalf("ListContainers failed: %v", res.Error)
	}
	data := res.Data.(map[string]interface{})
	list := data["containers"].([]map[string]interface{})
	if len(list) != 1 {
		t.Fatalf("got %d containers, want 1", len(list))
	}
	if list[0]["name"] != "mycontainer" {
		t.Errorf("name = %v", list[0]["name"])
	}
	if list[0]["port"] != 8080 {
		t.Errorf("port = %v, want 8080", list[0]["port"])
	}
}

func TestListContainersFallsBackToAll(t *testing.T) {
	var calls int
	h, closeFn := newFakeDockerHandler(t, func(w http.ResponseWriter, r *http.Request) {
		calls++
		// The first (label-filtered) call returns nothing, forcing fallback.
		if strings.Contains(r.URL.RawQuery, "label") {
			writeJSON(t, w, http.StatusOK, []types.Container{})
			return
		}
		writeJSON(t, w, http.StatusOK, []types.Container{{ID: "id1", Names: []string{"/x"}}})
	})
	defer closeFn()

	res := h.ListContainers(context.Background())
	if !res.OK {
		t.Fatalf("ListContainers failed: %v", res.Error)
	}
	if calls < 2 {
		t.Fatalf("expected fallback list call, got %d calls", calls)
	}
}

func TestListContainersError(t *testing.T) {
	h, closeFn := newFakeDockerHandler(t, func(w http.ResponseWriter, r *http.Request) {
		writeJSON(t, w, http.StatusInternalServerError, map[string]string{"message": "boom"})
	})
	defer closeFn()

	res := h.ListContainers(context.Background())
	if res.OK {
		t.Fatal("expected error result")
	}
	if res.Error == nil || !strings.Contains(*res.Error, "list containers") {
		t.Errorf("unexpected error: %v", res.Error)
	}
}

// ---------------------------------------------------------------------------
// CreateContainer
// ---------------------------------------------------------------------------

func TestCreateContainerSuccess(t *testing.T) {
	h, closeFn := newFakeDockerHandler(t, func(w http.ResponseWriter, r *http.Request) {
		path := r.URL.Path
		switch {
		case strings.Contains(path, "/images/create"):
			w.WriteHeader(http.StatusOK)
			_, _ = w.Write([]byte(`{"status":"Pull complete"}` + "\n"))
		case strings.HasSuffix(path, "/containers/create"):
			writeJSON(t, w, http.StatusCreated, map[string]interface{}{
				"Id":       "0123456789abcdef",
				"Warnings": []string{},
			})
		case strings.HasSuffix(path, "/start"):
			w.WriteHeader(http.StatusNoContent)
		case strings.HasSuffix(path, "/json"):
			writeJSON(t, w, http.StatusOK, map[string]interface{}{
				"NetworkSettings": map[string]interface{}{
					"Ports": map[string]interface{}{
						"8080/tcp": []map[string]string{{"HostIp": "127.0.0.1", "HostPort": "18080"}},
					},
				},
			})
		default:
			http.NotFound(w, r)
		}
	})
	defer closeFn()

	res := h.CreateContainer(context.Background(), protocol.CreateContainerPayload{
		Image:         "alpine:latest",
		ContainerName: "newcontainer",
		ContainerPort: 8080,
		HostPort:      18080,
		Volumes:       []protocol.VolumeMount{{Host: "/data", Container: "/data", Readonly: true}},
		Env:           map[string]string{"A": "1"},
		Devices:       []string{"/dev/nvidia0"},
		ShmSizeMb:     128,
		IpcMode:       "host",
		RestartPolicy: "always",
		ServerArgs:    []string{"--flag"},
		NetworkMode:   "bridge",
	})
	if !res.OK {
		t.Fatalf("CreateContainer failed: %v", res.Error)
	}
	data := res.Data.(map[string]interface{})
	if data["mappedPort"] != 18080 {
		t.Errorf("mappedPort = %v, want 18080", data["mappedPort"])
	}
}

func TestCreateContainerPullError(t *testing.T) {
	h, closeFn := newFakeDockerHandler(t, func(w http.ResponseWriter, r *http.Request) {
		if strings.Contains(r.URL.Path, "/images/create") {
			writeJSON(t, w, http.StatusInternalServerError, map[string]string{"message": "pull failed"})
			return
		}
		http.NotFound(w, r)
	})
	defer closeFn()

	res := h.CreateContainer(context.Background(), protocol.CreateContainerPayload{Image: "x", ContainerName: "y"})
	if res.OK {
		t.Fatal("expected pull error")
	}
	if res.Error == nil || !strings.Contains(*res.Error, "pull image") {
		t.Errorf("unexpected error: %v", res.Error)
	}
}

func TestCreateContainerCreateError(t *testing.T) {
	h, closeFn := newFakeDockerHandler(t, func(w http.ResponseWriter, r *http.Request) {
		switch {
		case strings.Contains(r.URL.Path, "/images/create"):
			w.WriteHeader(http.StatusOK)
		case strings.HasSuffix(r.URL.Path, "/containers/create"):
			writeJSON(t, w, http.StatusInternalServerError, map[string]string{"message": "create failed"})
		default:
			http.NotFound(w, r)
		}
	})
	defer closeFn()

	res := h.CreateContainer(context.Background(), protocol.CreateContainerPayload{Image: "x", ContainerName: "y"})
	if res.OK {
		t.Fatal("expected create error")
	}
	if res.Error == nil || !strings.Contains(*res.Error, "create container") {
		t.Errorf("unexpected error: %v", res.Error)
	}
}

// ---------------------------------------------------------------------------
// GetContainerLogs
// ---------------------------------------------------------------------------

func TestGetContainerLogsNonTTY(t *testing.T) {
	h, closeFn := newFakeDockerHandler(t, func(w http.ResponseWriter, r *http.Request) {
		switch {
		case strings.HasSuffix(r.URL.Path, "/json"):
			writeJSON(t, w, http.StatusOK, map[string]interface{}{
				"Config": map[string]interface{}{"Tty": false},
			})
		case strings.HasSuffix(r.URL.Path, "/logs"):
			var buf bytes.Buffer
			buf.Write(dockerLogFrame(1, "line one\nline two\n"))
			w.WriteHeader(http.StatusOK)
			_, _ = w.Write(buf.Bytes())
		default:
			http.NotFound(w, r)
		}
	})
	defer closeFn()

	res := h.GetContainerLogs(context.Background(), "c1", 50)
	if !res.OK {
		t.Fatalf("GetContainerLogs failed: %v", res.Error)
	}
	data := res.Data.(map[string]interface{})
	lines := data["logs"].([]string)
	if len(lines) != 2 || lines[0] != "line one" {
		t.Errorf("logs = %v, want [line one line two]", lines)
	}
}

func TestGetContainerLogsTTY(t *testing.T) {
	h, closeFn := newFakeDockerHandler(t, func(w http.ResponseWriter, r *http.Request) {
		switch {
		case strings.HasSuffix(r.URL.Path, "/json"):
			writeJSON(t, w, http.StatusOK, map[string]interface{}{
				"Config": map[string]interface{}{"Tty": true},
			})
		case strings.HasSuffix(r.URL.Path, "/logs"):
			_, _ = w.Write([]byte("tty line\n"))
		default:
			http.NotFound(w, r)
		}
	})
	defer closeFn()

	res := h.GetContainerLogs(context.Background(), "c1", 0)
	if !res.OK {
		t.Fatalf("GetContainerLogs failed: %v", res.Error)
	}
	lines := res.Data.(map[string]interface{})["logs"].([]string)
	if len(lines) != 1 || lines[0] != "tty line" {
		t.Errorf("logs = %v, want [tty line]", lines)
	}
}

func TestGetContainerLogsError(t *testing.T) {
	h, closeFn := newFakeDockerHandler(t, func(w http.ResponseWriter, r *http.Request) {
		writeJSON(t, w, http.StatusNotFound, map[string]string{"message": "No such container"})
	})
	defer closeFn()

	res := h.GetContainerLogs(context.Background(), "missing", 10)
	if res.OK {
		t.Fatal("expected error")
	}
}

// ---------------------------------------------------------------------------
// ListContainerStatuses / containerMemoryMb
// ---------------------------------------------------------------------------

func TestListContainerStatuses(t *testing.T) {
	h, closeFn := newFakeDockerHandler(t, func(w http.ResponseWriter, r *http.Request) {
		path := r.URL.Path
		switch {
		case strings.HasSuffix(path, "/containers/json"):
			writeJSON(t, w, http.StatusOK, []types.Container{
				{
					ID:     "abcdef1234567890",
					Names:  []string{"/svc"},
					State:  "running",
					Ports:  []types.Port{{PrivatePort: 8000, PublicPort: 9000, Type: "tcp"}},
					Status: "Up",
				},
			})
		case strings.HasSuffix(path, "/json"):
			writeJSON(t, w, http.StatusOK, map[string]interface{}{
				"Config": map[string]interface{}{"Tty": false},
				"State": map[string]interface{}{
					"Status":    "running",
					"Running":   true,
					"StartedAt": "2020-01-01T00:00:00Z",
				},
			})
		case strings.Contains(path, "/stats"):
			writeJSON(t, w, http.StatusOK, map[string]interface{}{
				"memory_stats": map[string]interface{}{"usage": 13000000},
			})
		default:
			http.NotFound(w, r)
		}
	})
	defer closeFn()

	got := h.ListContainerStatuses(context.Background())
	if len(got) != 1 {
		t.Fatalf("got %d statuses, want 1", len(got))
	}
	if got[0].ID != "abcdef123456" {
		t.Errorf("id = %q", got[0].ID)
	}
	if got[0].Name != "svc" {
		t.Errorf("name = %q", got[0].Name)
	}
	if got[0].Port != 9000 {
		t.Errorf("port = %d, want 9000", got[0].Port)
	}
	if got[0].Memory != "12 MB" {
		t.Errorf("memory = %q, want 12 MB", got[0].Memory)
	}
	if got[0].Uptime == "" {
		t.Error("expected non-empty uptime")
	}
}

func TestListContainerStatusesListError(t *testing.T) {
	h, closeFn := newFakeDockerHandler(t, func(w http.ResponseWriter, r *http.Request) {
		writeJSON(t, w, http.StatusInternalServerError, map[string]string{"message": "boom"})
	})
	defer closeFn()

	if got := h.ListContainerStatuses(context.Background()); got != nil {
		t.Errorf("expected nil on list error, got %v", got)
	}
}

// Stats failures are skipped, not fatal: the container is still reported.
func TestListContainerStatusesStatsSkipped(t *testing.T) {
	h, closeFn := newFakeDockerHandler(t, func(w http.ResponseWriter, r *http.Request) {
		path := r.URL.Path
		switch {
		case strings.HasSuffix(path, "/containers/json"):
			writeJSON(t, w, http.StatusOK, []types.Container{{ID: "id1", Names: []string{"/x"}, State: "running"}})
		case strings.Contains(path, "/stats"):
			writeJSON(t, w, http.StatusInternalServerError, map[string]string{"message": "stats failed"})
		default: // inspect returns nothing useful
			writeJSON(t, w, http.StatusInternalServerError, map[string]string{"message": "nope"})
		}
	})
	defer closeFn()

	got := h.ListContainerStatuses(context.Background())
	if len(got) != 1 {
		t.Fatalf("got %d statuses, want 1", len(got))
	}
	if got[0].Memory != "" {
		t.Errorf("memory should be empty when stats fail, got %q", got[0].Memory)
	}
}

func TestContainerMemoryMbDecodeError(t *testing.T) {
	h, closeFn := newFakeDockerHandler(t, func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		w.WriteHeader(http.StatusOK)
		_, _ = w.Write([]byte("not-json"))
	})
	defer closeFn()

	if _, err := h.containerMemoryMb(context.Background(), "id1"); err == nil {
		t.Fatal("expected decode error")
	}
}

func TestHandlerNewAndSocket(t *testing.T) {
	// New must construct a client without contacting a daemon.
	h, err := New("tcp://127.0.0.1:1")
	if err != nil {
		t.Fatalf("New: %v", err)
	}
	if h.Socket() != "tcp://127.0.0.1:1" {
		t.Errorf("Socket() = %q", h.Socket())
	}
	if got := h.createHostIP(); got != "127.0.0.1" {
		t.Errorf("createHostIP = %q, want 127.0.0.1", got)
	}
}

// ---------------------------------------------------------------------------
// nat port helpers
// ---------------------------------------------------------------------------

func TestNatPortNumber(t *testing.T) {
	if got := natPortNumber("80/tcp"); got != "80" {
		t.Errorf("natPortNumber(80/tcp) = %q, want 80", got)
	}
	if got := natPortNumber("80"); got != "80" {
		t.Errorf("natPortNumber(80) = %q, want 80", got)
	}
}

func TestNatPortLess(t *testing.T) {
	tests := []struct {
		name string
		a, b string
		want bool
	}{
		{"numeric smaller first", "80/tcp", "443/tcp", true},
		{"numeric larger second", "443/tcp", "80/tcp", false},
		{"parsable before unparsable", "80/tcp", "abc/tcp", true},
		{"unparsable after parsable", "abc/tcp", "80/tcp", false},
		{"both unparsable lexical", "abc", "abd", true},
		{"numeric equal falls back to lexical", "80/tcp", "80/udp", true},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			if got := natPortLess(nat.Port(tt.a), nat.Port(tt.b)); got != tt.want {
				t.Errorf("natPortLess(%q, %q) = %v, want %v", tt.a, tt.b, got, tt.want)
			}
		})
	}
}
