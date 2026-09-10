import { Cpu, MemoryStick, Gauge, Clock } from "lucide-react";
import { Badge } from "../../components/ui";
import type { AgentTelemetry, GPUMetrics } from "../../lib/api/types";

interface AgentMetricsBarProps {
  telemetry: AgentTelemetry;
}

function utilizationColor(pct: number): "success" | "warning" | "error" | "default" {
  if (pct < 0) return "default";
  if (pct < 60) return "success";
  if (pct < 85) return "warning";
  return "error";
}

function formatPct(pct: number): string {
  if (pct < 0) return "\u2014";
  return `${Math.round(pct)}%`;
}

function staleLabel(collectedAt: string): string | null {
  const ageMs = Date.now() - new Date(collectedAt).getTime();
  if (ageMs < 30_000) return null;
  const secs = Math.floor(ageMs / 1000);
  if (secs < 60) return `${secs}s ago`;
  const mins = Math.floor(secs / 60);
  return `${mins}m ago`;
}

function formatRam(usedMb: number, totalMb: number): string {
  if (usedMb < 0 || totalMb < 0) return "\u2014";
  const used = usedMb >= 1024 ? `${(usedMb / 1024).toFixed(1)}` : `${usedMb}`;
  const total = totalMb >= 1024 ? `${(totalMb / 1024).toFixed(1)}` : `${totalMb}`;
  const unit = usedMb >= 1024 || totalMb >= 1024 ? "GB" : "MB";
  return `${used}/${total} ${unit}`;
}

function GpuLine({ gpu }: { gpu: GPUMetrics }) {
  const coreLabel = gpu.corePercent < 0 ? "\u2014" : `${Math.round(gpu.corePercent)}%`;
  const memLabel = gpu.memoryPercent < 0 ? "\u2014" : `${Math.round(gpu.memoryPercent)}%`;

  return (
    <span className="inline-flex items-center gap-1.5 font-mono">
      <span className="text-[var(--color-text-muted)]">G{gpu.index}</span>
      <Badge variant={utilizationColor(gpu.corePercent)} size="sm">
        <Gauge className="size-2.5" />
        {coreLabel}
      </Badge>
      {gpu.memoryPercent >= 0 && (
        <Badge variant={utilizationColor(gpu.memoryPercent)} size="sm">
          <MemoryStick className="size-2.5" />
          {memLabel}
        </Badge>
      )}
    </span>
  );
}

export function AgentMetricsBar({ telemetry }: AgentMetricsBarProps) {
  const gpus = telemetry.gpus ?? [];
  const host = telemetry.host;
  const stale = staleLabel(telemetry.collectedAt);

  // Nothing to show if no GPUs and no host metrics
  if (gpus.length === 0 && !host) return null;

  return (
    <div className={`mb-3 flex flex-wrap items-center gap-2 rounded-[var(--radius-lg)] bg-[var(--color-bg-muted)] px-2.5 py-1.5 text-[10px]${stale ? " opacity-60" : ""}`}>
      {/* GPU section */}
      {gpus.length === 1 && (
        <GpuLine gpu={gpus[0]} />
      )}

      {gpus.length >= 2 && (
        <div className="grid grid-cols-2 gap-x-4 gap-y-0.5">
          {gpus.map((gpu) => (
            <GpuLine key={gpu.index} gpu={gpu} />
          ))}
        </div>
      )}

      {/* Host metrics — always at the end */}
      {host && (
        <>
          {gpus.length > 0 && (
            <span className="mx-0.5 h-4 w-px shrink-0 bg-[var(--color-border-subtle)]" />
          )}
          <span className="inline-flex items-center gap-1.5 font-mono">
            <span className="flex items-center gap-1 text-[var(--color-text-muted)]">
              <Cpu className="size-2.5" />
              CPU
            </span>
            <Badge variant={utilizationColor(host.cpuPercent)} size="sm">
              {formatPct(host.cpuPercent)}
            </Badge>
          </span>
          <span className="inline-flex items-center gap-1.5 font-mono">
            <span className="flex items-center gap-1 text-[var(--color-text-muted)]">
              <MemoryStick className="size-2.5" />
              RAM
            </span>
            <Badge variant={utilizationColor(host.ramPercent)} size="sm">
              {formatRam(host.ramUsedMb, host.ramTotalMb)}
            </Badge>
          </span>
        </>
      )}

      {stale && (
        <span className="ml-auto flex items-center gap-1 text-[var(--color-text-muted)]">
          <Clock className="size-2.5" />
          {stale}
        </span>
      )}
    </div>
  );
}
