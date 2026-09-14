package helpers

import (
	"testing"
)

func TestStrOrDash(t *testing.T) {
	m := map[string]any{
		"name":   "gpt-4",
		"family": "",
		"count":  42,
	}
	tests := []struct {
		key  string
		want string
	}{
		{"name", "gpt-4"},
		{"family", "-"},
		{"count", "42"},
		{"missing", "-"},
	}
	for _, tt := range tests {
		if got := StrOrDash(m, tt.key); got != tt.want {
			t.Errorf("StrOrDash(%q) = %q, want %q", tt.key, got, tt.want)
		}
	}

	// nil value
	m2 := map[string]any{"x": nil}
	if got := StrOrDash(m2, "x"); got != "-" {
		t.Errorf("StrOrDash(nil value) = %q, want %q", got, "-")
	}
}

func TestFloatOrDash(t *testing.T) {
	m := map[string]any{
		"cpu":  3.14159,
		"big":  1234567.0,
		"tiny": 0.00001,
		"neg":  -0.5,
	}
	tests := []struct {
		key  string
		want string
	}{
		{"cpu", "3.14"},
		{"big", "1.23e+06"},
		{"tiny", "1.00e-05"},
		{"neg", "-0.50"},
		{"missing", "-"},
	}
	for _, tt := range tests {
		if got := FloatOrDash(m, tt.key); got != tt.want {
			t.Errorf("FloatOrDash(%q) = %q, want %q", tt.key, got, tt.want)
		}
	}

	// nil value
	m2 := map[string]any{"x": nil}
	if got := FloatOrDash(m2, "x"); got != "-" {
		t.Errorf("FloatOrDash(nil value) = %q, want %q", got, "-")
	}
}

func TestIntOrDash(t *testing.T) {
	m := map[string]any{
		"port":   float64(8080),
		"memory": 512,
	}
	tests := []struct {
		key  string
		want string
	}{
		{"port", "8080"},
		{"memory", "512"},
		{"missing", "-"},
	}
	for _, tt := range tests {
		if got := IntOrDash(m, tt.key); got != tt.want {
			t.Errorf("IntOrDash(%q) = %q, want %q", tt.key, got, tt.want)
		}
	}

	m2 := map[string]any{"x": nil}
	if got := IntOrDash(m2, "x"); got != "-" {
		t.Errorf("IntOrDash(nil value) = %q, want %q", got, "-")
	}
}

func TestIntStr(t *testing.T) {
	tests := []struct {
		input any
		want  string
	}{
		{nil, "-"},
		{float64(42), "42"},
		{float64(0), "-"},
		{int(7), "7"},
		{int(0), "-"},
		{int64(100), "100"},
		{int64(0), "-"},
		{"hello", "hello"},
	}
	for _, tt := range tests {
		if got := IntStr(tt.input); got != tt.want {
			t.Errorf("IntStr(%v) = %q, want %q", tt.input, got, tt.want)
		}
	}
}

func TestBoolStr(t *testing.T) {
	tests := []struct {
		input any
		want  string
	}{
		{true, "yes"},
		{false, "no"},
		{"not a bool", "not a bool"},
		{nil, "<nil>"},
	}
	for _, tt := range tests {
		if got := BoolStr(tt.input); got != tt.want {
			t.Errorf("BoolStr(%v) = %q, want %q", tt.input, got, tt.want)
		}
	}
}

func TestFirstN(t *testing.T) {
	tests := []struct {
		s    string
		n    int
		want string
	}{
		{"hello", 3, "hel..."},
		{"hi", 5, "hi"},
		{"", 3, ""},
		{"abcdef", 6, "abcdef"},
		{"abcdef", 0, "..."},
	}
	for _, tt := range tests {
		if got := FirstN(tt.s, tt.n); got != tt.want {
			t.Errorf("FirstN(%q, %d) = %q, want %q", tt.s, tt.n, got, tt.want)
		}
	}
}

func TestContains(t *testing.T) {
	slice := []string{"alpha", "beta", "gamma"}
	if !Contains(slice, "beta") {
		t.Error("Contains should find 'beta'")
	}
	if Contains(slice, "delta") {
		t.Error("Contains should not find 'delta'")
	}
	if Contains(nil, "anything") {
		t.Error("Contains(nil) should return false")
	}
	if Contains([]string{}, "x") {
		t.Error("Contains(empty, 'x') should return false")
	}
}
