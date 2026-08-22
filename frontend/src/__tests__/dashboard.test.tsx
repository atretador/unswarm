import { describe, it, expect, beforeEach, vi } from "vitest";
import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { setMockLatency, mockClient } from "../lib/api/mock";
import { TestWrapper } from "./test-utils";
import Dashboard from "../features/dashboard";

beforeEach(() => {
  setMockLatency(0);
  vi.restoreAllMocks();
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
    expect(screen.getByText("142 ms")).toBeInTheDocument();
    // Queue depth = 2; use getAllByText since "2" appears in multiple stats
    expect(screen.getAllByText("2").length).toBeGreaterThanOrEqual(1);
    expect(screen.getByText("Avg switch")).toBeInTheDocument();
    expect(screen.getByText("2850 ms")).toBeInTheDocument();
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

  it("shows error state when API fails", async () => {
    vi.spyOn(mockClient, "getStats").mockRejectedValueOnce(new Error("Network failure"));

    render(
      <TestWrapper>
        <Dashboard />
      </TestWrapper>,
    );

    await waitFor(() => {
      expect(screen.getByText("Failed to load dashboard")).toBeInTheDocument();
    });

    expect(screen.getByText("Network failure")).toBeInTheDocument();
  });

  it("retry button refetches data after error", async () => {
    const user = userEvent.setup();
    vi.spyOn(mockClient, "getStats")
      .mockRejectedValueOnce(new Error("Temporary failure"))
      .mockResolvedValueOnce({
        totalRequests: 999,
        activeRequests: 0,
        avgLatencyMs: 50,
        totalTokensProcessed: 1000,
        totalPromptTokensCached: 0,
        uptimeSeconds: 3600,
        modelsLoaded: 1,
        containersRunning: 1,
        queueDepth: 0,
        requestsPerMinute: [1, 2, 3],
        errorsLast24h: 0,
        tokensPerSecond: [10, 20, 30],
        switchCount: 0,
        lastSwitchMs: 0,
        avgSwitchMs: 0,
      });

    render(
      <TestWrapper>
        <Dashboard />
      </TestWrapper>,
    );

    await waitFor(() => {
      expect(screen.getByText("Failed to load dashboard")).toBeInTheDocument();
    });

    await user.click(screen.getByRole("button", { name: /retry/i }));

    await waitFor(() => {
      expect(screen.getByText("999")).toBeInTheDocument();
    });
  });
});
