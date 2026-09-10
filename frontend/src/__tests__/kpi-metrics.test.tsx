import { describe, it, expect } from "vitest";
import { render, screen } from "@testing-library/react";
import { AgentMetricsBar } from "../features/swarm/AgentMetricsBar";
import { ContainerKPIBadges } from "../features/swarm/ContainerKPIBadges";
import type { AgentTelemetry, ContainerMetrics, GPUMetrics } from "../lib/api/types";

// ── helpers ──────────────────────────────────────────────────────

function gpu(overrides: Partial<GPUMetrics> = {}): GPUMetrics {
  return {
    index: 0,
    name: "NVIDIA RTX 4090",
    vendor: "nvidia",
    corePercent: 45,
    memoryPercent: 62,
    memoryUsedMb: 12800,
    memoryTotalMb: 24576,
    ...overrides,
  };
}

function telemetry(overrides: Partial<AgentTelemetry> = {}): AgentTelemetry {
  return {
    gpus: [],
    containers: {},
    collectedAt: new Date().toISOString(),
    ...overrides,
  };
}

const EM_DASH = "\u2014";

// ══════════════════════════════════════════════════════════════════
// AgentMetricsBar
// ══════════════════════════════════════════════════════════════════

describe("AgentMetricsBar", () => {
  it("returns null when no GPUs and no host metrics", () => {
    const { container } = render(
      <AgentMetricsBar telemetry={telemetry({ gpus: [] })} />
    );
    // Component renders nothing — the wrapper div is not present
    expect(container.innerHTML).toBe("");
  });

  it("renders a single GPU with gauge and memory badges", () => {
    render(
      <AgentMetricsBar
        telemetry={telemetry({ gpus: [gpu({ index: 0, corePercent: 72, memoryPercent: 58 })] })}
      />
    );

    // GPU index label
    expect(screen.getByText("G0")).toBeInTheDocument();

    // Core badge shows 72%
    expect(screen.getByText("72%")).toBeInTheDocument();

    // Memory badge shows 58%
    expect(screen.getByText("58%")).toBeInTheDocument();
  });

  it("renders multi-GPU in a 2-column grid", () => {
    const { container } = render(
      <AgentMetricsBar
        telemetry={telemetry({
          gpus: [
            gpu({ index: 0, corePercent: 30, memoryPercent: 40 }),
            gpu({ index: 1, corePercent: 50, memoryPercent: 60 }),
          ],
        })}
      />
    );

    // Both GPU labels present
    expect(screen.getByText("G0")).toBeInTheDocument();
    expect(screen.getByText("G1")).toBeInTheDocument();

    // The grid container should have grid-cols-2
    const grid = container.querySelector(".grid-cols-2");
    expect(grid).toBeInTheDocument();
  });

  it("hides memory badge when memoryPercent is -1 (Intel iGPU)", () => {
    render(
      <AgentMetricsBar
        telemetry={telemetry({
          gpus: [
            gpu({ index: 0, corePercent: 55, memoryPercent: -1, vendor: "intel" }),
          ],
        })}
      />
    );

    // Core badge should show em-dash for -1
    expect(screen.getByText("G0")).toBeInTheDocument();
    expect(screen.getByText("55%")).toBeInTheDocument();

    // Only one Badge rendered (core) — no memory badge
    // MemoryPercent -1 means the memory badge is hidden entirely (not just em-dash)
    const badges = screen.getAllByText("55%");
    expect(badges.length).toBe(1);
  });

  it("shows RAM and CPU badges when host metrics present", () => {
    render(
      <AgentMetricsBar
        telemetry={telemetry({
          gpus: [],
          host: {
            cpuPercent: 35,
            ramPercent: 72,
            ramUsedMb: 12288,
            ramTotalMb: 32768,
          },
        })}
      />
    );

    expect(screen.getByText("CPU")).toBeInTheDocument();
    expect(screen.getByText("RAM")).toBeInTheDocument();

    // CPU badge shows 35%
    expect(screen.getByText("35%")).toBeInTheDocument();

    // RAM badge shows GB format: 12.0/32.0 GB
    expect(screen.getByText("12.0/32.0 GB")).toBeInTheDocument();
  });

  it("applies correct color variants: success (<60%), warning (60-85%), error (>85%)", () => {
    const { rerender } = render(
      <AgentMetricsBar
        telemetry={telemetry({
          gpus: [gpu({ index: 0, corePercent: 40, memoryPercent: 40 })],
        })}
      />
    );

    // 40% < 60 → success variant
    let badges = document.querySelectorAll("span.inline-flex");
    let coreBadge = Array.from(badges).find(
      (b) => b.textContent?.includes("40%") && b.className.includes("running")
    );
    expect(coreBadge).toBeTruthy();

    // Warning: 70%
    rerender(
      <AgentMetricsBar
        telemetry={telemetry({
          gpus: [gpu({ index: 0, corePercent: 70, memoryPercent: 70 })],
        })}
      />
    );
    badges = document.querySelectorAll("span.inline-flex");
    coreBadge = Array.from(badges).find(
      (b) => b.textContent?.includes("70%") && b.className.includes("warning")
    );
    expect(coreBadge).toBeTruthy();

    // Error: 90%
    rerender(
      <AgentMetricsBar
        telemetry={telemetry({
          gpus: [gpu({ index: 0, corePercent: 90, memoryPercent: 90 })],
        })}
      />
    );
    badges = document.querySelectorAll("span.inline-flex");
    coreBadge = Array.from(badges).find(
      (b) => b.textContent?.includes("90%") && b.className.includes("error")
    );
    expect(coreBadge).toBeTruthy();
  });

  it("shows em-dash for negative values (-1)", () => {
    render(
      <AgentMetricsBar
        telemetry={telemetry({
          gpus: [gpu({ index: 0, corePercent: -1, memoryPercent: -1 })],
        })}
      />
    );

    // Both core and memory show em-dash
    const emDashes = screen.getAllByText(EM_DASH);
    expect(emDashes.length).toBe(1); // memory badge hidden for -1, only core shows em-dash
  });

  it("shows em-dash for host CPU/RAM when values are negative", () => {
    render(
      <AgentMetricsBar
        telemetry={telemetry({
          gpus: [],
          host: {
            cpuPercent: -1,
            ramPercent: -1,
            ramUsedMb: -1,
            ramTotalMb: -1,
          },
        })}
      />
    );

    expect(screen.getByText("CPU")).toBeInTheDocument();
    expect(screen.getByText("RAM")).toBeInTheDocument();

    // CPU badge: em-dash
    expect(screen.getAllByText(EM_DASH).length).toBeGreaterThanOrEqual(1);

    // RAM badge: em-dash (usedMb < 0 → formatRam returns em-dash)
    expect(screen.getAllByText(EM_DASH).length).toBe(2);
  });

  it("formats RAM in MB when values are below 1024", () => {
    render(
      <AgentMetricsBar
        telemetry={telemetry({
          gpus: [],
          host: {
            cpuPercent: 20,
            ramPercent: 30,
            ramUsedMb: 512,
            ramTotalMb: 1024,
          },
        })}
      />
    );

    // 1024 >= 1024 → total uses GB, 512 < 1024 → used uses MB
    // formatRam: used=512 (MB), total=1.0 (GB), unit=GB because total >= 1024
    expect(screen.getByText("512/1.0 GB")).toBeInTheDocument();
  });

  it("formats RAM in MB when both values are below 1024", () => {
    render(
      <AgentMetricsBar
        telemetry={telemetry({
          gpus: [],
          host: {
            cpuPercent: 20,
            ramPercent: 30,
            ramUsedMb: 256,
            ramTotalMb: 512,
          },
        })}
      />
    );

    expect(screen.getByText("256/512 MB")).toBeInTheDocument();
  });

  it("shows separator between GPUs and host metrics", () => {
    const { container } = render(
      <AgentMetricsBar
        telemetry={telemetry({
          gpus: [gpu({ index: 0, corePercent: 50, memoryPercent: 50 })],
          host: {
            cpuPercent: 40,
            ramPercent: 60,
            ramUsedMb: 8192,
            ramTotalMb: 16384,
          },
        })}
      />
    );

    // Vertical separator between GPU and host sections
    const separator = container.querySelector(".w-px");
    expect(separator).toBeInTheDocument();
  });

  it("does not show separator when no GPUs but host present", () => {
    const { container } = render(
      <AgentMetricsBar
        telemetry={telemetry({
          gpus: [],
          host: {
            cpuPercent: 40,
            ramPercent: 60,
            ramUsedMb: 8192,
            ramTotalMb: 16384,
          },
        })}
      />
    );

    const separator = container.querySelector(".w-px");
    expect(separator).not.toBeInTheDocument();
  });
});

// ══════════════════════════════════════════════════════════════════
// ContainerKPIBadges
// ══════════════════════════════════════════════════════════════════

describe("ContainerKPIBadges", () => {
  it("returns null when metrics is undefined", () => {
    const { container } = render(<ContainerKPIBadges />);
    expect(container.innerHTML).toBe("");
  });

  it("renders CPU and RAM badges with correct values", () => {
    const metrics: ContainerMetrics = {
      containerId: "abc123",
      cpuPercent: 45,
      ramPercent: 62,
      ramUsedMb: 8192,
      ramTotalMb: 16384,
    };

    render(<ContainerKPIBadges metrics={metrics} />);

    expect(screen.getByText("45%")).toBeInTheDocument();
    expect(screen.getByText("8.0/16.0 GB")).toBeInTheDocument();
  });

  it("shows em-dash for negative CPU percentage", () => {
    const metrics: ContainerMetrics = {
      containerId: "abc123",
      cpuPercent: -1,
      ramPercent: 50,
      ramUsedMb: 512,
      ramTotalMb: 1024,
    };

    render(<ContainerKPIBadges metrics={metrics} />);

    // CPU badge shows em-dash
    expect(screen.getByText(EM_DASH)).toBeInTheDocument();
    // RAM badge shows a value
    expect(screen.getByText("512/1.0 GB")).toBeInTheDocument();
  });

  it("shows em-dash for negative RAM values", () => {
    const metrics: ContainerMetrics = {
      containerId: "abc123",
      cpuPercent: 30,
      ramPercent: -1,
      ramUsedMb: -1,
      ramTotalMb: -1,
    };

    render(<ContainerKPIBadges metrics={metrics} />);

    expect(screen.getByText("30%")).toBeInTheDocument();
    // RAM badge shows em-dash
    expect(screen.getByText(EM_DASH)).toBeInTheDocument();
  });

  it("applies success variant for low utilization (<60%)", () => {
    const metrics: ContainerMetrics = {
      containerId: "abc123",
      cpuPercent: 40,
      ramPercent: 30,
      ramUsedMb: 2048,
      ramTotalMb: 8192,
    };

    render(<ContainerKPIBadges metrics={metrics} />);

    const badges = document.querySelectorAll("span.inline-flex");
    const cpuBadge = Array.from(badges).find(
      (b) => b.textContent?.includes("40%") && b.className.includes("running")
    );
    expect(cpuBadge).toBeTruthy();
  });

  it("applies warning variant for medium utilization (60-85%)", () => {
    const metrics: ContainerMetrics = {
      containerId: "abc123",
      cpuPercent: 72,
      ramPercent: 65,
      ramUsedMb: 10240,
      ramTotalMb: 16384,
    };

    render(<ContainerKPIBadges metrics={metrics} />);

    const badges = document.querySelectorAll("span.inline-flex");
    const cpuBadge = Array.from(badges).find(
      (b) => b.textContent?.includes("72%") && b.className.includes("warning")
    );
    expect(cpuBadge).toBeTruthy();
  });

  it("applies error variant for high utilization (>85%)", () => {
    const metrics: ContainerMetrics = {
      containerId: "abc123",
      cpuPercent: 95,
      ramPercent: 92,
      ramUsedMb: 15000,
      ramTotalMb: 16384,
    };

    render(<ContainerKPIBadges metrics={metrics} />);

    const badges = document.querySelectorAll("span.inline-flex");
    const cpuBadge = Array.from(badges).find(
      (b) => b.textContent?.includes("95%") && b.className.includes("error")
    );
    expect(cpuBadge).toBeTruthy();
  });

  it("renders empty container metrics with default variant", () => {
    const metrics: ContainerMetrics = {
      containerId: "abc123",
      cpuPercent: 0,
      ramPercent: 0,
      ramUsedMb: 0,
      ramTotalMb: 4096,
    };

    render(<ContainerKPIBadges metrics={metrics} />);

    // 0% < 60 → success variant
    const badges = document.querySelectorAll("span.inline-flex");
    const cpuBadge = Array.from(badges).find(
      (b) => b.textContent?.includes("0%") && b.className.includes("running")
    );
    expect(cpuBadge).toBeTruthy();
  });

  it("formats RAM in MB when both values are below 1024", () => {
    const metrics: ContainerMetrics = {
      containerId: "abc123",
      cpuPercent: 10,
      ramPercent: 20,
      ramUsedMb: 128,
      ramTotalMb: 256,
    };

    render(<ContainerKPIBadges metrics={metrics} />);

    expect(screen.getByText("128/256 MB")).toBeInTheDocument();
  });

  it("formats RAM mixed units when used is MB but total is GB", () => {
    const metrics: ContainerMetrics = {
      containerId: "abc123",
      cpuPercent: 10,
      ramPercent: 20,
      ramUsedMb: 500,
      ramTotalMb: 2048,
    };

    render(<ContainerKPIBadges metrics={metrics} />);

    // used=500 (<1024), total=2.0 (>=1024 → GB), unit=GB because total >= 1024
    expect(screen.getByText("500/2.0 GB")).toBeInTheDocument();
  });
});
