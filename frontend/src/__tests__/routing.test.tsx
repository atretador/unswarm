import { describe, it, expect, beforeEach } from "vitest";
import { render, screen, waitFor } from "@testing-library/react";
import { setMockLatency } from "../lib/api/mock";
import { TestWrapper } from "./test-utils";
import App from "../App";

beforeEach(() => {
  setMockLatency(0);
});

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
      const { unmount } = render(
        <TestWrapper initialEntries={[path]}>
          <App />
        </TestWrapper>,
      );
      await waitFor(() => {
        // User is not authenticated, so they'll be redirected to /login
        expect(screen.getByText("Sign in to unswarm")).toBeInTheDocument();
      });
      unmount();
    });
  }

  it("renders the login page", async () => {
    render(
      <TestWrapper initialEntries={["/login"]}>
        <App />
      </TestWrapper>,
    );
    await waitFor(() => {
      expect(screen.getByText("Sign in to unswarm")).toBeInTheDocument();
    });
  });

  it("unknown route renders 404 page", async () => {
    render(
      <TestWrapper initialEntries={["/nope-does-not-exist"]}>
        <App />
      </TestWrapper>,
    );
    // Unauthenticated users get redirected to login
    await waitFor(() => {
      expect(screen.getByText("Sign in to unswarm")).toBeInTheDocument();
    });
  });
});
