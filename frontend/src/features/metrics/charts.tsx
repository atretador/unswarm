// Charts for the Metrics page.
//
// Recharts is imported statically here and this whole module is lazy-loaded
// once from index.tsx. Do NOT lazy-load individual recharts components behind
// nested <Suspense> boundaries: recharts 3.x sets state inside a ref callback
// (RechartsWrapper portal refs), which loops infinitely under React 19 when
// the chart subtree suspends/reappears (recharts#7463).
import {
  AreaChart,
  Area,
  XAxis,
  YAxis,
  Tooltip,
  ResponsiveContainer,
  BarChart,
  Bar,
  Legend,
  Cell,
} from "recharts";
import type { MetricsTimeBucket, ProviderUsageSummary } from "../../lib/api/types";
import {
  bucketCost,
  type BlendedRates,
} from "./cost";
import type { LatencyBand } from "./metrics-api";

/** Which series the time chart renders. */
export type TimeSeriesMetric =
  | "tokens"
  | "requests"
  | "latency"
  | "cached"
  | "cost";

export interface DrillDownWindow {
  from: string;
  to: string;
}

function formatTokens(n: number): string {
  if (n >= 1_000_000) return `${(n / 1_000_000).toFixed(1)}M`;
  if (n >= 1_000) return `${(n / 1_000).toFixed(1)}K`;
  return String(n);
}

const PROVIDER_COLORS = [
  "var(--color-primary)",
  "var(--color-status-running)",
  "var(--color-status-warning)",
  "var(--color-status-error)",
  "var(--color-text-muted)",
];

const tooltipContentStyle = {
  background: "var(--color-bg-elevated)",
  border: "1px solid var(--color-border)",
  borderRadius: "var(--radius-lg)",
  fontSize: "12px",
};

interface TokenUsageChartProps {
  summary: MetricsTimeBucket[];
  /** Which series to render. Defaults to the original stacked token areas. */
  metric?: TimeSeriesMetric;
  /**
   * Token-weighted average cost rates for the current window. Required for the
   * "cost" series; when absent that series renders nothing meaningful.
   */
  blendedRates?: BlendedRates | null;
  /**
   * Called with the clicked bucket's time window (drill-down). The chart is
   * rendered as non-interactive when omitted.
   */
  onPointClick?: (window: DrillDownWindow) => void;
}

export function TokenUsageChart({
  summary,
  metric = "tokens",
  blendedRates = null,
  onPointClick,
}: TokenUsageChartProps) {
  const data = summary.map((b) => ({
    // bucket bounds ride along on every datum so point clicks can open a
    // drill-down window regardless of which metric is displayed.
    bucketStart: b.bucketStart,
    bucketEnd: b.bucketEnd,
    time: new Date(b.bucketStart).toLocaleDateString(undefined, {
      month: "short",
      day: "numeric",
      ...(metric === "requests" || metric === "latency" || metric === "cached" || metric === "cost"
        ? { hour: "2-digit" as const }
        : {}),
    }),
    prompt: b.promptTokens,
    completion: b.completionTokens,
    requests: b.requestCount,
    latency: Math.round(b.avgLatencyMs),
    cached: b.cachedTokens,
    cost: blendedRates ? bucketCost(b, blendedRates) : 0,
  }));

  const handleClick = (payload: unknown) => {
    if (!onPointClick) return;
    const datum = (
      Array.isArray(payload)
        ? (payload[0] as { payload?: Record<string, unknown> } | undefined)?.payload
        : (payload as { payload?: Record<string, unknown> } | null)?.payload ??
          (payload as Record<string, unknown> | null)
    ) as
      | { bucketStart?: string; bucketEnd?: string }
      | null
      | undefined;
    if (datum?.bucketStart && datum.bucketEnd) {
      onPointClick({ from: datum.bucketStart, to: datum.bucketEnd });
    }
  };

  return (
    <ResponsiveContainer width="100%" height={260}>
      <AreaChart data={data} onClick={handleClick}>
        <defs>
          <linearGradient id="promptGrad" x1="0" y1="0" x2="0" y2="1">
            <stop offset="0%" stopColor="var(--color-primary)" stopOpacity={0.3} />
            <stop offset="100%" stopColor="var(--color-primary)" stopOpacity={0} />
          </linearGradient>
          <linearGradient id="completionGrad" x1="0" y1="0" x2="0" y2="1">
            <stop
              offset="0%"
              stopColor="var(--color-status-running)"
              stopOpacity={0.3}
            />
            <stop
              offset="100%"
              stopColor="var(--color-status-running)"
              stopOpacity={0}
            />
          </linearGradient>
          <linearGradient id="singleGrad" x1="0" y1="0" x2="0" y2="1">
            <stop offset="0%" stopColor="var(--color-primary)" stopOpacity={0.35} />
            <stop offset="100%" stopColor="var(--color-primary)" stopOpacity={0.02} />
          </linearGradient>
          <linearGradient id="cachedGrad" x1="0" y1="0" x2="0" y2="1">
            <stop
              offset="0%"
              stopColor="var(--color-status-warning)"
              stopOpacity={0.35}
            />
            <stop
              offset="100%"
              stopColor="var(--color-status-warning)"
              stopOpacity={0.02}
            />
          </linearGradient>
        </defs>
        <XAxis
          dataKey="time"
          tick={{ fontSize: 10, fill: "var(--color-text-muted)" }}
          tickLine={false}
          axisLine={false}
        />
        <YAxis
          key={metric}
          tick={{ fontSize: 10, fill: "var(--color-text-muted)" }}
          tickLine={false}
          axisLine={false}
          width={48}
          tickFormatter={(v: number) =>
            metric === "latency" ? `${Math.round(v)}ms` : formatTokens(v)
          }
        />
        <Tooltip
          contentStyle={tooltipContentStyle}
          formatter={(value, name) => {
            if (name === "prompt") return [formatTokens(Number(value)), "Prompt tokens"];
            if (name === "completion")
              return [formatTokens(Number(value)), "Completion tokens"];
            if (name === "requests") return [Number(value).toLocaleString(), "Requests"];
            if (name === "latency") return [`${Math.round(Number(value))} ms`, "Avg latency"];
            if (name === "cached") return [formatTokens(Number(value)), "Cached tokens"];
            if (name === "cost") return [`$${Number(value).toFixed(4)}`, "Est. cost"];
            return [String(value), String(name)];
          }}
        />
        {metric === "tokens" && (
          <>
            <Legend iconType="circle" wrapperStyle={{ fontSize: "11px" }} />
            <Area
              type="monotone"
              dataKey="prompt"
              name="prompt"
              stroke="var(--color-primary)"
              fill="url(#promptGrad)"
              strokeWidth={2}
              onClick={handleClick}
              style={{ cursor: onPointClick ? "pointer" : undefined }}
            />
            <Area
              type="monotone"
              dataKey="completion"
              name="completion"
              stroke="var(--color-status-running)"
              fill="url(#completionGrad)"
              strokeWidth={2}
              onClick={handleClick}
              style={{ cursor: onPointClick ? "pointer" : undefined }}
            />
          </>
        )}
        {metric === "requests" && (
          <Area
            type="monotone"
            dataKey="requests"
            name="requests"
            stroke="var(--color-primary)"
            fill="url(#singleGrad)"
            strokeWidth={2}
            onClick={handleClick}
            style={{ cursor: onPointClick ? "pointer" : undefined }}
          />
        )}
        {metric === "latency" && (
          <Area
            type="monotone"
            dataKey="latency"
            name="latency"
            stroke="var(--color-status-running)"
            fill="url(#completionGrad)"
            strokeWidth={2}
            onClick={handleClick}
            style={{ cursor: onPointClick ? "pointer" : undefined }}
          />
        )}
        {metric === "cached" && (
          <Area
            type="monotone"
            dataKey="cached"
            name="cached"
            stroke="var(--color-status-warning)"
            fill="url(#cachedGrad)"
            strokeWidth={2}
            onClick={handleClick}
            style={{ cursor: onPointClick ? "pointer" : undefined }}
          />
        )}
        {metric === "cost" && (
          <Area
            type="monotone"
            dataKey="cost"
            name="cost"
            stroke="var(--color-status-error)"
            fill="url(#singleGrad)"
            strokeWidth={2}
            onClick={handleClick}
            style={{ cursor: onPointClick ? "pointer" : undefined }}
          />
        )}
      </AreaChart>
    </ResponsiveContainer>
  );
}

interface ProviderBreakdownChartProps {
  providers: ProviderUsageSummary[];
  filterProvider: string;
  onProviderSelect: (provider: string) => void;
}

export function ProviderBreakdownChart({
  providers,
  filterProvider,
  onProviderSelect,
}: ProviderBreakdownChartProps) {
  const handleBarClick = (data: unknown) => {
    const provider = (data as { provider?: string } | null)?.provider;
    if (provider) onProviderSelect(provider);
  };

  return (
    <ResponsiveContainer width="100%" height={Math.max(120, providers.length * 36)}>
      <BarChart data={providers} layout="vertical" margin={{ left: 0, right: 16 }}>
        <XAxis
          type="number"
          tick={{ fontSize: 10, fill: "var(--color-text-muted)" }}
          tickLine={false}
          axisLine={false}
          tickFormatter={(v: number) => formatTokens(v)}
        />
        <YAxis
          type="category"
          dataKey="provider"
          tick={{ fontSize: 11, fill: "var(--color-text-heading)" }}
          tickLine={false}
          axisLine={false}
          width={100}
        />
        <Tooltip
          contentStyle={tooltipContentStyle}
          formatter={(value, name) => {
            const labels: Record<string, string> = {
              requestCount: "Requests",
              promptTokens: "Prompt tokens",
              completionTokens: "Completion tokens",
            };
            return [
              name === "requestCount"
                ? Number(value).toLocaleString()
                : formatTokens(Number(value)),
              labels[String(name)] || String(name),
            ];
          }}
        />
        <Legend iconType="circle" wrapperStyle={{ fontSize: "11px" }} />
        {(
          [
            ["requestCount", 0],
            ["promptTokens", 1],
            ["completionTokens", 2],
          ] as const
        ).map(([dataKey, colorOffset]) => (
          <Bar
            key={dataKey}
            dataKey={dataKey}
            name={dataKey}
            // Series-level fill feeds the <Legend> icon color (per-provider
            // <Cell> fills below still win for the actual bar rendering).
            fill={PROVIDER_COLORS[colorOffset]}
            radius={[0, 4, 4, 0]}
            barSize={16}
            onClick={handleBarClick}
            style={{ cursor: "pointer" }}
          >
            {providers.map((entry, index) => (
              <Cell
                key={`${dataKey}-${entry.provider}`}
                fill={
                  filterProvider && filterProvider !== entry.provider
                    ? "var(--color-text-muted)"
                    : PROVIDER_COLORS[
                        (index + colorOffset) % PROVIDER_COLORS.length
                      ]
                }
                fillOpacity={
                  filterProvider && filterProvider !== entry.provider ? 0.3 : 1
                }
              />
            ))}
          </Bar>
        ))}
      </BarChart>
    </ResponsiveContainer>
  );
}

// ─── Latency Distribution ────────────────────────────────────────

/**
 * Vertical bar chart of the latency-band histogram (<500ms … >10s).
 * Band color ramps from primary (fast) through warning to error (slow).
 */
export function LatencyBandsChart({ bands }: { bands: LatencyBand[] }) {
  const last = Math.max(1, bands.length - 1);

  const bandColor = (index: number): string => {
    const t = index / last;
    if (t <= 0.5) {
      // primary → warning across the first half
      return `color-mix(in srgb, var(--color-primary) ${Math.round((1 - t * 2) * 100)}%, var(--color-status-warning))`;
    }
    // warning → error across the second half
    return `color-mix(in srgb, var(--color-status-warning) ${Math.round((1 - (t - 0.5) * 2) * 100)}%, var(--color-status-error))`;
  };

  const total = bands.reduce((sum, b) => sum + b.count, 0);

  return (
    <ResponsiveContainer width="100%" height={200}>
      <BarChart data={bands} margin={{ top: 8, right: 8, left: -16, bottom: 0 }}>
        <XAxis
          dataKey="label"
          tick={{ fontSize: 10, fill: "var(--color-text-muted)" }}
          tickLine={false}
          axisLine={false}
          interval={0}
        />
        <YAxis
          tick={{ fontSize: 10, fill: "var(--color-text-muted)" }}
          tickLine={false}
          axisLine={false}
          allowDecimals={false}
        />
        <Tooltip
          cursor={{ fill: "var(--color-bg-muted)", opacity: 0.5 }}
          contentStyle={tooltipContentStyle}
          formatter={(value) => [
            `${Number(value).toLocaleString()} request${Number(value) === 1 ? "" : "s"}`,
            total > 0 ? `${((Number(value) / total) * 100).toFixed(1)}% of total` : "",
          ]}
        />
        <Bar dataKey="count" name="requests" radius={[4, 4, 0, 0]} maxBarSize={56}>
          {bands.map((band, index) => (
            <Cell key={band.label} fill={bandColor(index)} fillOpacity={0.9} />
          ))}
        </Bar>
      </BarChart>
    </ResponsiveContainer>
  );
}
