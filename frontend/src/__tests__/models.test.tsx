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
  it("renders the discovered models list from the mock API", async () => {
    render(
      <TestWrapper>
        <Models />
      </TestWrapper>,
    );

    await waitFor(() => {
      expect(screen.getByText("Models")).toBeInTheDocument();
    });

    expect(screen.getByText("llama-3.1-70b")).toBeInTheDocument();
    expect(screen.getByText("mistral-large-2")).toBeInTheDocument();
    expect(screen.getByText("codestral-22b")).toBeInTheDocument();
    expect(screen.getByText("gemma-2-27b")).toBeInTheDocument();
  });

  it("shows status chips with fleet palette semantics", async () => {
    render(
      <TestWrapper>
        <Models />
      </TestWrapper>,
    );

    await waitFor(() => {
      expect(screen.getByText("llama-3.1-70b")).toBeInTheDocument();
    });

    // ready chips are green; validating is rendered as a distinct amber "validating…" chip
    expect(screen.getAllByText("ready").length).toBeGreaterThanOrEqual(2);
    expect(screen.getByText("validating…")).toBeInTheDocument();
    expect(screen.getByText("deprecated")).toBeInTheDocument();

    // the validating chip (Badge span wrapping the label) uses the warning palette
    const validatingBadge = screen.getByText("validating…");
    expect(validatingBadge.className).toContain("color-status-warning");
  });

  it("shows the last benchmark metrics with labels (speed, processing, tokens)", async () => {
    render(
      <TestWrapper>
        <Models />
      </TestWrapper>,
    );

    await waitFor(() => {
      expect(screen.getByText("llama-3.1-70b")).toBeInTheDocument();
    });

    // llama-3.1-70b has lastBenchmark {tokensPerSec: 42.3, latencyMs: 120, tokensGenerated: 512}
    expect(screen.getByText("42.3 tok/s")).toBeInTheDocument();
    expect(screen.getByText("120ms")).toBeInTheDocument();
    // tokensGenerated is seeded on this model → tokens chip renders
    expect(screen.getByText("512 tok")).toBeInTheDocument();
    // Labels are visible for each metric
    expect(screen.getAllByText("speed").length).toBeGreaterThanOrEqual(1);
    expect(screen.getAllByText("processing").length).toBeGreaterThanOrEqual(1);
    expect(screen.getAllByText("tokens").length).toBeGreaterThanOrEqual(1);
  });

  it("omits the tokens chip when lastBenchmark has no tokensGenerated", async () => {
    const sparseModels = [
      {
        id: "s1",
        name: "sparse-model",
        family: "Test",
        parameterSize: "1B",
        quantization: "Q8",
        status: "ready" as const,
        lastBenchmark: {
          // Wire contract only: no tokensGenerated field
          tokensPerSec: 33.1,
          latencyMs: 141,
          timestamp: new Date().toISOString(),
        },
        contextWindow: 4096,
        containerImage: "test/sparse",
        sourceRuntimeId: null,
        sourceRuntimeName: null,
        createdAt: new Date().toISOString(),
        updatedAt: new Date().toISOString(),
      },
    ];
    vi.spyOn(mockClient, "listModels").mockResolvedValueOnce(sparseModels);

    render(
      <TestWrapper>
        <Models />
      </TestWrapper>,
    );

    await waitFor(() => {
      expect(screen.getByText("sparse-model")).toBeInTheDocument();
    });

    // speed + processing + ran chips render; no tokens chip
    expect(screen.getByText("33.1 tok/s")).toBeInTheDocument();
    expect(screen.getByText("141ms")).toBeInTheDocument();
    expect(screen.queryByText("tokens")).not.toBeInTheDocument();
  });

  it('shows "Not benchmarked yet" when lastBenchmark is missing', async () => {
    render(
      <TestWrapper>
        <Models />
      </TestWrapper>,
    );

    await waitFor(() => {
      expect(screen.getByText("codestral-22b")).toBeInTheDocument();
    });

    // codestral-22b has no benchmark
    expect(screen.getAllByText("Not benchmarked yet").length).toBeGreaterThanOrEqual(1);
  });

  it("renders n/a for a zero-valued benchmark result", async () => {
    const zeroBenchModels = [
      {
        id: "z1",
        name: "zero-model",
        family: "Test",
        parameterSize: "1B",
        quantization: "Q8",
        status: "ready" as const,
        lastBenchmark: {
          id: "bz",
          modelId: "z1",
          modelName: "zero-model",
          prompt: "p",
          tokensPerSec: 0,
          latencyMs: 0,
          tokensGenerated: 0,
          timestamp: new Date().toISOString(),
          status: "completed" as const,
          errorMessage: null,
        },
        contextWindow: 4096,
        containerImage: "test/zero",
        sourceRuntimeId: null,
        sourceRuntimeName: null,
        createdAt: new Date().toISOString(),
        updatedAt: new Date().toISOString(),
      },
    ];
    vi.spyOn(mockClient, "listModels").mockResolvedValueOnce(zeroBenchModels);

    render(
      <TestWrapper>
        <Models />
      </TestWrapper>,
    );

    await waitFor(() => {
      expect(screen.getByText("zero-model")).toBeInTheDocument();
    });
    // zero tok/s → "n/a" chip; zero latency → "n/a"
    expect(screen.getAllByText("n/a").length).toBeGreaterThanOrEqual(1);
  });

  it("view container link navigates with the focus param", async () => {
    render(
      <TestWrapper initialEntries={["/models"]}>
        <Models />
      </TestWrapper>,
    );

    await waitFor(() => {
      expect(screen.getByText("llama-3.1-70b")).toBeInTheDocument();
    });

    // llama-3.1-70b and gemma-2-27b both have sourceRuntimeId rc1 → deep links
    const links = screen.getAllByRole("link", {
      name: "View source runtime on the Fleet page",
    });
    expect(links.length).toBeGreaterThanOrEqual(1);
    expect(links[0]).toHaveAttribute("href", "/fleet?focus=rc1");
  });

  it("renders muted not-registered for models without a source container", async () => {
    render(
      <TestWrapper>
        <Models />
      </TestWrapper>,
    );

    await waitFor(() => {
      expect(screen.getByText("mistral-large-2")).toBeInTheDocument();
    });

    // mistral-large-2, codestral-22b and phi-3.5-mini have sourceRuntimeId null
    expect(screen.getAllByText("not registered").length).toBeGreaterThanOrEqual(3);
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
      expect(screen.getByText("No models discovered yet")).toBeInTheDocument();
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
      expect(screen.getByText("No models discovered yet")).toBeInTheDocument();
    });
  });
});
