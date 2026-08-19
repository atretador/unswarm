import { describe, it, expect, beforeEach } from "vitest";
import { render, screen, waitFor } from "@testing-library/react";
import { setMockLatency } from "../lib/api/mock";
import { TestWrapper } from "./test-utils";
import Queue from "../features/queue";

beforeEach(() => {
  setMockLatency(0);
});

describe("Queue", () => {
  it("renders current slot from mock API", async () => {
    render(
      <TestWrapper>
        <Queue />
      </TestWrapper>,
    );

    await waitFor(() => {
      expect(screen.getByText("Queue")).toBeInTheDocument();
    });

    expect(screen.getByText("Current slot")).toBeInTheDocument();
    // llama-3.1-70b appears in current slot, waiting, and completed sections
    expect(screen.getAllByText("llama-3.1-70b").length).toBeGreaterThanOrEqual(1);
    expect(screen.getByText("processing")).toBeInTheDocument();
  });

  it("renders waiting queue items", async () => {
    render(
      <TestWrapper>
        <Queue />
      </TestWrapper>,
    );

    await waitFor(() => {
      expect(screen.getByText("Waiting (2)")).toBeInTheDocument();
    });

    expect(screen.getByText("#1")).toBeInTheDocument();
    expect(screen.getByText("#2")).toBeInTheDocument();
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
});
