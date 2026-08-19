package dispatch

import (
	"testing"

	"unswarm/agent/internal/protocol"
)

func TestRouterRoutesToRegisteredHandler(t *testing.T) {
	r := NewRouter()

	called := false
	r.RegisterMessage("inference_request", func(env protocol.Envelope) *protocol.Envelope {
		called = true
		resp := protocol.MustEnvelope("inference_response", env.ID, nil, map[string]string{"ok": "true"})
		return &resp
	})

	env := protocol.MustEnvelope("inference_request", strPtr("inf-1"), nil, nil)
	resp, handled := r.Route(env)
	if !handled {
		t.Fatal("Route() handled = false, want true")
	}
	if !called {
		t.Error("registered handler was not called")
	}
	if resp == nil {
		t.Fatal("Route() returned nil response")
	}
	if resp.Type != "inference_response" {
		t.Errorf("response type = %q, want inference_response", resp.Type)
	}
	if resp.ID == nil || *resp.ID != "inf-1" {
		t.Errorf("response id = %v, want inf-1", resp.ID)
	}
}

func TestRouterUnregisteredType(t *testing.T) {
	r := NewRouter()
	env := protocol.MustEnvelope("unknown_type", nil, nil, nil)
	resp, handled := r.Route(env)
	if handled {
		t.Error("Route() handled = true for unregistered type, want false")
	}
	if resp != nil {
		t.Errorf("Route() returned non-nil response %v for unregistered type", resp)
	}
}

func TestRouterNilResponseAllowed(t *testing.T) {
	r := NewRouter()
	r.RegisterMessage("fire_and_forget", func(env protocol.Envelope) *protocol.Envelope {
		return nil
	})
	env := protocol.MustEnvelope("fire_and_forget", nil, nil, nil)
	resp, handled := r.Route(env)
	if !handled {
		t.Fatal("Route() handled = false, want true")
	}
	if resp != nil {
		t.Errorf("Route() returned %v, want nil", resp)
	}
}

func TestRouterHasMessage(t *testing.T) {
	r := NewRouter()
	r.RegisterMessage("inference_request", func(env protocol.Envelope) *protocol.Envelope {
		return nil
	})
	if !r.HasMessage("inference_request") {
		t.Error("HasMessage('inference_request') = false, want true")
	}
	if r.HasMessage("nope") {
		t.Error("HasMessage('nope') = true, want false")
	}
}

func TestRouterDuplicateRegistrationPanics(t *testing.T) {
	defer func() {
		if r := recover(); r == nil {
			t.Error("Expected panic on duplicate message registration")
		}
	}()
	r := NewRouter()
	r.RegisterMessage("dup", func(env protocol.Envelope) *protocol.Envelope { return nil })
	r.RegisterMessage("dup", func(env protocol.Envelope) *protocol.Envelope { return nil })
}

func strPtr(s string) *string {
	return &s
}
