import { describe, it, expect, beforeEach, vi } from "vitest";
import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { setMockLatency, mockClient } from "../lib/api/mock";
import { TestWrapper } from "./test-utils";
import Models from "../features/models";

vi.mock("../features/api-keys/api-keys-api", async (importOriginal) => {
  const orig = await importOriginal<typeof import("../features/api-keys/api-keys-api")>();
  return {
    ...orig,
    getProviderModelCatalog: async () => [
      { name: "openai", kind: "cloud" as const, models: ["gpt-4o", "gpt-4o-mini"] },
      { name: "anthropic", kind: "cloud" as const, models: ["claude-sonnet-4-20250514", "claude-3-5-haiku-20241022", "claude-3-opus-20240229"] },
    ],
  };
});

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

    // Managed tab is default — swarm models visible
    expect(screen.getByText("llama-3.1-70b")).toBeInTheDocument();
    expect(screen.getByText("mistral-large-2")).toBeInTheDocument();
    expect(screen.getByText("codestral-22b")).toBeInTheDocument();
    expect(screen.getByText("gemma-2-27b")).toBeInTheDocument();

    // Cloud models should NOT be visible on the Managed tab
    expect(screen.queryByText("gpt-4o")).not.toBeInTheDocument();
    expect(screen.queryByText("claude-sonnet-4-20250514")).not.toBeInTheDocument();
  });

  it("shows status chips with swarm palette semantics", async () => {
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
        sourceRuntimeAgent: null,
        origin: "swarm",
        providerName: null,
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
        sourceRuntimeAgent: null,
        origin: "swarm",
        providerName: null,
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
      name: "View source runtime on the Swarm page",
    });
    expect(links.length).toBeGreaterThanOrEqual(1);
    expect(links[0]).toHaveAttribute("href", "/swarm?focus=rc1");
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

  // ─── Tab tests ──────────────────────────────────────────────────

  it("renders two tabs: Managed and Cloud", async () => {
    render(
      <TestWrapper>
        <Models />
      </TestWrapper>,
    );

    await waitFor(() => {
      expect(screen.getByText("Models")).toBeInTheDocument();
    });

    expect(screen.getByRole("tab", { name: /Managed/i })).toBeInTheDocument();
    expect(screen.getByRole("tab", { name: /Cloud/i })).toBeInTheDocument();
  });

  it("Managed tab is active by default", async () => {
    render(
      <TestWrapper>
        <Models />
      </TestWrapper>,
    );

    await waitFor(() => {
      expect(screen.getByRole("tab", { name: /Managed/i })).toBeInTheDocument();
    });

    const managedTab = screen.getByRole("tab", { name: /Managed/i });
    expect(managedTab).toHaveAttribute("aria-selected", "true");

    const cloudTab = screen.getByRole("tab", { name: /Cloud/i });
    expect(cloudTab).toHaveAttribute("aria-selected", "false");
  });

  it("tab counts match model origins", async () => {
    render(
      <TestWrapper>
        <Models />
      </TestWrapper>,
    );

    await waitFor(() => {
      expect(screen.getByText("Models")).toBeInTheDocument();
    });

    // 5 swarm models, 3 cloud models
    const managedTab = screen.getByRole("tab", { name: /Managed/i });
    expect(managedTab.textContent).toContain("5");

    const cloudTab = screen.getByRole("tab", { name: /Cloud/i });
    expect(cloudTab.textContent).toContain("3");
  });

  it("switching to Cloud tab shows cloud models", async () => {
    const user = userEvent.setup();
    render(
      <TestWrapper>
        <Models />
      </TestWrapper>,
    );

    await waitFor(() => {
      expect(screen.getByRole("tab", { name: /Cloud/i })).toBeInTheDocument();
    });

    // Cloud models not visible initially
    expect(screen.queryByText("gpt-4o")).not.toBeInTheDocument();

    await user.click(screen.getByRole("tab", { name: /Cloud/i }));

    // Cloud tab is now selected
    const cloudTab = screen.getByRole("tab", { name: /Cloud/i });
    expect(cloudTab).toHaveAttribute("aria-selected", "true");

    // Cloud models visible
    expect(screen.getByText("gpt-4o")).toBeInTheDocument();
    expect(screen.getByText("gpt-4o-mini")).toBeInTheDocument();
    expect(screen.getByText("claude-sonnet-4-20250514")).toBeInTheDocument();

    // Managed models NOT visible
    expect(screen.queryByText("llama-3.1-70b")).not.toBeInTheDocument();
  });

  it("cloud model rows show provider badges", async () => {
    const user = userEvent.setup();
    render(
      <TestWrapper>
        <Models />
      </TestWrapper>,
    );

    await waitFor(() => {
      expect(screen.getByRole("tab", { name: /Cloud/i })).toBeInTheDocument();
    });

    await user.click(screen.getByRole("tab", { name: /Cloud/i }));

    await waitFor(() => {
      expect(screen.getByText("gpt-4o")).toBeInTheDocument();
    });

    // Provider badges visible (also appears in the filter dropdown)
    expect(screen.getAllByText("openai").length).toBeGreaterThanOrEqual(2);
    expect(screen.getAllByText("anthropic").length).toBeGreaterThanOrEqual(2);
  });

  it("search filters within the active tab", async () => {
    const user = userEvent.setup();
    render(
      <TestWrapper>
        <Models />
      </TestWrapper>,
    );

    await waitFor(() => {
      expect(screen.getByText("llama-3.1-70b")).toBeInTheDocument();
    });

    // Search on managed tab
    const search = screen.getByRole("searchbox", { name: /search models/i });
    await user.type(search, "llama");

    expect(screen.getByText("llama-3.1-70b")).toBeInTheDocument();
    expect(screen.queryByText("gemma-2-27b")).not.toBeInTheDocument();

    // Switch to cloud tab — search should clear
    await user.click(screen.getByRole("tab", { name: /Cloud/i }));

    await waitFor(() => {
      expect(screen.getByText("gpt-4o")).toBeInTheDocument();
    });

    // Search bar is cleared
    expect(search).toHaveValue("");
  });

  it("shows empty search state within tab", async () => {
    const user = userEvent.setup();
    render(
      <TestWrapper>
        <Models />
      </TestWrapper>,
    );

    await waitFor(() => {
      expect(screen.getByText("llama-3.1-70b")).toBeInTheDocument();
    });

    const search = screen.getByRole("searchbox", { name: /search models/i });
    await user.type(search, "nonexistent");

    expect(screen.getByText("No models match your search")).toBeInTheDocument();
  });
});
