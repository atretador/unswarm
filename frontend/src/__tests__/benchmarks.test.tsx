import { describe, it, expect, beforeEach, vi } from "vitest";
import { render, screen, waitFor, within, fireEvent } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { setMockLatency, mockClient } from "../lib/api/mock";
import { TestWrapper } from "./test-utils";
import Benchmarks from "../features/benchmarks";
import type { BenchmarkResult } from "../lib/api/types";

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
    // Empty prompt → runBenchmark(modelId, undefined) → backend default
    expect(runSpy.mock.calls[0][0]).toBe("1");
    expect(runSpy.mock.calls[0][1]).toBeUndefined();
    // listBenchmarks was refetched after the run so the new entry appears
    expect(listSpy).toHaveBeenCalled();
  });

  it("sends the saved prompt id to runBenchmark", async () => {
    const user = userEvent.setup();
    const runSpy = vi.spyOn(mockClient, "runBenchmark");

    render(
      <TestWrapper>
        <Benchmarks />
      </TestWrapper>,
    );

    await waitFor(() => {
      expect(screen.getByText("Benchmarks")).toBeInTheDocument();
    });

    // Wait for both model and prompt options to populate
    await waitFor(() => {
      expect(screen.getByRole("combobox", { name: "Target model" })).toHaveAttribute("aria-label", "Target model");
    });
    await waitFor(() => {
      const promptCombo = screen.getByRole("combobox", { name: "Prompt (optional)" }) as HTMLSelectElement;
      expect(promptCombo.options.length).toBeGreaterThan(1); // at least "Default prompt" + one saved
    });

    await user.selectOptions(screen.getByRole("combobox", { name: "Target model" }), "1");
    // Select "Code review" prompt (id "p2")
    await user.selectOptions(screen.getByRole("combobox", { name: "Prompt (optional)" }), "p2");
    await user.click(screen.getByRole("button", { name: /run benchmark/i }));

    await waitFor(() => {
      expect(runSpy).toHaveBeenCalledTimes(1);
    });
    expect(runSpy.mock.calls[0][0]).toBe("1");
    // The saved prompt id is sent; the backend resolves it to prompt text
    expect(runSpy.mock.calls[0][1]).toEqual({ promptId: "p2" });
  });

  it("persists the selected prompt text into the run history", async () => {
    const user = userEvent.setup();

    render(
      <TestWrapper>
        <Benchmarks />
      </TestWrapper>,
    );

    await waitFor(() => {
      expect(screen.getByText("Benchmarks")).toBeInTheDocument();
    });

    await waitFor(() => {
      const promptCombo = screen.getByRole("combobox", { name: "Prompt (optional)" }) as HTMLSelectElement;
      expect(promptCombo.options.length).toBeGreaterThan(1);
    });

    await user.selectOptions(screen.getByRole("combobox", { name: "Target model" }), "1");
    // Select "Creative rewrite" prompt (id "p3")
    await user.selectOptions(screen.getByRole("combobox", { name: "Prompt (optional)" }), "p3");
    await user.click(screen.getByRole("button", { name: /run benchmark/i }));

    // The new run appears in the history; its stored prompt must match what was selected.
    await waitFor(() => {
      expect(screen.getAllByRole("button", { name: /llama-3.1-70b/ }).length).toBeGreaterThan(0);
    });
    const rows = screen.getAllByRole("button", { name: /llama-3.1-70b/ });
    await user.click(rows[0]);
    expect(screen.getByText(/Rewrite the following text in a more engaging/)).toBeInTheDocument();
  });

  it("sends undefined prompt when Default prompt is selected", async () => {
    const user = userEvent.setup();
    const runSpy = vi.spyOn(mockClient, "runBenchmark");

    render(
      <TestWrapper>
        <Benchmarks />
      </TestWrapper>,
    );

    await waitFor(() => {
      expect(screen.getByText("Benchmarks")).toBeInTheDocument();
    });

    await waitFor(() => {
      const combo = screen.getByRole("combobox", { name: "Target model" }) as HTMLSelectElement;
      expect(combo.options.length).toBeGreaterThan(1);
    });
    await user.selectOptions(screen.getByRole("combobox", { name: "Target model" }), "1");
    // Default prompt is the first option (value ""), which is the default selection
    await user.click(screen.getByRole("button", { name: /run benchmark/i }));

    await waitFor(() => {
      expect(runSpy).toHaveBeenCalledTimes(1);
    });
    expect(runSpy.mock.calls[0][1]).toBeUndefined();
  });

  it("keeps the prompt selection after a successful run", async () => {
    const user = userEvent.setup();
    vi.spyOn(mockClient, "runBenchmark");

    render(
      <TestWrapper>
        <Benchmarks />
      </TestWrapper>,
    );

    await waitFor(() => {
      expect(screen.getByText("Benchmarks")).toBeInTheDocument();
    });

    await waitFor(() => {
      const promptCombo = screen.getByRole("combobox", { name: "Prompt (optional)" }) as HTMLSelectElement;
      expect(promptCombo.options.length).toBeGreaterThan(1);
    });

    await user.selectOptions(screen.getByRole("combobox", { name: "Target model" }), "1");
    await user.selectOptions(screen.getByRole("combobox", { name: "Prompt (optional)" }), "p2");
    await user.click(screen.getByRole("button", { name: /run benchmark/i }));

    await waitFor(() => {
      expect(screen.getByText("Benchmarks")).toBeInTheDocument();
    });

    // Prompt selection is retained after a successful run
    expect(screen.getByRole("combobox", { name: "Prompt (optional)" })).toHaveValue("p2");
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

  // ─── Prompt library tests ──────────────────────────────────────

  it("run bar renders a prompt select and Manage prompts button", async () => {
    render(
      <TestWrapper>
        <Benchmarks />
      </TestWrapper>,
    );

    await waitFor(() => {
      expect(screen.getByText("Benchmarks")).toBeInTheDocument();
    });

    expect(screen.getByRole("combobox", { name: "Prompt (optional)" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: /manage prompts/i })).toBeInTheDocument();
  });

  it("Manage prompts button opens the prompt library modal", async () => {
    const user = userEvent.setup();
    render(
      <TestWrapper>
        <Benchmarks />
      </TestWrapper>,
    );

    await waitFor(() => {
      expect(screen.getByText("Benchmarks")).toBeInTheDocument();
    });

    await user.click(screen.getByRole("button", { name: /manage prompts/i }));

    const dialog = await screen.findByRole("dialog", { name: "Prompt library" });
    // Scope to the modal to avoid collisions with the run bar select
    expect(within(dialog).getByText("Concise summary")).toBeInTheDocument();
    expect(within(dialog).getByText("Code review")).toBeInTheDocument();
    expect(within(dialog).getByText("Creative rewrite")).toBeInTheDocument();
    expect(within(dialog).getByText("Long-form writing")).toBeInTheDocument();
  });

  it("prompt library modal traps focus, closes on Escape, and restores focus", async () => {
    const user = userEvent.setup();
    render(
      <TestWrapper>
        <Benchmarks />
      </TestWrapper>,
    );

    await waitFor(() => {
      expect(screen.getByText("Benchmarks")).toBeInTheDocument();
    });

    const manageBtn = screen.getByRole("button", { name: /manage prompts/i });
    await user.click(manageBtn);

    const dialog = await screen.findByRole("dialog", { name: "Prompt library" });
    // Initial focus lands on the close button (the dialog shell's first focusable).
    expect(within(dialog).getByRole("button", { name: "Close dialog" })).toHaveFocus();

    // Tab from the last focusable wraps to the first inside the dialog.
    const closeBtn = within(dialog).getByRole("button", { name: "Close dialog" });
    await user.tab();
    // Cycle through focusables until we come back to the close button (wrapped).
    let active = document.activeElement as HTMLElement;
    const seen = new Set<HTMLElement>();
    while (!seen.has(active)) {
      seen.add(active);
      await user.tab();
      active = document.activeElement as HTMLElement;
    }
    expect(seen.has(closeBtn)).toBe(true);

    // Escape closes the dialog and restores focus to the trigger.
    await user.keyboard("{Escape}");
    expect(screen.queryByRole("dialog", { name: "Prompt library" })).not.toBeInTheDocument();
    expect(manageBtn).toHaveFocus();
  });


  it("selecting a prompt in the library shows it in the editor", async () => {
    const user = userEvent.setup();
    render(
      <TestWrapper>
        <Benchmarks />
      </TestWrapper>,
    );

    await waitFor(() => {
      expect(screen.getByText("Benchmarks")).toBeInTheDocument();
    });

    await user.click(screen.getByRole("button", { name: /manage prompts/i }));

    const dialog = await screen.findByRole("dialog", { name: "Prompt library" });
    await user.click(within(dialog).getByText("Concise summary"));

    expect(within(dialog).getByLabelText("Name")).toHaveValue("Concise summary");
    expect((within(dialog).getByLabelText("Prompt text") as HTMLTextAreaElement).value).toContain("Summarize the input");
  });

  it("creating a new prompt calls createPrompt and shows in list", async () => {
    const user = userEvent.setup();
    const createSpy = vi.spyOn(mockClient, "createPrompt").mockResolvedValueOnce({
      id: "new-1", name: "Test prompt", text: "Hello world", maxTokens: 256,
      createdAt: new Date().toISOString(), updatedAt: new Date().toISOString(),
    });

    render(
      <TestWrapper>
        <Benchmarks />
      </TestWrapper>,
    );

    await waitFor(() => {
      expect(screen.getByText("Benchmarks")).toBeInTheDocument();
    });

    await user.click(screen.getByRole("button", { name: /manage prompts/i }));

    const dialog = await screen.findByRole("dialog", { name: "Prompt library" });

    // Click "+" to create new
    await user.click(within(dialog).getByText("New prompt"));
    expect(within(dialog).getByLabelText("Name")).toHaveValue("");
    expect((within(dialog).getByLabelText("Prompt text") as HTMLTextAreaElement).value).toBe("");

    await user.type(within(dialog).getByLabelText("Name"), "Test prompt");
    await user.type(within(dialog).getByLabelText("Prompt text"), "Hello world");
    await user.click(within(dialog).getByRole("button", { name: /create/i }));

    await waitFor(() => {
      expect(createSpy).toHaveBeenCalledTimes(1);
    });
    expect(createSpy.mock.calls[0][0]).toEqual({ name: "Test prompt", text: "Hello world", maxTokens: 256 });
  });

  it("editing and saving an existing prompt calls updatePrompt", async () => {
    const user = userEvent.setup();
    const updateSpy = vi.spyOn(mockClient, "updatePrompt").mockResolvedValueOnce({
      id: "p1", name: "Better summary", text: "Updated text", maxTokens: 256,
      createdAt: new Date().toISOString(), updatedAt: new Date().toISOString(),
    });

    render(
      <TestWrapper>
        <Benchmarks />
      </TestWrapper>,
    );

    await waitFor(() => {
      expect(screen.getByText("Benchmarks")).toBeInTheDocument();
    });

    await user.click(screen.getByRole("button", { name: /manage prompts/i }));

    const dialog = await screen.findByRole("dialog", { name: "Prompt library" });
    await user.click(within(dialog).getByText("Concise summary"));

    // Clear and retype the name
    const nameInput = within(dialog).getByLabelText("Name");
    await user.clear(nameInput);
    await user.type(nameInput, "Better summary");

    const saveBtn = within(dialog).getByRole("button", { name: /save/i });
    await user.click(saveBtn);

    await waitFor(() => {
      expect(updateSpy).toHaveBeenCalledTimes(1);
    });
    expect(updateSpy.mock.calls[0][1]).toMatchObject({ name: "Better summary" });
  });

  it("delete requires confirmation then removes the prompt", async () => {
    const user = userEvent.setup();
    const deleteSpy = vi.spyOn(mockClient, "deletePrompt").mockResolvedValueOnce(undefined);

    render(
      <TestWrapper>
        <Benchmarks />
      </TestWrapper>,
    );

    await waitFor(() => {
      expect(screen.getByText("Benchmarks")).toBeInTheDocument();
    });

    await user.click(screen.getByRole("button", { name: /manage prompts/i }));

    const dialog = await screen.findByRole("dialog", { name: "Prompt library" });

    // First click — arm confirmation
    await user.click(within(dialog).getByLabelText("Delete Concise summary"));
    expect(within(dialog).getByText("Delete")).toBeInTheDocument();

    // Confirm
    await user.click(within(dialog).getByText("Delete"));

    await waitFor(() => {
      expect(deleteSpy).toHaveBeenCalledWith("p1");
    });
  });

  it("delete cancel reverts the confirmation", async () => {
    const user = userEvent.setup();
    const deleteSpy = vi.spyOn(mockClient, "deletePrompt");

    render(
      <TestWrapper>
        <Benchmarks />
      </TestWrapper>,
    );

    await waitFor(() => {
      expect(screen.getByText("Benchmarks")).toBeInTheDocument();
    });

    await user.click(screen.getByRole("button", { name: /manage prompts/i }));

    const dialog = await screen.findByRole("dialog", { name: "Prompt library" });

    // First click — arm confirmation
    await user.click(within(dialog).getByLabelText("Delete Concise summary"));

    // Cancel
    await user.click(within(dialog).getByText("Cancel"));

    // No delete call
    expect(deleteSpy).not.toHaveBeenCalled();
  });

  it("closing the prompt library modal works", async () => {
    const user = userEvent.setup();
    render(
      <TestWrapper>
        <Benchmarks />
      </TestWrapper>,
    );

    await waitFor(() => {
      expect(screen.getByText("Benchmarks")).toBeInTheDocument();
    });

    await user.click(screen.getByRole("button", { name: /manage prompts/i }));

    await screen.findByRole("dialog", { name: "Prompt library" });

    // Close via X button
    await user.click(screen.getByLabelText("Close dialog"));

    await waitFor(() => {
      expect(screen.queryByRole("dialog", { name: "Prompt library" })).not.toBeInTheDocument();
    });
  });

  // ─── Prompt version history tests ───────────────────────────────

  it("shows history button next to prompt version", async () => {
    const user = userEvent.setup();
    render(
      <TestWrapper>
        <Benchmarks />
      </TestWrapper>,
    );

    await waitFor(() => {
      expect(screen.getByText("Benchmarks")).toBeInTheDocument();
    });

    await user.click(screen.getByRole("button", { name: /manage prompts/i }));
    const dialog = await screen.findByRole("dialog", { name: "Prompt library" });

    // History icon buttons should be present for each prompt
    const historyButtons = within(dialog).getAllByRole("button", { name: /version history for/i });
    expect(historyButtons.length).toBeGreaterThanOrEqual(4);
  });

  it("fetches and displays version list when history is opened", async () => {
    const user = userEvent.setup();
    const listVersionsSpy = vi.spyOn(mockClient, "listPromptVersions");

    render(
      <TestWrapper>
        <Benchmarks />
      </TestWrapper>,
    );

    await waitFor(() => {
      expect(screen.getByText("Benchmarks")).toBeInTheDocument();
    });

    await user.click(screen.getByRole("button", { name: /manage prompts/i }));
    const dialog = await screen.findByRole("dialog", { name: "Prompt library" });

    // Click the history button for "Concise summary" (p1)
    await user.click(within(dialog).getByRole("button", { name: /version history for concise summary/i }));

    await waitFor(() => {
      expect(listVersionsSpy).toHaveBeenCalledWith("p1");
    });

    // Version list should appear — v3 appears both as left-panel badge and right-panel header
    // so we check all occurrences exist; at minimum the version history panel has v3, v2, v1
    await waitFor(() => {
      expect(within(dialog).getAllByText("v3").length).toBeGreaterThanOrEqual(1);
    });
    expect(within(dialog).getAllByText("v2").length).toBeGreaterThanOrEqual(1);
    expect(within(dialog).getAllByText("v1").length).toBeGreaterThanOrEqual(1);

    // "Back to Editor" link should be visible
    expect(within(dialog).getByText(/back to editor/i)).toBeInTheDocument();
  });

  it("shows text preview when view button is clicked", async () => {
    const user = userEvent.setup();
    vi.spyOn(mockClient, "listPromptVersions");

    render(
      <TestWrapper>
        <Benchmarks />
      </TestWrapper>,
    );

    await waitFor(() => {
      expect(screen.getByText("Benchmarks")).toBeInTheDocument();
    });

    await user.click(screen.getByRole("button", { name: /manage prompts/i }));
    const dialog = await screen.findByRole("dialog", { name: "Prompt library" });

    // Open history for p1
    await user.click(within(dialog).getByRole("button", { name: /version history for concise summary/i }));

    // Wait for version history heading
    await waitFor(() => {
      expect(within(dialog).getByText("Version History", { exact: false })).toBeInTheDocument();
    });

    // Click the first view button (v3) to show its preview
    const viewBtn = within(dialog).getByRole("button", { name: "View version 3" });
    fireEvent.click(viewBtn);

    // Preview area should show the v3 text (exact mock data for p1 v3)
    await waitFor(() => {
      expect(
        within(dialog).getByText(/Use plain language without jargon/i),
      ).toBeInTheDocument();
    });
  });

  it("calls rollbackPrompt on confirm", async () => {
    const user = userEvent.setup();
    vi.spyOn(mockClient, "listPromptVersions");
    const rollbackSpy = vi.spyOn(mockClient, "rollbackPrompt");

    render(
      <TestWrapper>
        <Benchmarks />
      </TestWrapper>,
    );

    await waitFor(() => {
      expect(screen.getByText("Benchmarks")).toBeInTheDocument();
    });

    await user.click(screen.getByRole("button", { name: /manage prompts/i }));
    const dialog = await screen.findByRole("dialog", { name: "Prompt library" });

    // Open history for p1
    await user.click(within(dialog).getByRole("button", { name: /version history for concise summary/i }));

    // Wait for version history heading
    await waitFor(() => {
      expect(within(dialog).getByText("Version History", { exact: false })).toBeInTheDocument();
    });

    // Click rollback button for v1 (not current version — has rollback button)
    const rollbackBtn = within(dialog).getByRole("button", { name: "Rollback to version 1" });
    fireEvent.click(rollbackBtn);

    // Confirmation dialog should appear — find the ConfirmDialog by its title text
    await screen.findByText("Restore this version?");
    expect(screen.getByText(/this will create a new version/i)).toBeInTheDocument();

    // Confirm the rollback — find the primary "Restore" button
    await user.click(screen.getByRole("button", { name: /restore/i }));

    await waitFor(() => {
      expect(rollbackSpy).toHaveBeenCalledWith("p1", 1);
    });
  });

  // ─── Historic button tests ──────────────────────────────────────

  it("renders history button on each benchmark row", async () => {
    render(
      <TestWrapper>
        <Benchmarks />
      </TestWrapper>,
    );

    await waitFor(() => {
      expect(screen.getByText("Benchmarks")).toBeInTheDocument();
    });

    // Each benchmark row should have a "View historic results" button
    const historyButtons = screen.getAllByRole("button", { name: /historic results/i });
    expect(historyButtons.length).toBeGreaterThanOrEqual(1);
  });

  it("opens model results modal when history button is clicked", async () => {
    const user = userEvent.setup();
    render(
      <TestWrapper>
        <Benchmarks />
      </TestWrapper>,
    );

    await waitFor(() => {
      expect(screen.getByText("Benchmarks")).toBeInTheDocument();
    });

    // Click the first history button
    const historyButtons = screen.getAllByRole("button", { name: /historic results/i });
    await user.click(historyButtons[0]);

    // ModelResultsModal should open with the correct model name
    await waitFor(() => {
      expect(screen.getByRole("dialog", { name: /benchmark results$/i })).toBeInTheDocument();
    });
  });

  // ─── Run results modal: reasoning + output sections ─────────────

  function makeResult(overrides: Partial<BenchmarkResult> = {}): BenchmarkResult {
    return {
      id: "bx",
      modelId: "9",
      modelName: "thinker-8b",
      prompt: "Explain the scheduling strategy.",
      promptId: null,
      promptName: null,
      promptVersion: null,
      tokensPerSec: 42,
      latencyMs: 100,
      tokensGenerated: 256,
      timestamp: new Date().toISOString(),
      status: "completed",
      errorMessage: null,
      ...overrides,
    };
  }

  async function openRunModal(result: BenchmarkResult) {
    const user = userEvent.setup();
    vi.spyOn(mockClient, "listBenchmarks").mockResolvedValue([result]);

    render(
      <TestWrapper>
        <Benchmarks />
      </TestWrapper>,
    );

    const resultsBtn = await screen.findByRole("button", {
      name: `Results for ${result.modelName} run`,
    });
    await user.click(resultsBtn);

    const dialog = await screen.findByRole("dialog", { name: /run details/i });
    return { user, dialog };
  }

  it("reasoning-only entry shows the Thinking section without a false empty state", async () => {
    const { dialog } = await openRunModal(
      makeResult({ reasoning: "Step one: check the queue depth before sizing the batch." }),
    );

    // Thinking header is present, Output is not
    expect(within(dialog).getByText("Thinking")).toBeInTheDocument();
    expect(within(dialog).queryByText("Output")).not.toBeInTheDocument();

    // Reasoning text exists but is hidden while collapsed (collapsed by default)
    expect(
      within(dialog).queryByText(/Step one: check the queue depth/),
    ).not.toBeInTheDocument();

    // No false "No response captured" — reasoning counts as content
    expect(within(dialog).queryByText("No response captured")).not.toBeInTheDocument();
  });

  it("entry with both reasoning and response shows both sections", async () => {
    const { dialog } = await openRunModal(
      makeResult({
        reasoning: "Compare the two schedulers on tail latency first.",
        response: "The scheduler drains the active slot before swapping weights.",
      }),
    );

    expect(within(dialog).getByText("Thinking")).toBeInTheDocument();
    expect(within(dialog).getByText("Output")).toBeInTheDocument();

    // Output is expanded by default; Thinking is collapsed by default
    expect(
      within(dialog).getByText(/drains the active slot before swapping weights/),
    ).toBeInTheDocument();
    expect(
      within(dialog).queryByText(/Compare the two schedulers on tail latency/),
    ).not.toBeInTheDocument();

    // Per-section copy affordances are discoverable
    expect(within(dialog).getByRole("button", { name: "Copy output to clipboard" })).toBeInTheDocument();
    expect(within(dialog).getByRole("button", { name: "Copy thinking to clipboard" })).toBeInTheDocument();
  });

  it("entry with neither reasoning nor response shows No response captured", async () => {
    const { dialog } = await openRunModal(makeResult());

    expect(within(dialog).getByText("No response captured")).toBeInTheDocument();
    expect(within(dialog).queryByText("Thinking")).not.toBeInTheDocument();
    expect(within(dialog).queryByText("Output")).not.toBeInTheDocument();
  });

  it("thinking and output sections toggle via aria-expanded headers", async () => {
    const { user, dialog } = await openRunModal(
      makeResult({
        reasoning: "Reasoning trace that should appear when expanded.",
        response: "Visible output text.",
      }),
    );

    const thinkingToggle = within(dialog).getByRole("button", { name: "Thinking" });
    expect(thinkingToggle).toHaveAttribute("aria-expanded", "false");

    // Expand thinking → reasoning becomes visible
    await user.click(thinkingToggle);
    expect(thinkingToggle).toHaveAttribute("aria-expanded", "true");
    expect(
      within(dialog).getByText(/Reasoning trace that should appear/),
    ).toBeInTheDocument();

    // Collapse again → hidden
    await user.click(thinkingToggle);
    expect(thinkingToggle).toHaveAttribute("aria-expanded", "false");
    expect(
      within(dialog).queryByText(/Reasoning trace that should appear/),
    ).not.toBeInTheDocument();

    // Output starts expanded and can be collapsed too
    const outputToggle = within(dialog).getByRole("button", { name: "Output" });
    expect(outputToggle).toHaveAttribute("aria-expanded", "true");
    expect(within(dialog).getByText(/Visible output text/)).toBeInTheDocument();

    await user.click(outputToggle);
    expect(outputToggle).toHaveAttribute("aria-expanded", "false");
    expect(within(dialog).queryByText(/Visible output text/)).not.toBeInTheDocument();
  });

  // ─── Cloud model tests ──────────────────────────────────────────

  it("cloud models appear in the dropdown with [Cloud] prefix", async () => {
    render(
      <TestWrapper>
        <Benchmarks />
      </TestWrapper>,
    );

    await waitFor(() => {
      expect(screen.getByText("Benchmarks")).toBeInTheDocument();
    });

    await waitFor(() => {
      const combo = screen.getByRole("combobox", { name: "Target model" }) as HTMLSelectElement;
      expect(combo.options.length).toBeGreaterThan(1);
    });

    const combo = screen.getByRole("combobox", { name: "Target model" }) as HTMLSelectElement;
    const optionTexts = Array.from(combo.options).map((o) => o.text);
    expect(optionTexts.some((t) => t.includes("[Cloud]") && t.includes("gpt-4o"))).toBe(true);
    expect(optionTexts.some((t) => t.includes("[Cloud]") && t.includes("openai"))).toBe(true);
    expect(optionTexts.some((t) => t.includes("[Cloud]") && t.includes("claude-sonnet-4-20250514"))).toBe(true);
    expect(optionTexts.some((t) => t.includes("[Cloud]") && t.includes("anthropic"))).toBe(true);
  });

  it("can run a benchmark with a cloud model", async () => {
    const user = userEvent.setup();
    const runSpy = vi.spyOn(mockClient, "runBenchmark");

    render(
      <TestWrapper>
        <Benchmarks />
      </TestWrapper>,
    );

    await waitFor(() => {
      expect(screen.getByText("Benchmarks")).toBeInTheDocument();
    });

    await waitFor(() => {
      const combo = screen.getByRole("combobox", { name: "Target model" }) as HTMLSelectElement;
      expect(combo.options.length).toBeGreaterThan(1);
    });

    // Select gpt-4o (cloud model id "c1")
    await user.selectOptions(screen.getByRole("combobox", { name: "Target model" }), "c1");

    // Cloud models are always ready — run button should be enabled
    const runBtn = screen.getByRole("button", { name: /run benchmark/i });
    expect(runBtn).not.toBeDisabled();

    await user.click(runBtn);

    await waitFor(() => {
      expect(runSpy).toHaveBeenCalledTimes(1);
    });
    expect(runSpy.mock.calls[0][0]).toBe("c1");
  });

  it("swarm models show name without [Cloud] prefix in dropdown", async () => {
    render(
      <TestWrapper>
        <Benchmarks />
      </TestWrapper>,
    );

    await waitFor(() => {
      expect(screen.getByText("Benchmarks")).toBeInTheDocument();
    });

    await waitFor(() => {
      const combo = screen.getByRole("combobox", { name: "Target model" }) as HTMLSelectElement;
      expect(combo.options.length).toBeGreaterThan(1);
    });

    const combo = screen.getByRole("combobox", { name: "Target model" }) as HTMLSelectElement;
    const optionTexts = Array.from(combo.options).map((o) => o.text);
    // Swarm ready models show just the name
    expect(optionTexts).toContain("llama-3.1-70b");
    expect(optionTexts).toContain("mistral-large-2");
    // Swarm non-ready models show status suffix
    expect(optionTexts.some((t) => t.includes("codestral-22b") && t.includes("validating"))).toBe(true);
    expect(optionTexts.some((t) => t.includes("phi-3.5-mini") && t.includes("deprecated"))).toBe(true);
  });
});
