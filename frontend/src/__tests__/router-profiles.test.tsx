import { describe, it, expect, beforeEach, vi } from "vitest";
import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { setMockLatency, mockClient } from "../lib/api/mock";
import { TestWrapper } from "./test-utils";
import RouterProfiles from "../features/router-profiles";
import type { RouterProfile, Model } from "../lib/api/types";

beforeEach(() => {
  setMockLatency(0);
  vi.restoreAllMocks();
  // jsdom doesn't implement scrollIntoView – stub it for ModelSearchInput
  Element.prototype.scrollIntoView = vi.fn();
});

// ─── Test Data ───────────────────────────────────────────────────

const now = new Date().toISOString();

const mockProfiles: RouterProfile[] = [
  {
    id: "rp-1",
    name: "Fast Models",
    mode: "auto",
    entries: [
      { modelId: "1", priority: 1, isEnabled: true, thinkingEffortOverride: null },
      { modelId: "c1", priority: 2, isEnabled: true, thinkingEffortOverride: null },
    ],
    activeModelId: "1",
    createdAt: now,
    updatedAt: now,
  },
  {
    id: "rp-2",
    name: "Code Assistant",
    mode: "manual",
    entries: [
      { modelId: "3", priority: 0, isEnabled: true, thinkingEffortOverride: "high" },
    ],
    activeModelId: "3",
    createdAt: now,
    updatedAt: now,
  },
];

const mockModels: Model[] = [
  {
    id: "1",
    name: "llama-3.1-70b",
    family: "Llama",
    parameterSize: "70B",
    quantization: "Q4_K_M",
    status: "ready",
    lastBenchmark: null,
    contextWindow: 128000,
    containerImage: "unswarm/llama3.1:70b-q4km",
    sourceRuntimeId: "rc1",
    sourceRuntimeName: "llama3.1-70b",
    sourceRuntimeAgent: "host",
    origin: "swarm",
    providerName: null,
    createdAt: now,
    updatedAt: now,
  },
  {
    id: "c1",
    name: "gpt-4o",
    family: "GPT",
    parameterSize: "—",
    quantization: "—",
    status: "ready",
    lastBenchmark: null,
    contextWindow: 128000,
    containerImage: "",
    sourceRuntimeId: null,
    sourceRuntimeName: null,
    sourceRuntimeAgent: null,
    origin: "cloud",
    providerName: "openai",
    createdAt: now,
    updatedAt: now,
  },
  {
    id: "3",
    name: "codestral-22b",
    family: "Mistral",
    parameterSize: "22B",
    quantization: "Q6_K",
    status: "validating",
    lastBenchmark: null,
    contextWindow: 32000,
    containerImage: "unswarm/codestral:22b-q6k",
    sourceRuntimeId: null,
    sourceRuntimeName: null,
    sourceRuntimeAgent: null,
    origin: "swarm",
    providerName: null,
    createdAt: now,
    updatedAt: now,
  },
];

// ─── Helpers ──────────────────────────────────────────────────────

function renderWithProfiles(
  profiles: RouterProfile[] = mockProfiles,
  models: Model[] = mockModels,
) {
  vi.spyOn(mockClient, "listRouterProfiles").mockResolvedValue(profiles);
  vi.spyOn(mockClient, "listModels").mockResolvedValue(models);
  vi.spyOn(mockClient, "getRouterProfileStatus").mockResolvedValue({});

  return render(
    <TestWrapper initialEntries={["/router-profiles"]}>
      <RouterProfiles />
    </TestWrapper>,
  );
}

async function waitForProfiles() {
  await waitFor(() => {
    expect(screen.getByText("Fast Models")).toBeInTheDocument();
  });
}

/** Find icon-only delete buttons (Trash2 SVG, no text). */
function findDeleteButtons(): HTMLElement[] {
  return screen.getAllByRole("button").filter((btn) => {
    // Delete buttons: no text, contain an SVG icon
    const text = btn.textContent?.trim() ?? "";
    return text === "" && btn.querySelector("svg") !== null;
  });
}

// ─── Tests ────────────────────────────────────────────────────────

describe("RouterProfiles", () => {
  // ── Empty State ────────────────────────────────────────────────

  describe("empty state", () => {
    it("shows empty state when no profiles exist", async () => {
      vi.spyOn(mockClient, "listRouterProfiles").mockResolvedValue([]);
      vi.spyOn(mockClient, "listModels").mockResolvedValue([]);
      vi.spyOn(mockClient, "getRouterProfileStatus").mockResolvedValue({});

      render(
        <TestWrapper initialEntries={["/router-profiles"]}>
          <RouterProfiles />
        </TestWrapper>,
      );

      await waitFor(() => {
        expect(screen.getByText("No router profiles yet")).toBeInTheDocument();
      });

      expect(
        screen.getByText("Create a profile to chain multiple models together."),
      ).toBeInTheDocument();

      // Both the card header and empty state have Add Profile buttons
      const addBtns = screen.getAllByRole("button", { name: /add profile/i });
      expect(addBtns.length).toBeGreaterThanOrEqual(1);
    });
  });

  // ── Profile List Rendering ─────────────────────────────────────

  describe("profile list rendering", () => {
    it("renders page title and description", async () => {
      renderWithProfiles();
      await waitForProfiles();

      expect(screen.getByText("Router Profiles")).toBeInTheDocument();
      expect(
        screen.getByText(
          "Chain models with automatic fallback when errors occur.",
        ),
      ).toBeInTheDocument();
    });

    it("renders profile rows with correct info", async () => {
      renderWithProfiles();
      await waitForProfiles();

      expect(screen.getByText("Fast Models")).toBeInTheDocument();
      expect(screen.getByText("Code Assistant")).toBeInTheDocument();

      expect(screen.getByText("Auto")).toBeInTheDocument();
      expect(screen.getByText("Manual")).toBeInTheDocument();

      expect(screen.getByText("2 models")).toBeInTheDocument();
      expect(screen.getByText("1 model")).toBeInTheDocument();
    });

    it("renders Profiles label in card header", async () => {
      renderWithProfiles();
      await waitForProfiles();

      expect(screen.getByText("Profiles")).toBeInTheDocument();
    });

    it("profile row has correct aria-label", async () => {
      renderWithProfiles();
      await waitForProfiles();

      // Profile rows are <div> elements with aria-label
      expect(
        screen.getByRole("generic", {
          name: "Fast Models — 2 models",
        }),
      ).toBeInTheDocument();

      expect(
        screen.getByRole("generic", {
          name: "Code Assistant — 1 model",
        }),
      ).toBeInTheDocument();
    });
  });

  // ── Add Profile Dialog ─────────────────────────────────────────

  describe("add profile dialog", () => {
    it("opens the add profile dialog when Add Profile is clicked", async () => {
      const user = userEvent.setup();
      renderWithProfiles();
      await waitForProfiles();

      // Card header Add Profile button (first one)
      const addBtns = screen.getAllByRole("button", { name: /add profile/i });
      await user.click(addBtns[0]);

      await waitFor(() => {
        expect(screen.getByRole("dialog")).toBeInTheDocument();
      });

      const dialog = screen.getByRole("dialog");
      // Dialog has title h3 "Add Profile" and form fields
      expect(
        within(dialog).getByRole("heading", { name: /add profile/i }),
      ).toBeInTheDocument();
      expect(within(dialog).getByLabelText("Name")).toBeInTheDocument();
      expect(within(dialog).getByLabelText("Mode")).toBeInTheDocument();
    });

    it("validates empty name on submit", async () => {
      const user = userEvent.setup();
      renderWithProfiles();
      await waitForProfiles();

      const addBtns = screen.getAllByRole("button", { name: /add profile/i });
      await user.click(addBtns[0]);

      await waitFor(() => {
        expect(screen.getByRole("dialog")).toBeInTheDocument();
      });

      const dialog = screen.getByRole("dialog");
      const submitBtn = within(dialog).getByRole("button", {
        name: /^add profile$/i,
      });
      await user.click(submitBtn);

      await waitFor(() => {
        expect(
          within(dialog).getByText("Profile name is required."),
        ).toBeInTheDocument();
      });
    });

    it("validates model is required for each entry", async () => {
      const user = userEvent.setup();
      renderWithProfiles();
      await waitForProfiles();

      const addBtns = screen.getAllByRole("button", { name: /add profile/i });
      await user.click(addBtns[0]);

      await waitFor(() => {
        expect(screen.getByRole("dialog")).toBeInTheDocument();
      });

      const dialog = screen.getByRole("dialog");

      // Type a name so name validation passes
      const nameInput = within(dialog).getByLabelText("Name");
      await user.type(nameInput, "Test Profile");

      // Submit — the default entry row has an empty modelId
      const submitBtn = within(dialog).getByRole("button", {
        name: /^add profile$/i,
      });
      await user.click(submitBtn);

      await waitFor(() => {
        expect(
          within(dialog).getByText(/Model name is required for entry/i),
        ).toBeInTheDocument();
      });
    });

    it("creates a profile with valid data", async () => {
      const user = userEvent.setup();
      const createSpy = vi
        .spyOn(mockClient, "createRouterProfile")
        .mockResolvedValue({
          id: "rp-new",
          name: "New Profile",
          mode: "auto",
          entries: [
            {
              modelId: "1",
              priority: 0,
              isEnabled: true,
              thinkingEffortOverride: null,
            },
          ],
          activeModelId: null,
          createdAt: now,
          updatedAt: now,
        });
      vi.spyOn(mockClient, "listRouterProfiles")
        .mockResolvedValueOnce(mockProfiles)
        .mockResolvedValueOnce([
          ...mockProfiles,
          {
            id: "rp-new",
            name: "New Profile",
            mode: "auto" as const,
            entries: [
              {
                modelId: "1",
                priority: 0,
                isEnabled: true,
                thinkingEffortOverride: null,
              },
            ],
            activeModelId: null,
            createdAt: now,
            updatedAt: now,
          },
        ]);
      vi.spyOn(mockClient, "listModels").mockResolvedValue(mockModels);
      vi.spyOn(mockClient, "getRouterProfileStatus").mockResolvedValue({});

      render(
        <TestWrapper initialEntries={["/router-profiles"]}>
          <RouterProfiles />
        </TestWrapper>,
      );

      await waitForProfiles();

      const addBtns = screen.getAllByRole("button", { name: /add profile/i });
      await user.click(addBtns[0]);

      await waitFor(() => {
        expect(screen.getByRole("dialog")).toBeInTheDocument();
      });

      const dialog = screen.getByRole("dialog");

      // Fill in the name
      const nameInput = within(dialog).getByLabelText("Name");
      await user.type(nameInput, "New Profile");

      // Fill in the model via search input (scoped to dialog)
      const searchInput = within(dialog).getByPlaceholderText("Search models…");
      await user.type(searchInput, "llama");

      // Wait for autocomplete dropdown and select within the dialog
      await waitFor(() => {
        const options = within(dialog).getAllByRole("button");
        const llamaOption = options.find(
          (btn) => btn.textContent?.includes("llama-3.1-70b"),
        );
        expect(llamaOption).toBeDefined();
      });

      // Click the matching dropdown option (within dialog scope)
      const dialogButtons = within(dialog).getAllByRole("button");
      const llamaOption = dialogButtons.find((btn) =>
        btn.textContent?.includes("llama-3.1-70b"),
      );
      await user.click(llamaOption!);

      // Submit
      const submitBtn = within(dialog).getByRole("button", {
        name: /^add profile$/i,
      });
      await user.click(submitBtn);

      await waitFor(() => {
        expect(createSpy).toHaveBeenCalledOnce();
      });

      const callArg = createSpy.mock.calls[0][0];
      expect(callArg.name).toBe("New Profile");
      expect(callArg.mode).toBe("auto");
      expect(callArg.entries).toHaveLength(1);
      expect(callArg.entries[0].modelId).toBe("1");
    });
  });

  // ── Edit Profile Dialog ────────────────────────────────────────

  describe("edit profile dialog", () => {
    it("opens the edit dialog pre-filled when Edit is clicked", async () => {
      const user = userEvent.setup();
      renderWithProfiles();
      await waitForProfiles();

      const editButtons = screen.getAllByRole("button", { name: /edit/i });
      await user.click(editButtons[0]);

      await waitFor(() => {
        expect(screen.getByRole("dialog")).toBeInTheDocument();
      });

      const dialog = screen.getByRole("dialog");
      expect(
        within(dialog).getByRole("heading", { name: /edit profile/i }),
      ).toBeInTheDocument();

      const nameInput = within(dialog).getByLabelText("Name") as HTMLInputElement;
      expect(nameInput.value).toBe("Fast Models");
    });

    it("updates a profile on submit", async () => {
      const user = userEvent.setup();
      const updateSpy = vi
        .spyOn(mockClient, "updateRouterProfile")
        .mockResolvedValue(undefined);

      renderWithProfiles();
      await waitForProfiles();

      const editButtons = screen.getAllByRole("button", { name: /edit/i });
      await user.click(editButtons[0]);

      await waitFor(() => {
        expect(screen.getByRole("dialog")).toBeInTheDocument();
      });

      const dialog = screen.getByRole("dialog");

      const nameInput = within(dialog).getByLabelText("Name") as HTMLInputElement;
      await user.clear(nameInput);
      await user.type(nameInput, "Updated Models");

      const submitBtn = within(dialog).getByRole("button", {
        name: /^save changes$/i,
      });
      await user.click(submitBtn);

      await waitFor(() => {
        expect(updateSpy).toHaveBeenCalledOnce();
      });

      const callArg = updateSpy.mock.calls[0];
      expect(callArg[0]).toBe("rp-1");
      expect(callArg[1].name).toBe("Updated Models");
    });
  });

  // ── Delete Profile ─────────────────────────────────────────────

  describe("delete profile", () => {
    it("shows confirmation dialog when delete is clicked", async () => {
      const user = userEvent.setup();
      renderWithProfiles();
      await waitForProfiles();

      const deleteBtns = findDeleteButtons();
      expect(deleteBtns.length).toBeGreaterThanOrEqual(2);
      await user.click(deleteBtns[0]);

      // ConfirmDialog uses a native <dialog> element
      await waitFor(() => {
        expect(
          screen.getByText(/Delete profile "Fast Models"/),
        ).toBeInTheDocument();
      });
    });

    it("deletes a profile on confirm", async () => {
      const user = userEvent.setup();
      const deleteSpy = vi
        .spyOn(mockClient, "deleteRouterProfile")
        .mockResolvedValue(undefined);
      vi.spyOn(mockClient, "listRouterProfiles")
        .mockResolvedValueOnce(mockProfiles)
        .mockResolvedValueOnce([mockProfiles[1]]);

      renderWithProfiles();
      await waitForProfiles();

      const deleteBtns = findDeleteButtons();
      await user.click(deleteBtns[0]);

      await waitFor(() => {
        expect(
          screen.getByText(/Delete profile "Fast Models"/),
        ).toBeInTheDocument();
      });

      // ConfirmDialog's confirm button
      const confirmBtn = screen.getByRole("button", { name: /^delete$/i });
      await user.click(confirmBtn);

      await waitFor(() => {
        expect(deleteSpy).toHaveBeenCalledWith("rp-1");
      });
    });

    it("closes confirmation dialog on cancel", async () => {
      const user = userEvent.setup();
      renderWithProfiles();
      await waitForProfiles();

      const deleteBtns = findDeleteButtons();
      await user.click(deleteBtns[0]);

      await waitFor(() => {
        expect(
          screen.getByText(/Delete profile "Fast Models"/),
        ).toBeInTheDocument();
      });

      const cancelBtn = screen.getByRole("button", { name: /^cancel$/i });
      await user.click(cancelBtn);

      await waitFor(() => {
        expect(
          screen.queryByText(/Delete profile "Fast Models"/),
        ).not.toBeInTheDocument();
      });
    });
  });

  // ── Profile Row Details ────────────────────────────────────────

  describe("profile row details", () => {
    it("shows Auto badge for auto mode profiles", async () => {
      renderWithProfiles();
      await waitForProfiles();

      expect(screen.getAllByText("Auto").length).toBeGreaterThanOrEqual(1);
    });

    it("shows Manual badge for manual mode profiles", async () => {
      renderWithProfiles();
      await waitForProfiles();

      expect(screen.getAllByText("Manual").length).toBeGreaterThanOrEqual(1);
    });

    it("displays correct model count for each profile", async () => {
      renderWithProfiles();
      await waitForProfiles();

      expect(screen.getByText("2 models")).toBeInTheDocument();
      expect(screen.getByText("1 model")).toBeInTheDocument();
    });
  });

  // ── Add/Remove Entry in Dialog ─────────────────────────────────

  describe("add entry in dialog", () => {
    it("adds a new entry row when Add Entry is clicked", async () => {
      const user = userEvent.setup();
      renderWithProfiles();
      await waitForProfiles();

      const addBtns = screen.getAllByRole("button", { name: /add profile/i });
      await user.click(addBtns[0]);

      await waitFor(() => {
        expect(screen.getByRole("dialog")).toBeInTheDocument();
      });

      const dialog = screen.getByRole("dialog");

      // Should start with 1 entry row (default)
      expect(within(dialog).getAllByPlaceholderText("Search models…")).toHaveLength(1);

      // Click "Add Entry" button inside the dialog
      await user.click(
        within(dialog).getByRole("button", { name: /add entry/i }),
      );

      // Should now have 2 entry rows
      await waitFor(() => {
        expect(within(dialog).getAllByPlaceholderText("Search models…")).toHaveLength(2);
      });
    });

    it("removes an entry row when X is clicked", async () => {
      const user = userEvent.setup();
      renderWithProfiles();
      await waitForProfiles();

      const addBtns = screen.getAllByRole("button", { name: /add profile/i });
      await user.click(addBtns[0]);

      await waitFor(() => {
        expect(screen.getByRole("dialog")).toBeInTheDocument();
      });

      const dialog = screen.getByRole("dialog");

      // Add a second entry
      await user.click(
        within(dialog).getByRole("button", { name: /add entry/i }),
      );

      await waitFor(() => {
        expect(within(dialog).getAllByPlaceholderText("Search models…")).toHaveLength(2);
      });

      // Find remove buttons in the dialog — ghost variant buttons with SVG only, no text
      const dialogButtons = within(dialog).getAllByRole("button");
      const removeBtns = dialogButtons.filter((btn) => {
        const text = btn.textContent?.trim() ?? "";
        return text === "" && btn.querySelector("svg") !== null;
      });

      expect(removeBtns.length).toBeGreaterThanOrEqual(1);
      await user.click(removeBtns[0]);

      await waitFor(() => {
        expect(within(dialog).getAllByPlaceholderText("Search models…")).toHaveLength(1);
      });
    });
  });

  // ── Dialog Mode Selection ──────────────────────────────────────

  describe("dialog mode selection", () => {
    it("defaults to Auto mode in add dialog", async () => {
      const user = userEvent.setup();
      renderWithProfiles();
      await waitForProfiles();

      const addBtns = screen.getAllByRole("button", { name: /add profile/i });
      await user.click(addBtns[0]);

      await waitFor(() => {
        expect(screen.getByRole("dialog")).toBeInTheDocument();
      });

      const dialog = screen.getByRole("dialog");
      const modeSelect = within(dialog).getByLabelText("Mode") as HTMLSelectElement;
      expect(modeSelect.value).toBe("auto");
    });

    it("pre-fills mode from existing profile in edit dialog", async () => {
      const user = userEvent.setup();
      renderWithProfiles();
      await waitForProfiles();

      // Edit "Code Assistant" which is manual mode
      const editButtons = screen.getAllByRole("button", { name: /edit/i });
      await user.click(editButtons[1]);

      await waitFor(() => {
        expect(screen.getByRole("dialog")).toBeInTheDocument();
      });

      const dialog = screen.getByRole("dialog");
      const modeSelect = within(dialog).getByLabelText("Mode") as HTMLSelectElement;
      expect(modeSelect.value).toBe("manual");
    });

    it("allows changing mode and submitting", async () => {
      const user = userEvent.setup();
      const updateSpy = vi
        .spyOn(mockClient, "updateRouterProfile")
        .mockResolvedValue(undefined);

      renderWithProfiles();
      await waitForProfiles();

      const editButtons = screen.getAllByRole("button", { name: /edit/i });
      await user.click(editButtons[0]);

      await waitFor(() => {
        expect(screen.getByRole("dialog")).toBeInTheDocument();
      });

      const dialog = screen.getByRole("dialog");

      const modeSelect = within(dialog).getByLabelText("Mode");
      await user.selectOptions(modeSelect, "manual");

      const submitBtn = within(dialog).getByRole("button", {
        name: /^save changes$/i,
      });
      await user.click(submitBtn);

      await waitFor(() => {
        expect(updateSpy).toHaveBeenCalledOnce();
      });

      expect(updateSpy.mock.calls[0][1].mode).toBe("manual");
    });
  });

  // ── Dialog Cancellation ────────────────────────────────────────

  describe("dialog cancellation", () => {
    it("closes the add dialog when Cancel is clicked", async () => {
      const user = userEvent.setup();
      renderWithProfiles();
      await waitForProfiles();

      const addBtns = screen.getAllByRole("button", { name: /add profile/i });
      await user.click(addBtns[0]);

      await waitFor(() => {
        expect(screen.getByRole("dialog")).toBeInTheDocument();
      });

      const dialog = screen.getByRole("dialog");
      const cancelBtn = within(dialog).getByRole("button", { name: /cancel/i });
      await user.click(cancelBtn);

      await waitFor(() => {
        expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
      });
    });

    it("closes the edit dialog on Cancel", async () => {
      const user = userEvent.setup();
      renderWithProfiles();
      await waitForProfiles();

      const editButtons = screen.getAllByRole("button", { name: /edit/i });
      await user.click(editButtons[0]);

      await waitFor(() => {
        expect(screen.getByRole("dialog")).toBeInTheDocument();
      });

      const dialog = screen.getByRole("dialog");
      const cancelBtn = within(dialog).getByRole("button", { name: /cancel/i });
      await user.click(cancelBtn);

      await waitFor(() => {
        expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
      });
    });
  });

  // ── Loading State ──────────────────────────────────────────────

  describe("loading state", () => {
    it("shows loading skeletons while profiles are loading", async () => {
      vi.spyOn(mockClient, "listRouterProfiles").mockImplementation(
        () => new Promise(() => {}),
      );
      vi.spyOn(mockClient, "listModels").mockResolvedValue([]);
      vi.spyOn(mockClient, "getRouterProfileStatus").mockResolvedValue({});

      render(
        <TestWrapper initialEntries={["/router-profiles"]}>
          <RouterProfiles />
        </TestWrapper>,
      );

      // Skeletons use bg-gradient-to-r with shimmer animation
      await waitFor(() => {
        const skeletons = document.querySelectorAll('[aria-hidden="true"][style*="shimmer"]');
        expect(skeletons.length).toBeGreaterThan(0);
      });
    });
  });
});
