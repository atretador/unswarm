import { describe, it, expect, beforeEach, vi } from "vitest";
import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { setMockLatency, mockClient } from "../lib/api/mock";
import { TestWrapper } from "./test-utils";
import Fleet from "../features/fleet";
import type { Agent, RegisterRuntimePayload, RegisteredRuntime } from "../lib/api/types";

beforeEach(() => {
  setMockLatency(0);
  vi.restoreAllMocks();
});

/** Registered containers seeded for the host agent (rc1 ready + rc2 starting). */
const HOST_RCS: RegisteredRuntime[] = [
  {
    id: "rc1",
    displayName: "llama-server",
    image: "unswarm/llama3.1:70b-q4km",
    containerPort: 8080,
    agent: "host",
    canRunAlongWith: [],
    maxConcurrentInferences: 1,
    status: "ready",
    runtimeContainerId: "c1",
    mappedPort: 8081,
    errorMessage: null,
    createdAt: new Date().toISOString(),
    lastDiscoveredAt: new Date().toISOString(),
    discoveredModels: [
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
        createdAt: new Date().toISOString(),
        updatedAt: new Date().toISOString(),
      },
      {
        id: "5",
        name: "gemma-2-27b",
        family: "Gemma",
        parameterSize: "27B",
        quantization: "Q4_K_S",
        status: "ready",
        lastBenchmark: null,
        contextWindow: 8192,
        containerImage: "unswarm/gemma2:27b-q4ks",
        sourceRuntimeId: "rc1",
        createdAt: new Date().toISOString(),
        updatedAt: new Date().toISOString(),
      },
    ],
  },
  {
    id: "rc2",
    displayName: "mistral-server",
    image: "unswarm/mistral-large:123b-q5km",
    containerPort: 8080,
    agent: "host",
    canRunAlongWith: [],
    maxConcurrentInferences: 1,
    status: "starting",
    runtimeContainerId: null,
    mappedPort: null,
    errorMessage: null,
    createdAt: new Date().toISOString(),
    lastDiscoveredAt: null,
    discoveredModels: [],
  },
];

/** Restore the default registered-containers seed so tests stay independent. */
function seedRegisteredRuntimes(rcs: RegisteredRuntime[]) {
  // The mock keeps module state; force a fresh value via the public API:
  // delete the real seed, then re-register the desired fixtures.
  // Simpler: stub listRegisteredRuntimes for the test's lifetime.
  vi.spyOn(mockClient, "listRegisteredRuntimes").mockResolvedValue([...rcs]);
}

describe("Fleet", () => {
  it("renders agent sections with host first, remotes collapsed", async () => {
    seedRegisteredRuntimes(HOST_RCS);
    render(
      <TestWrapper>
        <Fleet />
      </TestWrapper>,
    );

    await waitFor(() => {
      expect(screen.getByText("Fleet")).toBeInTheDocument();
    });

    const hostHeader = screen.getByRole("button", { name: "Toggle host section" });
    const edgeHeader = screen.getByRole("button", { name: "Toggle edge-node-1 section" });
    expect(hostHeader).toBeInTheDocument();
    expect(edgeHeader).toBeInTheDocument();

    // Host is expanded by default (its registered containers are visible)
    expect(await screen.findByText("llama-server")).toBeInTheDocument();
    // Remote is collapsed — its empty state is hidden
    expect(screen.queryByText("No runtimes registered")).not.toBeInTheDocument();
  });

  it("shows registered containers inside the host section", async () => {
    seedRegisteredRuntimes(HOST_RCS);
    render(
      <TestWrapper>
        <Fleet />
      </TestWrapper>,
    );

    await waitFor(() => {
      expect(screen.getByText("llama-server")).toBeInTheDocument();
    });

    expect(screen.getByText("mistral-server")).toBeInTheDocument();
  });

  it("shows registration statuses on cards", async () => {
    seedRegisteredRuntimes(HOST_RCS);
    render(
      <TestWrapper>
        <Fleet />
      </TestWrapper>,
    );

    await waitFor(() => {
      expect(screen.getByText("llama-server")).toBeInTheDocument();
    });

    // rc1 is ready, rc2 is starting
    expect(screen.getAllByText("ready").length).toBeGreaterThanOrEqual(1);
    expect(screen.getByText("starting")).toBeInTheDocument();
  });

  it("shows discovered model count and chips", async () => {
    seedRegisteredRuntimes(HOST_RCS);
    render(
      <TestWrapper>
        <Fleet />
      </TestWrapper>,
    );

    await waitFor(() => {
      expect(screen.getByText("llama-server")).toBeInTheDocument();
    });

    // llama-server discovers 2 models: llama-3.1-70b (ready) and gemma-2-27b (ready)
    expect(screen.getByText("llama-3.1-70b")).toBeInTheDocument();
    expect(screen.getByText("gemma-2-27b")).toBeInTheDocument();
  });

  it("empty agent shows a Manage runtimes button once expanded", async () => {
    seedRegisteredRuntimes(HOST_RCS);
    const user = userEvent.setup();
    render(
      <TestWrapper>
        <Fleet />
      </TestWrapper>,
    );

    await waitFor(() => {
      expect(screen.getByRole("button", { name: "Toggle edge-node-1 section" })).toBeInTheDocument();
    });

    // edge-node-1 has no registered containers → empty state, hidden while collapsed
    expect(screen.queryByText("No runtimes registered")).not.toBeInTheDocument();

    await user.click(screen.getByRole("button", { name: "Toggle edge-node-1 section" }));

    await waitFor(() => {
      expect(screen.getByText("No runtimes registered")).toBeInTheDocument();
    });
    expect(screen.getByRole("button", { name: "Manage runtimes" })).toBeInTheDocument();
  });

  it("manage modal opens and lists the agent's running containers", async () => {
    seedRegisteredRuntimes(HOST_RCS);
    const user = userEvent.setup();
    render(
      <TestWrapper>
        <Fleet />
      </TestWrapper>,
    );

    await waitFor(() => {
      expect(screen.getByRole("button", { name: "Toggle edge-node-1 section" })).toBeInTheDocument();
    });
    await user.click(screen.getByRole("button", { name: "Toggle edge-node-1 section" }));

    const manageButton = await screen.findByRole("button", { name: "Manage runtimes" });
    await user.click(manageButton);

    const dialog = await screen.findByRole("dialog", { name: /manage runtimes on edge-node-1/i });
    expect(dialog).toBeInTheDocument();

    await waitFor(() => {
      expect(screen.getByText("vllm-serve")).toBeInTheDocument();
    });
    expect(screen.getByText("stable-diffusion-api")).toBeInTheDocument();
    expect(screen.getByText("ray-worker")).toBeInTheDocument();
  });

  it("filter narrows the container list in the manage modal", async () => {
    seedRegisteredRuntimes(HOST_RCS);
    const user = userEvent.setup();
    render(
      <TestWrapper>
        <Fleet />
      </TestWrapper>,
    );

    await waitFor(() => {
      expect(screen.getByRole("button", { name: "Toggle edge-node-1 section" })).toBeInTheDocument();
    });
    await user.click(screen.getByRole("button", { name: "Toggle edge-node-1 section" }));
    await user.click(await screen.findByRole("button", { name: "Manage runtimes" }));

    await screen.findByRole("dialog", { name: /manage runtimes on edge-node-1/i });
    await waitFor(() => {
      expect(screen.getByText("vllm-serve")).toBeInTheDocument();
    });

    const filter = screen.getByRole("searchbox", { name: /filter containers/i });
    await user.type(filter, "stable");

    expect(screen.queryByText("vllm-serve")).not.toBeInTheDocument();
    expect(screen.getByText("stable-diffusion-api")).toBeInTheDocument();
  });

  it("manage modal shows pagination for many containers", async () => {
    seedRegisteredRuntimes(HOST_RCS);
    const manyContainers = Array.from({ length: 20 }, (_, i) => ({
      id: `c${i + 1}`,
      modelId: "",
      modelName: `container-${i + 1}`,
      status: "running" as const,
      port: 8000 + i,
      pid: 100 + i,
      memoryMb: 1024 + i,
      cpuPercent: i % 10,
      uptime: 1000 + i,
      lastHealthCheck: new Date().toISOString(),
      errorMessage: null,
      createdAt: new Date().toISOString(),
    }));
    vi.spyOn(mockClient, "listAgentContainers").mockResolvedValueOnce(manyContainers);

    const user = userEvent.setup();
    render(
      <TestWrapper>
        <Fleet />
      </TestWrapper>,
    );

    await waitFor(() => {
      expect(screen.getByRole("button", { name: "Toggle edge-node-1 section" })).toBeInTheDocument();
    });
    await user.click(screen.getByRole("button", { name: "Toggle edge-node-1 section" }));
    await user.click(await screen.findByRole("button", { name: "Manage runtimes" }));

    const dialog = await screen.findByRole("dialog", { name: /manage runtimes on edge-node-1/i });
    await waitFor(() => {
      expect(screen.getByText("container-1")).toBeInTheDocument();
    });

    // 9 per page → 20 containers = 3 pages; page 2 exists
    expect(screen.getByRole("button", { name: "Page 2" })).toBeInTheDocument();
    expect(screen.queryByText("container-10")).not.toBeInTheDocument();

    await user.click(screen.getByRole("button", { name: "Page 2" }));
    expect(within(dialog).getByText("container-10")).toBeInTheDocument();
    expect(within(dialog).queryByText("container-1")).not.toBeInTheDocument();
  });

  it("selecting a container prefills display name + detected port and registers with agent", async () => {
    seedRegisteredRuntimes(HOST_RCS);
    const user = userEvent.setup();
    const registerSpy = vi.spyOn(mockClient, "registerRuntime");

    render(
      <TestWrapper>
        <Fleet />
      </TestWrapper>,
    );

    await waitFor(() => {
      expect(screen.getByRole("button", { name: "Toggle edge-node-1 section" })).toBeInTheDocument();
    });
    await user.click(screen.getByRole("button", { name: "Toggle edge-node-1 section" }));
    await user.click(await screen.findByRole("button", { name: "Manage runtimes" }));

    const dialog = await screen.findByRole("dialog", { name: /manage runtimes on edge-node-1/i });
    await waitFor(() => {
      expect(screen.getByText("vllm-serve")).toBeInTheDocument();
    });

    await user.click(screen.getByText("vllm-serve"));

    // Port auto-fills from the container's detected port (8000 in the mock seed)
    const portInput = within(dialog).getByRole("spinbutton", { name: /port/i });
    expect(portInput).toHaveValue(8000);

    await user.click(await screen.findByRole("button", { name: /register on edge-node-1/i }));

    await waitFor(() => {
      expect(registerSpy).toHaveBeenCalledTimes(1);
    });
    const payload = registerSpy.mock.calls[0][0] as RegisterRuntimePayload;
    expect(payload.agent).toBe("edge-node-1");
    expect(payload.image).toBe("vllm-serve");
    expect(payload.containerPort).toBe(8000);
    expect(payload.displayName).toBeTruthy();
  });

  it("allows manually overriding the prefilled container port", async () => {
    seedRegisteredRuntimes(HOST_RCS);
    const user = userEvent.setup();
    const registerSpy = vi.spyOn(mockClient, "registerRuntime");

    render(
      <TestWrapper>
        <Fleet />
      </TestWrapper>,
    );

    await waitFor(() => {
      expect(screen.getByRole("button", { name: "Toggle edge-node-1 section" })).toBeInTheDocument();
    });
    await user.click(screen.getByRole("button", { name: "Toggle edge-node-1 section" }));
    await user.click(await screen.findByRole("button", { name: "Manage runtimes" }));

    const dialog = await screen.findByRole("dialog", { name: /manage runtimes on edge-node-1/i });
    await waitFor(() => {
      expect(screen.getByText("vllm-serve")).toBeInTheDocument();
    });

    await user.click(screen.getByText("vllm-serve"));

    const portInput = within(dialog).getByRole("spinbutton", { name: /port/i });
    expect(portInput).toHaveValue(8000);

    // Manual override of the auto-detected port
    await user.clear(portInput);
    await user.type(portInput, "9000");

    await user.click(await screen.findByRole("button", { name: /register on edge-node-1/i }));

    await waitFor(() => {
      expect(registerSpy).toHaveBeenCalledTimes(1);
    });
    const payload = registerSpy.mock.calls[0][0] as RegisterRuntimePayload;
    expect(payload.image).toBe("vllm-serve");
    expect(payload.containerPort).toBe(9000);
  });

  it("falls back to 8080 when the container reports no detected port", async () => {
    seedRegisteredRuntimes(HOST_RCS);
    const user = userEvent.setup();

    render(
      <TestWrapper>
        <Fleet />
      </TestWrapper>,
    );

    await waitFor(() => {
      expect(screen.getByRole("button", { name: "Toggle edge-node-1 section" })).toBeInTheDocument();
    });
    await user.click(screen.getByRole("button", { name: "Toggle edge-node-1 section" }));
    await user.click(await screen.findByRole("button", { name: "Manage runtimes" }));

    const dialog = await screen.findByRole("dialog", { name: /manage runtimes on edge-node-1/i });
    await waitFor(() => {
      expect(screen.getByText("ray-worker")).toBeInTheDocument();
    });

    // ray-worker is stopped → telemetry reports no port → fallback default
    await user.click(screen.getByText("ray-worker"));

    const portInput = within(dialog).getByRole("spinbutton", { name: /port/i });
    expect(portInput).toHaveValue(8080);
  });

  it("modal closes after a successful registration", async () => {
    seedRegisteredRuntimes(HOST_RCS);
    const user = userEvent.setup();
    render(
      <TestWrapper>
        <Fleet />
      </TestWrapper>,
    );

    await waitFor(() => {
      expect(screen.getByRole("button", { name: "Toggle edge-node-1 section" })).toBeInTheDocument();
    });
    await user.click(screen.getByRole("button", { name: "Toggle edge-node-1 section" }));
    await user.click(await screen.findByRole("button", { name: "Manage runtimes" }));

    const dialog = await screen.findByRole("dialog", { name: /manage runtimes on edge-node-1/i });
    await waitFor(() => {
      expect(screen.getByText("vllm-serve")).toBeInTheDocument();
    });

    await user.click(screen.getByText("vllm-serve"));
    await user.click(await screen.findByRole("button", { name: /register on edge-node-1/i }));

    await waitFor(() => {
      expect(dialog).not.toBeInTheDocument();
    });
  });

  it("shows a registered badge for already-registered containers", async () => {
    seedRegisteredRuntimes(HOST_RCS);
    const user = userEvent.setup();
    render(
      <TestWrapper>
        <Fleet />
      </TestWrapper>,
    );

    await waitFor(() => {
      expect(screen.getByText("llama-server")).toBeInTheDocument();
    });

    // Open the manage modal for the host via the header action
    await user.click(screen.getByRole("button", { name: "Manage runtimes on host" }));

    const dialog = await screen.findByRole("dialog", { name: /manage runtimes on host/i });
    await waitFor(() => {
      expect(within(dialog).getByText("llama-3.1-70b")).toBeInTheDocument();
    });

    // rc1 has runtimeContainerId c1 and image unswarm/llama3.1:70b-q4km —
    // the c1 card in the host picker must show the "registered" badge
    expect(within(dialog).getAllByText("registered").length).toBeGreaterThanOrEqual(1);
  });

  it("rediscover button calls rediscoverRuntime", async () => {
    seedRegisteredRuntimes(HOST_RCS);
    const user = userEvent.setup();
    const rediscoverSpy = vi.spyOn(mockClient, "rediscoverRuntime");

    render(
      <TestWrapper>
        <Fleet />
      </TestWrapper>,
    );

    await waitFor(() => {
      expect(screen.getByText("llama-server")).toBeInTheDocument();
    });

    const rediscoverButtons = screen.getAllByRole("button", { name: /rediscover/i });
    await user.click(rediscoverButtons[0]);

    await waitFor(() => {
      expect(rediscoverSpy).toHaveBeenCalled();
    });
    expect(rediscoverSpy).toHaveBeenCalledWith("rc1");
  });

  it("rediscover failure renders inline error and can be dismissed", async () => {
    seedRegisteredRuntimes(HOST_RCS);
    const user = userEvent.setup();
    vi.spyOn(mockClient, "rediscoverRuntime").mockRejectedValue(
      new Error("Model discovery failed: connection refused"),
    );

    render(
      <TestWrapper>
        <Fleet />
      </TestWrapper>,
    );

    await waitFor(() => {
      expect(screen.getByText("llama-server")).toBeInTheDocument();
    });

    const rediscoverButtons = screen.getAllByRole("button", { name: /rediscover/i });
    await user.click(rediscoverButtons[0]);

    await waitFor(() => {
      expect(screen.getByText("Model discovery failed: connection refused")).toBeInTheDocument();
    });

    // Dismiss clears the inline error.
    await user.click(screen.getByRole("button", { name: /dismiss rediscover error/i }));
    await waitFor(() => {
      expect(screen.queryByText("Model discovery failed: connection refused")).not.toBeInTheDocument();
    });
  });

  it("benchmark button calls runBenchmark with the first discovered model", async () => {
    seedRegisteredRuntimes(HOST_RCS);
    const user = userEvent.setup();
    const benchmarkSpy = vi.spyOn(mockClient, "runBenchmark");

    render(
      <TestWrapper>
        <Fleet />
      </TestWrapper>,
    );

    await waitFor(() => {
      expect(screen.getByText("llama-server")).toBeInTheDocument();
    });

    const benchmarkButtons = screen.getAllByRole("button", { name: /benchmark/i });
    await user.click(benchmarkButtons[0]);

    await waitFor(() => {
      expect(benchmarkSpy).toHaveBeenCalled();
    });
    // llama-server's first discovered model is llama-3.1-70b (id "1")
    expect(benchmarkSpy).toHaveBeenCalledTimes(1);
    expect(benchmarkSpy.mock.calls[0][0]).toBe("1");
  });

  it("benchmark result chip appears inline after running", async () => {
    seedRegisteredRuntimes(HOST_RCS);
    const user = userEvent.setup();
    vi.spyOn(mockClient, "runBenchmark").mockResolvedValueOnce({
      id: "b-test",
      modelId: "1",
      modelName: "llama-3.1-70b",
      prompt: "test",
      tokensPerSec: 55.2,
      latencyMs: 88,
      tokensGenerated: 384,
      timestamp: new Date().toISOString(),
      status: "completed",
      errorMessage: null,
    });

    render(
      <TestWrapper>
        <Fleet />
      </TestWrapper>,
    );

    await waitFor(() => {
      expect(screen.getByText("llama-server")).toBeInTheDocument();
    });

    await user.click(screen.getAllByRole("button", { name: /benchmark/i })[0]);

    await waitFor(() => {
      expect(screen.getByText(/55\.2 tok\/s · 88ms/)).toBeInTheDocument();
    });
  });

  it("benchmark is disabled when no models discovered", async () => {
    seedRegisteredRuntimes(HOST_RCS);
    render(
      <TestWrapper>
        <Fleet />
      </TestWrapper>,
    );

    // mistral-server (rc2) has no discovered models
    await waitFor(() => {
      expect(screen.getByText("mistral-server")).toBeInTheDocument();
    });

    // Walk up from the display name to the card root (Card renders div.rounded-\[var(--radius-xl)\])
    let mistralCard = screen.getByText("mistral-server").parentElement as HTMLElement;
    for (let i = 0; i < 5 && mistralCard; i++) {
      if (mistralCard.className.includes("rounded-")) break;
      mistralCard = mistralCard.parentElement as HTMLElement;
    }
    const mistralBench = within(mistralCard).getByRole("button", { name: /benchmark/i });
    expect(mistralBench).toBeDisabled();
  });

  it("renders validating model chips distinctly", async () => {
    const validatingRcs = HOST_RCS.map((rc) =>
      rc.id === "rc1"
        ? {
            ...rc,
            discoveredModels: [
              {
                ...rc.discoveredModels[0],
                name: "codestral-22b",
                id: "3",
                status: "validating" as const,
              },
            ],
          }
        : rc,
    );
    seedRegisteredRuntimes(validatingRcs);
    render(
      <TestWrapper>
        <Fleet />
      </TestWrapper>,
    );

    await waitFor(() => {
      expect(screen.getByText("codestral-22b")).toBeInTheDocument();
    });

    // The validating chip is rendered with the amber warning palette and
    // carries a distinct "validating…" label (not "ready", not an error)
    expect(screen.getByText("validating…")).toBeInTheDocument();
    let chip = screen.getByText("codestral-22b").parentElement as HTMLElement;
    for (let i = 0; i < 3 && chip; i++) {
      if (chip.className.includes("border-")) break;
      chip = chip.parentElement as HTMLElement;
    }
    expect(chip.className).toContain("color-status-warning");
  });

  it("shows loading state", () => {
    setMockLatency(500);
    const { container } = render(
      <TestWrapper>
        <Fleet />
      </TestWrapper>,
    );

    const skeletons = container.querySelectorAll("[aria-hidden='true']");
    expect(skeletons.length).toBeGreaterThan(0);
  });

  it("shows error state when API fails", async () => {
    vi.spyOn(mockClient, "listAgents").mockRejectedValueOnce(new Error("Connection refused"));

    render(
      <TestWrapper>
        <Fleet />
      </TestWrapper>,
    );

    await waitFor(() => {
      expect(screen.getByText("Failed to load fleet")).toBeInTheDocument();
    });

    expect(screen.getByText("Connection refused")).toBeInTheDocument();
  });

  it("retry button refetches after error", async () => {
    const user = userEvent.setup();
    vi.spyOn(mockClient, "listAgents")
      .mockRejectedValueOnce(new Error("Temporary failure"))
      .mockResolvedValueOnce([]);

    render(
      <TestWrapper>
        <Fleet />
      </TestWrapper>,
    );

    await waitFor(() => {
      expect(screen.getByText("Failed to load fleet")).toBeInTheDocument();
    });

    await user.click(screen.getByRole("button", { name: /retry/i }));

    await waitFor(() => {
      expect(screen.getByText("No agents connected")).toBeInTheDocument();
    });
  });

  it("add agent modal opens with connection instructions", async () => {
    seedRegisteredRuntimes(HOST_RCS);
    const user = userEvent.setup();
    render(
      <TestWrapper>
        <Fleet />
      </TestWrapper>,
    );

    await waitFor(() => {
      expect(screen.getByText("Fleet")).toBeInTheDocument();
    });

    const addButtons = screen.getAllByRole("button", { name: /add agent/i });
    await user.click(addButtons[0]);

    const dialog = await screen.findByRole("dialog", { name: /add an agent/i });
    expect(dialog).toBeInTheDocument();
    expect(screen.getByText(/unswarm-agent --config agent.yaml/i)).toBeInTheDocument();
    expect(screen.getByText(/api_key/i)).toBeInTheDocument();
  });

  it("esc closes the manage modal", async () => {
    seedRegisteredRuntimes(HOST_RCS);
    const user = userEvent.setup();
    render(
      <TestWrapper>
        <Fleet />
      </TestWrapper>,
    );

    await waitFor(() => {
      expect(screen.getByRole("button", { name: "Toggle edge-node-1 section" })).toBeInTheDocument();
    });
    await user.click(screen.getByRole("button", { name: "Toggle edge-node-1 section" }));
    await user.click(await screen.findByRole("button", { name: "Manage runtimes" }));

    const dialog = await screen.findByRole("dialog", { name: /manage runtimes on edge-node-1/i });
    await user.keyboard("{Escape}");
    await waitFor(() => {
      expect(dialog).not.toBeInTheDocument();
    });
  });

  it("locks body scroll while a modal is open and restores it on close", async () => {
    seedRegisteredRuntimes(HOST_RCS);
    const user = userEvent.setup();
    render(
      <TestWrapper>
        <Fleet />
      </TestWrapper>,
    );

    await waitFor(() => {
      expect(screen.getByRole("button", { name: "Toggle edge-node-1 section" })).toBeInTheDocument();
    });
    // Body should be scrollable before the modal opens
    expect(document.body.style.overflow).not.toBe("hidden");

    await user.click(screen.getByRole("button", { name: "Toggle edge-node-1 section" }));
    await user.click(await screen.findByRole("button", { name: "Manage runtimes" }));
    await screen.findByRole("dialog", { name: /manage runtimes on edge-node-1/i });

    // While open: background scroll locked
    expect(document.body.style.overflow).toBe("hidden");
    expect(document.body.style.touchAction).toBe("none");

    await user.keyboard("{Escape}");
    await waitFor(() => {
      expect(document.body.style.overflow).not.toBe("hidden");
    });
    expect(document.body.style.touchAction).not.toBe("none");
  });

  it("traps focus inside the dialog and restores focus to the trigger on close", async () => {
    seedRegisteredRuntimes(HOST_RCS);
    const user = userEvent.setup();
    render(
      <TestWrapper>
        <Fleet />
      </TestWrapper>,
    );

    await waitFor(() => {
      expect(screen.getByRole("button", { name: "Toggle edge-node-1 section" })).toBeInTheDocument();
    });
    await user.click(screen.getByRole("button", { name: "Toggle edge-node-1 section" }));

    const manageButton = await screen.findByRole("button", { name: "Manage runtimes" });
    manageButton.focus();
    await user.click(manageButton);

    const dialog = await screen.findByRole("dialog", { name: /manage runtimes on edge-node-1/i });
    await waitFor(() => {
      expect(screen.getByText("vllm-serve")).toBeInTheDocument();
    });

    // Close button (first focusable) is focused on open
    const closeBtn = screen.getByRole("button", { name: "Close dialog" });
    expect(closeBtn).toHaveFocus();

    // Tab from the last focusable wraps back to the first
    const focusable = dialog.querySelectorAll<HTMLElement>(
      'button:not([disabled]), input:not([disabled]), [tabindex]:not([tabindex="-1"])',
    );
    const last = focusable[focusable.length - 1];
    last.focus();
    await user.tab();
    expect(closeBtn).toHaveFocus();

    // Shift+Tab from the first wraps to the last
    closeBtn.focus();
    await user.tab({ shift: true });
    expect(last).toHaveFocus();

    // Close restores focus to the trigger (manage button)
    await user.keyboard("{Escape}");
    await waitFor(() => {
      expect(dialog).not.toBeInTheDocument();
    });
    expect(manageButton).toHaveFocus();
  });

  it("delete requires confirmation: first click does not delete, confirm does", async () => {
    seedRegisteredRuntimes(HOST_RCS);
    const user = userEvent.setup();
    const deleteSpy = vi.spyOn(mockClient, "deleteRuntime");

    render(
      <TestWrapper>
        <Fleet />
      </TestWrapper>,
    );

    await waitFor(() => {
      expect(screen.getByText("llama-server")).toBeInTheDocument();
    });

    // First click arms the inline confirmation — no client call yet
    const deleteBtn = screen.getByRole("button", { name: "Delete llama-server registration" });
    await user.click(deleteBtn);
    expect(deleteSpy).not.toHaveBeenCalled();

    // Inline confirm appears; clicking Delete confirms and calls the client
    const confirmBtn = screen.getByRole("button", { name: "Confirm delete llama-server registration" });
    await user.click(confirmBtn);
    await waitFor(() => {
      expect(deleteSpy).toHaveBeenCalledTimes(1);
    });
    expect(deleteSpy).toHaveBeenCalledWith("rc1");
  });

  it("cancel resets the delete confirmation without deleting", async () => {
    seedRegisteredRuntimes(HOST_RCS);
    const user = userEvent.setup();
    const deleteSpy = vi.spyOn(mockClient, "deleteRuntime");

    render(
      <TestWrapper>
        <Fleet />
      </TestWrapper>,
    );

    await waitFor(() => {
      expect(screen.getByText("llama-server")).toBeInTheDocument();
    });

    await user.click(screen.getByRole("button", { name: "Delete llama-server registration" }));
    await user.click(screen.getByRole("button", { name: "Cancel delete llama-server registration" }));

    expect(deleteSpy).not.toHaveBeenCalled();
    // The inline confirm is gone; the plain delete affordance is back
    expect(screen.queryByRole("button", { name: "Confirm delete llama-server registration" })).not.toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Delete llama-server registration" })).toBeInTheDocument();
  });

  it("shows an error message when registering a container fails", async () => {
    seedRegisteredRuntimes(HOST_RCS);
    const user = userEvent.setup();
    vi.spyOn(mockClient, "registerRuntime").mockRejectedValueOnce(new Error("Image not found"));

    render(
      <TestWrapper>
        <Fleet />
      </TestWrapper>,
    );

    await waitFor(() => {
      expect(screen.getByRole("button", { name: "Toggle edge-node-1 section" })).toBeInTheDocument();
    });
    await user.click(screen.getByRole("button", { name: "Toggle edge-node-1 section" }));
    await user.click(await screen.findByRole("button", { name: "Manage runtimes" }));

    await screen.findByRole("dialog", { name: /manage runtimes on edge-node-1/i });
    await waitFor(() => {
      expect(screen.getByText("vllm-serve")).toBeInTheDocument();
    });

    await user.click(screen.getByText("vllm-serve"));
    await user.click(await screen.findByRole("button", { name: /register on edge-node-1/i }));

    await waitFor(() => {
      expect(screen.getByText("Image not found")).toBeInTheDocument();
    });
    // Dialog stays open so the user can retry
    expect(screen.getByRole("dialog", { name: /manage runtimes on edge-node-1/i })).toBeInTheDocument();
  });

  it("shows a created-status container with the starting dot variant", async () => {
    seedRegisteredRuntimes(HOST_RCS);
    const user = userEvent.setup();
    vi.spyOn(mockClient, "listAgentContainers").mockResolvedValueOnce([
      {
        id: "c-created",
        modelId: "",
        modelName: "spawn-me",
        status: "created" as const,
        port: null,
        pid: null,
        memoryMb: 0,
        cpuPercent: 0,
        uptime: 0,
        lastHealthCheck: null,
        errorMessage: null,
        createdAt: new Date().toISOString(),
      },
    ]);

    render(
      <TestWrapper>
        <Fleet />
      </TestWrapper>,
    );

    await waitFor(() => {
      expect(screen.getByRole("button", { name: "Toggle edge-node-1 section" })).toBeInTheDocument();
    });
    await user.click(screen.getByRole("button", { name: "Toggle edge-node-1 section" }));
    await user.click(await screen.findByRole("button", { name: "Manage runtimes" }));

    const dialog = await screen.findByRole("dialog", { name: /manage runtimes on edge-node-1/i });
    await waitFor(() => {
      expect(within(dialog).getByText("spawn-me")).toBeInTheDocument();
    });

    // The status dot for "created" maps to the starting (blue) variant — rendered
    // with the starting color and the pulsing ring.
    const dot = within(dialog).getByRole("status", { name: "created" });
    expect(dot.querySelector(".inline-block")).toHaveClass("bg-[var(--color-status-starting)]");
    expect(dot.querySelector(".absolute")).toBeInTheDocument();
  });

  it("treats a registered container as registered case-insensitively", async () => {
    // Register rc1's runtime container as "C1" — a case-mismatch with the
    // picker's id "c1" — and ensure the card is still marked registered/disabled.
    const caseRcs = HOST_RCS.map((rc) =>
      rc.id === "rc1" ? { ...rc, runtimeContainerId: "C1" } : rc,
    );
    seedRegisteredRuntimes(caseRcs);
    const user = userEvent.setup();
    render(
      <TestWrapper>
        <Fleet />
      </TestWrapper>,
    );

    await waitFor(() => {
      expect(screen.getByText("llama-server")).toBeInTheDocument();
    });

    await user.click(screen.getByRole("button", { name: "Manage runtimes on host" }));
    const dialog = await screen.findByRole("dialog", { name: /manage runtimes on host/i });
    await waitFor(() => {
      expect(within(dialog).getByText("llama-3.1-70b")).toBeInTheDocument();
    });

    // The host picker lists c1 (id "c1"); with runtimeContainerId "C1" it must
    // still render as registered (disabled, not selectable).
    expect(within(dialog).getAllByText("registered").length).toBeGreaterThanOrEqual(1);
  });

  it("deep link focus expands a collapsed agent and highlights its container", async () => {
    // rc3 lives on edge-node-1, which normally starts collapsed.
    const edgeRcs = HOST_RCS.map((rc) =>
      rc.id === "rc1"
        ? {
            ...rc,
            id: "rc3",
            displayName: "edge-llama-server",
            agent: "edge-node-1",
            runtimeContainerId: "en-vllm",
          }
        : rc,
    );
    seedRegisteredRuntimes([...edgeRcs, HOST_RCS[0]]);
    render(
      <TestWrapper initialEntries={["/fleet?focus=rc3"]}>
        <Fleet />
      </TestWrapper>,
    );

    // The edge-node-1 section is force-expanded by the focus param, so its card is visible.
    await waitFor(() => {
      expect(screen.getByText("edge-llama-server")).toBeInTheDocument();
    });

    const toggle = screen.getByRole("button", { name: "Toggle edge-node-1 section" });
    expect(toggle).toHaveAttribute("aria-expanded", "true");

    // The focused card carries the highlight ring
    let card = screen.getByText("edge-llama-server").parentElement as HTMLElement;
    for (let i = 0; i < 5 && card; i++) {
      if (card.className.includes("ring-")) break;
      card = card.parentElement as HTMLElement;
    }
    expect(card.className).toContain("ring-");
  });

  it("deep link focus does not expand agents that do not own the container", async () => {
    // focus=rc1 belongs to host (already expanded by default) — edge-node-1 must stay collapsed.
    seedRegisteredRuntimes(HOST_RCS);
    render(
      <TestWrapper initialEntries={["/fleet?focus=rc1"]}>
        <Fleet />
      </TestWrapper>,
    );

    await waitFor(() => {
      expect(screen.getByText("llama-server")).toBeInTheDocument();
    });

    const edgeToggle = screen.getByRole("button", { name: "Toggle edge-node-1 section" });
    expect(edgeToggle).toHaveAttribute("aria-expanded", "false");
  });

  // ─── Runtime status dot + contextual lifecycle buttons ──────────

  /** Host agent with a custom runtime telemetry status for c1 (rc1's runtime container). */
  function seedHostTelemetry(status: string) {
    vi.spyOn(mockClient, "listAgents").mockResolvedValue([
      {
        name: "host",
        connectionId: null,
        connectedAt: null,
        lastSeen: new Date().toISOString(),
        isConnected: true,
        dockerSocket: "/var/run/docker.sock",
        version: "1.2.3",
        hostname: "workstation",
        osPlatform: "linux/amd64",
        gpuInfo: null,
        totalMemoryMb: 131072,
        cpuCores: 16,
        containers: [
          { containerId: "c1", modelName: "llama-3.1-70b", status, port: 8081 },
          { containerId: "c2", modelName: "mistral-large-2", status: "starting", port: null },
          { containerId: "c3", modelName: "gemma-2-27b", status: "stopped", port: null },
        ],
        scripts: [],
      },
    ]);
  }

  /** Walk up from a display name to its card root. */
  function cardFor(displayName: string): HTMLElement {
    let el = screen.getByText(displayName).parentElement as HTMLElement;
    for (let i = 0; i < 6 && el; i++) {
      if (el.className.includes("rounded-")) break;
      el = el.parentElement as HTMLElement;
    }
    return el;
  }

  it("shows a red runtime dot and a Start button when the container is stopped", async () => {
    seedRegisteredRuntimes(HOST_RCS);
    seedHostTelemetry("stopped");
    render(
      <TestWrapper>
        <Fleet />
      </TestWrapper>,
    );

    await waitFor(() => {
      expect(screen.getByText("llama-server")).toBeInTheDocument();
    });

    // Red dot = error color; tooltip says Stopped (scope to llama-server's card)
    const card = cardFor("llama-server");
    expect(within(card).getByRole("status", { name: "error" })).toBeInTheDocument();
    expect(screen.getAllByText("Runtime: Stopped").length).toBeGreaterThanOrEqual(1);

    // Stop/Restart are hidden; Start (primary) is shown on this card
    expect(within(card).getByRole("button", { name: /^start$/i })).toBeInTheDocument();
    expect(within(card).queryByRole("button", { name: /^stop$/i })).not.toBeInTheDocument();
    expect(within(card).queryByRole("button", { name: /^restart$/i })).not.toBeInTheDocument();
  });

  it("clicking Start calls startRegisteredRuntime with the registered id", async () => {
    seedRegisteredRuntimes(HOST_RCS);
    seedHostTelemetry("stopped");
    const user = userEvent.setup();
    const startSpy = vi.spyOn(mockClient, "startRegisteredRuntime");

    render(
      <TestWrapper>
        <Fleet />
      </TestWrapper>,
    );

    await waitFor(() => {
      expect(screen.getByText("llama-server")).toBeInTheDocument();
    });

    await user.click(within(cardFor("llama-server")).getByRole("button", { name: /^start$/i }));

    await waitFor(() => {
      expect(startSpy).toHaveBeenCalledTimes(1);
    });
    expect(startSpy).toHaveBeenCalledWith("rc1");
  });

  it("refreshes the runtime dot after a successful start", async () => {
    seedRegisteredRuntimes(HOST_RCS);
    const user = userEvent.setup();
    // Shared, mutable agent telemetry: listAgents refetches this same array after
    // invalidation, so flipping the status here is what a live backend would report.
    const agents: Agent[] = [
      {
        name: "host",
        connectionId: null,
        connectedAt: null,
        lastSeen: new Date().toISOString(),
        isConnected: true,
        dockerSocket: "/var/run/docker.sock",
        version: "1.2.3",
        hostname: "workstation",
        osPlatform: "linux/amd64",
        gpuInfo: null,
        totalMemoryMb: 131072,
        cpuCores: 16,
        containers: [
          { containerId: "c1", modelName: "llama-3.1-70b", status: "stopped", port: null },
        ],
        scripts: [],
      },
    ];
    vi.spyOn(mockClient, "listAgents").mockImplementation(async () =>
      agents.map((a) => ({ ...a, containers: a.containers.map((c) => ({ ...c })), scripts: a.scripts.map((s) => ({ ...s })) })),
    );
    // The mock keeps module state (prior tests may have removed rc1), so stub the
    // start directly: onSuccess only invalidates — the returned value is unused.
    vi.spyOn(mockClient, "startRegisteredRuntime").mockImplementation(async (id) => {
      agents[0].containers[0].status = "running";
      return HOST_RCS.find((rc) => rc.id === id) ?? HOST_RCS[0];
    });

    render(
      <TestWrapper>
        <Fleet />
      </TestWrapper>,
    );

    await waitFor(() => {
      expect(screen.getByText("llama-server")).toBeInTheDocument();
    });

    const card = cardFor("llama-server");
    expect(within(card).getByRole("button", { name: /^start$/i })).toBeInTheDocument();

    await user.click(within(card).getByRole("button", { name: /^start$/i }));

    // The ["agents"] invalidation refetches telemetry; the dot flips to running
    // and the lifecycle buttons switch from Start to Stop/Restart.
    await waitFor(() => {
      expect(screen.getAllByText("Runtime: Running").length).toBeGreaterThanOrEqual(1);
    });
    expect(within(card).getByRole("button", { name: /^stop$/i })).toBeInTheDocument();
    expect(within(card).queryByRole("button", { name: /^start$/i })).not.toBeInTheDocument();
  });

  it("shows a green runtime dot with Stop/Restart when the container is running", async () => {
    seedRegisteredRuntimes(HOST_RCS);
    seedHostTelemetry("running");
    render(
      <TestWrapper>
        <Fleet />
      </TestWrapper>,
    );

    await waitFor(() => {
      expect(screen.getByText("llama-server")).toBeInTheDocument();
    });

    const card = cardFor("llama-server");
    expect(within(card).getByRole("status", { name: "running" })).toBeInTheDocument();
    expect(screen.getAllByText("Runtime: Running").length).toBeGreaterThanOrEqual(1);

    expect(within(card).getByRole("button", { name: /^stop$/i })).toBeInTheDocument();
    expect(within(card).getByRole("button", { name: /^restart$/i })).toBeInTheDocument();
    expect(within(card).queryByRole("button", { name: /^start$/i })).not.toBeInTheDocument();
  });

  it("shows a yellow dot for a transitional runtime status with no lifecycle buttons", async () => {
    seedRegisteredRuntimes(HOST_RCS);
    seedHostTelemetry("starting");
    render(
      <TestWrapper>
        <Fleet />
      </TestWrapper>,
    );

    await waitFor(() => {
      expect(screen.getByText("llama-server")).toBeInTheDocument();
    });

    const card = cardFor("llama-server");
    // Transitional maps to the starting dot; assert its semantics.
    expect(within(card).getByRole("status", { name: "starting" })).toBeInTheDocument();
    expect(screen.getAllByText("Runtime: Starting…").length).toBeGreaterThanOrEqual(1);

    // No lifecycle action for a transitional container
    expect(within(card).queryByRole("button", { name: /^start$/i })).not.toBeInTheDocument();
    expect(within(card).queryByRole("button", { name: /^stop$/i })).not.toBeInTheDocument();
    expect(within(card).queryByRole("button", { name: /^restart$/i })).not.toBeInTheDocument();
  });

  it("shows a gray dot for unknown runtime status but still offers Start", async () => {
    seedRegisteredRuntimes(HOST_RCS);
    // c1 not present in telemetry → no match → unknown
    vi.spyOn(mockClient, "listAgents").mockResolvedValue([
      {
        name: "host",
        connectionId: null,
        connectedAt: null,
        lastSeen: new Date().toISOString(),
        isConnected: true,
        dockerSocket: "/var/run/docker.sock",
        version: "1.2.3",
        hostname: "workstation",
        osPlatform: "linux/amd64",
        gpuInfo: null,
        totalMemoryMb: 131072,
        cpuCores: 16,
        containers: [
          { containerId: "cX", modelName: "some-other-container", status: "running", port: 9999 },
        ],
        scripts: [],
      },
    ]);
    render(
      <TestWrapper>
        <Fleet />
      </TestWrapper>,
    );

    await waitFor(() => {
      expect(screen.getByText("llama-server")).toBeInTheDocument();
    });

    const card = cardFor("llama-server");
    // Unknown → neutral gray dot (stopped color) + "Unknown" tooltip
    expect(within(card).getByRole("status", { name: "stopped" })).toBeInTheDocument();
    expect(screen.getAllByText("Runtime: Unknown").length).toBeGreaterThanOrEqual(1);

    // Decision: unknown → show Start (the container may simply be down / unreported).
    expect(within(card).getByRole("button", { name: /^start$/i })).toBeInTheDocument();
  });

  it("rediscover failure on a stopped container shows the start-first hint", async () => {
    seedRegisteredRuntimes(HOST_RCS);
    seedHostTelemetry("stopped");
    const user = userEvent.setup();
    vi.spyOn(mockClient, "rediscoverRuntime").mockRejectedValueOnce(new Error("Container is not responding"));

    render(
      <TestWrapper>
        <Fleet />
      </TestWrapper>,
    );

    await waitFor(() => {
      expect(screen.getByText("llama-server")).toBeInTheDocument();
    });

    await user.click(screen.getAllByRole("button", { name: /rediscover/i })[0]);

    await waitFor(() => {
      expect(
        screen.getByText(/container appears to be stopped; start it first/i),
      ).toBeInTheDocument();
    });
    expect(screen.getByText(/Container is not responding/)).toBeInTheDocument();
  });

  // ─── Concurrency modal ──────────────────────────────────────

  const CONCURRENCY_RCS: RegisteredRuntime[] = [
    {
      id: "rc1",
      displayName: "llama-server",
      image: "unswarm/llama3.1:70b-q4km",
      containerPort: 8080,
      agent: "host",
      canRunAlongWith: [],
      maxConcurrentInferences: 1,
      status: "ready",
      runtimeContainerId: "c1",
      mappedPort: 8081,
      errorMessage: null,
      createdAt: new Date().toISOString(),
      lastDiscoveredAt: new Date().toISOString(),
      discoveredModels: [
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
          createdAt: new Date().toISOString(),
          updatedAt: new Date().toISOString(),
        },
      ],
    },
    {
      id: "rc2",
      displayName: "mistral-server",
      image: "unswarm/mistral-large:123b-q5km",
      containerPort: 8080,
      agent: "host",
      canRunAlongWith: ["llama-server"],
      maxConcurrentInferences: 1,
      status: "ready",
      runtimeContainerId: "c2",
      mappedPort: 8082,
      errorMessage: null,
      createdAt: new Date().toISOString(),
      lastDiscoveredAt: new Date().toISOString(),
      discoveredModels: [],
    },
    {
      id: "rc3",
      displayName: "vllm-script",
      image: "run_vllm",
      containerPort: 8080,
      agent: "host",
      canRunAlongWith: [],
      maxConcurrentInferences: 1,
      status: "ready",
      runtimeContainerId: null,
      mappedPort: 8083,
      errorMessage: null,
      createdAt: new Date().toISOString(),
      lastDiscoveredAt: null,
      discoveredModels: [],
      runtimeKind: "script",
      launcherPath: "/opt/scripts/run_vllm.sh",
    },
  ];

  it("agent header shows Concurrency button and opens the matrix dialog", async () => {
    seedRegisteredRuntimes(CONCURRENCY_RCS);
    const user = userEvent.setup();
    render(
      <TestWrapper>
        <Fleet />
      </TestWrapper>,
    );

    await waitFor(() => {
      expect(screen.getByText("llama-server")).toBeInTheDocument();
    });

    const concButton = screen.getByRole("button", { name: "Concurrency on host" });
    expect(concButton).toBeInTheDocument();
    await user.click(concButton);

    const dialog = await screen.findByRole("dialog", { name: /concurrency on host/i });
    expect(dialog).toBeInTheDocument();
  });

  it("matrix renders N x N switches for seeded runtimes including a script runtime", async () => {
    seedRegisteredRuntimes(CONCURRENCY_RCS);
    const user = userEvent.setup();
    render(
      <TestWrapper>
        <Fleet />
      </TestWrapper>,
    );

    await waitFor(() => {
      expect(screen.getByText("llama-server")).toBeInTheDocument();
    });

    await user.click(screen.getByRole("button", { name: "Concurrency on host" }));
    const dialog = await screen.findByRole("dialog", { name: /concurrency on host/i });

    await waitFor(() => {
      expect(within(dialog).getAllByText("llama-server").length).toBeGreaterThanOrEqual(1);
    });
    expect(within(dialog).getAllByText("mistral-server").length).toBeGreaterThanOrEqual(1);
    expect(within(dialog).getAllByText("vllm-script").length).toBeGreaterThanOrEqual(1);

    // Row and column headers: displayName appears at least twice (row + column)
    const llamaHeaders = within(dialog).getAllByText("llama-server");
    expect(llamaHeaders.length).toBeGreaterThanOrEqual(2);

    // 3 runtimes => 6 off-diagonal switches
    const switches = within(dialog).getAllByRole("switch");
    expect(switches.length).toBe(6);

    // Pre-existing: mistral-server.canRunAlongWith includes "llama-server"
    const mistralWithLlama = within(dialog).getByRole("switch", {
      name: /mistral-server with llama-server/i,
    });
    expect(mistralWithLlama).toBeChecked();

    // Reverse (llama -> mistral) is OFF since llama-server.canRunAlongWith is empty
    const llamaWithMistral = within(dialog).getByRole("switch", {
      name: /llama-server with mistral-server/i,
    });
    expect(llamaWithMistral).not.toBeChecked();
  });

  it("toggling cell OFF->ON calls toggleRuntimeConcurrency once with correct payload", async () => {
    seedRegisteredRuntimes(CONCURRENCY_RCS);
    const user = userEvent.setup();
    const toggleSpy = vi.spyOn(mockClient, "toggleRuntimeConcurrency").mockImplementation(
      async (payload) => {
        const rcA = CONCURRENCY_RCS.find((r) => r.id === payload.runtimeAId) ?? CONCURRENCY_RCS[0];
        const rcB = CONCURRENCY_RCS.find((r) => r.id === payload.runtimeBId) ?? CONCURRENCY_RCS[2];
        const newA = payload.canRunAlongWith
          ? [...rcA.canRunAlongWith, rcB.displayName]
          : rcA.canRunAlongWith.filter((n) => n !== rcB.displayName);
        const newB = payload.canRunAlongWith
          ? [...rcB.canRunAlongWith, rcA.displayName]
          : rcB.canRunAlongWith.filter((n) => n !== rcA.displayName);
        return {
          a: { ...rcA, canRunAlongWith: newA },
          b: { ...rcB, canRunAlongWith: newB },
        };
      },
    );

    render(
      <TestWrapper>
        <Fleet />
      </TestWrapper>,
    );

    await waitFor(() => {
      expect(screen.getByText("llama-server")).toBeInTheDocument();
    });

    await user.click(screen.getByRole("button", { name: "Concurrency on host" }));
    await screen.findByRole("dialog", { name: /concurrency on host/i });

    // Toggle llama-server <-> vllm-script from OFF to ON
    const switchEl = screen.getByRole("switch", {
      name: /llama-server with vllm-script/i,
    });
    expect(switchEl).not.toBeChecked();
    await user.click(switchEl);

    await waitFor(() => {
      expect(toggleSpy).toHaveBeenCalledTimes(1);
    });

    // Single atomic call with both runtime IDs and canRunAlongWith=true
    const [callPayload] = toggleSpy.mock.calls[0];
    expect(callPayload.runtimeAId).toBe("rc1");
    expect(callPayload.runtimeBId).toBe("rc3");
    expect(callPayload.canRunAlongWith).toBe(true);
  });

  it("empty-list runtime shows the runs-alone hint", async () => {
    seedRegisteredRuntimes(CONCURRENCY_RCS);
    const user = userEvent.setup();
    render(
      <TestWrapper>
        <Fleet />
      </TestWrapper>,
    );

    await waitFor(() => {
      expect(screen.getByText("llama-server")).toBeInTheDocument();
    });

    await user.click(screen.getByRole("button", { name: "Concurrency on host" }));
    await screen.findByRole("dialog", { name: /concurrency on host/i });

    await waitFor(() => {
      expect(screen.getByText(/run independently and will not share resources/i)).toBeInTheDocument();
    });
  });

  it("mock client updateRuntimeConcurrency returns the updated record", async () => {
    // Register a fresh runtime so the module-level array has a known entry
    const rc = await mockClient.registerRuntime({
      displayName: "test-concurrency",
      image: "test-image:latest",
      containerPort: 9090,
      agent: "host",
    });
    const result = await mockClient.updateRuntimeConcurrency(rc.id, {
      canRunAlongWith: ["some-peer"],
    });
    expect(result.id).toBe(rc.id);
    expect(result.canRunAlongWith).toEqual(["some-peer"]);
  });

  // ─── Available scripts picker (remote flow) ────────────────────

  it("scripts tab lists available .sh files as selectable cards", async () => {
    seedRegisteredRuntimes(HOST_RCS);
    const user = userEvent.setup();

    render(
      <TestWrapper>
        <Fleet />
      </TestWrapper>,
    );

    await waitFor(() => {
      expect(screen.getByRole("button", { name: "Toggle edge-node-1 section" })).toBeInTheDocument();
    });
    await user.click(screen.getByRole("button", { name: "Toggle edge-node-1 section" }));
    await user.click(await screen.findByRole("button", { name: "Manage runtimes" }));

    const dialog = await screen.findByRole("dialog", { name: /manage runtimes on edge-node-1/i });

    // Switch to scripts tab
    await user.click(within(dialog).getByRole("tab", { name: /scripts/i }));

    // Mock returns 3 available scripts for edge-node-1
    await waitFor(() => {
      expect(within(dialog).getByText("run_llama.sh")).toBeInTheDocument();
    });
    expect(within(dialog).getByText("run_vllm.sh")).toBeInTheDocument();
    expect(within(dialog).getByText("start_api.sh")).toBeInTheDocument();
  });

  it("selecting an available script and registering sends correct payload", async () => {
    seedRegisteredRuntimes(HOST_RCS);
    const user = userEvent.setup();
    const registerSpy = vi.spyOn(mockClient, "registerRuntime");

    render(
      <TestWrapper>
        <Fleet />
      </TestWrapper>,
    );

    await waitFor(() => {
      expect(screen.getByRole("button", { name: "Toggle edge-node-1 section" })).toBeInTheDocument();
    });
    await user.click(screen.getByRole("button", { name: "Toggle edge-node-1 section" }));
    await user.click(await screen.findByRole("button", { name: "Manage runtimes" }));

    const dialog = await screen.findByRole("dialog", { name: /manage runtimes on edge-node-1/i });
    await user.click(within(dialog).getByRole("tab", { name: /scripts/i }));

    await waitFor(() => {
      expect(within(dialog).getByText("run_vllm.sh")).toBeInTheDocument();
    });

    // Click the script card to select it
    await user.click(within(dialog).getByText("run_vllm.sh"));

    // Confirm panel appears — port default is 8080
    const portInput = within(dialog).getByRole("spinbutton", { name: /port/i });
    expect(portInput).toHaveValue(8080);

    // Change port
    await user.clear(portInput);
    await user.type(portInput, "9000");

    // Click register
    await user.click(within(dialog).getByRole("button", { name: /register on edge-node-1/i }));

    await waitFor(() => {
      expect(registerSpy).toHaveBeenCalledTimes(1);
    });

    const payload = registerSpy.mock.calls[0][0] as RegisterRuntimePayload;
    expect(payload.runtimeKind).toBe("script");
    expect(payload.launcherPath).toBe("/home/user/scripts/run_vllm.sh");
    expect(payload.containerPort).toBe(9000);
    expect(payload.agent).toBe("edge-node-1");
  });

  it("empty available scripts shows grounded copy", async () => {
    seedRegisteredRuntimes(HOST_RCS);
    // gpu-node-1 returns [] from the mock
    vi.spyOn(mockClient, "listAgents").mockResolvedValue([
      {
        name: "gpu-node-1",
        connectionId: "conn-gpu",
        connectedAt: new Date().toISOString(),
        lastSeen: new Date().toISOString(),
        isConnected: true,
        dockerSocket: "/var/run/docker.sock",
        version: "1.0.0",
        hostname: "gpu-node-1",
        osPlatform: "linux/amd64",
        gpuInfo: null,
        totalMemoryMb: 16384,
        cpuCores: 8,
        containers: [],
        scripts: [],
      },
    ]);

    const user = userEvent.setup();
    render(
      <TestWrapper>
        <Fleet />
      </TestWrapper>,
    );

    await waitFor(() => {
      expect(screen.getByRole("button", { name: "Toggle gpu-node-1 section" })).toBeInTheDocument();
    });
    await user.click(screen.getByRole("button", { name: "Toggle gpu-node-1 section" }));
    await user.click(await screen.findByRole("button", { name: "Manage runtimes" }));

    const dialog = await screen.findByRole("dialog", { name: /manage runtimes on gpu-node-1/i });
    await user.click(within(dialog).getByRole("tab", { name: /scripts/i }));

    await waitFor(() => {
      expect(within(dialog).getByText("No scripts found")).toBeInTheDocument();
    });
    expect(within(dialog).getByText(/No scripts found on gpu-node-1\. Add \.sh files to the agent's scripts_dir\./i)).toBeInTheDocument();
  });

  it("error fetching available scripts shows inline error with retry", async () => {
    seedRegisteredRuntimes(HOST_RCS);
    vi.spyOn(mockClient, "listAvailableScripts").mockRejectedValueOnce(
      new Error("Agent unreachable"),
    );

    const user = userEvent.setup();
    render(
      <TestWrapper>
        <Fleet />
      </TestWrapper>,
    );

    await waitFor(() => {
      expect(screen.getByRole("button", { name: "Toggle edge-node-1 section" })).toBeInTheDocument();
    });
    await user.click(screen.getByRole("button", { name: "Toggle edge-node-1 section" }));
    await user.click(await screen.findByRole("button", { name: "Manage runtimes" }));

    const dialog = await screen.findByRole("dialog", { name: /manage runtimes on edge-node-1/i });
    await user.click(within(dialog).getByRole("tab", { name: /scripts/i }));

    await waitFor(() => {
      expect(within(dialog).getByText("Couldn't reach edge-node-1 to list scripts.")).toBeInTheDocument();
    });

    // Retry refetches
    vi.spyOn(mockClient, "listAvailableScripts").mockResolvedValueOnce([
      { path: "/home/user/scripts/run_llama.sh", name: "run_llama.sh" },
    ]);

    await user.click(within(dialog).getByRole("button", { name: /retry/i }));

    await waitFor(() => {
      expect(within(dialog).getByText("run_llama.sh")).toBeInTheDocument();
    });
  });

  // ─── Script Stop button ──────────────────────────────────────────

  const SCRIPT_RC: RegisteredRuntime[] = [
    {
      id: "rc-script-1",
      displayName: "vllm-script",
      image: "run_vllm",
      containerPort: 8080,
      agent: "host",
      canRunAlongWith: [],
      maxConcurrentInferences: 1,
      status: "ready",
      runtimeContainerId: null,
      mappedPort: 8083,
      errorMessage: null,
      createdAt: new Date().toISOString(),
      lastDiscoveredAt: null,
      discoveredModels: [],
      runtimeKind: "script",
      launcherPath: "/opt/scripts/run_vllm.sh",
    },
  ];

  it("running script renders Stop button and calls stopRegisteredRuntime", async () => {
    seedRegisteredRuntimes(SCRIPT_RC);
    // The agent must have the script listed with status "running" so runtimeSignal resolves to "running".
    vi.spyOn(mockClient, "listAgents").mockResolvedValue([
      {
        name: "host",
        connectionId: null,
        connectedAt: null,
        lastSeen: new Date().toISOString(),
        isConnected: true,
        dockerSocket: "/var/run/docker.sock",
        version: "1.2.3",
        hostname: "workstation",
        osPlatform: "linux/amd64",
        gpuInfo: "NVIDIA GeForce RTX 4090 (24GB)",
        totalMemoryMb: 131072,
        cpuCores: 16,
        containers: [],
        scripts: [
          { path: "/opt/scripts/run_vllm.sh", pid: 1234, status: "running", port: 8083, startTime: Date.now() },
        ],
      },
      {
        name: "edge-node-1",
        connectionId: "conn-1",
        connectedAt: new Date().toISOString(),
        lastSeen: new Date().toISOString(),
        isConnected: true,
        dockerSocket: "/var/run/docker.sock",
        version: "0.9.1",
        hostname: "edge-node-1",
        osPlatform: "linux/arm64",
        gpuInfo: null,
        totalMemoryMb: 16384,
        cpuCores: 8,
        containers: [],
        scripts: [],
      },
    ]);

    const stopSpy = vi.spyOn(mockClient, "stopRegisteredRuntime").mockImplementation(async (id) => {
      const rc = SCRIPT_RC.find((r) => r.id === id)!;
      return { ...rc, status: "registered" };
    });

    const user = userEvent.setup();
    render(
      <TestWrapper>
        <Fleet />
      </TestWrapper>,
    );

    await waitFor(() => {
      expect(screen.getByText("vllm-script")).toBeInTheDocument();
    });

    // Stop button should be visible for a running script
    const stopBtn = screen.getByRole("button", { name: /Stop/i });
    expect(stopBtn).toBeInTheDocument();

    await user.click(stopBtn);

    await waitFor(() => {
      expect(stopSpy).toHaveBeenCalledWith("rc-script-1");
    });
  });

  it("down script renders Start button", async () => {
    // Script with status "registered" + agent script status "stopped" => signal "down"
    const downScriptRc: RegisteredRuntime[] = [
      {
        ...SCRIPT_RC[0],
        status: "registered" as const,
      },
    ];
    seedRegisteredRuntimes(downScriptRc);
    vi.spyOn(mockClient, "listAgents").mockResolvedValue([
      {
        name: "host",
        connectionId: null,
        connectedAt: null,
        lastSeen: new Date().toISOString(),
        isConnected: true,
        dockerSocket: "/var/run/docker.sock",
        version: "1.2.3",
        hostname: "workstation",
        osPlatform: "linux/amd64",
        gpuInfo: "NVIDIA GeForce RTX 4090 (24GB)",
        totalMemoryMb: 131072,
        cpuCores: 16,
        containers: [],
        scripts: [
          { path: "/opt/scripts/run_vllm.sh", pid: 0, status: "stopped", port: 0, startTime: 0 },
        ],
      },
      {
        name: "edge-node-1",
        connectionId: "conn-1",
        connectedAt: new Date().toISOString(),
        lastSeen: new Date().toISOString(),
        isConnected: true,
        dockerSocket: "/var/run/docker.sock",
        version: "0.9.1",
        hostname: "edge-node-1",
        osPlatform: "linux/arm64",
        gpuInfo: null,
        totalMemoryMb: 16384,
        cpuCores: 8,
        containers: [],
        scripts: [],
      },
    ]);

    render(
      <TestWrapper>
        <Fleet />
      </TestWrapper>,
    );

    await waitFor(() => {
      expect(screen.getByText("vllm-script")).toBeInTheDocument();
    });

    // Start button should be visible for a down script
    expect(screen.getByRole("button", { name: /Start/i })).toBeInTheDocument();
    // No Stop button for a down script
    expect(screen.queryByRole("button", { name: /Stop/i })).not.toBeInTheDocument();
  });

  it("mock stopRegisteredRuntime flips status to registered and preserves runtimeKind", async () => {
    const rc = await mockClient.registerRuntime({
      displayName: "test-script-stop",
      image: "test-image:latest",
      containerPort: 9090,
      agent: "host",
      runtimeKind: "script",
      launcherPath: "/tmp/test.sh",
    });
    expect(rc.runtimeKind).toBe("script");
    expect(rc.launcherPath).toBe("/tmp/test.sh");

    const result = await mockClient.stopRegisteredRuntime(rc.id);
    expect(result.status).toBe("registered");
    expect(result.runtimeKind).toBe("script");
    expect(result.launcherPath).toBe("/tmp/test.sh");
  });
});
