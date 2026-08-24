// Tests for the Metrics page multi-select filtering, the dedicated Filters
// modal, filter chips/presets (including v1→v2 preset migration), and the
// comparison (split-by-provider/model) view. Runs against the mock client,
// which mirrors the backend's analytics aggregation semantics.

import { describe, it, expect, beforeEach, vi } from "vitest";
import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { setMockLatency } from "../lib/api/mock";
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
    // local-agent, which was not selected).
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

    expect(await screen.findByText("Provider comparison")).toBeInTheDocument();
    // Every seeded provider appears as a compared entity.
    expect(
      screen.getAllByText("openai").length,
    ).toBeGreaterThanOrEqual(1);
    expect(screen.getAllByText("anthropic").length).toBeGreaterThanOrEqual(1);
    expect(screen.getAllByText("local-agent").length).toBeGreaterThanOrEqual(1);

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
});
