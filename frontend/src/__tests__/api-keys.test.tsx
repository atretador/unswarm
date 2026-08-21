import { describe, it, expect, beforeEach, vi } from "vitest";
import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { setMockLatency, mockClient } from "../lib/api/mock";
import { TestWrapper } from "./test-utils";
import ApiKeys from "../features/api-keys";

beforeEach(() => {
  setMockLatency(0);
  vi.restoreAllMocks();
});

describe("API Keys page", () => {
  it("renders the page and its guidance", async () => {
    render(
      <TestWrapper>
        <ApiKeys />
      </TestWrapper>,
    );

    await waitFor(() => {
      expect(screen.getByText("API Keys")).toBeInTheDocument();
    });

    expect(
      screen.getByText(/not login credentials/, { exact: false }),
    ).toBeInTheDocument();
  });

  it("shows seeded keys with scope badges across tabs", async () => {
    const user = userEvent.setup();
    render(
      <TestWrapper>
        <ApiKeys />
      </TestWrapper>,
    );

    // Wait for the page to fully load
    await waitFor(() => {
      expect(screen.getByRole("tab", { name: /Inference/i })).toBeInTheDocument();
    });

    // On the default Inference tab, the "Local dashboard test" key (inference-scoped) is visible
    await waitFor(() => {
      expect(screen.getByText("Local dashboard test")).toBeInTheDocument();
    });
    // Inference badge appears in the key row
    expect(screen.getAllByText("Inference").length).toBeGreaterThanOrEqual(1);

    // Switch to Agent tab — "Go agent" key (agent-scoped) appears
    await user.click(screen.getByRole("tab", { name: /Agent/i }));
    await waitFor(() => {
      expect(screen.getByText("Go agent")).toBeInTheDocument();
    });
    // Agent badge appears in the key row
    expect(screen.getAllByText("Agent").length).toBeGreaterThanOrEqual(1);
  });

  it("creates a new inference key and reveals the secret once", async () => {
    const user = userEvent.setup();
    render(
      <TestWrapper>
        <ApiKeys />
      </TestWrapper>,
    );

    await waitFor(() => {
      expect(screen.getByRole("button", { name: "Create Key" })).toBeInTheDocument();
    });

    await user.type(screen.getByLabelText("Key name"), "CI runner");
    await user.click(screen.getByRole("button", { name: "Create Key" }));

    // The freshly created key appears in the list.
    await waitFor(() => {
      expect(screen.getByText("CI runner")).toBeInTheDocument();
    });

    // The secret banner is shown immediately after creation.
    await waitFor(() => {
      expect(
        screen.getByText("Key created — copy your secret now."),
      ).toBeInTheDocument();
    });
  });

  // NOTE: this test must run before the confirm-revoke test below — the mock
  // client's API_KEYS store is module-level mutable state shared across tests,
  // and revoking "Go agent" permanently deactivates it for later tests.
  it("cancel on revoke dialog keeps the key active", async () => {
    const user = userEvent.setup();
    const revokeSpy = vi.spyOn(mockClient, "revokeApiKey");

    render(
      <TestWrapper>
        <ApiKeys />
      </TestWrapper>,
    );

    await waitFor(() => {
      expect(screen.getByRole("tab", { name: /Agent/i })).toBeInTheDocument();
    });
    await user.click(screen.getByRole("tab", { name: /Agent/i }));

    await waitFor(() => {
      expect(screen.getByText("Go agent")).toBeInTheDocument();
    });

    // Click Revoke — opens the ConfirmDialog
    await user.click(screen.getByRole("button", { name: "Revoke Go agent" }));

    const dialog = await screen.findByRole("dialog");
    expect(dialog).toBeInTheDocument();

    // Click Cancel — dialog closes, no revoke call
    await user.click(within(dialog).getByRole("button", { name: /Cancel/i }));

    // ConfirmDialog keeps the <dialog> element mounted but unsets `open`
    await waitFor(() => {
      expect(dialog).not.toHaveAttribute("open");
    });
    expect(revokeSpy).not.toHaveBeenCalled();

    // Revoke button is still active
    expect(screen.getByRole("button", { name: "Revoke Go agent" })).not.toBeDisabled();
  });

  it("revoke opens a confirm dialog; confirm disables the key", async () => {
    const user = userEvent.setup();

    render(
      <TestWrapper>
        <ApiKeys />
      </TestWrapper>,
    );

    // "Go agent" is on the Agent tab — switch there first
    await waitFor(() => {
      expect(screen.getByRole("tab", { name: /Agent/i })).toBeInTheDocument();
    });
    await user.click(screen.getByRole("tab", { name: /Agent/i }));

    await waitFor(() => {
      expect(screen.getByText("Go agent")).toBeInTheDocument();
    });

    // Click Revoke — opens the ConfirmDialog
    await user.click(screen.getByRole("button", { name: "Revoke Go agent" }));

    // ConfirmDialog appears with the key name
    const dialog = await screen.findByRole("dialog");
    expect(within(dialog).getByText(/Revoke "Go agent"\?/)).toBeInTheDocument();
    expect(within(dialog).getByText(/Existing clients will lose access immediately/)).toBeInTheDocument();

    // Click the destructive confirm button inside the dialog
    await user.click(within(dialog).getByRole("button", { name: /Revoke/i }));

    await waitFor(() => {
      expect(
        screen.getByRole("button", { name: "Revoke Go agent" }),
      ).toBeDisabled();
    });
  });

  it("rotates a key and reveals a new secret", async () => {
    const user = userEvent.setup();
    render(
      <TestWrapper>
        <ApiKeys />
      </TestWrapper>,
    );

    // "Go agent" is on the Agent tab
    await waitFor(() => {
      expect(screen.getByRole("tab", { name: /Agent/i })).toBeInTheDocument();
    });
    await user.click(screen.getByRole("tab", { name: /Agent/i }));

    await waitFor(() => {
      expect(screen.getByText("Go agent")).toBeInTheDocument();
    });

    await user.click(screen.getByRole("button", { name: "Rotate Go agent" }));

    await waitFor(() => {
      expect(
        screen.getByText("Key rotated — copy your new secret now."),
      ).toBeInTheDocument();
    });
  });

  // ─── Tab bar ──────────────────────────────────────────────────

  it("renders two tabs: Inference and Agent", async () => {
    render(
      <TestWrapper>
        <ApiKeys />
      </TestWrapper>,
    );

    await waitFor(() => {
      expect(screen.getByRole("tab", { name: /Inference/i })).toBeInTheDocument();
    });
    expect(screen.getByRole("tab", { name: /Agent/i })).toBeInTheDocument();
  });

  it("Inference tab is active by default", async () => {
    render(
      <TestWrapper>
        <ApiKeys />
      </TestWrapper>,
    );

    await waitFor(() => {
      expect(screen.getByRole("tab", { name: /Inference/i })).toBeInTheDocument();
    });

    const inferenceTab = screen.getByRole("tab", { name: /Inference/i });
    expect(inferenceTab).toHaveAttribute("aria-selected", "true");

    // Inference keys are shown
    await waitFor(() => {
      expect(screen.getByText("Local dashboard test")).toBeInTheDocument();
    });
    // Inference section heading
    expect(screen.getByText("Inference Keys")).toBeInTheDocument();
  });

  it("switching to Agent tab shows agent keys and agent create form", async () => {
    const user = userEvent.setup();
    render(
      <TestWrapper>
        <ApiKeys />
      </TestWrapper>,
    );

    await waitFor(() => {
      expect(screen.getByRole("tab", { name: /Agent/i })).toBeInTheDocument();
    });

    await user.click(screen.getByRole("tab", { name: /Agent/i }));

    // Agent tab is now selected
    const agentTab = screen.getByRole("tab", { name: /Agent/i });
    expect(agentTab).toHaveAttribute("aria-selected", "true");

    // Agent section heading
    await waitFor(() => {
      expect(screen.getByText("Agent Keys")).toBeInTheDocument();
    });
    // The seeded "Go agent" key is agent-scoped
    expect(screen.getByText("Go agent")).toBeInTheDocument();
    // Agent key create form heading is present
    expect(screen.getByText("New Agent Key")).toBeInTheDocument();
  });

  it("switching tabs filters keys by scope", async () => {
    const user = userEvent.setup();
    render(
      <TestWrapper>
        <ApiKeys />
      </TestWrapper>,
    );

    await waitFor(() => {
      expect(screen.getByRole("tab", { name: /Inference/i })).toBeInTheDocument();
    });

    // On inference tab, "Local dashboard test" should be visible (inference-scoped)
    await waitFor(() => {
      expect(screen.getByText("Local dashboard test")).toBeInTheDocument();
    });

    // "Go agent" should NOT be visible on the inference tab
    expect(screen.queryByText("Go agent")).not.toBeInTheDocument();

    // Switch to agent tab
    await user.click(screen.getByRole("tab", { name: /Agent/i }));
    await waitFor(() => {
      expect(screen.getByText("Go agent")).toBeInTheDocument();
    });

    // "Local dashboard test" should NOT be visible on the agent tab
    expect(screen.queryByText("Local dashboard test")).not.toBeInTheDocument();
  });

  it("creates an agent-scoped key via createAgentApiKey", async () => {
    const user = userEvent.setup();
    const createAgentSpy = vi.spyOn(mockClient, "createAgentApiKey");

    render(
      <TestWrapper>
        <ApiKeys />
      </TestWrapper>,
    );

    await waitFor(() => {
      expect(screen.getByRole("tab", { name: /Agent/i })).toBeInTheDocument();
    });

    // Switch to Agent tab
    await user.click(screen.getByRole("tab", { name: /Agent/i }));

    await waitFor(() => {
      expect(screen.getByRole("button", { name: "Create Key" })).toBeInTheDocument();
    });

    await user.type(screen.getByLabelText("Key name"), "Remote worker");
    await user.click(screen.getByRole("button", { name: "Create Key" }));

    await waitFor(() => {
      expect(createAgentSpy).toHaveBeenCalledWith("Remote worker");
    });

    // Secret banner shown
    await waitFor(() => {
      expect(
        screen.getByText("Key created — copy your secret now."),
      ).toBeInTheDocument();
    });
  });

  it("empty agent keys shows appropriate empty state", async () => {
    // Mock listApiKeys to return only inference keys
    vi.spyOn(mockClient, "listApiKeys").mockResolvedValue([
      {
        id: "ak-inf-1",
        name: "Inference only",
        keyPrefix: "usk_test123",
        scope: "inference",
        isActive: true,
        createdAt: new Date().toISOString(),
        lastUsedAt: null,
      },
    ]);

    const user = userEvent.setup();
    render(
      <TestWrapper>
        <ApiKeys />
      </TestWrapper>,
    );

    await waitFor(() => {
      expect(screen.getByRole("tab", { name: /Agent/i })).toBeInTheDocument();
    });

    await user.click(screen.getByRole("tab", { name: /Agent/i }));

    await waitFor(() => {
      expect(screen.getByText("No agent keys yet")).toBeInTheDocument();
    });
  });
});
