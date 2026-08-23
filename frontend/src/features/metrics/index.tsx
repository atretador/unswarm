import { Suspense, lazy, useState, useMemo, useCallback, useEffect } from "react";
import { useQuery } from "@tanstack/react-query";
import { Link } from "react-router-dom";
import { motion } from "motion/react";
import {
  Activity,
  Zap,
  ArrowUpRight,
  Database,
  AlertTriangle,
  RefreshCw,
  ChevronUp,
  ChevronDown,
  X,
  Calculator,
  Plus,
  Trash2,
} from "lucide-react";
import { client } from "../../lib/query-client";
import {
  Card,
  Badge,
  Skeleton,
  EmptyState,
  Button,
  Spinner,
  Select,
  Dialog,
} from "../../components/ui";
import { formatModelName } from "../../lib/format-model-name";

// Lazy-load recharts to keep the main bundle lean
const LazyAreaChart = lazy(() =>
  import("recharts").then((m) => ({ default: m.AreaChart })),
);
const LazyArea = lazy(() =>
  import("recharts").then((m) => ({ default: m.Area })),
);
const LazyXAxis = lazy(() =>
  import("recharts").then((m) => ({ default: m.XAxis })),
);
const LazyYAxis = lazy(() =>
  import("recharts").then((m) => ({ default: m.YAxis })),
);
const LazyTooltip = lazy(() =>
  import("recharts").then((m) => ({ default: m.Tooltip })),
);
const LazyResponsiveContainer = lazy(() =>
  import("recharts").then((m) => ({ default: m.ResponsiveContainer })),
);
const LazyBarChart = lazy(() =>
  import("recharts").then((m) => ({ default: m.BarChart })),
);
const LazyBar = lazy(() =>
  import("recharts").then((m) => ({ default: m.Bar })),
);
const LazyLegend = lazy(() =>
  import("recharts").then((m) => ({ default: m.Legend })),
);
const LazyCell = lazy(() =>
  import("recharts").then((m) => ({ default: m.Cell })),
);

function ChartSkeleton() {
  return (
    <div className="h-48 flex items-center justify-center">
      <Spinner size="sm" />
    </div>
  );
}

function formatTokens(n: number): string {
  if (n >= 1_000_000) return `${(n / 1_000_000).toFixed(1)}M`;
  if (n >= 1_000) return `${(n / 1_000).toFixed(1)}K`;
  return String(n);
}

function formatMs(ms: number): string {
  return `${Math.round(ms)} ms`;
}

function formatCurrency(n: number): string {
  if (n === 0) return "$0.00";
  if (n < 0.01) return `$${n.toFixed(6)}`;
  if (n < 1) return `$${n.toFixed(4)}`;
  return `$${n.toFixed(2)}`;
}

// ─── Time Range ─────────────────────────────────────────────────

type TimeRange = "24h" | "7d" | "30d" | "all";

const TIME_RANGE_OPTIONS: { value: TimeRange; label: string }[] = [
  { value: "24h", label: "Last 24h" },
  { value: "7d", label: "Last 7 days" },
  { value: "30d", label: "Last 30 days" },
  { value: "all", label: "All time" },
];

function getTimeRangeParams(range: TimeRange): {
  from?: string;
  to?: string;
} {
  if (range === "all") return {};
  const now = new Date();
  const to = now.toISOString();
  let from: Date;
  switch (range) {
    case "24h":
      from = new Date(now.getTime() - 24 * 60 * 60 * 1000);
      break;
    case "7d":
      from = new Date(now.getTime() - 7 * 24 * 60 * 60 * 1000);
      break;
    case "30d":
      from = new Date(now.getTime() - 30 * 24 * 60 * 60 * 1000);
      break;
  }
  return { from: from!.toISOString(), to };
}

// ─── Sort ────────────────────────────────────────────────────────

type SortField =
  | "model"
  | "requestCount"
  | "promptTokens"
  | "completionTokens"
  | "cacheHitRate"
  | "avgLatencyMs";
type SortDirection = "asc" | "desc";

// ─── Cost Rates (localStorage) ──────────────────────────────────

const COST_RATES_KEY = "unswarm-cost-rates";

interface ProviderCostRate {
  promptPer1M: number;
  completionPer1M: number;
}

type CostRatesMap = Record<string, ProviderCostRate>;

function loadCostRates(): CostRatesMap {
  try {
    const raw = localStorage.getItem(COST_RATES_KEY);
    if (raw) return JSON.parse(raw);
  } catch {
    // corrupt data — start fresh
  }
  return {};
}

function saveCostRates(rates: CostRatesMap): void {
  try {
    localStorage.setItem(COST_RATES_KEY, JSON.stringify(rates));
  } catch {
    // storage full or unavailable — silently ignore
  }
}

// ─── Animations ──────────────────────────────────────────────────

const fadeUp = {
  initial: { opacity: 0, y: 8 },
  animate: { opacity: 1, y: 0 },
};

// ─── Main Component ──────────────────────────────────────────────

export default function Metrics() {
  const [timeRange, setTimeRange] = useState<TimeRange>("7d");
  const [sortField, setSortField] = useState<SortField>("requestCount");
  const [sortDirection, setSortDirection] = useState<SortDirection>("desc");
  const [filterProvider, setFilterProvider] = useState<string>("");
  const [filterModel, setFilterModel] = useState<string>("");
  const [costDialogOpen, setCostDialogOpen] = useState(false);

  const rangeParams = useMemo(() => getTimeRangeParams(timeRange), [timeRange]);

  // Combined filter params passed to every API call
  const filterParams = useMemo(
    () => ({
      ...rangeParams,
      ...(filterProvider ? { provider: filterProvider } : {}),
      ...(filterModel ? { model: filterModel } : {}),
    }),
    [rangeParams, filterProvider, filterModel],
  );

  // ── Queries ──────────────────────────────────────────────────

  const {
    data: totals,
    isLoading: totalsLoading,
    error: totalsError,
    refetch: refetchTotals,
  } = useQuery({
    queryKey: ["metrics", "totals", filterParams],
    queryFn: () => client.getMetricsTotals(filterParams),
  });

  const {
    data: summary,
    isLoading: summaryLoading,
    error: summaryError,
    refetch: refetchSummary,
  } = useQuery({
    queryKey: ["metrics", "summary", filterParams],
    queryFn: () =>
      client.getMetricsSummary({
        ...filterParams,
        granularity: timeRange === "24h" ? "hour" : "day",
      }),
  });

  const {
    data: models,
    isLoading: modelsLoading,
    error: modelsError,
    refetch: refetchModels,
  } = useQuery({
    queryKey: ["metrics", "models", filterParams],
    queryFn: () => client.getMetricsModels(filterParams),
  });

  const {
    data: providers,
    isLoading: providersLoading,
    error: providersError,
    refetch: refetchProviders,
  } = useQuery({
    queryKey: ["metrics", "providers", rangeParams],
    queryFn: () => client.getMetricsProviders(rangeParams),
  });

  // Fetch the full unfiltered models list once to populate filter dropdowns
  const { data: allModels } = useQuery({
    queryKey: ["metrics", "models", "all"],
    queryFn: () => client.getMetricsModels(),
  });

  const { data: settings } = useQuery({
    queryKey: ["settings"],
    queryFn: () => client.getSettings(),
  });

  // ── Derived: filter dropdown options ────────────────────────

  const providerOptions = useMemo(() => {
    if (!allModels) return [];
    const distinct = [...new Set(allModels.map((m) => m.provider))].sort();
    return distinct.map((p) => ({ value: p, label: p }));
  }, [allModels]);

  const modelOptions = useMemo(() => {
    if (!allModels) return [];
    // Apply provider filter to the model list if one is set
    const source = filterProvider
      ? allModels.filter((m) => m.provider === filterProvider)
      : allModels;
    const distinct = [...new Set(source.map((m) => m.model))].sort();
    return distinct.map((m) => ({
      value: m,
      label: formatModelName(
        m,
        filterProvider || "",
        settings?.hideOriginPrefix ?? false,
        settings?.agentDisplayNames ?? {},
      ),
    }));
  }, [allModels, filterProvider, settings]);

  const hasActiveFilters = filterProvider !== "" || filterModel !== "";

  const clearFilters = useCallback(() => {
    setFilterProvider("");
    setFilterModel("");
  }, []);

  // ── Hooks (must all be called before any early returns) ────

  // Sort models
  const sortedModels = useMemo(() => {
    if (!models) return [];
    const sorted = [...models].sort((a, b) => {
      const dir = sortDirection === "asc" ? 1 : -1;
      switch (sortField) {
        case "model":
          return dir * a.model.localeCompare(b.model);
        case "requestCount":
          return dir * (a.requestCount - b.requestCount);
        case "promptTokens":
          return dir * (a.promptTokens - b.promptTokens);
        case "completionTokens":
          return dir * (a.completionTokens - b.completionTokens);
        case "cacheHitRate": {
          const rateA = a.promptTokens > 0 ? a.cachedTokens / a.promptTokens : 0;
          const rateB = b.promptTokens > 0 ? b.cachedTokens / b.promptTokens : 0;
          return dir * (rateA - rateB);
        }
        case "avgLatencyMs":
          return dir * (a.avgLatencyMs - b.avgLatencyMs);
        default:
          return 0;
      }
    });
    return sorted;
  }, [models, sortField, sortDirection]);

  function handleSort(field: SortField) {
    if (sortField === field) {
      setSortDirection((d) => (d === "asc" ? "desc" : "asc"));
    } else {
      setSortField(field);
      setSortDirection("desc");
    }
  }

  function SortIcon({ field }: { field: SortField }) {
    if (sortField !== field) return null;
    return sortDirection === "asc" ? (
      <ChevronUp className="size-3 ml-0.5 inline" />
    ) : (
      <ChevronDown className="size-3 ml-0.5 inline" />
    );
  }

  // Provider bar chart colors
  const PROVIDER_COLORS = [
    "var(--color-primary)",
    "var(--color-status-running)",
    "var(--color-status-warning)",
    "var(--color-status-error)",
    "var(--color-text-muted)",
  ];

  // ── Loading / Error States ──────────────────────────────────

  const isLoading = totalsLoading || summaryLoading || modelsLoading || providersLoading;
  const error = totalsError || summaryError || modelsError || providersError;

  if (isLoading) {
    return (
      <div className="p-6 space-y-6 max-w-6xl">
        <div className="flex items-center justify-between">
          <Skeleton className="h-6 w-32" />
          <Skeleton className="h-8 w-64" />
        </div>
        <Skeleton className="h-9 w-64" />
        <div className="grid grid-cols-2 md:grid-cols-4 gap-4">
          {Array.from({ length: 4 }, (_, i) => (
            <Card key={i} padding="md">
              <Skeleton className="h-3 w-24 mb-2" />
              <Skeleton className="h-7 w-16" />
            </Card>
          ))}
        </div>
        <Card padding="lg">
          <Skeleton className="h-4 w-40 mb-4" />
          <Skeleton className="h-48 w-full" />
        </Card>
        <Card padding="lg">
          <Skeleton className="h-4 w-40 mb-4" />
          <Skeleton className="h-32 w-full" />
        </Card>
      </div>
    );
  }

  if (error) {
    return (
      <div className="p-6 max-w-6xl">
        <EmptyState
          icon={<AlertTriangle className="size-12" strokeWidth={1.5} />}
          title="Failed to load metrics"
          description={error.message}
          action={
            <Button
              variant="secondary"
              size="sm"
              onClick={() => {
                refetchTotals();
                refetchSummary();
                refetchModels();
                refetchProviders();
              }}
            >
              <RefreshCw className="size-3.5" />
              Retry
            </Button>
          }
        />
      </div>
    );
  }

  // ── Derived Data ─────────────────────────────────────────────

  const summaryCards = totals
    ? [
        {
          label: "Total requests",
          value: totals.totalRequests.toLocaleString(),
          icon: Activity,
          color: "text-[var(--color-primary)]",
        },
        {
          label: "Prompt tokens",
          value: formatTokens(totals.totalPromptTokens),
          icon: Zap,
          color: "text-[var(--color-status-running)]",
        },
        {
          label: "Completion tokens",
          value: formatTokens(totals.totalCompletionTokens),
          icon: ArrowUpRight,
          color: "text-[var(--color-status-running)]",
        },
        {
          label: "Cache hit rate",
          value:
            totals.totalPromptTokens > 0
              ? `${((totals.totalCachedTokens / totals.totalPromptTokens) * 100).toFixed(1)}%`
              : "\u2014",
          icon: Database,
          color: "text-[var(--color-status-warning)]",
        },
      ]
    : [];

  // ── Render ────────────────────────────────────────────────────

  return (
    <div className="p-6 space-y-6 max-w-6xl">
      {/* Header + Time Range */}
      <motion.div
        variants={fadeUp}
        initial="initial"
        animate="animate"
        className="flex flex-col sm:flex-row sm:items-center sm:justify-between gap-4"
      >
        <h1 className="text-xl font-semibold font-heading text-[var(--color-text-heading)]">
          Metrics
        </h1>
        <div className="flex gap-1.5 bg-[var(--color-bg-muted)] rounded-[var(--radius-lg)] p-1">
          {TIME_RANGE_OPTIONS.map((opt) => (
            <button
              key={opt.value}
              type="button"
              onClick={() => setTimeRange(opt.value)}
              className={`
                px-3 py-1.5 text-xs font-medium rounded-[var(--radius-md)]
                transition-all duration-[var(--duration-fast)]
                cursor-pointer
                ${
                  timeRange === opt.value
                    ? "bg-[var(--color-bg-surface)] text-[var(--color-text-heading)] shadow-sm"
                    : "text-[var(--color-text-muted)] hover:text-[var(--color-text)]"
                }
              `}
            >
              {opt.label}
            </button>
          ))}
        </div>
      </motion.div>

      {/* Filter Bar */}
      <motion.div
        variants={fadeUp}
        initial="initial"
        animate="animate"
        transition={{ delay: 0.05 }}
      >
        <div className="flex flex-col sm:flex-row sm:items-end gap-3 p-3 rounded-[var(--radius-lg)] border border-[var(--color-border-subtle)] bg-[var(--color-bg-muted)]/50">
          <Select
            label="Provider"
            options={[{ value: "", label: "All providers" }, ...providerOptions]}
            value={filterProvider}
            onChange={(e) => {
              setFilterProvider(e.target.value);
              // Clear model filter when provider changes, since model list will shift
              setFilterModel("");
            }}
            className="min-w-[160px]"
          />
          <Select
            label="Model"
            options={[{ value: "", label: "All models" }, ...modelOptions]}
            value={filterModel}
            onChange={(e) => setFilterModel(e.target.value)}
            className="min-w-[200px]"
          />
          {hasActiveFilters && (
            <Button
              variant="ghost"
              size="sm"
              onClick={clearFilters}
              className="gap-1 text-[var(--color-text-muted)] shrink-0"
            >
              <X className="size-3" />
              Clear filters
            </Button>
          )}
          <div className="flex-1" />
          <Button
            variant="secondary"
            size="sm"
            onClick={() => setCostDialogOpen(true)}
            className="gap-1.5 shrink-0"
          >
            <Calculator className="size-3.5" />
            Cost Calculator
          </Button>
        </div>
      </motion.div>

      {/* Summary Cards */}
      <div className="grid grid-cols-2 md:grid-cols-4 gap-4">
        {summaryCards.map((stat, i) => (
          <motion.div
            key={stat.label}
            variants={fadeUp}
            initial="initial"
            animate="animate"
            transition={{ delay: 0.08 + i * 0.05 }}
          >
            <Card padding="md">
              <div className="flex items-center justify-between mb-1">
                <p className="text-xs text-[var(--color-text-muted)]">
                  {stat.label}
                </p>
                <stat.icon className={`size-3.5 ${stat.color}`} />
              </div>
              <p className="text-xl font-semibold font-heading text-[var(--color-text-heading)]">
                {stat.value}
              </p>
            </Card>
          </motion.div>
        ))}
      </div>

      {/* Token Usage Over Time Chart */}
      <motion.div
        variants={fadeUp}
        initial="initial"
        animate="animate"
        transition={{ delay: 0.2 }}
      >
        <Card padding="lg">
          <p className="text-xs font-medium text-[var(--color-text-muted)] uppercase tracking-wider mb-4">
            Token usage over time
          </p>
          {summary && summary.length > 0 ? (
            <Suspense fallback={<ChartSkeleton />}>
              <LazyResponsiveContainer width="100%" height={260}>
                <LazyAreaChart
                  data={summary.map((b) => ({
                    time: new Date(b.bucketStart).toLocaleDateString(undefined, {
                      month: "short",
                      day: "numeric",
                    }),
                    prompt: b.promptTokens,
                    completion: b.completionTokens,
                  }))}
                >
                  <defs>
                    <linearGradient id="promptGrad" x1="0" y1="0" x2="0" y2="1">
                      <stop
                        offset="0%"
                        stopColor="var(--color-primary)"
                        stopOpacity={0.3}
                      />
                      <stop
                        offset="100%"
                        stopColor="var(--color-primary)"
                        stopOpacity={0}
                      />
                    </linearGradient>
                    <linearGradient
                      id="completionGrad"
                      x1="0"
                      y1="0"
                      x2="0"
                      y2="1"
                    >
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
                  </defs>
                  <LazyXAxis
                    dataKey="time"
                    tick={{ fontSize: 10, fill: "var(--color-text-muted)" }}
                    tickLine={false}
                    axisLine={false}
                  />
                  <LazyYAxis
                    tick={{ fontSize: 10, fill: "var(--color-text-muted)" }}
                    tickLine={false}
                    axisLine={false}
                    width={40}
                    tickFormatter={(v: number) => formatTokens(v)}
                  />
                  <LazyTooltip
                    contentStyle={{
                      background: "var(--color-bg-elevated)",
                      border: "1px solid var(--color-border)",
                      borderRadius: "var(--radius-lg)",
                      fontSize: "12px",
                    }}
                    formatter={(value: number, name: string) => [
                      formatTokens(value),
                      name === "prompt" ? "Prompt tokens" : "Completion tokens",
                    ]}
                  />
                  <LazyLegend
                    iconType="circle"
                    wrapperStyle={{ fontSize: "11px" }}
                  />
                  <LazyArea
                    type="monotone"
                    dataKey="prompt"
                    name="prompt"
                    stroke="var(--color-primary)"
                    fill="url(#promptGrad)"
                    strokeWidth={2}
                  />
                  <LazyArea
                    type="monotone"
                    dataKey="completion"
                    name="completion"
                    stroke="var(--color-status-running)"
                    fill="url(#completionGrad)"
                    strokeWidth={2}
                  />
                </LazyAreaChart>
              </LazyResponsiveContainer>
            </Suspense>
          ) : (
            <p className="text-sm text-[var(--color-text-muted)] py-8 text-center">
              No usage data for this time range.
            </p>
          )}
        </Card>
      </motion.div>

      {/* Per-Model Breakdown Table */}
      <motion.div
        variants={fadeUp}
        initial="initial"
        animate="animate"
        transition={{ delay: 0.25 }}
      >
        <Card padding="lg">
          <p className="text-xs font-medium text-[var(--color-text-muted)] uppercase tracking-wider mb-4">
            Per-model breakdown
          </p>
          {sortedModels.length > 0 ? (
            <div className="overflow-x-auto">
              <table className="w-full text-sm">
                <thead>
                  <tr className="border-b border-[var(--color-border)]">
                    <th
                      className="text-left py-2 pr-4 text-xs font-medium text-[var(--color-text-muted)] cursor-pointer hover:text-[var(--color-text)] select-none"
                      onClick={() => handleSort("model")}
                    >
                      Model
                      <SortIcon field="model" />
                    </th>
                    <th
                      className="text-right py-2 px-4 text-xs font-medium text-[var(--color-text-muted)] cursor-pointer hover:text-[var(--color-text)] select-none"
                      onClick={() => handleSort("requestCount")}
                    >
                      Requests
                      <SortIcon field="requestCount" />
                    </th>
                    <th
                      className="text-right py-2 px-4 text-xs font-medium text-[var(--color-text-muted)] cursor-pointer hover:text-[var(--color-text)] select-none hidden sm:table-cell"
                      onClick={() => handleSort("promptTokens")}
                    >
                      Prompt Tokens
                      <SortIcon field="promptTokens" />
                    </th>
                    <th
                      className="text-right py-2 px-4 text-xs font-medium text-[var(--color-text-muted)] cursor-pointer hover:text-[var(--color-text)] select-none hidden md:table-cell"
                      onClick={() => handleSort("completionTokens")}
                    >
                      Completion Tokens
                      <SortIcon field="completionTokens" />
                    </th>
                    <th
                      className="text-right py-2 px-4 text-xs font-medium text-[var(--color-text-muted)] cursor-pointer hover:text-[var(--color-text)] select-none hidden lg:table-cell"
                      onClick={() => handleSort("cacheHitRate")}
                    >
                      Cache Hit %
                      <SortIcon field="cacheHitRate" />
                    </th>
                    <th
                      className="text-right py-2 pl-4 text-xs font-medium text-[var(--color-text-muted)] cursor-pointer hover:text-[var(--color-text)] select-none"
                      onClick={() => handleSort("avgLatencyMs")}
                    >
                      Avg Latency
                      <SortIcon field="avgLatencyMs" />
                    </th>
                  </tr>
                </thead>
                <tbody>
                  {sortedModels.map((m) => (
                    <tr
                      key={`${m.provider}-${m.model}`}
                      className="border-b border-[var(--color-border)] last:border-0"
                    >
                      <td className="py-2.5 pr-4">
                        <Link
                          to={`/models?selected=${encodeURIComponent(m.model)}`}
                          className="font-medium text-[var(--color-primary)] hover:underline decoration-[var(--color-primary)] underline-offset-2"
                        >
                          {formatModelName(
                            m.model,
                            m.provider,
                            settings?.hideOriginPrefix ?? false,
                            settings?.agentDisplayNames ?? {},
                          )}
                        </Link>
                        <Badge variant="info" size="sm" className="ml-2">
                          {m.provider}
                        </Badge>
                      </td>
                      <td className="py-2.5 px-4 text-right font-mono text-[var(--color-text)]">
                        {m.requestCount.toLocaleString()}
                      </td>
                      <td className="py-2.5 px-4 text-right font-mono text-[var(--color-text)] hidden sm:table-cell">
                        {formatTokens(m.promptTokens)}
                      </td>
                      <td className="py-2.5 px-4 text-right font-mono text-[var(--color-text)] hidden md:table-cell">
                        {formatTokens(m.completionTokens)}
                      </td>
                      <td className="py-2.5 px-4 text-right font-mono text-[var(--color-text)] hidden lg:table-cell">
                        {m.promptTokens > 0
                          ? `${((m.cachedTokens / m.promptTokens) * 100).toFixed(1)}%`
                          : "\u2014"}
                      </td>
                      <td className="py-2.5 pl-4 text-right font-mono text-[var(--color-text)]">
                        {formatMs(m.avgLatencyMs)}
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          ) : (
            <p className="text-sm text-[var(--color-text-muted)] py-8 text-center">
              No model usage data for this time range.
            </p>
          )}
        </Card>
      </motion.div>

      {/* Per-Provider Breakdown */}
      <motion.div
        variants={fadeUp}
        initial="initial"
        animate="animate"
        transition={{ delay: 0.3 }}
      >
        <Card padding="lg">
          <p className="text-xs font-medium text-[var(--color-text-muted)] uppercase tracking-wider mb-4">
            Per-provider breakdown
          </p>
          {providers && providers.length > 0 ? (
            <Suspense fallback={<ChartSkeleton />}>
              <LazyResponsiveContainer width="100%" height={Math.max(120, providers.length * 36)}>
                <LazyBarChart
                  data={providers}
                  layout="vertical"
                  margin={{ left: 0, right: 16 }}
                >
                  <LazyXAxis
                    type="number"
                    tick={{ fontSize: 10, fill: "var(--color-text-muted)" }}
                    tickLine={false}
                    axisLine={false}
                    tickFormatter={(v: number) => formatTokens(v)}
                  />
                  <LazyYAxis
                    type="category"
                    dataKey="provider"
                    tick={{ fontSize: 11, fill: "var(--color-text-heading)" }}
                    tickLine={false}
                    axisLine={false}
                    width={100}
                  />
                  <LazyTooltip
                    contentStyle={{
                      background: "var(--color-bg-elevated)",
                      border: "1px solid var(--color-border)",
                      borderRadius: "var(--radius-lg)",
                      fontSize: "12px",
                    }}
                    formatter={(value: number, name: string) => {
                      const labels: Record<string, string> = {
                        requestCount: "Requests",
                        promptTokens: "Prompt tokens",
                        completionTokens: "Completion tokens",
                      };
                      return [name === "requestCount" ? value.toLocaleString() : formatTokens(value), labels[name] || name];
                    }}
                  />
                  <LazyLegend
                    iconType="circle"
                    wrapperStyle={{ fontSize: "11px" }}
                  />
                  <LazyBar
                    dataKey="requestCount"
                    name="requestCount"
                    radius={[0, 4, 4, 0]}
                    barSize={16}
                    onClick={(data: { provider: string }) => {
                      if (data?.provider) {
                        setFilterProvider((prev) =>
                          prev === data.provider ? "" : data.provider,
                        );
                        setFilterModel("");
                      }
                    }}
                    style={{ cursor: "pointer" }}
                  >
                    {providers.map((entry, index) => (
                      <LazyCell
                        key={`req-${entry.provider}`}
                        fill={
                          filterProvider && filterProvider !== entry.provider
                            ? "var(--color-text-muted)"
                            : PROVIDER_COLORS[index % PROVIDER_COLORS.length]
                        }
                        fillOpacity={filterProvider && filterProvider !== entry.provider ? 0.3 : 1}
                      />
                    ))}
                  </LazyBar>
                  <LazyBar
                    dataKey="promptTokens"
                    name="promptTokens"
                    radius={[0, 4, 4, 0]}
                    barSize={16}
                    onClick={(data: { provider: string }) => {
                      if (data?.provider) {
                        setFilterProvider((prev) =>
                          prev === data.provider ? "" : data.provider,
                        );
                        setFilterModel("");
                      }
                    }}
                    style={{ cursor: "pointer" }}
                  >
                    {providers.map((entry, index) => (
                      <LazyCell
                        key={`pt-${entry.provider}`}
                        fill={
                          filterProvider && filterProvider !== entry.provider
                            ? "var(--color-text-muted)"
                            : PROVIDER_COLORS[(index + 1) % PROVIDER_COLORS.length]
                        }
                        fillOpacity={filterProvider && filterProvider !== entry.provider ? 0.3 : 1}
                      />
                    ))}
                  </LazyBar>
                  <LazyBar
                    dataKey="completionTokens"
                    name="completionTokens"
                    radius={[0, 4, 4, 0]}
                    barSize={16}
                    onClick={(data: { provider: string }) => {
                      if (data?.provider) {
                        setFilterProvider((prev) =>
                          prev === data.provider ? "" : data.provider,
                        );
                        setFilterModel("");
                      }
                    }}
                    style={{ cursor: "pointer" }}
                  >
                    {providers.map((entry, index) => (
                      <LazyCell
                        key={`ct-${entry.provider}`}
                        fill={
                          filterProvider && filterProvider !== entry.provider
                            ? "var(--color-text-muted)"
                            : PROVIDER_COLORS[(index + 2) % PROVIDER_COLORS.length]
                        }
                        fillOpacity={filterProvider && filterProvider !== entry.provider ? 0.3 : 1}
                      />
                    ))}
                  </LazyBar>
                </LazyBarChart>
              </LazyResponsiveContainer>
            </Suspense>
          ) : (
            <p className="text-sm text-[var(--color-text-muted)] py-8 text-center">
              No provider usage data for this time range.
            </p>
          )}
        </Card>
      </motion.div>

      {/* Cost Calculator Dialog */}
      <CostCalculatorDialog
        open={costDialogOpen}
        onOpenChange={setCostDialogOpen}
        providers={providers}
        totals={totals}
        filterProvider={filterProvider}
      />
    </div>
  );
}

// ─── Cost Calculator Dialog ──────────────────────────────────────

interface CostCalculatorDialogProps {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  providers: import("../../lib/api/types").ProviderUsageSummary[] | undefined;
  totals: import("../../lib/api/types").UsageTotalsResponse | undefined;
  filterProvider: string;
}

interface RateRow {
  provider: string;
  promptPer1M: string;
  completionPer1M: string;
}

function CostCalculatorDialog({
  open,
  onOpenChange,
  providers,
  totals,
  filterProvider,
}: CostCalculatorDialogProps) {
  // Build rate rows from saved data + provider list when dialog opens
  const [rows, setRows] = useState<RateRow[]>([]);

  useEffect(() => {
    if (open) {
      const saved = loadCostRates();
      if (providers && providers.length > 0) {
        const newRows: RateRow[] = providers.map((p) => ({
          provider: p.provider,
          promptPer1M: saved[p.provider]?.promptPer1M?.toString() ?? "",
          completionPer1M: saved[p.provider]?.completionPer1M?.toString() ?? "",
        }));
        setRows(newRows);
      } else {
        setRows([]);
      }
    }
  }, [open, providers]);

  function updateRow(index: number, field: "promptPer1M" | "completionPer1M", value: string) {
    setRows((prev) =>
      prev.map((r, i) => (i === index ? { ...r, [field]: value } : r)),
    );
  }

  function addRow() {
    setRows((prev) => [...prev, { provider: "", promptPer1M: "", completionPer1M: "" }]);
  }

  function removeRow(index: number) {
    setRows((prev) => prev.filter((_, i) => i !== index));
  }

  function saveRates() {
    const newRates: CostRatesMap = {};
    for (const row of rows) {
      if (row.provider) {
        newRates[row.provider] = {
          promptPer1M: parseFloat(row.promptPer1M) || 0,
          completionPer1M: parseFloat(row.completionPer1M) || 0,
        };
      }
    }
    saveCostRates(newRates);
    setRates(newRates);
  }

  // Calculate costs per provider from the current totals data
  // We use the totals across all providers and split by the provider breakdown data
  // to approximate per-provider token counts, then apply rates
  const costBreakdown = useMemo(() => {
    if (!providers || !totals) return [];
    return rows.map((row) => {
      const promptPer1M = parseFloat(row.promptPer1M) || 0;
      const completionPer1M = parseFloat(row.completionPer1M) || 0;

      // Find provider data to get token counts
      const providerData = providers.find((p) => p.provider === row.provider);
      const promptTokens = providerData?.promptTokens ?? 0;
      const completionTokens = providerData?.completionTokens ?? 0;

      const promptCost = (promptTokens / 1_000_000) * promptPer1M;
      const completionCost = (completionTokens / 1_000_000) * completionPer1M;

      return {
        ...row,
        promptTokens,
        completionTokens,
        promptCost,
        completionCost,
        totalCost: promptCost + completionCost,
      };
    });
  }, [rows, providers, totals]);

  const grandTotal = useMemo(
    () => costBreakdown.reduce((sum, r) => sum + r.totalCost, 0),
    [costBreakdown],
  );

  return (
    <Dialog
      open={open}
      onOpenChange={onOpenChange}
      title="Cost Calculator"
      className="sm:max-w-3xl"
    >
      <div className="px-5 py-4 space-y-5">
        <p className="text-xs text-[var(--color-text-muted)]">
          Set your cost-per-million-token rates for each provider. Rates are
          saved to your browser's local storage and applied against the current
          filter selection.
        </p>

        {/* Rate Entry Table */}
        <div className="space-y-2">
          {rows.map((row, i) => (
            <div
              key={i}
              className="flex flex-col sm:flex-row items-start sm:items-end gap-2 p-2.5 rounded-[var(--radius-lg)] border border-[var(--color-border-subtle)] bg-[var(--color-bg-muted)]/30"
            >
              <div className="flex flex-col gap-1 flex-1 min-w-[120px]">
                {i === 0 && (
                  <label className="text-xs font-medium text-[var(--color-text-muted)]">
                    Provider
                  </label>
                )}
                <input
                  type="text"
                  value={row.provider}
                  onChange={(e) => {
                    setRows((prev) =>
                      prev.map((r, idx) =>
                        idx === i ? { ...r, provider: e.target.value } : r,
                      ),
                    );
                  }}
                  placeholder="e.g. openai"
                  className="h-8 rounded-[var(--radius-lg)] border bg-[var(--color-bg-surface)] px-3 text-sm text-[var(--color-text)] border-[var(--color-border)] focus:outline-none focus:border-[var(--color-primary)] focus:ring-1 focus:ring-[var(--color-focus-ring)] transition-colors w-full"
                />
              </div>
              <div className="flex flex-col gap-1 flex-1 min-w-[120px]">
                {i === 0 && (
                  <label className="text-xs font-medium text-[var(--color-text-muted)]">
                    Prompt $/1M tokens
                  </label>
                )}
                <input
                  type="number"
                  value={row.promptPer1M}
                  onChange={(e) => updateRow(i, "promptPer1M", e.target.value)}
                  placeholder="0.00"
                  min="0"
                  step="0.01"
                  className="h-8 rounded-[var(--radius-lg)] border bg-[var(--color-bg-surface)] px-3 text-sm text-[var(--color-text)] border-[var(--color-border)] focus:outline-none focus:border-[var(--color-primary)] focus:ring-1 focus:ring-[var(--color-focus-ring)] transition-colors w-full font-mono"
                />
              </div>
              <div className="flex flex-col gap-1 flex-1 min-w-[120px]">
                {i === 0 && (
                  <label className="text-xs font-medium text-[var(--color-text-muted)]">
                    Completion $/1M tokens
                  </label>
                )}
                <input
                  type="number"
                  value={row.completionPer1M}
                  onChange={(e) => updateRow(i, "completionPer1M", e.target.value)}
                  placeholder="0.00"
                  min="0"
                  step="0.01"
                  className="h-8 rounded-[var(--radius-lg)] border bg-[var(--color-bg-surface)] px-3 text-sm text-[var(--color-text)] border-[var(--color-border)] focus:outline-none focus:border-[var(--color-primary)] focus:ring-1 focus:ring-[var(--color-focus-ring)] transition-colors w-full font-mono"
                />
              </div>
              <button
                type="button"
                onClick={() => removeRow(i)}
                className="h-8 w-8 shrink-0 flex items-center justify-center rounded-[var(--radius-md)] text-[var(--color-text-muted)] hover:text-[var(--color-status-error)] hover:bg-[var(--color-bg-muted)] transition-colors cursor-pointer"
                aria-label="Remove row"
              >
                <Trash2 className="size-3.5" />
              </button>
            </div>
          ))}

          <button
            type="button"
            onClick={addRow}
            className="flex items-center gap-1.5 px-3 py-1.5 text-xs font-medium text-[var(--color-primary)] hover:bg-[var(--color-bg-muted)] rounded-[var(--radius-md)] transition-colors cursor-pointer"
          >
            <Plus className="size-3" />
            Add provider
          </button>
        </div>

        <div className="flex justify-end">
          <Button variant="primary" size="sm" onClick={saveRates}>
            Save Rates
          </Button>
        </div>

        {/* Cost Breakdown Table */}
        {costBreakdown.length > 0 && (
          <div className="overflow-x-auto">
            <table className="w-full text-sm">
              <thead>
                <tr className="border-b border-[var(--color-border)]">
                  <th className="text-left py-2 pr-3 text-xs font-medium text-[var(--color-text-muted)]">
                    Provider
                  </th>
                  <th className="text-right py-2 px-3 text-xs font-medium text-[var(--color-text-muted)]">
                    Prompt Rate
                  </th>
                  <th className="text-right py-2 px-3 text-xs font-medium text-[var(--color-text-muted)]">
                    Completion Rate
                  </th>
                  <th className="text-right py-2 px-3 text-xs font-medium text-[var(--color-text-muted)] hidden sm:table-cell">
                    Prompt Tokens
                  </th>
                  <th className="text-right py-2 px-3 text-xs font-medium text-[var(--color-text-muted)] hidden sm:table-cell">
                    Completion Tokens
                  </th>
                  <th className="text-right py-2 px-3 text-xs font-medium text-[var(--color-text-muted)]">
                    Prompt Cost
                  </th>
                  <th className="text-right py-2 px-3 text-xs font-medium text-[var(--color-text-muted)]">
                    Completion Cost
                  </th>
                  <th className="text-right py-2 pl-3 text-xs font-medium text-[var(--color-text-muted)]">
                    Total Cost
                  </th>
                </tr>
              </thead>
              <tbody>
                {costBreakdown.map((row) => (
                  <tr
                    key={row.provider}
                    className="border-b border-[var(--color-border)] last:border-0"
                  >
                    <td className="py-2 pr-3 font-medium text-[var(--color-text-heading)]">
                      {row.provider || <span className="text-[var(--color-text-muted)] italic">unnamed</span>}
                    </td>
                    <td className="py-2 px-3 text-right font-mono text-[var(--color-text)]">
                      {row.promptPer1M ? `$${parseFloat(row.promptPer1M).toFixed(2)}` : "—"}
                    </td>
                    <td className="py-2 px-3 text-right font-mono text-[var(--color-text)]">
                      {row.completionPer1M ? `$${parseFloat(row.completionPer1M).toFixed(2)}` : "—"}
                    </td>
                    <td className="py-2 px-3 text-right font-mono text-[var(--color-text)] hidden sm:table-cell">
                      {formatTokens(row.promptTokens)}
                    </td>
                    <td className="py-2 px-3 text-right font-mono text-[var(--color-text)] hidden sm:table-cell">
                      {formatTokens(row.completionTokens)}
                    </td>
                    <td className="py-2 px-3 text-right font-mono text-[var(--color-text)]">
                      {formatCurrency(row.promptCost)}
                    </td>
                    <td className="py-2 px-3 text-right font-mono text-[var(--color-text)]">
                      {formatCurrency(row.completionCost)}
                    </td>
                    <td className="py-2 pl-3 text-right font-mono font-medium text-[var(--color-text-heading)]">
                      {formatCurrency(row.totalCost)}
                    </td>
                  </tr>
                ))}
              </tbody>
              <tfoot>
                <tr className="border-t border-[var(--color-border-strong)]">
                  <td
                    colSpan={6}
                    className="py-2 pr-3 text-right text-xs font-semibold text-[var(--color-text-heading)] sm:col-span-6"
                  >
                    Grand Total
                  </td>
                  <td
                    colSpan={2}
                    className="py-2 pl-3 text-right font-mono font-semibold text-[var(--color-text-heading)]"
                  >
                    {formatCurrency(grandTotal)}
                  </td>
                </tr>
              </tfoot>
            </table>
          </div>
        )}
      </div>
    </Dialog>
  );
}
