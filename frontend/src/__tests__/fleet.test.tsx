import { describe, it, expect, beforeEach, vi } from "vitest";
import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { setMockLatency, mockClient } from "../lib/api/mock";
import { TestWrapper } from "./test-utils";
import Fleet from "../features/fleet";

beforeEach(() => {
  setMockLatency(0);
  vi.restoreAllMocks();
});

describe("Fleet", () => {
  it("renders container cards from mock API", async () => {
    render(
      <TestWrapper>
        <Fleet />
      </TestWrapper>,
    );

    await waitFor(() => {
      expect(screen.getByText("Fleet")).toBeInTheDocument();
    });

    expect(screen.getByText("llama-3.1-70b")).toBeInTheDocument();
    expect(screen.getByText("mistral-large-2")).toBeInTheDocument();
    expect(screen.getByText("gemma-2-27b")).toBeInTheDocument();
  });

  it("shows container statuses", async () => {
    render(
      <TestWrapper>
        <Fleet />
      </TestWrapper>,
    );

    await waitFor(() => {
      expect(screen.getByText("llama-3.1-70b")).toBeInTheDocument();
    });

    expect(screen.getByText("running")).toBeInTheDocument();
    expect(screen.getByText("starting")).toBeInTheDocument();
    expect(screen.getByText("stopped")).toBeInTheDocument();
  });

  it("shows container details (port, memory, uptime)", async () => {
    render(
      <TestWrapper>
        <Fleet />
      </TestWrapper>,
    );

    await waitFor(() => {
      expect(screen.getByText("llama-3.1-70b")).toBeInTheDocument();
    });

    expect(screen.getByText("8081")).toBeInTheDocument();
    expect(screen.getByText("37.5 GB")).toBeInTheDocument();
  });

  it("start button is disabled for running containers", async () => {
    render(
      <TestWrapper>
        <Fleet />
      </TestWrapper>,
    );

    await waitFor(() => {
      expect(screen.getByText("llama-3.1-70b")).toBeInTheDocument();
    });

    // Find the first Start button (for llama-3.1-70b which is running)
    const startButtons = screen.getAllByText("Start");
    expect(startButtons[0]).toBeDisabled();
  });

  it("stop button is enabled for running containers", async () => {
    render(
      <TestWrapper>
        <Fleet />
      </TestWrapper>,
    );

    await waitFor(() => {
      expect(screen.getByText("llama-3.1-70b")).toBeInTheDocument();
    });

    const stopButtons = screen.getAllByText("Stop");
    expect(stopButtons[0]).not.toBeDisabled();
  });

  it("shows loading state", () => {
    setMockLatency(500);
    const { container } = render(
      <TestWrapper>
        <Fleet />
      </TestWrapper>,
    );

    const skeletons = container.querySelectorAll("[aria-hidden='true']");
    expect(skeletons.length).toBeGreaterThan(0);
  });

  it("shows error state when API fails", async () => {
    vi.spyOn(mockClient, "listContainers").mockRejectedValueOnce(new Error("Connection refused"));

    render(
      <TestWrapper>
        <Fleet />
      </TestWrapper>,
    );

    await waitFor(() => {
      expect(screen.getByText("Failed to load fleet")).toBeInTheDocument();
    });

    expect(screen.getByText("Connection refused")).toBeInTheDocument();
  });

  it("retry button refetches after error", async () => {
    const user = userEvent.setup();
    vi.spyOn(mockClient, "listContainers")
      .mockRejectedValueOnce(new Error("Temporary failure"))
      .mockResolvedValueOnce([]);

    render(
      <TestWrapper>
        <Fleet />
      </TestWrapper>,
    );

    await waitFor(() => {
      expect(screen.getByText("Failed to load fleet")).toBeInTheDocument();
    });

    await user.click(screen.getByRole("button", { name: /retry/i }));

    await waitFor(() => {
      expect(screen.getByText("No containers running")).toBeInTheDocument();
    });
  });

  it("shows empty state when no containers exist", async () => {
    vi.spyOn(mockClient, "listContainers").mockResolvedValueOnce([]);

    render(
      <TestWrapper>
        <Fleet />
      </TestWrapper>,
    );

    await waitFor(() => {
      expect(screen.getByText("No containers running")).toBeInTheDocument();
    });
  });
});
