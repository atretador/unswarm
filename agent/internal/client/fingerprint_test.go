package client

import (
	"crypto/ecdsa"
	"crypto/elliptic"
	"crypto/rand"
	"crypto/sha256"
	"crypto/x509"
	"crypto/x509/pkix"
	"encoding/hex"
	"errors"
	"math/big"
	"testing"
	"time"

	"unswarm/agent/internal/config"
)

func makeDer(t *testing.T, serial int64) []byte {
	t.Helper()
	key, _ := ecdsa.GenerateKey(elliptic.P256(), rand.Reader)
	tmpl := &x509.Certificate{
		SerialNumber: big.NewInt(serial),
		Subject:      pkix.Name{CommonName: "backend"},
		NotBefore:    time.Now().Add(-time.Hour),
		NotAfter:     time.Now().Add(time.Hour),
	}
	der, err := x509.CreateCertificate(rand.Reader, tmpl, tmpl, &key.PublicKey, key)
	if err != nil {
		t.Fatal(err)
	}
	return der
}

func TestFingerprintVerifyCallback(t *testing.T) {
	der := makeDer(t, 1)
	sum := sha256.Sum256(der)
	fp := hex.EncodeToString(sum[:])

	c := New(config.Config{
		ExpectedServerFingerprint: fp, // case/space-insensitive parsing
	}, discardLogger())
	tlsCfg, err := c.tlsConfig()
	if err != nil {
		t.Fatalf("tlsConfig: %v", err)
	}
	if tlsCfg == nil {
		t.Fatal("expected non-nil tls.Config when fingerprint is set")
	}

	// Matching certificate passes.
	if err := tlsCfg.VerifyPeerCertificate([][]byte{der}, nil); err != nil {
		t.Errorf("matching cert rejected: %v", err)
	}

	// Different certificate fails closed with ErrFingerprintMismatch.
	other := makeDer(t, 2)
	err = tlsCfg.VerifyPeerCertificate([][]byte{other}, nil)
	if !errors.Is(err, ErrFingerprintMismatch) {
		t.Errorf("mismatching cert: got %v, want ErrFingerprintMismatch", err)
	}

	// No certificate fails closed.
	if err := tlsCfg.VerifyPeerCertificate(nil, nil); !errors.Is(err, ErrFingerprintMismatch) {
		t.Errorf("empty chain: got %v, want ErrFingerprintMismatch", err)
	}
}

func TestTLSConfigNilWithoutFingerprint(t *testing.T) {
	c := New(config.Config{}, discardLogger())
	tlsCfg, err := c.tlsConfig()
	if err != nil || tlsCfg != nil {
		t.Errorf("expected (nil, nil) without fingerprint, got (%v, %v)", tlsCfg, err)
	}
}

func TestNormalizeFingerprintFormats(t *testing.T) {
	const fp = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"
	for _, in := range []string{
		fp,
		"0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF",
		"01:23:45:67:89:ab:cd:ef:01:23:45:67:89:ab:cd:ef:01:23:45:67:89:ab:cd:ef:01:23:45:67:89:ab:cd:ef",
		" 0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef ",
	} {
		got, err := config.NormalizeFingerprint(in)
		if err != nil || got != fp {
			t.Errorf("NormalizeFingerprint(%q) = %q, %v; want %q", in, got, err, fp)
		}
	}
	for _, bad := range []string{"abc", "zzzz", fp + "00"} {
		if _, err := config.NormalizeFingerprint(bad); err == nil {
			t.Errorf("NormalizeFingerprint(%q) expected error", bad)
		}
	}
	if got, err := config.NormalizeFingerprint(""); err != nil || got != "" {
		t.Errorf("empty fingerprint should be disabled, got %q, %v", got, err)
	}
}
