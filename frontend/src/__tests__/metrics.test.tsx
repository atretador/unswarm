// Tests for the Metrics page multi-select filtering, the dedicated Filters
// modal, filter chips/presets (including v1→v2 preset migration), and the
// comparison (split-by-provider/model) view. Runs against the mock client,
// which mirrors the backend's analytics aggregation semantics.

import { describe, it, expect, beforeEach, vi } from "vitest";
import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { mockClient, setMockLatency } from "../lib/api/mock";
import type {
  MetricsTimeBucket,
  ModelUsageSummary,
  ProviderUsageSummary,
  UsageTotalsResponse,
} from "../lib/api/types";
import { TestWrapper } from "./test-utils";
import Metrics from "../features/metrics";

beforeEach(() => {
  setMockLatency(0);
  vi.restoreAllMocks();
  localStorage.clear();
});

async function renderMetrics() {
  render(
    <TestWrapper>
      <Metrics />
    </TestWrapper>,
  );
  await waitFor(() => {
    expect(screen.getByText("Total requests")).toBeInTheDocument();
  });
}

// ── Controlled fixtures for the cost-engine tests ────────────────

function makeBucket(
  overrides: Partial<MetricsTimeBucket> & { bucketStart: string },
): MetricsTimeBucket {
  return {
    bucketEnd: new Date(
      new Date(overrides.bucketStart).getTime() + 86_400_000,
    ).toISOString(),
    group: null,
    provider: null,
    model: null,
    requestCount: 1,
    streamingRequests: 0,
    promptTokens: 0,
    completionTokens: 0,
    cachedTokens: 0,
    avgLatencyMs: 12,
    ...overrides,
  };
}

function makeTotals(from: string, to: string): UsageTotalsResponse {
  return {
    from,
    to,
    totalRequests: 2,
    totalStreamingRequests: 0,
    totalPromptTokens: 2_000_000,
    totalCompletionTokens: 0,
    totalCachedTokens: 0,
    avgLatencyMs: 12,
  };
}

function makeModel(
  provider: string,
  model: string,
  promptTokens: number,
): ModelUsageSummary {
  return {
    provider,
    model,
    requestCount: 1,
    streamingRequests: 0,
    promptTokens,
    completionTokens: 0,
    cachedTokens: 0,
    avgLatencyMs: 12,
  };
}

function makeProvider(provider: string, promptTokens: number): ProviderUsageSummary {
  return {
    provider,
    requestCount: 1,
    streamingRequests: 0,
    promptTokens,
    completionTokens: 0,
    cachedTokens: 0,
  };
}

/** Spy the mock client so the page sees only controlled cost fixtures. */
function stubMetrics(
  grouped: MetricsTimeBucket[],
  totals: UsageTotalsResponse,
  models: ModelUsageSummary[],
  providers: ProviderUsageSummary[],
  combined: MetricsTimeBucket[] = [],
) {
  vi.spyOn(mockClient, "getMetricsSummary").mockImplementation(async (opts) =>
    opts?.groupBy === "provider_model" ? grouped : combined,
  );
  vi.spyOn(mockClient, "getMetricsTotals").mockResolvedValue(totals);
  vi.spyOn(mockClient, "getMetricsModels").mockResolvedValue(models);
  vi.spyOn(mockClient, "getMetricsProviders").mockResolvedValue(providers);
}

describe("Metrics page", () => {
  it("renders summary cards and the per-model breakdown from aggregated usage", async () => {
    await renderMetrics();

    expect(screen.getByText("Prompt tokens")).toBeInTheDocument();
    expect(screen.getByText("Cache hit rate")).toBeInTheDocument();
    expect(screen.getByText("Per-model breakdown")).toBeInTheDocument();
    // Providers from the synthetic usage seed appear in the model table badges.
    expect(screen.getAllByText("openai").length).toBeGreaterThan(0);
    expect(screen.getByText("gpt-4o")).toBeInTheDocument();
  });

  it("shows the empty-filter hint while no filters are active", async () => {
    await renderMetrics();

    expect(screen.getByText("All providers · All models")).toBeInTheDocument();
    expect(screen.queryByText("Clear all")).not.toBeInTheDocument();
  });

  it("selects multiple providers in the Filters modal and reflects them as chips", async () => {
    const user = userEvent.setup();
    await renderMetrics();

    await user.click(screen.getByRole("button", { name: /filters/i }));
    const dialog = screen.getByRole("dialog");
    expect(within(dialog).getByText("Filter data")).toBeInTheDocument();

    // Search narrows the provider list.
    await user.type(
      within(dialog).getByPlaceholderText("Search providers…"),
      "open",
    );
    expect(within(dialog).getByRole("checkbox", { name: /openai/ })).toBeInTheDocument();
    expect(
      within(dialog).queryByRole("checkbox", { name: /anthropic/ }),
    ).not.toBeInTheDocument();

    await user.click(within(dialog).getByRole("checkbox", { name: /openai/ }));

    // Clearing the search reveals the second provider to select.
    await user.clear(within(dialog).getByPlaceholderText("Search providers…"));
    await user.click(within(dialog).getByRole("checkbox", { name: /anthropic/ }));

    await user.click(within(dialog).getByRole("button", { name: "Apply (2)" }));

    // Both selections appear as removable chips.
    expect(
      screen.getByLabelText("Remove provider filter openai"),
    ).toBeInTheDocument();
    expect(
      screen.getByLabelText("Remove provider filter anthropic"),
    ).toBeInTheDocument();

    // The applied selection narrows the per-model table (llama-3 belongs to
    // the host agent, which was not selected).
    await waitFor(() => {
      expect(screen.queryByText("llama-3")).not.toBeInTheDocument();
    });
  });

  it("removes an individual chip and clears the rest", async () => {
    const user = userEvent.setup();
    await renderMetrics();

    await user.click(screen.getByRole("button", { name: /filters/i }));
    const dialog = screen.getByRole("dialog");
    await user.click(within(dialog).getByRole("checkbox", { name: /openai/ }));
    await user.click(within(dialog).getByRole("checkbox", { name: /anthropic/ }));
    await user.click(within(dialog).getByRole("button", { name: "Apply (2)" }));

    await user.click(screen.getByLabelText("Remove provider filter anthropic"));
    expect(
      screen.queryByLabelText("Remove provider filter anthropic"),
    ).not.toBeInTheDocument();
    expect(
      screen.getByLabelText("Remove provider filter openai"),
    ).toBeInTheDocument();

    await user.click(screen.getByRole("button", { name: /clear all/i }));
    expect(screen.getByText("All providers · All models")).toBeInTheDocument();
  });

  it("modal cancel discards drafted selections", async () => {
    const user = userEvent.setup();
    await renderMetrics();

    await user.click(screen.getByRole("button", { name: /filters/i }));
    const dialog = screen.getByRole("dialog");
    await user.click(within(dialog).getByRole("checkbox", { name: /openai/ }));
    await user.click(within(dialog).getByRole("button", { name: /cancel/i }));

    expect(
      screen.queryByLabelText("Remove provider filter openai"),
    ).not.toBeInTheDocument();

    // Reopening seeds the draft from the (unchanged) live selection.
    await user.click(screen.getByRole("button", { name: /filters/i }));
    const reopened = screen.getByRole("dialog");
    const checkbox = within(reopened).getByRole("checkbox", { name: /openai/ });
    expect(checkbox).not.toBeChecked();
  });

  it("model search finds models across providers and applies model chips", async () => {
    const user = userEvent.setup();
    await renderMetrics();

    await user.click(screen.getByRole("button", { name: /filters/i }));
    const dialog = screen.getByRole("dialog");
    await user.type(within(dialog).getByPlaceholderText("Search models…"), "claude");
    await user.click(within(dialog).getByRole("checkbox", { name: /claude/ }));
    await user.click(within(dialog).getByRole("button", { name: "Apply (1)" }));

    expect(
      screen.getByLabelText("Remove model filter claude-3-5-sonnet"),
    ).toBeInTheDocument();

    // Model-only selection removes other providers' models from the table.
    await waitFor(() => {
      expect(screen.queryByText("gpt-4o")).not.toBeInTheDocument();
    });
    // The selected model remains (chip + its breakdown row).
    expect(screen.getAllByText("claude-3-5-sonnet").length).toBeGreaterThanOrEqual(1);
  });

  it("splits the time series by provider and shows the comparison table", async () => {
    const user = userEvent.setup();
    await renderMetrics();

    await user.click(screen.getByRole("button", { name: "By provider" }));

    expect(await screen.findByText("Provider / Agent comparison")).toBeInTheDocument();
    // Every seeded provider appears as a compared entity.
    expect(
      screen.getAllByText("openai").length,
    ).toBeGreaterThanOrEqual(1);
    expect(screen.getAllByText("anthropic").length).toBeGreaterThanOrEqual(1);
    expect(screen.getAllByText("host").length).toBeGreaterThanOrEqual(1);

    // Switching to model split relabels the table.
    await user.click(screen.getByRole("button", { name: "By model" }));
    expect(await screen.findByText("Model comparison")).toBeInTheDocument();
  });

  it("saves the current selection as a preset and reapplies it after clearing", async () => {
    const user = userEvent.setup();
    await renderMetrics();

    await user.click(screen.getByRole("button", { name: /filters/i }));
    let dialog = screen.getByRole("dialog");
    await user.click(within(dialog).getByRole("checkbox", { name: /openai/ }));
    await user.click(within(dialog).getByRole("button", { name: "Apply (1)" }));

    await user.type(screen.getByLabelText("Preset name"), "cloud only");
    await user.click(screen.getByRole("button", { name: /save/i }));

    expect(screen.getByText("cloud only")).toBeInTheDocument();

    await user.click(screen.getByRole("button", { name: /clear all/i }));
    expect(screen.getByText("All providers · All models")).toBeInTheDocument();

    await user.click(screen.getByText("cloud only"));
    expect(
      screen.getByLabelText("Remove provider filter openai"),
    ).toBeInTheDocument();
    void dialog;
  });

  it("migrates legacy v1 presets (singular provider/model) instead of dropping them", async () => {
    localStorage.setItem(
      "unswarm-metrics-presets",
      JSON.stringify([
        { name: "legacy", provider: "openai", model: "", range: "24h" },
      ]),
    );

    const user = userEvent.setup();
    await renderMetrics();

    expect(screen.getByText("legacy")).toBeInTheDocument();

    // Applying the migrated preset restores its provider selection.
    await user.click(screen.getByText("legacy"));
    expect(
      screen.getByLabelText("Remove provider filter openai"),
    ).toBeInTheDocument();
  });

  it("prices per-model cost across a mid-window rate boundary", async () => {
    stubMetrics(
      [
        makeBucket({
          bucketStart: "2024-01-05T00:00:00.000Z",
          group: "openai|gpt-4o",
          provider: "openai",
          model: "gpt-4o",
          promptTokens: 1_000_000,
        }),
        makeBucket({
          bucketStart: "2024-01-20T00:00:00.000Z",
          group: "openai|gpt-4o",
          provider: "openai",
          model: "gpt-4o",
          promptTokens: 1_000_000,
        }),
      ],
      makeTotals("2024-01-01T00:00:00.000Z", "2024-02-01T00:00:00.000Z"),
      [makeModel("openai", "gpt-4o", 2_000_000)],
      [makeProvider("openai", 2_000_000)],
    );
    // $1/1M before Jan 15, $2/1M from Jan 15 → 1 + 2 = $3.00.
    localStorage.setItem(
      "unswarm-cost-rates:v3",
      JSON.stringify([
        { id: "r1", provider: "openai", model: null, from: null, to: "2024-01-15", mode: "per-token", promptPer1M: 1, completionPer1M: 0, monthlyPrice: 0, monthlyCost: 0 },
        { id: "r2", provider: "openai", model: null, from: "2024-01-15", to: null, mode: "per-token", promptPer1M: 2, completionPer1M: 0, monthlyPrice: 0, monthlyCost: 0 },
      ]),
    );

    await renderMetrics();

    const costs = await screen.findAllByText("$3.00");
    expect(costs.length).toBeGreaterThanOrEqual(1);
  });

  it("prorates a flat monthly rate for the active window", async () => {
    stubMetrics(
      [
        makeBucket({
          bucketStart: "2024-01-10T00:00:00.000Z",
          group: "acme|m1",
          provider: "acme",
          model: "m1",
          promptTokens: 500,
        }),
      ],
      makeTotals("2024-01-01T00:00:00.000Z", "2024-01-16T00:00:00.000Z"),
      [makeModel("acme", "m1", 500)],
      [makeProvider("acme", 500)],
    );
    // $200/mo over a 15-of-31-day January window → $96.77.
    localStorage.setItem(
      "unswarm-cost-rates:v3",
      JSON.stringify([
        { id: "s1", provider: "acme", model: null, from: null, to: null, mode: "subscription", promptPer1M: 0, completionPer1M: 0, monthlyPrice: 200, monthlyCost: 0 },
      ]),
    );

    await renderMetrics();

    expect(
      await screen.findByText(/\+ \$96\.77 subscriptions/),
    ).toBeInTheDocument();
  });

  it("shows the missing-rate hint when no periods are configured", async () => {
    stubMetrics(
      [
        makeBucket({
          bucketStart: "2024-01-10T00:00:00.000Z",
          group: "openai|gpt-4o",
          provider: "openai",
          model: "gpt-4o",
          promptTokens: 1_000,
        }),
      ],
      makeTotals("2024-01-01T00:00:00.000Z", "2024-02-01T00:00:00.000Z"),
      [makeModel("openai", "gpt-4o", 1_000)],
      [makeProvider("openai", 1_000)],
    );

    await renderMetrics();

    expect(
      await screen.findByText(/have no cost rate set/),
    ).toBeInTheDocument();
    expect(screen.getByText("open the Cost Calculator")).toBeInTheDocument();
  });
});
