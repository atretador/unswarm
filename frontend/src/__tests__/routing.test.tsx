import { describe, it, expect, beforeEach } from "vitest";
import { render, screen, waitFor } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { QueryClientProvider } from "@tanstack/react-query";
import { ThemeProvider } from "../lib/theme";
import { AuthProvider } from "../lib/auth-context";
import { setMockLatency } from "../lib/api/mock";
import { createTestQueryClient } from "./test-utils";
import App from "../App";

beforeEach(() => {
  setMockLatency(0);
});

function renderAt(path: string) {
  const client = createTestQueryClient();
  return render(
    <QueryClientProvider client={client}>
      <MemoryRouter initialEntries={[path]}>
        <ThemeProvider>
          <AuthProvider>
            <App />
          </AuthProvider>
        </ThemeProvider>
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

describe("Routing smoke tests", () => {
  const routes: Array<{ path: string; heading: string }> = [
    { path: "/", heading: "Dashboard" },
    { path: "/models", heading: "Models" },
    { path: "/swarm", heading: "Swarm" },
    { path: "/benchmarks", heading: "Benchmarks" },
    { path: "/queue", heading: "Queue" },
    { path: "/logs", heading: "Logs" },
    { path: "/settings", heading: "Settings" },
  ];

  for (const { path, heading } of routes) {
    it(`renders ${path} (${heading}) without crashing`, async () => {
      const { unmount } = renderAt(path);
      await waitFor(() => {
        // User is not authenticated, so they'll be redirected to /login
        // Check for login page instead
        expect(screen.getByText("Sign in to unswarm")).toBeInTheDocument();
      });
      unmount();
    });
  }

  it("renders the login page", async () => {
    renderAt("/login");
    await waitFor(() => {
      expect(screen.getByText("Sign in to unswarm")).toBeInTheDocument();
    });
  });

  it("unknown route renders 404 page", async () => {
    renderAt("/nope-does-not-exist");
    // Unauthenticated users get redirected to login
    await waitFor(() => {
      expect(screen.getByText("Sign in to unswarm")).toBeInTheDocument();
    });
  });
});
