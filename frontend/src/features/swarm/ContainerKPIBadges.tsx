import { Cpu, MemoryStick } from "lucide-react";
import { Badge } from "../../components/ui";
import type { ContainerMetrics } from "../../lib/api/types";

interface ContainerKPIBadgesProps {
  metrics?: ContainerMetrics;
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

function formatRam(usedMb: number, totalMb: number): string {
  if (usedMb < 0 || totalMb < 0) return "\u2014";
  const used = usedMb >= 1024 ? `${(usedMb / 1024).toFixed(1)}` : `${usedMb}`;
  const total = totalMb >= 1024 ? `${(totalMb / 1024).toFixed(1)}` : `${totalMb}`;
  const unit = usedMb >= 1024 || totalMb >= 1024 ? "GB" : "MB";
  return `${used}/${total} ${unit}`;
}

export function ContainerKPIBadges({ metrics }: ContainerKPIBadgesProps) {
  if (!metrics) return null;

  return (
    <div className="flex items-center gap-1">
      <Badge variant={utilizationColor(metrics.cpuPercent)} size="sm">
        <Cpu className="size-2.5" />
        {formatPct(metrics.cpuPercent)}
      </Badge>
      <Badge variant={utilizationColor(metrics.ramPercent)} size="sm">
        <MemoryStick className="size-2.5" />
        {formatRam(metrics.ramUsedMb, metrics.ramTotalMb)}
      </Badge>
    </div>
  );
}
