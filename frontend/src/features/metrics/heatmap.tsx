// Requests-by-hour heatmap: day-of-week × hour-of-day grid built from
// /api/metrics/summary with hourly granularity. The window is capped at the
// last 7 days (a 24-column grid over longer ranges would just repeat cells).

import { useMemo } from "react";
import { useQuery } from "@tanstack/react-query";
import { client } from "../../lib/query-client";

const DAY_LABELS = ["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"];
/** JS getDay(): 0=Sun … 6=Sat, rendered Mon-first. */
const DAY_ORDER = [1, 2, 3, 4, 5, 6, 0];

export interface HourlyHeatmapProps {
  /** Whether the selected range is within 24h (uses a tighter window). */
  rangeIs24h: boolean;
  provider?: string;
  model?: string;
  autoRefreshMs: number;
}

function startOfHour(date: Date): Date {
  const d = new Date(date);
  d.setMinutes(0, 0, 0);
  return d;
}

export function HourlyHeatmap({
  rangeIs24h,
  provider,
  model,
  autoRefreshMs,
}: HourlyHeatmapProps) {
  const hours = rangeIs24h ? 24 : 24 * 7;
  const from = useMemo(
    () => new Date(Date.now() - hours * 60 * 60 * 1000),
    [hours],
  );

  const { data: buckets, isLoading } = useQuery({
    queryKey: ["metrics", "summary", "heatmap", from.toISOString(), provider, model],
    queryFn: () =>
      client.getMetricsSummary({
        from: startOfHour(from).toISOString(),
        to: new Date().toISOString(),
        granularity: "hour",
        ...(provider ? { provider } : {}),
        ...(model ? { model } : {}),
      }),
    refetchInterval: autoRefreshMs || false,
  });

  const { grid, maxCount, totalRequests } = useMemo(() => {
    // grid[dow][hour] = summed request count
    const g: number[][] = DAY_ORDER.map(() => Array<number>(24).fill(0));
    let max = 0;
    let total = 0;
    for (const b of buckets ?? []) {
      const d = new Date(b.bucketStart);
      const cell = g[DAY_ORDER.indexOf(d.getDay())];
      if (!cell) continue;
      cell[d.getHours()] += b.requestCount;
      total += b.requestCount;
    }
    for (const row of g) {
      for (const v of row) if (v > max) max = v;
    }
    return { grid: g, maxCount: max, totalRequests: total };
  }, [buckets]);

  return (
    <div>
      <div className="overflow-x-auto">
        <div
          className="grid gap-[3px] min-w-[560px]"
          style={{ gridTemplateColumns: "34px repeat(24, minmax(0, 1fr))" }}
        >
          {/* Header row: hour labels every 3h */}
          <div />
          {Array.from({ length: 24 }, (_, h) => (
            <div
              key={`h-${h}`}
              className="text-[9px] text-[var(--color-text-muted)] text-center select-none"
            >
              {h % 3 === 0 ? h : ""}
            </div>
          ))}

          {grid.map((row, i) => (
            <HeatmapRow key={DAY_LABELS[i]} label={DAY_LABELS[i]} row={row} max={maxCount} />
          ))}
        </div>
      </div>

      <div className="flex items-center justify-between mt-3">
        <p className="text-xs text-[var(--color-text-muted)]">
          {rangeIs24h ? "Last 24 hours" : "Last 7 days"} ·{" "}
          {totalRequests.toLocaleString()} request{totalRequests === 1 ? "" : "s"}
        </p>
        <div className="flex items-center gap-1.5 text-xs text-[var(--color-text-muted)]">
          <span>less</span>
          {[0.08, 0.25, 0.55, 1].map((level) => (
            <span
              key={level}
              className="size-3 rounded-[3px]"
              style={{
                backgroundColor: `color-mix(in srgb, var(--color-primary) ${Math.round(level * 100)}%, transparent)`,
              }}
            />
          ))}
          <span>more</span>
        </div>
      </div>

      {isLoading && (
        <p className="text-xs text-[var(--color-text-muted)] mt-2">Loading…</p>
      )}
      {!isLoading && maxCount === 0 && (
        <p className="text-xs text-[var(--color-text-muted)] mt-2">
          No requests recorded in this window.
        </p>
      )}
    </div>
  );
}

function HeatmapRow({
  label,
  row,
  max,
}: {
  label: string;
  row: number[];
  max: number;
}) {
  return (
    <>
      <div className="text-[10px] text-[var(--color-text-muted)] leading-none self-center select-none">
        {label}
      </div>
      {row.map((count, hour) => {
        const intensity = max > 0 ? count / max : 0;
        return (
          <div
            key={`${label}-${hour}`}
            title={
              count > 0
                ? `${label} ${String(hour).padStart(2, "0")}:00 — ${count.toLocaleString()} request${count === 1 ? "" : "s"}`
                : undefined
            }
            className="aspect-square min-h-[14px] rounded-[3px] transition-transform duration-100 hover:scale-110"
            style={{
              backgroundColor:
                count > 0
                  ? `color-mix(in srgb, var(--color-primary) ${Math.max(8, Math.round(intensity * 100))}%, transparent)`
                  : "var(--color-bg-muted)",
            }}
          />
        );
      })}
    </>
  );
}
