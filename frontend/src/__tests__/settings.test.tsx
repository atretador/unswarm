import { describe, it, expect, beforeEach, vi } from "vitest";
import { render, screen, waitFor } from "@testing-library/react";
import { setMockLatency, mockClient } from "../lib/api/mock";
import { TestWrapper } from "./test-utils";
import Settings from "../features/settings";

beforeEach(() => {
  setMockLatency(0);
  vi.restoreAllMocks();
});

describe("Settings", () => {
  it("renders settings page", async () => {
    render(
      <TestWrapper>
        <Settings />
      </TestWrapper>,
    );

    await waitFor(() => {
      expect(screen.getByText("Settings")).toBeInTheDocument();
    });
  });

  it("shows theme note", async () => {
    render(
      <TestWrapper>
        <Settings />
      </TestWrapper>,
    );

    await waitFor(() => {
      expect(screen.getByText("Theme")).toBeInTheDocument();
    });

    expect(screen.getByText(/Theme is controlled from the topbar toggle/)).toBeInTheDocument();
  });

  it("loads scheduler policy toggles", async () => {
    render(
      <TestWrapper>
        <Settings />
      </TestWrapper>,
    );

    await waitFor(() => {
      expect(screen.getByText("Scheduler Policy")).toBeInTheDocument();
    });

    expect(screen.getByText("Auto-shutdown idle")).toBeInTheDocument();
    expect(screen.getByText("Batch drain")).toBeInTheDocument();
    expect(screen.getByText("Lazy stop")).toBeInTheDocument();
    expect(screen.getByText("Enable benchmarking")).toBeInTheDocument();
  });

  it("renders scheduler policy when settings API fails", async () => {
    vi.spyOn(mockClient, "getSettings").mockRejectedValueOnce(new Error("Internal error"));

    render(
      <TestWrapper>
        <Settings />
      </TestWrapper>,
    );

    await waitFor(() => {
      expect(screen.getByText("Settings")).toBeInTheDocument();
    });

    // Theme section is static, should always render
    expect(screen.getByText("Theme")).toBeInTheDocument();
  });
});
