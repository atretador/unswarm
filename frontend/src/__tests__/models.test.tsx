import { describe, it, expect, beforeEach, vi } from "vitest";
import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { setMockLatency, mockClient } from "../lib/api/mock";
import { TestWrapper } from "./test-utils";
import Models from "../features/models";

beforeEach(() => {
  setMockLatency(0);
  vi.restoreAllMocks();
});

describe("Models", () => {
  it("renders model list from mock API", async () => {
    render(
      <TestWrapper>
        <Models />
      </TestWrapper>,
    );

    await waitFor(() => {
      expect(screen.getByText("Model Registry")).toBeInTheDocument();
    });

    expect(screen.getByText("llama-3.1-70b")).toBeInTheDocument();
    expect(screen.getByText("mistral-large-2")).toBeInTheDocument();
    expect(screen.getByText("codestral-22b")).toBeInTheDocument();
    expect(screen.getByText("gemma-2-27b")).toBeInTheDocument();
  });

  it("shows status badges", async () => {
    render(
      <TestWrapper>
        <Models />
      </TestWrapper>,
    );

    await waitFor(() => {
      expect(screen.getByText("llama-3.1-70b")).toBeInTheDocument();
    });

    // Check for status badges
    const readyBadges = screen.getAllByText("ready");
    expect(readyBadges.length).toBeGreaterThanOrEqual(2);

    expect(screen.getByText("validating")).toBeInTheDocument();
  });

  it("shows loading state", () => {
    setMockLatency(500);
    const { container } = render(
      <TestWrapper>
        <Models />
      </TestWrapper>,
    );

    const skeletons = container.querySelectorAll("[aria-hidden='true']");
    expect(skeletons.length).toBeGreaterThan(0);
  });

  it("opens register form when clicking Register button", async () => {
    const user = userEvent.setup();
    render(
      <TestWrapper>
        <Models />
      </TestWrapper>,
    );

    await waitFor(() => {
      expect(screen.getByText("Model Registry")).toBeInTheDocument();
    });

    await user.click(screen.getByText("Register"));

    await waitFor(() => {
      expect(screen.getByText("Register Model")).toBeInTheDocument();
    });

    expect(screen.getByPlaceholderText("my-model-7b")).toBeInTheDocument();
  });

  it("displays model count", async () => {
    render(
      <TestWrapper>
        <Models />
      </TestWrapper>,
    );

    await waitFor(() => {
      expect(screen.getByText("5 models")).toBeInTheDocument();
    });
  });

  it("shows error state when API fails", async () => {
    vi.spyOn(mockClient, "listModels").mockRejectedValueOnce(new Error("Server error"));

    render(
      <TestWrapper>
        <Models />
      </TestWrapper>,
    );

    await waitFor(() => {
      expect(screen.getByText("Failed to load models")).toBeInTheDocument();
    });

    expect(screen.getByText("Server error")).toBeInTheDocument();
  });

  it("retry button refetches after error", async () => {
    const user = userEvent.setup();
    vi.spyOn(mockClient, "listModels")
      .mockRejectedValueOnce(new Error("Temporary failure"))
      .mockResolvedValueOnce([]);

    render(
      <TestWrapper>
        <Models />
      </TestWrapper>,
    );

    await waitFor(() => {
      expect(screen.getByText("Failed to load models")).toBeInTheDocument();
    });

    await user.click(screen.getByRole("button", { name: /retry/i }));

    await waitFor(() => {
      expect(screen.getByText("No models registered")).toBeInTheDocument();
    });
  });

  it("shows empty state when no models exist", async () => {
    vi.spyOn(mockClient, "listModels").mockResolvedValueOnce([]);

    render(
      <TestWrapper>
        <Models />
      </TestWrapper>,
    );

    await waitFor(() => {
      expect(screen.getByText("No models registered")).toBeInTheDocument();
    });
  });
});
