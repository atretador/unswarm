import { describe, it, expect, beforeEach } from "vitest";
import { render, screen, waitFor } from "@testing-library/react";
import { setMockLatency } from "../lib/api/mock";
import { TestWrapper } from "./test-utils";
import Dashboard from "../features/dashboard";

beforeEach(() => {
  setMockLatency(0);
});

describe("Dashboard", () => {
  it("renders stat cards with data from mock API", async () => {
    render(
      <TestWrapper>
        <Dashboard />
      </TestWrapper>,
    );

    await waitFor(() => {
      expect(screen.getByText("Total requests")).toBeInTheDocument();
    });

    expect(screen.getByText("14,287")).toBeInTheDocument();
    expect(screen.getByText("24.9M")).toBeInTheDocument();
    expect(screen.getByText("142ms")).toBeInTheDocument();
    // Queue depth = 2; use getAllByText since "2" appears in multiple stats
    expect(screen.getAllByText("2").length).toBeGreaterThanOrEqual(1);
  });

  it("renders chart sections", async () => {
    render(
      <TestWrapper>
        <Dashboard />
      </TestWrapper>,
    );

    await waitFor(() => {
      expect(screen.getByText("Requests per minute")).toBeInTheDocument();
    });

    expect(screen.getByText("Tokens per second")).toBeInTheDocument();
  });

  it("shows loading skeletons initially", () => {
    // Set slow latency to see loading state
    setMockLatency(500);
    const { container } = render(
      <TestWrapper>
        <Dashboard />
      </TestWrapper>,
    );

    // Should show skeleton elements
    const skeletons = container.querySelectorAll("[aria-hidden='true']");
    expect(skeletons.length).toBeGreaterThan(0);
  });

  it("shows quick stats row", async () => {
    render(
      <TestWrapper>
        <Dashboard />
      </TestWrapper>,
    );

    await waitFor(() => {
      expect(screen.getByText("Active requests")).toBeInTheDocument();
    });

    expect(screen.getByText("Models loaded")).toBeInTheDocument();
    expect(screen.getByText("Uptime")).toBeInTheDocument();
    expect(screen.getByText("Errors (24h)")).toBeInTheDocument();
  });
});
