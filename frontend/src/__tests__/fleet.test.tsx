import { describe, it, expect, beforeEach } from "vitest";
import { render, screen, waitFor } from "@testing-library/react";
import { setMockLatency } from "../lib/api/mock";
import { TestWrapper } from "./test-utils";
import Fleet from "../features/fleet";

beforeEach(() => {
  setMockLatency(0);
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
});
