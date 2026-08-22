import { describe, it, expect, beforeEach, vi } from "vitest";
import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { setMockLatency, mockClient } from "../lib/api/mock";
import { TestWrapper } from "./test-utils";
import Queue from "../features/queue";

beforeEach(() => {
  setMockLatency(0);
  vi.restoreAllMocks();
});

describe("Queue", () => {
  it("renders target sections and active items from mock API", async () => {
    render(
      <TestWrapper>
        <Queue />
      </TestWrapper>,
    );

    await waitFor(() => {
      expect(screen.getByText("Queue")).toBeInTheDocument();
    });

    // Both host and agent targets should always be visible
    expect(screen.getByText("Host (local)")).toBeInTheDocument();
    expect(screen.getByText("Agent: gpu-node-1")).toBeInTheDocument();
    // Processing item shows model name
    expect(screen.getAllByText("llama-3.1-70b").length).toBeGreaterThanOrEqual(1);
  });

  it("renders waiting queue items grouped by target", async () => {
    render(
      <TestWrapper>
        <Queue />
      </TestWrapper>,
    );

    await waitFor(() => {
      expect(screen.getByText("Host (local)")).toBeInTheDocument();
    });

    // Waiting items are numbered per-target within their sections.
    // mistral-large-2 appears twice: one lane processing it + one waiting copy.
    expect(screen.getAllByText("mistral-large-2").length).toBe(2);
  });

  it("shows recent completed items", async () => {
    render(
      <TestWrapper>
        <Queue />
      </TestWrapper>,
    );

    await waitFor(() => {
      expect(screen.getByText("Recent completed (1)")).toBeInTheDocument();
    });

    expect(screen.getByText("completed")).toBeInTheDocument();
  });

  it("shows loading state", () => {
    setMockLatency(500);
    const { container } = render(
      <TestWrapper>
        <Queue />
      </TestWrapper>,
    );

    const skeletons = container.querySelectorAll("[aria-hidden='true']");
    expect(skeletons.length).toBeGreaterThan(0);
  });

  it("shows live polling indicator", async () => {
    render(
      <TestWrapper>
        <Queue />
      </TestWrapper>,
    );

    await waitFor(() => {
      expect(screen.getByText(/Live.*polling every 2s/)).toBeInTheDocument();
    });
  });

  it("shows error state when API fails", async () => {
    vi.spyOn(mockClient, "getQueueSnapshot").mockRejectedValueOnce(new Error("Timeout"));

    render(
      <TestWrapper>
        <Queue />
      </TestWrapper>,
    );

    await waitFor(() => {
      expect(screen.getByText("Failed to load queue")).toBeInTheDocument();
    });

    expect(screen.getByText("Timeout")).toBeInTheDocument();
  });

  it("retry button refetches after error", async () => {
    const user = userEvent.setup();
    vi.spyOn(mockClient, "getQueueSnapshot")
      .mockRejectedValueOnce(new Error("Temporary failure"))
      .mockResolvedValueOnce({
        processing: [],
        currentSlot: null,
        waiting: [],
        recentCompleted: [],
        activeTransitions: [],
        skipsUsed: 0,
        skipsRemaining: 0,
      });

    render(
      <TestWrapper>
        <Queue />
      </TestWrapper>,
    );

    await waitFor(() => {
      expect(screen.getByText("Failed to load queue")).toBeInTheDocument();
    });

    await user.click(screen.getByRole("button", { name: /retry/i }));

    await waitFor(() => {
      expect(screen.getByText("Queue")).toBeInTheDocument();
    });
  });
});
