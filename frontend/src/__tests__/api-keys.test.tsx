import { describe, it, expect, beforeEach, vi } from "vitest";
import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { mockClient, setMockLatency } from "../lib/api/mock";
import { TestWrapper } from "./test-utils";
import ApiKeys from "../features/api-keys";

vi.mock("../features/api-keys/api-keys-api", async (importOriginal) => ({
  ...await importOriginal<typeof import("../features/api-keys/api-keys-api")>(),
  getApiKeyUsage: vi.fn().mockResolvedValue({ totals: { requestCount: 0, promptTokens: 0, completionTokens: 0, cachedTokens: 0 }, models: [] }),
}));

beforeEach(() => {
  setMockLatency(0);
  vi.restoreAllMocks();
});

describe("CLI API key tab and permission matrix", () => {
  const renderPage = () => render(<TestWrapper initialEntries={["/api-keys"]}><ApiKeys /></TestWrapper>);

  it("renders three tabs and the seeded CLI key permission summary", async () => {
    const user = userEvent.setup(); renderPage();
    await waitFor(() => expect(screen.getByRole("tab", { name: /CLI/i })).toBeInTheDocument());
    expect(screen.getAllByRole("tab")).toHaveLength(3);
    await user.click(screen.getByRole("tab", { name: /CLI/i }));
    await waitFor(() => expect(screen.getByText("CLI read-only")).toBeInTheDocument());
    expect(screen.getByText("models:r, metrics:r, logs:r, stats:r")).toBeInTheDocument();
  });

  it("renders all permission groups and read/write controls in the create dialog", async () => {
    const user = userEvent.setup(); renderPage();
    await waitFor(() => expect(screen.getByRole("tab", { name: /CLI/i })).toBeInTheDocument());
    await user.click(screen.getByRole("tab", { name: /CLI/i }));
    await user.click(screen.getByRole("button", { name: "Create CLI Key" }));
    const dialog = await screen.findByRole("dialog");
    expect(within(dialog).getByLabelText("Key name")).toBeInTheDocument();
    for (const group of ["Infrastructure", "Configuration", "Observability"]) expect(within(dialog).getByText(group)).toBeInTheDocument();
    for (const domain of ["models", "runtimes", "agents", "queue", "settings", "users", "apikeys", "routerprofiles", "cloudproviders", "prompts", "metrics", "logs", "stats", "benchmarks", "scripts"]) {
      expect(within(dialog).getByRole("switch", { name: `${domain} Read` })).toBeInTheDocument();
      expect(within(dialog).getByRole("switch", { name: `${domain} Write` })).toBeInTheDocument();
    }
  });

  it("selects and clears every CLI permission", async () => {
    const user = userEvent.setup(); renderPage();
    await waitFor(() => expect(screen.getByRole("tab", { name: /CLI/i })).toBeInTheDocument());
    await user.click(screen.getByRole("tab", { name: /CLI/i })); await user.click(screen.getByRole("button", { name: "Create CLI Key" }));
    const dialog = await screen.findByRole("dialog");
    await user.click(within(dialog).getByRole("button", { name: "Select all" }));
    expect(within(dialog).getAllByRole("switch").every((x) => x.getAttribute("aria-checked") === "true")).toBe(true);
    await user.click(within(dialog).getByRole("button", { name: "Clear all" }));
    expect(within(dialog).getAllByRole("switch").every((x) => x.getAttribute("aria-checked") === "false")).toBe(true);
  });

  it("creates a CLI key with permissions and displays its one-time secret and env hint", async () => {
    const user = userEvent.setup();
    const spy = vi.spyOn(mockClient, "createControlPlaneApiKey").mockResolvedValueOnce({ id: "new-cli", name: "Deploy", keyPrefix: "ck_new", scope: "control-plane", isActive: true, createdAt: new Date().toISOString(), lastUsedAt: null, permissions: { models: "rw", metrics: "r" }, secret: "cli-secret-once" });
    renderPage(); await waitFor(() => expect(screen.getByRole("tab", { name: /CLI/i })).toBeInTheDocument());
    await user.click(screen.getByRole("tab", { name: /CLI/i })); await user.click(screen.getByRole("button", { name: "Create CLI Key" }));
    const dialog = await screen.findByRole("dialog"); await user.type(within(dialog).getByLabelText("Key name"), "Deploy");
    await user.click(within(dialog).getByRole("switch", { name: "models Write" })); await user.click(within(dialog).getByRole("switch", { name: "metrics Read" }));
    await user.click(within(dialog).getByRole("button", { name: "Create CLI Key" }));
    await waitFor(() => expect(spy).toHaveBeenCalledWith("Deploy", expect.objectContaining({ models: "rw", metrics: "r" })));
    expect(await screen.findByText("cli-secret-once")).toBeInTheDocument(); expect(screen.getByText("export UNSWARM_API_KEY=cli-secret-once")).toBeInTheDocument();
  });

  it("makes write imply read and turning read off clear write", async () => {
    const user = userEvent.setup(); renderPage(); await waitFor(() => expect(screen.getByRole("tab", { name: /CLI/i })).toBeInTheDocument());
    await user.click(screen.getByRole("tab", { name: /CLI/i })); await user.click(screen.getByRole("button", { name: "Create CLI Key" })); const dialog = await screen.findByRole("dialog");
    await user.click(within(dialog).getByRole("switch", { name: "models Write" })); expect(within(dialog).getByRole("switch", { name: "models Read" })).toHaveAttribute("aria-checked", "true");
    await user.click(within(dialog).getByRole("switch", { name: "models Read" })); expect(within(dialog).getByRole("switch", { name: "models Write" })).toHaveAttribute("aria-checked", "false");
  });

  it("edits and saves permissions for a CLI key", async () => {
    const user = userEvent.setup(); const spy = vi.spyOn(mockClient, "updateApiKeyPermissions").mockResolvedValueOnce({ models: "rw", metrics: "r", logs: "r", stats: "r" }); renderPage();
    await waitFor(() => expect(screen.getByRole("tab", { name: /CLI/i })).toBeInTheDocument()); await user.click(screen.getByRole("tab", { name: /CLI/i })); await waitFor(() => expect(screen.getByText("CLI read-only")).toBeInTheDocument());
    await user.click(screen.getByRole("button", { name: "Manage CLI read-only" })); const dialog = await screen.findByRole("dialog"); await waitFor(() => expect(within(dialog).getByRole("switch", { name: "models Write" })).toBeInTheDocument());
    await user.click(within(dialog).getByRole("switch", { name: "models Write" })); await user.click(within(dialog).getByRole("button", { name: "Save access" })); await waitFor(() => expect(spy).toHaveBeenCalledWith("ak-seed-0003", expect.objectContaining({ models: "rw" })));
  });
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
      screen.getAllByText(/not login credentials/, { exact: false }),
    ).toHaveLength(2);
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
