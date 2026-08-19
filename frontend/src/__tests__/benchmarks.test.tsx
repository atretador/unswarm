import { describe, it, expect, beforeEach, vi } from "vitest";
import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { setMockLatency, mockClient } from "../lib/api/mock";
import { TestWrapper } from "./test-utils";
import Benchmarks from "../features/benchmarks";

beforeEach(() => {
  setMockLatency(0);
  vi.restoreAllMocks();
});

describe("Benchmarks", () => {
  it("renders benchmark results from the mock API", async () => {
    render(
      <TestWrapper>
        <Benchmarks />
      </TestWrapper>,
    );

    await waitFor(() => {
      expect(screen.getByText("Benchmarks")).toBeInTheDocument();
    });

    // Newest-first seed history contains llama, mistral, gemma entries (also in the select)
    expect(screen.getAllByText("llama-3.1-70b").length).toBeGreaterThanOrEqual(1);
    expect(screen.getAllByText("mistral-large-2").length).toBeGreaterThanOrEqual(1);
    expect(screen.getAllByText("gemma-2-27b").length).toBeGreaterThanOrEqual(1);
  });

  it("renders completed runs with tokens/sec and latency", async () => {
    render(
      <TestWrapper>
        <Benchmarks />
      </TestWrapper>,
    );

    await waitFor(() => {
      expect(screen.getByText("Benchmarks")).toBeInTheDocument();
    });

    // Newest llama run: 44.7 tok/s, 108ms, 368 tok
    expect(screen.getByText("44.7 tok/s")).toBeInTheDocument();
    expect(screen.getByText("108ms")).toBeInTheDocument();
    expect(screen.getAllByText("completed").length).toBeGreaterThanOrEqual(1);
  });

  it("renders error runs with the error message", async () => {
    render(
      <TestWrapper>
        <Benchmarks />
      </TestWrapper>,
    );

    await waitFor(() => {
      expect(screen.getAllByText("codestral-22b").length).toBeGreaterThanOrEqual(1);
    });

    // codestral run is an error with a message; n/a for metrics
    expect(screen.getAllByText("error").length).toBeGreaterThanOrEqual(1);
    expect(screen.getAllByText("n/a").length).toBeGreaterThanOrEqual(1);
  });

  it("shows the error message when an error run is expanded", async () => {
    const user = userEvent.setup();
    render(
      <TestWrapper>
        <Benchmarks />
      </TestWrapper>,
    );

    await waitFor(() => {
      expect(screen.getAllByText("codestral-22b").length).toBeGreaterThanOrEqual(1);
    });

    // Error message is inside the expandable detail area
    expect(
      screen.queryByText("Model is still validating — refused to serve. No response tokens produced."),
    ).not.toBeInTheDocument();

    await user.click(screen.getAllByText("codestral-22b")[0]);

    await waitFor(() => {
      expect(
        screen.getByText("Model is still validating — refused to serve. No response tokens produced."),
      ).toBeInTheDocument();
    });
  });

  it("prompt detail expands and collapses", async () => {
    const user = userEvent.setup();
    render(
      <TestWrapper>
        <Benchmarks />
      </TestWrapper>,
    );

    await waitFor(() => {
      expect(screen.getByText("Benchmarks")).toBeInTheDocument();
    });

    // The long prompt is only visible after expanding the row
    expect(screen.queryByText(/You are an inference engineer/)).not.toBeInTheDocument();

    const rows = screen.getAllByRole("button", { name: /llama-3.1-70b/ });
    const newestRow = rows[0];
    expect(newestRow).toHaveAttribute("aria-expanded", "false");

    await user.click(newestRow);
    expect(screen.getByText(/You are an inference engineer/)).toBeInTheDocument();
    expect(newestRow).toHaveAttribute("aria-expanded", "true");

    await user.click(newestRow);
    await waitFor(() => {
      expect(screen.queryByText(/You are an inference engineer/)).not.toBeInTheDocument();
    });
  });

  it("run benchmark flow: select a ready model, POST, and refresh the list", async () => {
    const user = userEvent.setup();
    const runSpy = vi.spyOn(mockClient, "runBenchmark");
    const listSpy = vi.spyOn(mockClient, "listBenchmarks");

    render(
      <TestWrapper>
        <Benchmarks />
      </TestWrapper>,
    );

    await waitFor(() => {
      expect(screen.getByText("Benchmarks")).toBeInTheDocument();
    });

    // Wait for the model options to populate, then select llama-3.1-70b (value "1")
    await waitFor(() => {
      const combo = screen.getByRole("combobox", { name: "Target model" }) as HTMLSelectElement;
      expect(combo.options.length).toBeGreaterThan(1);
    });
    await user.selectOptions(screen.getByRole("combobox", { name: "Target model" }), "1");
    await user.click(screen.getByRole("button", { name: /run benchmark/i }));

    await waitFor(() => {
      expect(runSpy).toHaveBeenCalledTimes(1);
    });
    expect(runSpy.mock.calls[0][0]).toBe("1");
    // listBenchmarks was refetched after the run so the new entry appears
    expect(listSpy).toHaveBeenCalled();
  });

  it("run button is disabled for non-ready models", async () => {
    const user = userEvent.setup();
    render(
      <TestWrapper>
        <Benchmarks />
      </TestWrapper>,
    );

    await waitFor(() => {
      expect(screen.getByText("Benchmarks")).toBeInTheDocument();
    });

    // Wait for the model options to populate, then pick codestral-22b (validating)
    await waitFor(() => {
      const combo = screen.getByRole("combobox", { name: "Target model" }) as HTMLSelectElement;
      expect(combo.options.length).toBeGreaterThan(1);
    });
    await user.selectOptions(screen.getByRole("combobox", { name: "Target model" }), "3");
    expect(screen.getByRole("button", { name: /run benchmark/i })).toBeDisabled();
  });

  it("shows loading state", () => {
    setMockLatency(500);
    const { container } = render(
      <TestWrapper>
        <Benchmarks />
      </TestWrapper>,
    );

    const skeletons = container.querySelectorAll("[aria-hidden='true']");
    expect(skeletons.length).toBeGreaterThan(0);
  });

  it("shows error state when API fails", async () => {
    vi.spyOn(mockClient, "listBenchmarks").mockRejectedValueOnce(new Error("Server error"));

    render(
      <TestWrapper>
        <Benchmarks />
      </TestWrapper>,
    );

    await waitFor(() => {
      expect(screen.getByText("Failed to load benchmarks")).toBeInTheDocument();
    });

    expect(screen.getByText("Server error")).toBeInTheDocument();
  });

  it("retry button refetches after error", async () => {
    const user = userEvent.setup();
    vi.spyOn(mockClient, "listBenchmarks")
      .mockRejectedValueOnce(new Error("Temporary failure"))
      .mockResolvedValueOnce([]);

    render(
      <TestWrapper>
        <Benchmarks />
      </TestWrapper>,
    );

    await waitFor(() => {
      expect(screen.getByText("Failed to load benchmarks")).toBeInTheDocument();
    });

    await user.click(screen.getByRole("button", { name: /retry/i }));

    await waitFor(() => {
      expect(screen.getByText("No benchmark runs yet")).toBeInTheDocument();
    });
  });

  it("shows empty state when no benchmarks exist", async () => {
    vi.spyOn(mockClient, "listBenchmarks").mockResolvedValueOnce([]);

    render(
      <TestWrapper>
        <Benchmarks />
      </TestWrapper>,
    );

    await waitFor(() => {
      expect(screen.getByText("No benchmark runs yet")).toBeInTheDocument();
    });
  });
});
