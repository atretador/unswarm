import { describe, it, expect, beforeEach } from "vitest";
import { render, screen, waitFor } from "@testing-library/react";
import { setMockLatency } from "../lib/api/mock";
import { TestWrapper } from "./test-utils";
import Logs from "../features/logs";

beforeEach(() => {
  setMockLatency(0);
});

describe("Logs", () => {
  it("renders log entries from mock API", async () => {
    render(
      <TestWrapper>
        <Logs />
      </TestWrapper>,
    );

    await waitFor(() => {
      expect(screen.getByText("Logs")).toBeInTheDocument();
    });

    expect(screen.getByText("Health check passed — 12ms response")).toBeInTheDocument();
    expect(screen.getByText("OOM killed — model requires 32GB, container limit was 16GB")).toBeInTheDocument();
  });

  it("shows level badges", async () => {
    render(
      <TestWrapper>
        <Logs />
      </TestWrapper>,
    );

    await waitFor(() => {
      expect(screen.getByText("Health check passed — 12ms response")).toBeInTheDocument();
    });

    const infoBadges = screen.getAllByText("info");
    expect(infoBadges.length).toBeGreaterThanOrEqual(1);

    expect(screen.getByText("warn")).toBeInTheDocument();
    expect(screen.getByText("error")).toBeInTheDocument();
    expect(screen.getByText("debug")).toBeInTheDocument();
  });

  it("shows filter controls", async () => {
    render(
      <TestWrapper>
        <Logs />
      </TestWrapper>,
    );

    await waitFor(() => {
      expect(screen.getByText("All levels")).toBeInTheDocument();
    });

    expect(screen.getByText("All sources")).toBeInTheDocument();
    expect(screen.getByText("Pause")).toBeInTheDocument();
    expect(screen.getByText("Follow")).toBeInTheDocument();
    expect(screen.getByText("Clear")).toBeInTheDocument();
  });

  it("shows entry count", async () => {
    render(
      <TestWrapper>
        <Logs />
      </TestWrapper>,
    );

    await waitFor(() => {
      expect(screen.getByText("6 entries")).toBeInTheDocument();
    });
  });

  it("shows streaming indicator", async () => {
    render(
      <TestWrapper>
        <Logs />
      </TestWrapper>,
    );

    await waitFor(() => {
      expect(screen.getByText("streaming")).toBeInTheDocument();
    });
  });

  it("shows loading state", () => {
    setMockLatency(500);
    const { container } = render(
      <TestWrapper>
        <Logs />
      </TestWrapper>,
    );

    const skeletons = container.querySelectorAll("[aria-hidden='true']");
    expect(skeletons.length).toBeGreaterThan(0);
  });
});
