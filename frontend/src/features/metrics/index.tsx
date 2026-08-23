import { Suspense, lazy, useState, useMemo, useCallback, useEffect, useRef, type ReactNode } from "react";
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
  Download,
  TrendingUp,
  TrendingDown,
  Minus,
  Copy,
  Check,
  BookmarkPlus,
  Bookmark,
  PiggyBank,
  Radio,
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
  Tooltip,
} from "../../components/ui";
import { formatModelName } from "../../lib/format-model-name";
import type {
  ProviderUsageSummary,
  UsageTotalsResponse,
} from "../../lib/api/types";
import type {
  TimeSeriesMetric,
  DrillDownWindow,
} from "./charts";
import {
  loadCostRates,
  saveCostRates,
  modelCost,
  computeBlendedRates,
  cacheSavings,
  hasAnyRates,
  isFlatRateProvider,
  isSelfHostedProvider,
  isSubscriptionProvider,
  flatCostTotals,
  type CostRatesMap,
  type PricingMode,
} from "./cost";
import {
  formatTokens,
  formatMs,
  formatCurrency,
} from "./format";
import { useMetricsPresets } from "./persisted";
import { RecentRequestsTable } from "./recent-requests-table";
import { HourlyHeatmap } from "./heatmap";
import { BudgetsPanel } from "./budgets";
import { RetentionControl } from "./retention-control";
import { ApiKeysCard, LatencyBandsCard } from "./breakdown-cards";
import {
  getMetricsApiKeys,
  getMetricsLatencyBands,
  getMetricsModels,
  getMetricsProviderCatalog,
  getMetricsProviders,
  getMetricsSummary,
  getMetricsTotals,
  type MetricsFilterParams,
} from "./metrics-api";

// Lazy-load the charts module (which imports recharts statically) to keep the
// main bundle lean. Do NOT lazy-load individual recharts components behind
// nested <Suspense>: recharts 3.x + React 19 hits an infinite setState loop in
// RechartsWrapper's ref callback when the chart subtree suspends/reappears
// (recharts#7463) — "Maximum update depth exceeded" on page load.
const LazyTokenUsageChart = lazy(() =>
  import("./charts").then((m) => ({ default: m.TokenUsageChart })),
);
const LazyProviderBreakdownChart = lazy(() =>
  import("./charts").then((m) => ({ default: m.ProviderBreakdownChart })),
);

function ChartSkeleton() {
  return (
    <div className="h-48 flex items-center justify-center">
      <Spinner size="sm" />
    </div>
  );
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

/** The equivalent window immediately before the selected one. */
function getPreviousRangeParams(range: TimeRange): {
  from?: string;
  to?: string;
} {
  if (range === "all") return {};
  const params = getTimeRangeParams(range);
  if (!params.from || !params.to) return {};
  const fromMs = new Date(params.from).getTime();
  const toMs = new Date(params.to).getTime();
  return {
    from: new Date(fromMs - (toMs - fromMs)).toISOString(),
    to: params.from,
  };
}

// ─── Time-Series Metric Toggle ──────────────────────────────────

const METRIC_OPTIONS: { value: TimeSeriesMetric; label: string }[] = [
  { value: "tokens", label: "Tokens" },
  { value: "requests", label: "Requests" },
  { value: "latency", label: "Latency" },
  { value: "cached", label: "Cached" },
  { value: "cost", label: "Cost" },
];

const METRIC_TITLES: Record<TimeSeriesMetric, string> = {
  tokens: "Token usage over time",
  requests: "Requests over time",
  latency: "Average latency over time",
  cached: "Cached tokens over time",
  cost: "Estimated cost over time",
};

// ─── Auto-refresh ────────────────────────────────────────────────

type AutoRefreshInterval = 0 | 10_000 | 30_000;

const AUTO_REFRESH_OPTIONS: { value: string; label: string }[] = [
  { value: "0", label: "Auto: off" },
  { value: "10000", label: "Every 10s" },
  { value: "30000", label: "Every 30s" },
];

// ─── Sort ────────────────────────────────────────────────────────

type SortField =
  | "model"
  | "requestCount"
  | "promptTokens"
  | "completionTokens"
  | "cacheHitRate"
  | "avgLatencyMs"
  | "p95LatencyMs"
  | "maxLatencyMs"
  | "estCost";
type SortDirection = "asc" | "desc";

// ─── Animations ──────────────────────────────────────────────────

const fadeUp = {
  initial: { opacity: 0, y: 8 },
  animate: { opacity: 1, y: 0 },
};

// ─── Main Component ──────────────────────────────────────────────

export default function Metrics() {
  const [timeRange, setTimeRangeRaw] = useState<TimeRange>("7d");
  const [sortField, setSortField] = useState<SortField>("requestCount");
  const [sortDirection, setSortDirection] = useState<SortDirection>("desc");
  const [filterProvider, setFilterProvider] = useState<string>("");
  const [filterModel, setFilterModel] = useState<string>("");
  const [costDialogOpen, setCostDialogOpen] = useState(false);

  // Series toggle + auto-refresh + drill-down window
  const [seriesMetric, setSeriesMetric] = useState<TimeSeriesMetric>("tokens");
  const [autoRefreshMs, setAutoRefreshMs] = useState<AutoRefreshInterval>(0);
  const [customWindow, setCustomWindow] = useState<DrillDownWindow | null>(null);
  const recentSectionRef = useRef<HTMLDivElement>(null);

  // Preset name input
  const [presetName, setPresetName] = useState("");
  const { presets, savePreset, deletePreset } = useMetricsPresets();

  // Cost rates are re-read whenever the calculator dialog closes so edits
  // made there flow into every estimate immediately.
  const [costRates, setCostRates] = useState<CostRatesMap>(() => loadCostRates());
  useEffect(() => {
    if (!costDialogOpen) setCostRates(loadCostRates());
  }, [costDialogOpen]);

  const rangeParams = useMemo(() => getTimeRangeParams(timeRange), [timeRange]);

  // Changing the time range invalidates any active drill-down window.
  const setTimeRange = useCallback((range: TimeRange) => {
    setTimeRangeRaw(range);
    setCustomWindow(null);
  }, []);

  // Combined filter params passed to every API call
  const filterParams = useMemo(
    () => ({
      ...rangeParams,
      ...(filterProvider ? { provider: filterProvider } : {}),
      ...(filterModel ? { model: filterModel } : {}),
    }),
    [rangeParams, filterProvider, filterModel],
  );

  // Same duration, immediately before the selected window (period comparison).
  const prevFilterParams = useMemo(() => {
    const prev = getPreviousRangeParams(timeRange);
    if (!prev.from || !prev.to) return null;
    return {
      ...prev,
      ...(filterProvider ? { provider: filterProvider } : {}),
      ...(filterModel ? { model: filterModel } : {}),
    };
  }, [timeRange, filterProvider, filterModel]);

  // Current calendar month, for budget progress bars.
  const monthParams = useMemo(() => {
    const now = new Date();
    return {
      from: new Date(now.getFullYear(), now.getMonth(), 1).toISOString(),
      to: now.toISOString(),
    };
  }, []);

  const refetchInterval = autoRefreshMs || false;

  // ── Queries ──────────────────────────────────────────────────

  const {
    data: totals,
    isLoading: totalsLoading,
    error: totalsError,
    refetch: refetchTotals,
  } = useQuery({
    queryKey: ["metrics", "totals", filterParams],
    queryFn: () => getMetricsTotals(filterParams),
    refetchInterval,
  });

  const {
    data: summary,
    isLoading: summaryLoading,
    error: summaryError,
    refetch: refetchSummary,
  } = useQuery({
    queryKey: ["metrics", "summary", filterParams],
    queryFn: () =>
      getMetricsSummary({
        ...filterParams,
        granularity: timeRange === "24h" ? "hour" : "day",
      }),
    refetchInterval,
  });

  const {
    data: models,
    isLoading: modelsLoading,
    error: modelsError,
    refetch: refetchModels,
  } = useQuery({
    queryKey: ["metrics", "models", filterParams],
    queryFn: () => getMetricsModels(filterParams),
    refetchInterval,
  });

  const {
    data: providers,
    isLoading: providersLoading,
    error: providersError,
    refetch: refetchProviders,
  } = useQuery({
    queryKey: ["metrics", "providers", rangeParams],
    queryFn: () => getMetricsProviders(rangeParams),
    refetchInterval,
  });

  // Latency distribution + API-key attribution for the current window.
  const latencyFilterParams: MetricsFilterParams = filterParams;
  const {
    data: latencyBands,
    isLoading: latencyBandsLoading,
    refetch: refetchLatencyBands,
  } = useQuery({
    queryKey: ["metrics", "latency-bands", latencyFilterParams],
    queryFn: () => getMetricsLatencyBands(latencyFilterParams),
    refetchInterval,
  });

  const {
    data: apiKeyUsage,
    isLoading: apiKeyUsageLoading,
    refetch: refetchApiKeys,
  } = useQuery({
    // Endpoint is time-window scoped (no provider/model split server-side).
    queryKey: ["metrics", "api-keys", rangeParams],
    queryFn: () => getMetricsApiKeys(rangeParams),
    refetchInterval,
  });

  // Previous equivalent window, for % deltas on the summary cards.
  const { data: prevTotals } = useQuery({
    queryKey: ["metrics", "totals", "previous", prevFilterParams],
    queryFn: () => getMetricsTotals(prevFilterParams!),
    enabled: prevFilterParams !== null,
    refetchInterval,
  });

  // Month-to-date usage per provider, for budget progress bars.
  const { data: monthProviders, isLoading: monthProvidersLoading } = useQuery({
    queryKey: ["metrics", "providers", "month", monthParams],
    queryFn: () => getMetricsProviders(monthParams),
    refetchInterval,
  });

  // Fetch the full unfiltered models list once to populate filter dropdowns
  const { data: allModels } = useQuery({
    queryKey: ["metrics", "models", "all"],
    queryFn: () => client.getMetricsModels(),
  });

  // Provider catalog for the cost calculator's provider picker. Falls back to
  // distinct usage providers (kind inferred) when the endpoint isn't live yet.
  const { data: providerCatalog } = useQuery({
    queryKey: ["metrics", "provider-catalog"],
    queryFn: () => getMetricsProviderCatalog(),
    staleTime: 5 * 60 * 1000,
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

  // ── Cost estimates ───────────────────────────────────────────

  const blendedRates = useMemo(
    () => (models ? computeBlendedRates(models, costRates) : null),
    [models, costRates],
  );

  const { estCostTotal, missingRateCount, flatTotals } = useMemo(() => {
    if (!models) {
      return {
        estCostTotal: null as number | null,
        missingRateCount: 0,
        flatTotals: { subscriptions: 0, selfHosted: 0 },
      };
    }
    let total = 0;
    let missing = 0;
    for (const m of models) {
      if (isFlatRateProvider(costRates, m.provider)) continue;
      const c = modelCost(m, costRates);
      if (c === null) missing += 1;
      else total += c;
    }
    // Flat monthly costs for subscription / self-hosted providers with any
    // usage in this window — reported as lump sums, never blended into
    // token math or the time series.
    const activeProviders = new Set(models.map((m) => m.provider));
    return {
      estCostTotal: total,
      missingRateCount: missing,
      flatTotals: flatCostTotals(costRates, activeProviders),
    };
  }, [models, costRates]);

  const savingsEstimate = useMemo(
    () =>
      totals && totals.totalCachedTokens > 0
        ? cacheSavings(totals.totalCachedTokens, blendedRates)
        : totals
          ? 0
          : null,
    [totals, blendedRates],
  );

  const anyRates = useMemo(() => hasAnyRates(costRates), [costRates]);

  // ── Keyboard shortcut: press "R" outside inputs to refresh ──

  const refreshAll = useCallback(() => {
    refetchTotals();
    refetchSummary();
    refetchModels();
    refetchProviders();
    refetchLatencyBands();
    refetchApiKeys();
  }, [refetchTotals, refetchSummary, refetchModels, refetchProviders, refetchLatencyBands, refetchApiKeys]);

  useEffect(() => {
    function onKeyDown(e: KeyboardEvent) {
      if (e.key !== "r" && e.key !== "R") return;
      if (e.metaKey || e.ctrlKey || e.altKey) return;
      const target = e.target as HTMLElement | null;
      const tag = target?.tagName;
      if (
        tag === "INPUT" ||
        tag === "SELECT" ||
        tag === "TEXTAREA" ||
        target?.isContentEditable
      ) {
        return;
      }
      refreshAll();
    }
    window.addEventListener("keydown", onKeyDown);
    return () => window.removeEventListener("keydown", onKeyDown);
  }, [refreshAll]);

  // ── Drill-down: clicking a chart point narrows the feed ─────

  const handlePointClick = useCallback((w: DrillDownWindow) => {
    setCustomWindow(w);
    requestAnimationFrame(() => {
      recentSectionRef.current?.scrollIntoView({ behavior: "smooth", block: "start" });
    });
  }, []);

  // ── Presets ──────────────────────────────────────────────────

  const applyPreset = useCallback(
    (preset: { provider: string; model: string; range: string }) => {
      const validRange = TIME_RANGE_OPTIONS.some((o) => o.value === preset.range)
        ? (preset.range as TimeRange)
        : "7d";
      setFilterProvider(preset.provider);
      setFilterModel(preset.model);
      setTimeRange(validRange);
    },
    [setTimeRange],
  );

  const handleSavePreset = useCallback(() => {
    const name =
      presetName.trim() ||
      [filterProvider || "all providers", filterModel || "all models", timeRange].join(" · ");
    savePreset({ name, provider: filterProvider, model: filterModel, range: timeRange });
    setPresetName("");
  }, [presetName, filterProvider, filterModel, timeRange, savePreset]);

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
        case "p95LatencyMs":
          return dir * ((a.p95LatencyMs ?? a.avgLatencyMs) - (b.p95LatencyMs ?? b.avgLatencyMs));
        case "maxLatencyMs":
          return dir * ((a.maxLatencyMs ?? a.avgLatencyMs) - (b.maxLatencyMs ?? b.avgLatencyMs));
        case "estCost": {
          // Flat-rate rows (subscription / self-hosted) carry no per-token
          // cost; they sort as one constant group at the far end of either
          // direction.
          const costOf = (m: (typeof models)[number]) =>
            isFlatRateProvider(costRates, m.provider)
              ? Number.POSITIVE_INFINITY
              : (modelCost(m, costRates) ?? -1);
          const costA = costOf(a);
          const costB = costOf(b);
          return dir * (costA - costB);
        }
        default:
          return 0;
      }
    });
    return sorted;
  }, [models, sortField, sortDirection, costRates]);

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

  // ── CSV export (client-side blob download) ───────────────────

  const exportCsv = useCallback(() => {
    if (!sortedModels.length) return;
    const escape = (v: string | number) => {
      const s = String(v);
      return /[",\n]/.test(s) ? `"${s.replace(/"/g, '""')}"` : s;
    };
    const header = [
      "Model",
      "Provider",
      "Requests",
      "Prompt Tokens",
      "Completion Tokens",
      "Cached Tokens",
      "Cache Hit %",
      "Avg Latency Ms",
      "Est Cost USD",
    ];
    const lines = sortedModels.map((m) =>
      [
        escape(m.model),
        escape(m.provider),
        m.requestCount,
        m.promptTokens,
        m.completionTokens,
        m.cachedTokens,
        m.promptTokens > 0
          ? ((m.cachedTokens / m.promptTokens) * 100).toFixed(2)
          : "",
        Math.round(m.avgLatencyMs),
        isFlatRateProvider(costRates, m.provider)
          ? "incl."
          : (modelCost(m, costRates) ?? "").toString(),
      ].join(","),
    );
    const blob = new Blob([[header.join(","), ...lines].join("\n")], {
      type: "text/csv;charset=utf-8",
    });
    const url = URL.createObjectURL(blob);
    const anchor = document.createElement("a");
    anchor.href = url;
    anchor.download = `unswarm-model-usage-${new Date().toISOString().slice(0, 10)}.csv`;
    document.body.appendChild(anchor);
    anchor.click();
    anchor.remove();
    URL.revokeObjectURL(url);
  }, [sortedModels, costRates]);

  // ── Loading / Error States ──────────────────────────────────

  const isLoading =
    totalsLoading ||
    summaryLoading ||
    modelsLoading ||
    providersLoading ||
    monthProvidersLoading;
  const error = totalsError || summaryError || modelsError || providersError;

  if (isLoading) {
    return (
      <div className="p-6 space-y-6 max-w-6xl">
        <div className="flex items-center justify-between">
          <Skeleton className="h-6 w-32" />
          <Skeleton className="h-8 w-64" />
        </div>
        <Skeleton className="h-9 w-64" />
        <div className="grid grid-cols-2 md:grid-cols-3 xl:grid-cols-6 gap-4">
          {Array.from({ length: 6 }, (_, i) => (
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
            <Button variant="secondary" size="sm" onClick={refreshAll}>
              <RefreshCw className="size-3.5" />
              Retry
            </Button>
          }
        />
      </div>
    );
  }

  // ── Onboarding empty state: nothing recorded yet ────────────
  const showOnboarding =
    !!totals && totals.totalRequests === 0 && !hasActiveFilters;

  // ── Summary Cards (with period-comparison deltas) ────────────

  const hitRate =
    totals && totals.totalPromptTokens > 0
      ? (totals.totalCachedTokens / totals.totalPromptTokens) * 100
      : null;
  const prevHitRate =
    prevTotals && prevTotals.totalPromptTokens > 0
      ? (prevTotals.totalCachedTokens / prevTotals.totalPromptTokens) * 100
      : null;

  interface StatCard {
    label: string;
    value: ReactNode;
    icon: typeof Activity;
    color: string;
    /** Small secondary line(s) under the headline value. */
    sub?: ReactNode;
    delta?: { current: number; previous: number | null };
  }

  const streamingSub =
    totals?.totalStreamingRequests !== undefined
      ? `${totals.totalStreamingRequests.toLocaleString()} streaming`
      : undefined;

  const summaryCards: StatCard[] = totals
    ? [
        {
          label: "Total requests",
          value: totals.totalRequests.toLocaleString(),
          icon: Activity,
          color: "text-[var(--color-primary)]",
          sub: streamingSub,
          delta: prevTotals
            ? { current: totals.totalRequests, previous: prevTotals.totalRequests }
            : undefined,
        },
        {
          label: "Prompt tokens",
          value: formatTokens(totals.totalPromptTokens),
          icon: Zap,
          color: "text-[var(--color-status-running)]",
          delta: prevTotals
            ? { current: totals.totalPromptTokens, previous: prevTotals.totalPromptTokens }
            : undefined,
        },
        {
          label: "Completion tokens",
          value: formatTokens(totals.totalCompletionTokens),
          icon: ArrowUpRight,
          color: "text-[var(--color-status-running)]",
          delta: prevTotals
            ? {
                current: totals.totalCompletionTokens,
                previous: prevTotals.totalCompletionTokens,
              }
            : undefined,
        },
        {
          label: "Cache hit rate",
          value: hitRate !== null ? `${hitRate.toFixed(1)}%` : "\u2014",
          icon: Database,
          color: "text-[var(--color-status-warning)]",
          delta:
            prevTotals && hitRate !== null
              ? { current: hitRate, previous: prevHitRate }
              : undefined,
        },
        {
          label: "Est. cost",
          value: anyRates ? formatCurrency(estCostTotal ?? 0) : undefined,
          icon: Calculator,
          color: "text-[var(--color-status-error)]",
          sub:
            anyRates && (flatTotals.subscriptions > 0 || flatTotals.selfHosted > 0) ? (
              <>
                {flatTotals.subscriptions > 0 && (
                  <span className="block">
                    + {formatCurrency(flatTotals.subscriptions)} subscriptions
                  </span>
                )}
                {flatTotals.selfHosted > 0 && (
                  <span className="block">
                    + {formatCurrency(flatTotals.selfHosted)} self-hosted
                  </span>
                )}
              </>
            ) : undefined,
        },
        {
          label: "Cache savings",
          value: anyRates ? formatCurrency(savingsEstimate ?? 0) : undefined,
          icon: PiggyBank,
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
        <div className="flex flex-wrap gap-1.5 bg-[var(--color-bg-muted)] rounded-[var(--radius-lg)] p-1">
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
          {/* Save current filter combo as a preset */}
          <div className="flex items-end gap-1.5 shrink-0">
            <input
              type="text"
              value={presetName}
              onChange={(e) => setPresetName(e.target.value)}
              onKeyDown={(e) => {
                if (e.key === "Enter") {
                  e.preventDefault();
                  handleSavePreset();
                }
              }}
              placeholder="Preset name…"
              aria-label="Preset name"
              className="h-8 w-36 rounded-[var(--radius-lg)] border bg-[var(--color-bg-surface)] px-2.5 text-xs text-[var(--color-text)] border-[var(--color-border)] placeholder:text-[var(--color-text-muted)] focus:outline-none focus:border-[var(--color-primary)] focus:ring-1 focus:ring-[var(--color-focus-ring)] transition-colors"
            />
            <Button
              variant="secondary"
              size="sm"
              onClick={handleSavePreset}
              className="gap-1 shrink-0"
              title="Save current filters as a preset"
            >
              <BookmarkPlus className="size-3.5" />
              Save
            </Button>
          </div>
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

        {/* Saved preset chips */}
        {presets.length > 0 && (
          <div className="flex flex-wrap items-center gap-1.5 mt-2 px-1">
            <span className="text-xs text-[var(--color-text-muted)] mr-0.5 inline-flex items-center gap-1">
              <Bookmark className="size-3" />
              Presets
            </span>
            {presets.map((p) => (
              <span
                key={p.name}
                className="inline-flex items-center rounded-full border border-[var(--color-border)] bg-[var(--color-bg-surface)] overflow-hidden transition-colors hover:border-[var(--color-primary)]"
              >
                <button
                  type="button"
                  onClick={() => applyPreset(p)}
                  title={`${p.provider || "all providers"} · ${p.model || "all models"} · ${p.range}`}
                  className="pl-2.5 pr-1.5 py-1 text-xs text-[var(--color-text)] cursor-pointer max-w-[220px] truncate"
                >
                  {p.name}
                </button>
                <button
                  type="button"
                  onClick={() => deletePreset(p.name)}
                  aria-label={`Delete preset ${p.name}`}
                  className="pr-2 py-1 text-[var(--color-text-muted)] hover:text-[var(--color-status-error)] cursor-pointer"
                >
                  <X className="size-3" />
                </button>
              </span>
            ))}
          </div>
        )}
      </motion.div>

      {/* Onboarding empty state */}
      {showOnboarding ? (
        <motion.div variants={fadeUp} initial="initial" animate="animate">
          <Card padding="lg">
            <EmptyState
              icon={<Radio className="size-12" strokeWidth={1.5} />}
              title="No usage recorded yet"
              description="Connect a client to the local inference proxy and its requests will show up here automatically."
              action={
                <div className="flex flex-col items-center gap-3">
                  <ProxyUrlSnippet />
                  <p className="text-xs text-[var(--color-text-muted)] max-w-sm">
                    Point any OpenAI-compatible client at the base URL above
                    (for example <code className="font-mono">base_url="{window.location.origin}/v1"</code>)
                    and send a request.
                  </p>
                </div>
              }
            />
          </Card>
        </motion.div>
      ) : (
        <>
          {/* Toolbar: auto-refresh · manual refresh · CSV export */}
          <motion.div
            variants={fadeUp}
            initial="initial"
            animate="animate"
            transition={{ delay: 0.06 }}
            className="flex flex-wrap items-center justify-end gap-2"
          >
            <Select
              aria-label="Auto-refresh interval"
              options={AUTO_REFRESH_OPTIONS}
              value={String(autoRefreshMs)}
              onChange={(e) =>
                setAutoRefreshMs(Number(e.target.value) as AutoRefreshInterval)
              }
              className="w-[130px]"
            />
            <Tooltip content="Refresh data — or press R" side="bottom">
              <Button
                variant="secondary"
                size="sm"
                onClick={refreshAll}
                className="gap-1.5"
                title="Refresh metrics (R)"
              >
                <RefreshCw className="size-3.5" />
                Refresh
              </Button>
            </Tooltip>
            <Button
              variant="secondary"
              size="sm"
              onClick={exportCsv}
              disabled={sortedModels.length === 0}
              className="gap-1.5"
              title="Download the model breakdown as CSV"
            >
              <Download className="size-3.5" />
              Export CSV
            </Button>
          </motion.div>

          {/* Summary Cards */}
          <div className="grid grid-cols-2 md:grid-cols-3 xl:grid-cols-6 gap-4">
            {summaryCards.map((stat, i) => (
              <motion.div
                key={stat.label}
                variants={fadeUp}
                initial="initial"
                animate="animate"
                transition={{ delay: 0.08 + i * 0.05 }}
              >
                <Card padding="md" className="h-full">
                  <div className="flex items-center justify-between mb-1">
                    <p className="text-xs text-[var(--color-text-muted)]">
                      {stat.label}
                    </p>
                    <stat.icon className={`size-3.5 ${stat.color}`} />
                  </div>
                  {stat.value !== undefined ? (
                    <>
                      <p className="text-xl font-semibold font-heading text-[var(--color-text-heading)]">
                        {stat.value}
                      </p>
                      {stat.sub && (
                        <p className="text-[10px] text-[var(--color-text-muted)]">
                          {stat.sub}
                        </p>
                      )}
                    </>
                  ) : (
                    <button
                      type="button"
                      onClick={() => setCostDialogOpen(true)}
                      className="text-xs text-[var(--color-primary)] hover:underline underline-offset-2 cursor-pointer mt-1.5"
                    >
                      Set rates →
                    </button>
                  )}
                  {stat.delta && (
                    <div className="mt-1">
                      <TrendDelta
                        current={stat.delta.current}
                        previous={stat.delta.previous}
                      />
                    </div>
                  )}
                </Card>
              </motion.div>
            ))}
          </div>

          {/* Time-Series Chart with metric toggle */}
          <motion.div
            variants={fadeUp}
            initial="initial"
            animate="animate"
            transition={{ delay: 0.2 }}
          >
            <Card padding="lg">
              <div className="flex flex-col sm:flex-row sm:items-center sm:justify-between gap-3 mb-4">
                <p className="text-xs font-medium text-[var(--color-text-muted)] uppercase tracking-wider">
                  {METRIC_TITLES[seriesMetric]}
                </p>
                <div className="flex items-center gap-3">
                  <span className="text-[10px] text-[var(--color-text-muted)] hidden lg:inline">
                    Click a point to inspect those requests
                  </span>
                  <div className="flex gap-1 bg-[var(--color-bg-muted)] rounded-[var(--radius-lg)] p-0.5">
                    {METRIC_OPTIONS.map((opt) => {
                      const disabled = opt.value === "cost" && !blendedRates;
                      return (
                        <button
                          key={opt.value}
                          type="button"
                          disabled={disabled}
                          onClick={() => setSeriesMetric(opt.value)}
                          title={
                            disabled
                              ? "Set cost rates first (Cost Calculator)"
                              : undefined
                          }
                          className={`
                            px-2.5 py-1 text-xs font-medium rounded-[var(--radius-md)]
                            transition-all duration-[var(--duration-fast)]
                            ${
                              disabled
                                ? "opacity-40 cursor-not-allowed text-[var(--color-text-muted)]"
                                : seriesMetric === opt.value
                                  ? "bg-[var(--color-bg-surface)] text-[var(--color-text-heading)] shadow-sm cursor-pointer"
                                  : "text-[var(--color-text-muted)] hover:text-[var(--color-text)] cursor-pointer"
                            }
                          `}
                        >
                          {opt.label}
                        </button>
                      );
                    })}
                  </div>
                </div>
              </div>
              {summary && summary.length > 0 ? (
                <Suspense fallback={<ChartSkeleton />}>
                  <LazyTokenUsageChart
                    summary={summary}
                    metric={seriesMetric}
                    blendedRates={blendedRates}
                    onPointClick={handlePointClick}
                  />
                </Suspense>
              ) : (
                <p className="text-sm text-[var(--color-text-muted)] py-8 text-center">
                  No usage data for this time range.
                </p>
              )}
              {seriesMetric === "cost" &&
                (flatTotals.subscriptions > 0 || flatTotals.selfHosted > 0) && (
                  <p className="text-[10px] text-[var(--color-text-muted)] mt-2">
                    Excludes{" "}
                    {[
                      flatTotals.subscriptions > 0
                        ? `${formatCurrency(flatTotals.subscriptions)}/mo subscriptions`
                        : null,
                      flatTotals.selfHosted > 0
                        ? `${formatCurrency(flatTotals.selfHosted)}/mo self-hosted costs`
                        : null,
                    ]
                      .filter(Boolean)
                      .join(" and ")}
                    {" — "}those aren't time-distributed.
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
                <>
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
                            className="text-right py-2 px-4 text-xs font-medium text-[var(--color-text-muted)] cursor-pointer hover:text-[var(--color-text)] select-none hidden lg:table-cell"
                            onClick={() => handleSort("estCost")}
                          >
                            Est. Cost
                            <SortIcon field="estCost" />
                          </th>
                          <th
                            className="text-right py-2 px-4 text-xs font-medium text-[var(--color-text-muted)] cursor-pointer hover:text-[var(--color-text)] select-none"
                            onClick={() => handleSort("avgLatencyMs")}
                          >
                            Avg Latency
                            <SortIcon field="avgLatencyMs" />
                          </th>
                          <th
                            className="text-right py-2 px-4 text-xs font-medium text-[var(--color-text-muted)] cursor-pointer hover:text-[var(--color-text)] select-none hidden xl:table-cell"
                            onClick={() => handleSort("p95LatencyMs")}
                          >
                            p95
                            <SortIcon field="p95LatencyMs" />
                          </th>
                          <th
                            className="text-right py-2 pl-4 text-xs font-medium text-[var(--color-text-muted)] cursor-pointer hover:text-[var(--color-text)] select-none hidden xl:table-cell"
                            onClick={() => handleSort("maxLatencyMs")}
                          >
                            Max
                            <SortIcon field="maxLatencyMs" />
                          </th>
                        </tr>
                      </thead>
                      <tbody>
                        {sortedModels.map((m) => {
                          const cost = modelCost(m, costRates);
                          return (
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
                              <td
                                className="py-2.5 px-4 text-right font-mono hidden lg:table-cell"
                                title={
                                  isSubscriptionProvider(costRates, m.provider)
                                    ? `Included in the ${formatCurrency(
                                        costRates[m.provider]?.monthlyPrice ?? 0,
                                      )}/mo subscription for ${m.provider}`
                                    : isSelfHostedProvider(costRates, m.provider)
                                      ? `Included in the ${formatCurrency(
                                          costRates[m.provider]?.monthlyCost ?? 0,
                                        )}/mo self-hosted cost for ${m.provider}`
                                      : cost === null
                                        ? `No rate set for ${m.provider}`
                                        : undefined
                                }
                              >
                                {isFlatRateProvider(costRates, m.provider) ? (
                                  <Badge variant="outline" size="sm">
                                    incl.
                                  </Badge>
                                ) : cost !== null ? (
                                  <span className="text-[var(--color-text)]">
                                    {formatCurrency(cost)}
                                  </span>
                                ) : (
                                  <span className="text-[var(--color-text-muted)]">
                                    {"\u2014"}
                                  </span>
                                )}
                              </td>
                              <td className="py-2.5 px-4 text-right font-mono text-[var(--color-text)]">
                                {formatMs(m.avgLatencyMs)}
                              </td>
                              <td className="py-2.5 px-4 text-right font-mono text-[var(--color-text)] hidden xl:table-cell">
                                {m.p95LatencyMs !== undefined
                                  ? formatMs(m.p95LatencyMs)
                                  : "\u2014"}
                              </td>
                              <td className="py-2.5 pl-4 text-right font-mono hidden xl:table-cell">
                                {m.maxLatencyMs !== undefined ? (
                                  <span
                                    className={
                                      m.maxLatencyMs >= 10_000
                                        ? "text-[var(--color-status-error)]"
                                        : m.maxLatencyMs >= 5_000
                                          ? "text-[var(--color-status-warning)]"
                                          : "text-[var(--color-text)]"
                                    }
                                  >
                                    {formatMs(m.maxLatencyMs)}
                                  </span>
                                ) : (
                                  <span className="text-[var(--color-text-muted)]">
                                    {"\u2014"}
                                  </span>
                                )}
                              </td>
                            </tr>
                          );
                        })}
                      </tbody>
                    </table>
                  </div>
                  {missingRateCount > 0 && (
                    <p className="text-xs text-[var(--color-text-muted)] mt-3">
                      {missingRateCount} model{missingRateCount === 1 ? "" : "s"}{" "}
                      have no cost rate set —{" "}
                      <button
                        type="button"
                        onClick={() => setCostDialogOpen(true)}
                        className="text-[var(--color-primary)] hover:underline underline-offset-2 cursor-pointer"
                      >
                        open the Cost Calculator
                      </button>{" "}
                      to complete the estimates.
                    </p>
                  )}
                </>
              ) : (
                <p className="text-sm text-[var(--color-text-muted)] py-8 text-center">
                  No model usage data for this time range.
                </p>
              )}
            </Card>
          </motion.div>

          {/* Hourly Heatmap */}
          <motion.div
            variants={fadeUp}
            initial="initial"
            animate="animate"
            transition={{ delay: 0.27 }}
          >
            <Card padding="lg">
              <p className="text-xs font-medium text-[var(--color-text-muted)] uppercase tracking-wider mb-4">
                Requests by hour
              </p>
              <HourlyHeatmap
                rangeIs24h={timeRange === "24h"}
                provider={filterProvider || undefined}
                model={filterModel || undefined}
                autoRefreshMs={autoRefreshMs}
              />
            </Card>
          </motion.div>

          {/* Latency distribution + API-key attribution */}
          <div className="grid gap-6 lg:grid-cols-2 items-start">
            <motion.div
              variants={fadeUp}
              initial="initial"
              animate="animate"
              transition={{ delay: 0.28 }}
            >
              <LatencyBandsCard
                bands={latencyBands}
                loading={latencyBandsLoading}
              />
            </motion.div>
            <motion.div
              variants={fadeUp}
              initial="initial"
              animate="animate"
              transition={{ delay: 0.3 }}
            >
              <ApiKeysCard rows={apiKeyUsage} loading={apiKeyUsageLoading} />
            </motion.div>
          </div>

          {/* Per-Provider Breakdown + Budgets */}
          <div className="grid gap-6 lg:grid-cols-2 items-start">
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
                    <LazyProviderBreakdownChart
                      providers={providers}
                      filterProvider={filterProvider}
                      onProviderSelect={(provider) => {
                        setFilterProvider((prev) =>
                          prev === provider ? "" : provider,
                        );
                        setFilterModel("");
                      }}
                    />
                  </Suspense>
                ) : (
                  <p className="text-sm text-[var(--color-text-muted)] py-8 text-center">
                    No provider usage data for this time range.
                  </p>
                )}
              </Card>
            </motion.div>

            <motion.div
              variants={fadeUp}
              initial="initial"
              animate="animate"
              transition={{ delay: 0.33 }}
            >
              <BudgetsPanel
                monthProviders={monthProviders}
                loading={monthProvidersLoading}
                costRates={costRates}
              />
            </motion.div>
          </div>

          {/* Recent Requests Feed (drill-down target) */}
          <motion.div
            ref={recentSectionRef}
            variants={fadeUp}
            initial="initial"
            animate="animate"
            transition={{ delay: 0.36 }}
            className="scroll-mt-6"
          >
            <Card padding="lg">
              <div className="flex items-center justify-between gap-3 mb-4">
                <p className="text-xs font-medium text-[var(--color-text-muted)] uppercase tracking-wider">
                  Recent requests
                </p>
                <RetentionControl onPurged={refreshAll} />
              </div>
              <RecentRequestsTable
                filterParams={filterParams}
                customWindow={customWindow}
                autoRefreshMs={autoRefreshMs}
                onClearCustomWindow={() => setCustomWindow(null)}
              />
            </Card>
          </motion.div>
        </>
      )}

      {/* Cost Calculator Dialog */}
      <CostCalculatorDialog
        open={costDialogOpen}
        onOpenChange={setCostDialogOpen}
        providers={providers}
        totals={totals}
        catalog={providerCatalog}
        monthProviders={monthProviders}
      />
    </div>
  );
}

// ─── Small shared pieces ─────────────────────────────────────────

/** Colored % delta arrow comparing the current window to the previous one. */
function TrendDelta({
  current,
  previous,
}: {
  current: number;
  previous: number | null;
}) {
  if (previous === null) {
    return (
      <span className="inline-flex items-center gap-1 text-[10px] text-[var(--color-text-muted)]">
        <Minus className="size-3" /> no prior data
      </span>
    );
  }
  if (previous === 0 && current === 0) {
    return (
      <span className="inline-flex items-center gap-1 text-[10px] text-[var(--color-text-muted)]">
        <Minus className="size-3" /> flat
      </span>
    );
  }
  const pct = previous === 0 ? 100 : ((current - previous) / previous) * 100;
  if (Math.abs(pct) < 0.5) {
    return (
      <span className="inline-flex items-center gap-1 text-[10px] text-[var(--color-text-muted)]">
        <Minus className="size-3" /> flat
      </span>
    );
  }
  const up = pct > 0;
  return (
    <span
      className={`inline-flex items-center gap-1 text-[10px] font-medium ${
        up
          ? "text-[var(--color-status-running)]"
          : "text-[var(--color-status-error)]"
      }`}
    >
      {up ? (
        <TrendingUp className="size-3" />
      ) : (
        <TrendingDown className="size-3" />
      )}
      {up ? "+" : "\u2212"}
      {Math.abs(pct).toFixed(1)}% vs prev
    </span>
  );
}

/** Copyable proxy base-URL snippet used by the onboarding empty state. */
function ProxyUrlSnippet() {
  const [copied, setCopied] = useState(false);
  const url = `${window.location.origin}/v1`;

  function copy() {
    navigator.clipboard?.writeText(url).then(
      () => {
        setCopied(true);
        setTimeout(() => setCopied(false), 1500);
      },
      () => {
        // clipboard unavailable — the snippet is still visible/selectable
      },
    );
  }

  return (
    <button
      type="button"
      onClick={copy}
      className="inline-flex items-center gap-2 rounded-[var(--radius-lg)] border border-[var(--color-border)] bg-[var(--color-bg-muted)] px-3 py-2 font-mono text-xs text-[var(--color-text)] cursor-pointer hover:border-[var(--color-primary)] transition-colors"
      title="Copy to clipboard"
    >
      {url}
      {copied ? (
        <Check className="size-3.5 text-[var(--color-status-running)]" />
      ) : (
        <Copy className="size-3.5 text-[var(--color-text-muted)]" />
      )}
    </button>
  );
}

// ─── Cost Calculator Dialog ──────────────────────────────────────

interface CostCalculatorDialogProps {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  providers: ProviderUsageSummary[] | undefined;
  totals: UsageTotalsResponse | undefined;
  /** Provider catalog for the picker; undefined = fall back to free text. */
  catalog: import("./metrics-api").ProviderCatalogEntry[] | undefined;
  /** Calendar-month usage, for derived self-hosted $/1M rates. */
  monthProviders: ProviderUsageSummary[] | undefined;
}

interface RateRow {
  provider: string;
  mode: PricingMode;
  promptPer1M: string;
  completionPer1M: string;
  monthlyPrice: string;
  monthlyCost: string;
  /** True while this row's provider is a hand-typed custom name. */
  customProvider?: boolean;
}

/** Sentinel option value that switches a row into custom-entry mode. */
const CUSTOM_PROVIDER = "__custom__";

function CostCalculatorDialog({
  open,
  onOpenChange,
  providers,
  totals,
  catalog,
  monthProviders,
}: CostCalculatorDialogProps) {
  // Build rate rows from saved data + provider list when dialog opens
  const [rows, setRows] = useState<RateRow[]>([]);

  useEffect(() => {
    if (open) {
      const saved = loadCostRates();
      if (providers && providers.length > 0) {
        const known = new Set((catalog ?? []).map((c) => c.name));
        const newRows: RateRow[] = providers.map((p) => ({
          provider: p.provider,
          mode: saved[p.provider]?.mode ?? "per-token",
          promptPer1M: saved[p.provider]?.promptPer1M?.toString() ?? "",
          completionPer1M: saved[p.provider]?.completionPer1M?.toString() ?? "",
          monthlyPrice: saved[p.provider]?.monthlyPrice?.toString() ?? "",
          monthlyCost: saved[p.provider]?.monthlyCost?.toString() ?? "",
          // Providers not in the catalog start in custom-entry mode.
          customProvider: catalog !== undefined && !known.has(p.provider),
        }));
        setRows(newRows);
      } else {
        setRows([]);
      }
    }
  }, [open]);

  function updateRow(
    index: number,
    field: "promptPer1M" | "completionPer1M" | "monthlyPrice" | "monthlyCost",
    value: string,
  ) {
    setRows((prev) =>
      prev.map((r, i) => (i === index ? { ...r, [field]: value } : r)),
    );
  }

  function setRowMode(index: number, mode: PricingMode) {
    // Switching modes only changes which inputs are shown — the stored values
    // for the other modes are preserved so toggling back restores them.
    setRows((prev) =>
      prev.map((r, i) => (i === index ? { ...r, mode } : r)),
    );
  }

  function setRowProvider(index: number, name: string, custom: boolean) {
    setRows((prev) =>
      prev.map((r, i) =>
        i === index ? { ...r, provider: name, customProvider: custom } : r,
      ),
    );
  }

  function addRow() {
    setRows((prev) => [
      ...prev,
      {
        provider: "",
        mode: "per-token",
        promptPer1M: "",
        completionPer1M: "",
        monthlyPrice: "",
        monthlyCost: "",
      },
    ]);
  }

  function removeRow(index: number) {
    setRows((prev) => prev.filter((_, i) => i !== index));
  }

  function saveRates() {
    const newRates: CostRatesMap = {};
    for (const row of rows) {
      if (row.provider) {
        // All value sets are always written so switching a provider's mode
        // later never loses its previously entered rates.
        newRates[row.provider] = {
          mode: row.mode,
          promptPer1M: parseFloat(row.promptPer1M) || 0,
          completionPer1M: parseFloat(row.completionPer1M) || 0,
          monthlyPrice: parseFloat(row.monthlyPrice) || 0,
          monthlyCost: parseFloat(row.monthlyCost) || 0,
        };
      }
    }
    saveCostRates(newRates);
  }

  // Month-to-date tokens per provider — the denominator for derived
  // self-hosted $/1M rates (monthly cost ÷ month-to-date tokens × 1M).
  const monthTokensByProvider = useMemo(() => {
    const map = new Map<string, number>();
    for (const p of monthProviders ?? []) {
      map.set(p.provider, p.promptTokens + p.completionTokens);
    }
    return map;
  }, [monthProviders]);

  /**
   * Derived effective $/1M rate for a self-hosted row. Null when there's no
   * monthly cost entered or no usage this month to divide by.
   */
  function derivedSelfHostedRate(row: RateRow): number | null {
    if (row.mode !== "self-hosted") return null;
    const cost = parseFloat(row.monthlyCost) || 0;
    if (cost <= 0) return null;
    const tokens = monthTokensByProvider.get(row.provider.trim()) ?? 0;
    if (tokens <= 0) return null;
    return (cost / tokens) * 1_000_000;
  }

  // Calculate costs per provider from the current totals data
  // We use the totals across all providers and split by the provider breakdown data
  // to approximate per-provider token counts, then apply rates
  const costBreakdown = useMemo(() => {
    if (!providers || !totals) return [];
    return rows.map((row) => {
      const isSub = row.mode === "subscription";
      const isSelfHosted = row.mode === "self-hosted";
      const monthlyFee = parseFloat(row.monthlyPrice) || 0;
      const monthlyCost = parseFloat(row.monthlyCost) || 0;

      // Find provider data to get token counts
      const providerData = providers.find((p) => p.provider === row.provider);
      const promptTokens = providerData?.promptTokens ?? 0;
      const completionTokens = providerData?.completionTokens ?? 0;

      // Flat-rate modes aren't usage-based: the whole fixed cost lands in
      // Total Cost once, with no token-level attribution.
      const promptCost = isSub || isSelfHosted
        ? 0
        : ((promptTokens / 1_000_000) * (parseFloat(row.promptPer1M) || 0));
      const completionCost = isSub || isSelfHosted
        ? 0
        : (completionTokens / 1_000_000) * (parseFloat(row.completionPer1M) || 0);

      return {
        ...row,
        isSub,
        isSelfHosted,
        derivedRate: derivedSelfHostedRate(row),
        monthlyFee,
        monthlyCost,
        promptTokens,
        completionTokens,
        promptCost,
        completionCost,
        totalCost: isSub ? monthlyFee : isSelfHosted ? monthlyCost : promptCost + completionCost,
      };
    });
  }, [rows, providers, totals, monthTokensByProvider]);

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
          Set pricing for each provider — usage-based rates per 1M tokens, or a
          fixed monthly subscription. Rates are saved to your browser's local
          storage and applied against the current filter selection.
        </p>

        {/* Rate Entry Table */}
        <div className="space-y-2">
          {rows.map((row, i) => (
            <div
              key={i}
              className="flex flex-col sm:flex-row items-start sm:items-end gap-2 p-2.5 rounded-[var(--radius-lg)] border border-[var(--color-border-subtle)] bg-[var(--color-bg-muted)]/30"
            >
              {/* Provider picker: catalog select with custom-entry fallback */}
              <div className="flex flex-col gap-1 flex-1 min-w-[160px]">
                {i === 0 && (
                  <label className="text-xs font-medium text-[var(--color-text-muted)]">
                    Provider
                  </label>
                )}
                {!catalog || row.customProvider ? (
                  <div className="flex gap-1.5">
                    <input
                      type="text"
                      value={row.provider}
                      onChange={(e) => setRowProvider(i, e.target.value, true)}
                      placeholder="e.g. openai"
                      className="h-8 rounded-[var(--radius-lg)] border bg-[var(--color-bg-surface)] px-3 text-sm text-[var(--color-text)] border-[var(--color-border)] focus:outline-none focus:border-[var(--color-primary)] focus:ring-1 focus:ring-[var(--color-focus-ring)] transition-colors w-full min-w-0"
                    />
                    {catalog && (
                      <button
                        type="button"
                        onClick={() => setRowProvider(i, "", false)}
                        title="Pick from the provider list instead"
                        className="h-8 px-2 shrink-0 rounded-[var(--radius-lg)] border border-[var(--color-border)] text-xs text-[var(--color-text-muted)] hover:text-[var(--color-text)] hover:border-[var(--color-border-strong)] transition-colors cursor-pointer whitespace-nowrap"
                      >
                        List
                      </button>
                    )}
                  </div>
                ) : (
                  <select
                    value={row.provider === "" ? "" : row.provider}
                    onChange={(e) => {
                      if (e.target.value === CUSTOM_PROVIDER) {
                        setRowProvider(i, "", true);
                      } else {
                        setRowProvider(i, e.target.value, false);
                      }
                    }}
                    className="h-8 rounded-[var(--radius-lg)] border bg-[var(--color-bg-surface)]
                      px-3 pr-8 text-sm text-[var(--color-text)]
                      border-[var(--color-border)]
                      focus:outline-none focus:border-[var(--color-primary)] focus:ring-1 focus:ring-[var(--color-focus-ring)]
                      transition-colors duration-[var(--duration-fast)]
                      appearance-none cursor-pointer w-full
                      bg-[url('data:image/svg+xml;charset=utf-8,%3Csvg%20xmlns%3D%22http%3A%2F%2Fwww.w3.org%2F2000%2Fsvg%22%20width%3D%2216%22%20height%3D%2216%22%20viewBox%3D%220%200%2024%2024%22%20fill%3D%22none%22%20stroke%3D%22%236b7280%22%20stroke-width%3D%222%22%3E%3Cpath%20d%3D%22m6%209%206%206%206-6%22%2F%3E%3C/svg%3E')]
                      bg-[position:right_0.5rem_center] bg-no-repeat"
                  >
                    <option value="">Select provider…</option>
                    {/* Preserve a saved provider that isn't in the catalog */}
                    {row.provider !== "" &&
                      !catalog.some((c) => c.name === row.provider) && (
                        <option value={row.provider}>{row.provider}</option>
                      )}
                    {catalog.some((c) => c.kind === "cloud") && (
                      <optgroup label="Cloud providers">
                        {catalog
                          .filter((c) => c.kind === "cloud")
                          .map((c) => (
                            <option key={`cloud-${c.name}`} value={c.name}>
                              {c.name}
                            </option>
                          ))}
                      </optgroup>
                    )}
                    {catalog.some((c) => c.kind === "local") && (
                      <optgroup label="Self-hosted agents">
                        {catalog
                          .filter((c) => c.kind === "local")
                          .map((c) => (
                            <option key={`local-${c.name}`} value={c.name}>
                              {c.name}
                            </option>
                          ))}
                      </optgroup>
                    )}
                    <option value={CUSTOM_PROVIDER}>Custom…</option>
                  </select>
                )}
              </div>
              {/* Pricing mode toggle */}
              <div className="flex flex-col gap-1 shrink-0">
                {i === 0 && (
                  <span className="text-xs font-medium text-[var(--color-text-muted)]">
                    Pricing
                  </span>
                )}
                <div className="flex gap-0.5 bg-[var(--color-bg-muted)] rounded-[var(--radius-md)] p-0.5 h-8 items-center">
                  {(
                    [
                      ["per-token", "Per 1M tokens", "Usage-based pricing per 1M tokens"],
                      ["subscription", "Monthly", "Fixed monthly subscription"],
                      [
                        "self-hosted",
                        "Self-hosted",
                        "Self-hosted (power & hardware) — flat monthly cost",
                      ],
                    ] as const
                  ).map(([mode, label, hint]) => (
                    <button
                      key={mode}
                      type="button"
                      onClick={() => setRowMode(i, mode)}
                      title={hint}
                      className={`
                        px-2 py-1 text-xs font-medium rounded-[calc(var(--radius-md)-2px)]
                        transition-all duration-[var(--duration-fast)] whitespace-nowrap
                        ${
                          row.mode === mode
                            ? "bg-[var(--color-bg-surface)] text-[var(--color-text-heading)] shadow-sm cursor-pointer"
                            : "text-[var(--color-text-muted)] hover:text-[var(--color-text)] cursor-pointer"
                        }
                      `}
                    >
                      {label}
                    </button>
                  ))}
                </div>
              </div>
              {row.mode === "per-token" && (
                <>
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
                </>
              )}
              {row.mode === "subscription" && (
                <div className="flex flex-col gap-1 flex-1 min-w-[120px]">
                  {i === 0 && (
                    <label className="text-xs font-medium text-[var(--color-text-muted)]">
                      Monthly subscription price
                    </label>
                  )}
                  <input
                    type="number"
                    value={row.monthlyPrice}
                    onChange={(e) => updateRow(i, "monthlyPrice", e.target.value)}
                    placeholder="0.00"
                    min="0"
                    step="0.01"
                    className="h-8 rounded-[var(--radius-lg)] border bg-[var(--color-bg-surface)] px-3 text-sm text-[var(--color-text)] border-[var(--color-border)] focus:outline-none focus:border-[var(--color-primary)] focus:ring-1 focus:ring-[var(--color-focus-ring)] transition-colors w-full font-mono"
                  />
                </div>
              )}
              {row.mode === "self-hosted" && (
                <div className="flex flex-col gap-1 flex-1 min-w-[150px]">
                  {i === 0 && (
                    <label className="text-xs font-medium text-[var(--color-text-muted)]">
                      Monthly cost (power, hardware, etc.)
                    </label>
                  )}
                  <input
                    type="number"
                    value={row.monthlyCost}
                    onChange={(e) => updateRow(i, "monthlyCost", e.target.value)}
                    placeholder="0.00"
                    min="0"
                    step="0.01"
                    className="h-8 rounded-[var(--radius-lg)] border bg-[var(--color-bg-surface)] px-3 text-sm text-[var(--color-text)] border-[var(--color-border)] focus:outline-none focus:border-[var(--color-primary)] focus:ring-1 focus:ring-[var(--color-focus-ring)] transition-colors w-full font-mono"
                  />
                  {(() => {
                    const derived = derivedSelfHostedRate(row);
                    return derived !== null ? (
                      <span className="text-[10px] text-[var(--color-text-muted)]">
                        ≈ ${derived.toFixed(2)} per 1M tokens{" "}
                        <em className="not-italic text-[var(--color-primary)]">(derived)</em>
                      </span>
                    ) : (
                      <span className="text-[10px] text-[var(--color-text-muted)] italic">
                        — no usage this month yet
                      </span>
                    );
                  })()}
                </div>
              )}
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
                    <td className="py-2 pr-3 font-medium text-[var(--color-text-heading)] whitespace-nowrap">
                      {row.provider || <span className="text-[var(--color-text-muted)] italic">unnamed</span>}
                      {row.isSub && (
                        <Badge variant="outline" size="sm" className="ml-2">
                          monthly
                        </Badge>
                      )}
                      {row.isSelfHosted && (
                        <Badge variant="outline" size="sm" className="ml-2">
                          self-hosted
                        </Badge>
                      )}
                    </td>
                    {row.isSub ? (
                      <>
                        <td className="py-2 px-3 text-right text-[var(--color-text-muted)]" colSpan={2}>
                          incl.
                        </td>
                        <td className="py-2 px-3 text-right font-mono text-[var(--color-text-muted)] hidden sm:table-cell">
                          {formatTokens(row.promptTokens)}
                        </td>
                        <td className="py-2 px-3 text-right font-mono text-[var(--color-text-muted)] hidden sm:table-cell">
                          {formatTokens(row.completionTokens)}
                        </td>
                        <td className="py-2 px-3 text-right font-mono text-[var(--color-text)]" colSpan={2}>
                          {formatCurrency(row.monthlyFee)}/mo
                        </td>
                      </>
                    ) : row.isSelfHosted ? (
                      <>
                        {/* Derived effective rate sits next to cloud per-token
                            prices so the comparison is visible at a glance. */}
                        <td className="py-2 px-3 text-right text-xs text-[var(--color-text-muted)]" colSpan={2}>
                          {row.derivedRate !== null ? (
                            <>
                              ${row.derivedRate.toFixed(2)} /1M tok{" "}
                              <em className="not-italic text-[var(--color-primary)]">(derived)</em>
                            </>
                          ) : (
                            <span className="italic">
                              — no usage this month yet
                            </span>
                          )}
                        </td>
                        <td className="py-2 px-3 text-right font-mono text-[var(--color-text-muted)] hidden sm:table-cell">
                          {formatTokens(row.promptTokens)}
                        </td>
                        <td className="py-2 px-3 text-right font-mono text-[var(--color-text-muted)] hidden sm:table-cell">
                          {formatTokens(row.completionTokens)}
                        </td>
                        <td className="py-2 px-3 text-right font-mono text-[var(--color-text)]" colSpan={2}>
                          {formatCurrency(row.monthlyCost)}/mo
                        </td>
                      </>
                    ) : (
                      <>
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
                      </>
                    )}
                    <td className="py-2 pl-3 text-right font-mono font-medium text-[var(--color-text-heading)]">
                      {formatCurrency(row.totalCost)}
                      {row.isSub && (
                        <span className="text-[10px] font-normal text-[var(--color-text-muted)] ml-1">
                          /mo
                        </span>
                      )}
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
