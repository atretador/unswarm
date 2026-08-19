import { describe, it, expect, beforeEach } from "vitest";
import { render, screen, waitFor } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { QueryClientProvider } from "@tanstack/react-query";
import { ThemeProvider } from "../lib/theme";
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
          <App />
        </ThemeProvider>
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

describe("Routing smoke tests", () => {
  const routes: Array<{ path: string; heading: string }> = [
    { path: "/", heading: "Dashboard" },
    { path: "/models", heading: "Model Registry" },
    { path: "/fleet", heading: "Fleet" },
    { path: "/queue", heading: "Queue" },
    { path: "/logs", heading: "Logs" },
    { path: "/settings", heading: "Settings" },
  ];

  for (const { path, heading } of routes) {
    it(`renders ${path} (${heading}) without crashing`, async () => {
      const { unmount } = renderAt(path);
      await waitFor(() => {
        expect(screen.getAllByText(heading).length).toBeGreaterThanOrEqual(1);
      });
      unmount();
    });
  }

  it("unknown route renders 404 page", () => {
    renderAt("/nope-does-not-exist");
    expect(screen.getByText("Page not found")).toBeInTheDocument();
    expect(
      screen.getByText("The page you are looking for does not exist or has been moved."),
    ).toBeInTheDocument();
  });
});
