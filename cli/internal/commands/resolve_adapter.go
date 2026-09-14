package commands

import (
	"context"

	"github.com/unswarm/cli/internal/client"
	"github.com/unswarm/cli/internal/resolve"
)

// clientAdapter wraps *client.Client to satisfy the resolve.Client interface.
// client.Client.Do returns *client.Response, but resolve.Client expects *resolve.Response.
type clientAdapter struct {
	c *client.Client
}

// Do delegates to the underlying client and converts the response type.
func (a *clientAdapter) Do(ctx context.Context, method, path string, body any) (*resolve.Response, error) {
	resp, err := a.c.Do(ctx, method, path, body)
	if err != nil {
		return nil, err
	}
	return &resolve.Response{
		StatusCode: resp.StatusCode,
		Body:       resp.Body,
	}, nil
}

// newResolveAdapter wraps a *client.Client as a resolve.Client.
func newResolveAdapter(c *client.Client) resolve.Client {
	return &clientAdapter{c: c}
}
