import { describe, it, expect } from "vitest";
import { render, screen } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { ThemeProvider } from "../lib/theme";
import App from "../App";

function renderAt(path: string) {
  return render(
    <MemoryRouter initialEntries={[path]}>
      <ThemeProvider>
        <App />
      </ThemeProvider>
    </MemoryRouter>,
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
    it(`renders ${path} (${heading}) without crashing`, () => {
      const { unmount } = renderAt(path);
      // The page heading is in the main content area
      expect(screen.getAllByText(heading).length).toBeGreaterThanOrEqual(1);
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
