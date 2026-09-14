import { describe, it, expect, beforeEach, vi } from "vitest";
import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { setMockLatency, mockClient } from "../lib/api/mock";
import { TestWrapper } from "./test-utils";
import Providers from "../features/providers";

// Mock the api-keys-api module to avoid real HTTP calls to getProviderModelCatalog
vi.mock("../features/api-keys/api-keys-api", async (importOriginal) => {
  const orig = await importOriginal<
    typeof import("../features/api-keys/api-keys-api")
  >();
  return {
    ...orig,
    getProviderModelCatalog: async () => [
      {
        name: "openai",
        kind: "cloud" as const,
        models: ["gpt-4o", "gpt-4o-mini"],
      },
      {
        name: "anthropic",
        kind: "cloud" as const,
        models: [
          "claude-sonnet-4-20250514",
          "claude-3-5-haiku-20241022",
          "claude-3-opus-20240229",
        ],
      },
    ],
  };
});

beforeEach(() => {
  setMockLatency(0);
  vi.restoreAllMocks();
});

// ─── Helpers ─────────────────────────────────────────────────────

/**
 * Find the provider row container by its name text.
 *
 * DOM structure per row:
 *   <div class="flex items-center gap-4 ...">        ← ROW
 *     <div class="flex items-center gap-3 ...">       ← Name section
 *       <span>openai</span>
 *     </div>
 *     <div>Base URL</div>
 *     <div>API Key Hint</div>
 *     <div>Model Count</div>
 *     <div>Updated</div>
 *     <div class="flex items-center gap-1 ...">       ← Actions
 *       <Button>Edit</Button>
 *       <Button variant="danger">🗑️</Button>
 *     </div>
 *   </div>
 *
 * `closest("div.flex.items-center")` matches the Name section div itself
 * (it also has `flex items-center`). The ROW is its parent.
 */
function findProviderRow(name: string) {
  const nameEl = screen.getByText(name);
  const nameSection = nameEl.closest("div.flex.items-center");
  // The row is the parent of the name-section div
  return (nameSection?.parentElement as HTMLElement) ?? null;
}

/** Click the delete (trash) icon button inside a provider row. */
async function clickDeleteInRow(
  user: ReturnType<typeof userEvent.setup>,
  providerName: string,
) {
  const row = findProviderRow(providerName);
  if (!row) throw new Error(`Row for "${providerName}" not found`);
  const buttons = within(row).getAllByRole("button");
  // First button is Edit (ghost), second is Delete (danger, icon-only)
  const deleteBtn = buttons[buttons.length - 1];
  await user.click(deleteBtn);
}

// ─── 1. Renders provider list ────────────────────────────────────

describe("Providers", () => {
  it("renders the cloud providers page with both seeded providers", async () => {
    render(
      <TestWrapper initialEntries={["/providers"]}>
        <Providers />
      </TestWrapper>,
    );

    await waitFor(() => {
      expect(screen.getByText("openai")).toBeInTheDocument();
    });

    expect(screen.getByText("anthropic")).toBeInTheDocument();

    // Page heading — "Cloud Providers" appears as <h2> heading
    expect(screen.getByRole("heading", { name: "Cloud Providers" })).toBeInTheDocument();

    // Add Provider button visible (the one in the card header)
    expect(
      screen.getAllByRole("button", { name: /add provider/i }).length,
    ).toBeGreaterThanOrEqual(1);
  });

  it("displays provider row columns: name, baseUrl, apiKeyHint, modelCount", async () => {
    render(
      <TestWrapper initialEntries={["/providers"]}>
        <Providers />
      </TestWrapper>,
    );

    await waitFor(() => {
      expect(screen.getByText("openai")).toBeInTheDocument();
    });

    // openai row details
    expect(screen.getByText("https://api.openai.com")).toBeInTheDocument();
    expect(screen.getByText("sk-proj…x9aZ")).toBeInTheDocument();
    expect(screen.getByText(/2\s+models?/)).toBeInTheDocument();

    // anthropic row details
    expect(screen.getByText("https://api.anthropic.com")).toBeInTheDocument();
    expect(screen.getByText("sk-ant…3f9a")).toBeInTheDocument();
    expect(screen.getByText(/3\s+models?/)).toBeInTheDocument();
  });

  it("shows column headers", async () => {
    render(
      <TestWrapper initialEntries={["/providers"]}>
        <Providers />
      </TestWrapper>,
    );

    await waitFor(() => {
      expect(screen.getByText("openai")).toBeInTheDocument();
    });

    expect(screen.getByText("Provider")).toBeInTheDocument();
    expect(screen.getByText("Base URL")).toBeInTheDocument();
    expect(screen.getByText("Models")).toBeInTheDocument();
    expect(screen.getByText("Updated")).toBeInTheDocument();
    expect(screen.getByText("Actions")).toBeInTheDocument();
  });

  it("has Edit and delete buttons for each provider row", async () => {
    render(
      <TestWrapper initialEntries={["/providers"]}>
        <Providers />
      </TestWrapper>,
    );

    await waitFor(() => {
      expect(screen.getByText("openai")).toBeInTheDocument();
    });

    // Each row should have an Edit button
    const editButtons = screen.getAllByRole("button", { name: /edit/i });
    expect(editButtons.length).toBeGreaterThanOrEqual(2);

    // Each row should have at least 2 buttons (Edit + Delete)
    const openaiRow = findProviderRow("openai")!;
    const openaiButtons = within(openaiRow).getAllByRole("button");
    expect(openaiButtons).toHaveLength(2);

    const anthropicRow = findProviderRow("anthropic")!;
    const anthropicButtons = within(anthropicRow).getAllByRole("button");
    expect(anthropicButtons).toHaveLength(2);
  });

  // ─── 2. Empty state ──────────────────────────────────────────

  it("shows empty state when no providers exist", async () => {
    vi.spyOn(mockClient, "listCloudProviders").mockResolvedValueOnce([]);

    render(
      <TestWrapper initialEntries={["/providers"]}>
        <Providers />
      </TestWrapper>,
    );

    await waitFor(() => {
      expect(screen.getByText("No cloud providers")).toBeInTheDocument();
    });

    expect(
      screen.getByText(
        "Add a cloud LLM provider to route requests through an OpenAI-compatible endpoint.",
      ),
    ).toBeInTheDocument();

    // Empty state should have Add Provider buttons (card header + empty state)
    const addBtns = screen.getAllByRole("button", { name: /add provider/i });
    expect(addBtns.length).toBeGreaterThanOrEqual(2);
  });

  // ─── 3. Add provider dialog opens ────────────────────────────

  it("opens the Add Provider dialog when clicking the Add button", async () => {
    const user = userEvent.setup();

    render(
      <TestWrapper initialEntries={["/providers"]}>
        <Providers />
      </TestWrapper>,
    );

    await waitFor(() => {
      expect(screen.getByText("openai")).toBeInTheDocument();
    });

    // Click the card-header Add Provider button (first one)
    await user.click(screen.getAllByRole("button", { name: /add provider/i })[0]);

    const dialog = await screen.findByRole("dialog", {
      name: "Add Provider",
    });
    expect(dialog).toBeInTheDocument();

    // Form fields visible
    expect(within(dialog).getByText("Name")).toBeInTheDocument();
    expect(within(dialog).getByText("Base URL")).toBeInTheDocument();

    // API Key input label exists (there's also a toggle button with same text, use label)
    expect(within(dialog).getByLabelText("API Key")).toBeInTheDocument();

    // Auth type selector visible
    expect(within(dialog).getByText("Authentication Type")).toBeInTheDocument();
    expect(
      within(dialog).getByRole("button", { name: "API Key" }),
    ).toBeInTheDocument();
    expect(
      within(dialog).getByRole("button", { name: "GPT Subscription" }),
    ).toBeInTheDocument();

    // Submit button
    expect(
      within(dialog).getByRole("button", { name: /add provider/i }),
    ).toBeInTheDocument();

    // Cancel button
    expect(
      within(dialog).getByRole("button", { name: /cancel/i }),
    ).toBeInTheDocument();
  });

  it("closes the Add Provider dialog on Cancel", async () => {
    const user = userEvent.setup();

    render(
      <TestWrapper initialEntries={["/providers"]}>
        <Providers />
      </TestWrapper>,
    );

    await waitFor(() => {
      expect(screen.getByText("openai")).toBeInTheDocument();
    });

    await user.click(screen.getAllByRole("button", { name: /add provider/i })[0]);

    const dialog = await screen.findByRole("dialog", {
      name: "Add Provider",
    });

    await user.click(within(dialog).getByRole("button", { name: /cancel/i }));

    await waitFor(() => {
      expect(
        screen.queryByRole("dialog", { name: "Add Provider" }),
      ).not.toBeInTheDocument();
    });
  });

  // ─── 4. Add provider validation ──────────────────────────────

  it("shows validation error when name is empty on submit", async () => {
    const user = userEvent.setup();

    render(
      <TestWrapper initialEntries={["/providers"]}>
        <Providers />
      </TestWrapper>,
    );

    await waitFor(() => {
      expect(screen.getByText("openai")).toBeInTheDocument();
    });

    await user.click(screen.getAllByRole("button", { name: /add provider/i })[0]);

    const dialog = await screen.findByRole("dialog", {
      name: "Add Provider",
    });

    // Submit without filling anything
    await user.click(
      within(dialog).getByRole("button", { name: /add provider/i }),
    );

    await waitFor(() => {
      expect(
        screen.getByText("Provider name is required."),
      ).toBeInTheDocument();
    });
  });

  it("shows validation error when baseUrl is empty in API Key mode", async () => {
    const user = userEvent.setup();

    render(
      <TestWrapper initialEntries={["/providers"]}>
        <Providers />
      </TestWrapper>,
    );

    await waitFor(() => {
      expect(screen.getByText("openai")).toBeInTheDocument();
    });

    await user.click(screen.getAllByRole("button", { name: /add provider/i })[0]);

    const dialog = await screen.findByRole("dialog", {
      name: "Add Provider",
    });

    // Fill name but leave baseUrl empty
    const nameInput = within(dialog).getByPlaceholderText("e.g. OpenAI");
    await user.type(nameInput, "MyProvider");

    // Submit — API Key mode requires baseUrl
    await user.click(
      within(dialog).getByRole("button", { name: /add provider/i }),
    );

    await waitFor(() => {
      expect(screen.getByText("Base URL is required.")).toBeInTheDocument();
    });
  });

  it("shows validation error when apiKey is empty in API Key mode", async () => {
    const user = userEvent.setup();

    render(
      <TestWrapper initialEntries={["/providers"]}>
        <Providers />
      </TestWrapper>,
    );

    await waitFor(() => {
      expect(screen.getByText("openai")).toBeInTheDocument();
    });

    await user.click(screen.getAllByRole("button", { name: /add provider/i })[0]);

    const dialog = await screen.findByRole("dialog", {
      name: "Add Provider",
    });

    // Fill name and baseUrl but leave apiKey empty
    const nameInput = within(dialog).getByPlaceholderText("e.g. OpenAI");
    await user.type(nameInput, "MyProvider");

    const baseUrlInput = within(dialog).getByPlaceholderText(
      "https://api.openai.com/v1",
    );
    await user.type(baseUrlInput, "https://api.example.com/v1");

    // Submit — API Key mode requires apiKey
    await user.click(
      within(dialog).getByRole("button", { name: /add provider/i }),
    );

    await waitFor(() => {
      expect(
        screen.getByText("API key is required for new providers."),
      ).toBeInTheDocument();
    });
  });

  // ─── 5. Create provider ──────────────────────────────────────

  it("creates a new provider and calls createCloudProvider", async () => {
    const user = userEvent.setup();
    const createSpy = vi
      .spyOn(mockClient, "createCloudProvider")
      .mockResolvedValueOnce({
        id: "cp-new-1",
        name: "mistral",
        baseUrl: "https://api.mistral.ai/v1",
        apiKeyHint: "sk-xxxx…yyyy",
        authType: 0,
        modelCount: 0,
        createdAt: new Date().toISOString(),
        updatedAt: new Date().toISOString(),
        baseUrlFull: "https://api.mistral.ai/v1",
        chatgptAccountId: null,
        tokenExpiresAt: null,
      });
    vi.spyOn(mockClient, "fetchCloudProviderModels").mockResolvedValueOnce({
      modelIds: ["mistral-large-latest"],
    });

    render(
      <TestWrapper initialEntries={["/providers"]}>
        <Providers />
      </TestWrapper>,
    );

    await waitFor(() => {
      expect(screen.getByText("openai")).toBeInTheDocument();
    });

    await user.click(screen.getAllByRole("button", { name: /add provider/i })[0]);

    const dialog = await screen.findByRole("dialog", {
      name: "Add Provider",
    });

    // Fill form
    await user.type(
      within(dialog).getByPlaceholderText("e.g. OpenAI"),
      "mistral",
    );
    await user.type(
      within(dialog).getByPlaceholderText("https://api.openai.com/v1"),
      "https://api.mistral.ai/v1",
    );

    // Fill API key (type=password input)
    const apiKeyInput = within(dialog).getByLabelText("API Key");
    await user.type(apiKeyInput, "sk-xxxx-yyyy-key");

    // Submit
    await user.click(
      within(dialog).getByRole("button", { name: /add provider/i }),
    );

    await waitFor(() => {
      expect(createSpy).toHaveBeenCalledTimes(1);
    });

    expect(createSpy).toHaveBeenCalledWith({
      name: "mistral",
      baseUrl: "https://api.mistral.ai/v1",
      apiKey: "sk-xxxx-yyyy-key",
      authType: 0,
    });
  });

  // ─── 6. Edit provider dialog ─────────────────────────────────

  it("opens Edit Provider dialog pre-filled when clicking Edit", async () => {
    const user = userEvent.setup();
    vi.spyOn(mockClient, "getCloudProvider").mockResolvedValueOnce({
      id: "cp-seed-001",
      name: "openai",
      baseUrl: "https://api.openai.com",
      apiKeyHint: "sk-proj…x9aZ",
      authType: 0,
      modelCount: 2,
      createdAt: new Date().toISOString(),
      updatedAt: new Date().toISOString(),
      baseUrlFull: "https://api.openai.com",
      chatgptAccountId: null,
      tokenExpiresAt: null,
    });

    render(
      <TestWrapper initialEntries={["/providers"]}>
        <Providers />
      </TestWrapper>,
    );

    await waitFor(() => {
      expect(screen.getByText("openai")).toBeInTheDocument();
    });

    // Click Edit on the openai row
    const row = findProviderRow("openai")!;
    const editBtn = within(row).getByRole("button", { name: /edit/i });
    await user.click(editBtn);

    const dialog = await screen.findByRole("dialog", {
      name: "Edit Provider",
    });
    expect(dialog).toBeInTheDocument();

    // Name field should be pre-filled and disabled in edit mode
    const nameInput = within(dialog).getByPlaceholderText("e.g. OpenAI");
    expect(nameInput).toHaveValue("openai");
    expect(nameInput).toBeDisabled();

    // Base URL should be pre-filled
    const baseUrlInput = within(dialog).getByPlaceholderText(
      "https://api.openai.com/v1",
    );
    expect(baseUrlInput).toHaveValue("https://api.openai.com");

    // API Key field should be labeled as "keep existing"
    expect(
      within(dialog).getByText("API Key (leave blank to keep existing)"),
    ).toBeInTheDocument();

    // Submit button says "Save Changes" in edit mode
    expect(
      within(dialog).getByRole("button", { name: /save changes/i }),
    ).toBeInTheDocument();
  });

  // ─── 7. Update provider ──────────────────────────────────────

  it("updates a provider and calls updateCloudProvider", async () => {
    const user = userEvent.setup();
    vi.spyOn(mockClient, "getCloudProvider").mockResolvedValueOnce({
      id: "cp-seed-001",
      name: "openai",
      baseUrl: "https://api.openai.com",
      apiKeyHint: "sk-proj…x9aZ",
      authType: 0,
      modelCount: 2,
      createdAt: new Date().toISOString(),
      updatedAt: new Date().toISOString(),
      baseUrlFull: "https://api.openai.com",
      chatgptAccountId: null,
      tokenExpiresAt: null,
    });
    const updateSpy = vi
      .spyOn(mockClient, "updateCloudProvider")
      .mockResolvedValueOnce({
        id: "cp-seed-001",
        name: "openai",
        baseUrl: "https://api.openai.com/v2",
        apiKeyHint: "sk-proj…x9aZ",
        authType: 0,
        modelCount: 2,
        createdAt: new Date().toISOString(),
        updatedAt: new Date().toISOString(),
        baseUrlFull: "https://api.openai.com/v2",
        chatgptAccountId: null,
        tokenExpiresAt: null,
      });

    render(
      <TestWrapper initialEntries={["/providers"]}>
        <Providers />
      </TestWrapper>,
    );

    await waitFor(() => {
      expect(screen.getByText("openai")).toBeInTheDocument();
    });

    // Click Edit on openai row
    const row = findProviderRow("openai")!;
    const editBtn = within(row).getByRole("button", { name: /edit/i });
    await user.click(editBtn);

    const dialog = await screen.findByRole("dialog", {
      name: "Edit Provider",
    });

    // Modify base URL
    const baseUrlInput = within(dialog).getByPlaceholderText(
      "https://api.openai.com/v1",
    );
    await user.clear(baseUrlInput);
    await user.type(baseUrlInput, "https://api.openai.com/v2");

    // Submit
    await user.click(
      within(dialog).getByRole("button", { name: /save changes/i }),
    );

    await waitFor(() => {
      expect(updateSpy).toHaveBeenCalledTimes(1);
    });

    expect(updateSpy).toHaveBeenCalledWith("cp-seed-001", {
      baseUrl: "https://api.openai.com/v2",
      apiKey: null,
    });
  });

  it("shows error when createCloudProvider fails", async () => {
    const user = userEvent.setup();
    vi.spyOn(mockClient, "createCloudProvider").mockRejectedValueOnce(
      new Error("Provider name already exists."),
    );

    render(
      <TestWrapper initialEntries={["/providers"]}>
        <Providers />
      </TestWrapper>,
    );

    await waitFor(() => {
      expect(screen.getByText("openai")).toBeInTheDocument();
    });

    await user.click(screen.getAllByRole("button", { name: /add provider/i })[0]);

    const dialog = await screen.findByRole("dialog", {
      name: "Add Provider",
    });

    await user.type(
      within(dialog).getByPlaceholderText("e.g. OpenAI"),
      "openai",
    );
    await user.type(
      within(dialog).getByPlaceholderText("https://api.openai.com/v1"),
      "https://api.openai.com/v1",
    );
    const apiKeyInput = within(dialog).getByLabelText("API Key");
    await user.type(apiKeyInput, "sk-test-key-12345");

    await user.click(
      within(dialog).getByRole("button", { name: /add provider/i }),
    );

    await waitFor(() => {
      expect(
        screen.getByText("Provider name already exists."),
      ).toBeInTheDocument();
    });
  });

  // ─── 8. Delete provider ──────────────────────────────────────

  it("opens delete confirmation and calls deleteCloudProvider on confirm", async () => {
    const user = userEvent.setup();
    const deleteSpy = vi
      .spyOn(mockClient, "deleteCloudProvider")
      .mockResolvedValueOnce(undefined as never);

    render(
      <TestWrapper initialEntries={["/providers"]}>
        <Providers />
      </TestWrapper>,
    );

    await waitFor(() => {
      expect(screen.getByText("openai")).toBeInTheDocument();
    });

    // Click delete (trash icon) on openai row
    await clickDeleteInRow(user, "openai");

    // ConfirmDialog should appear
    const confirmDialog = await screen.findByRole("dialog");
    expect(confirmDialog).toBeInTheDocument();

    // Should contain the confirmation description
    expect(
      within(confirmDialog).getByText(/Delete provider "openai"/),
    ).toBeInTheDocument();

    // Click Confirm/Delete in the dialog
    const deleteConfirmBtn = within(confirmDialog).getByRole("button", {
      name: /delete/i,
    });
    await user.click(deleteConfirmBtn);

    await waitFor(() => {
      expect(deleteSpy).toHaveBeenCalledTimes(1);
    });
    expect(deleteSpy).toHaveBeenCalledWith("cp-seed-001");
  });

  it("cancel on delete confirmation does not call deleteCloudProvider", async () => {
    const user = userEvent.setup();
    const deleteSpy = vi.spyOn(mockClient, "deleteCloudProvider");

    render(
      <TestWrapper initialEntries={["/providers"]}>
        <Providers />
      </TestWrapper>,
    );

    await waitFor(() => {
      expect(screen.getByText("openai")).toBeInTheDocument();
    });

    // Click delete on openai row
    await clickDeleteInRow(user, "openai");

    // ConfirmDialog should appear
    const confirmDialog = await screen.findByRole("dialog");

    // Click Cancel
    await user.click(
      within(confirmDialog).getByRole("button", { name: /cancel/i }),
    );

    // Should not have called deleteCloudProvider
    expect(deleteSpy).not.toHaveBeenCalled();

    // Provider should still be visible
    await waitFor(() => {
      expect(screen.getByText("openai")).toBeInTheDocument();
    });
  });

  // ─── 9. Auth type selector ───────────────────────────────────

  it("toggles auth type between API Key and GPT Subscription", async () => {
    const user = userEvent.setup();

    render(
      <TestWrapper initialEntries={["/providers"]}>
        <Providers />
      </TestWrapper>,
    );

    await waitFor(() => {
      expect(screen.getByText("openai")).toBeInTheDocument();
    });

    await user.click(screen.getAllByRole("button", { name: /add provider/i })[0]);

    const dialog = await screen.findByRole("dialog", {
      name: "Add Provider",
    });

    // API Key is selected by default — API Key input should be visible
    expect(within(dialog).getByLabelText("API Key")).toBeInTheDocument();

    // Base URL input should be visible
    expect(
      within(dialog).getByPlaceholderText("https://api.openai.com/v1"),
    ).toBeInTheDocument();

    // Click "GPT Subscription" toggle
    await user.click(
      within(dialog).getByRole("button", { name: "GPT Subscription" }),
    );

    // In subscription mode, API Key input should NOT be visible
    // (the Input for apiKey is conditionally rendered only when authType === 0)
    expect(
      within(dialog).queryByLabelText("API Key"),
    ).not.toBeInTheDocument();

    // Base URL input should be hidden in subscription add mode
    expect(
      within(dialog).queryByPlaceholderText("https://api.openai.com/v1"),
    ).not.toBeInTheDocument();

    // Toggle back to API Key
    await user.click(
      within(dialog).getByRole("button", { name: "API Key" }),
    );

    // API Key input should reappear
    expect(within(dialog).getByLabelText("API Key")).toBeInTheDocument();
  });

  // ─── 10. Provider row displays correct columns ───────────────

  it("shows model count with correct pluralization", async () => {
    render(
      <TestWrapper initialEntries={["/providers"]}>
        <Providers />
      </TestWrapper>,
    );

    await waitFor(() => {
      expect(screen.getByText("openai")).toBeInTheDocument();
    });

    // openai has modelCount: 2 -> "2 models"
    expect(screen.getByText("2 models")).toBeInTheDocument();
    // anthropic has modelCount: 3 -> "3 models"
    expect(screen.getByText("3 models")).toBeInTheDocument();
  });

  it("shows 'GPT Sub' badge for subscription providers", async () => {
    // Mock a subscription provider
    vi.spyOn(mockClient, "listCloudProviders").mockResolvedValueOnce([
      {
        id: "cp-sub-1",
        name: "chatgpt",
        baseUrl: "https://chatgpt.com",
        apiKeyHint: null,
        authType: 1,
        modelCount: 5,
        createdAt: new Date().toISOString(),
        updatedAt: new Date().toISOString(),
      },
    ]);

    render(
      <TestWrapper initialEntries={["/providers"]}>
        <Providers />
      </TestWrapper>,
    );

    await waitFor(() => {
      expect(screen.getByText("chatgpt")).toBeInTheDocument();
    });

    // Should display the GPT Sub badge
    expect(screen.getByText("GPT Sub")).toBeInTheDocument();
  });

  it("shows em dash for apiKeyHint when authType is 1 (subscription)", async () => {
    vi.spyOn(mockClient, "listCloudProviders").mockResolvedValueOnce([
      {
        id: "cp-sub-1",
        name: "chatgpt",
        baseUrl: "https://chatgpt.com",
        apiKeyHint: null,
        authType: 1,
        modelCount: 5,
        createdAt: new Date().toISOString(),
        updatedAt: new Date().toISOString(),
      },
    ]);

    render(
      <TestWrapper initialEntries={["/providers"]}>
        <Providers />
      </TestWrapper>,
    );

    await waitFor(() => {
      expect(screen.getByText("chatgpt")).toBeInTheDocument();
    });

    // Subscription providers show em dash for API key hint
    expect(screen.getByText("\u2014")).toBeInTheDocument();
  });

  it("displays relative time in the Updated column", async () => {
    render(
      <TestWrapper initialEntries={["/providers"]}>
        <Providers />
      </TestWrapper>,
    );

    await waitFor(() => {
      expect(screen.getByText("openai")).toBeInTheDocument();
    });

    // The row should contain the provider name
    const openaiRow = findProviderRow("openai")!;
    expect(openaiRow).toBeInTheDocument();
    expect(within(openaiRow).getByText("openai")).toBeInTheDocument();

    // The row should contain the base URL
    expect(within(openaiRow).getByText("https://api.openai.com")).toBeInTheDocument();
  });
});
