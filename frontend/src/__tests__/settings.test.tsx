import { describe, it, expect, beforeEach, vi } from "vitest";
import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
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

  it("loads API keys from mock", async () => {
    render(
      <TestWrapper>
        <Settings />
      </TestWrapper>,
    );

    await waitFor(() => {
      expect(screen.getByText("dev-local")).toBeInTheDocument();
    });

    expect(screen.getByText("ci-pipeline")).toBeInTheDocument();
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

  it("shows Create button for API keys", async () => {
    render(
      <TestWrapper>
        <Settings />
      </TestWrapper>,
    );

    await waitFor(() => {
      expect(screen.getByText("dev-local")).toBeInTheDocument();
    });

    expect(screen.getByText("Create")).toBeInTheDocument();
  });

  it("opens create form when clicking Create", async () => {
    const user = userEvent.setup();
    render(
      <TestWrapper>
        <Settings />
      </TestWrapper>,
    );

    await waitFor(() => {
      expect(screen.getByText("dev-local")).toBeInTheDocument();
    });

    const createButtons = screen.getAllByText("Create");
    await user.click(createButtons[0]);

    await waitFor(() => {
      expect(screen.getByPlaceholderText("my-api-key")).toBeInTheDocument();
    });
  });

  it("renders theme and API keys sections when settings API fails", async () => {
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

    // API keys section makes its own call, should still work
    await waitFor(() => {
      expect(screen.getByText("dev-local")).toBeInTheDocument();
    });
  });
});
