import { Suspense, useState, useMemo, useCallback, useEffect, useRef, type ReactNode } from "react";
import { lazyWithRetry } from "../../lib/lazy-with-retry";
import { useTranslation } from "react-i18next";
import { useQuery } from "@tanstack/react-query";
import { Link } from "react-router-dom";
import { motion } from "motion/react";
import {
  Activity,
  Zap,
  ArrowUpRight,
  ArrowRight,
  Database,
  AlertTriangle,
  AlertCircle,
  Info,
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
  SlidersHorizontal,
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
  MetricsAnalyticsParams,
  MetricsTimeBucket,
  Model,
  ProviderCatalogEntry,
  ProviderUsageSummary,
} from "../../lib/api/types";
import type {
  TimeSeriesMetric,
  DrillDownWindow,
  CompareDataPoint,
} from "./charts";
import {
  loadCostRates,
  saveCostRates,
  rateAt,
  pricingModeAt,
  hasAnyRates,
  bucketCost,
  modelCost,
  sumBucketCosts,
  cacheSavings,
  flatCostTotals,
  dayInstant,
  inclusiveToExclusiveInstant,
  inclusiveToExclusiveDate,
  exclusiveToInclusiveDate,
  type RatePeriod,
  type CostBucket,
  type ModelCost,
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
import { FiltersModal } from "./filter-modal";

// Lazy-load the charts module (which imports recharts statically) to keep the
// main bundle lean. Do NOT lazy-load individual recharts components behind
// nested <Suspense>: recharts 3.x + React 19 hits an infinite setState loop in
// RechartsWrapper's ref callback when the chart subtree suspends/reappears
// (recharts#7463) — "Maximum update depth exceeded" on page load.
const LazyTokenUsageChart = lazyWithRetry(() =>
  import("./charts").then((m) => ({ default: m.TokenUsageChart })),
);
const LazyProviderBreakdownChart = lazyWithRetry(() =>
  import("./charts").then((m) => ({ default: m.ProviderBreakdownChart })),
);
const LazyMultiSeriesChart = lazyWithRetry(() =>
  import("./charts").then((m) => ({ default: m.MultiSeriesChart })),
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

const TIME_RANGE_OPTIONS: { value: TimeRange; labelKey: string }[] = [
  { value: "24h", labelKey: "timeRanges.24h" },
  { value: "7d", labelKey: "timeRanges.7d" },
  { value: "30d", labelKey: "timeRanges.30d" },
  { value: "all", labelKey: "timeRanges.all" },
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

const METRIC_OPTIONS: { value: TimeSeriesMetric; labelKey: string }[] = [
  { value: "tokens", labelKey: "metricOptions.tokens" },
  { value: "requests", labelKey: "metricOptions.requests" },
  { value: "latency", labelKey: "metricOptions.latency" },
  { value: "cached", labelKey: "metricOptions.cached" },
  { value: "cost", labelKey: "metricOptions.cost" },
];

const METRIC_TITLES_KEYS: Record<TimeSeriesMetric, string> = {
  tokens: "metricTitles.tokens",
  requests: "metricTitles.requests",
  latency: "metricTitles.latency",
  cached: "metricTitles.cached",
  cost: "metricTitles.cost",
};

// ─── Auto-refresh ────────────────────────────────────────────────

type AutoRefreshInterval = 0 | 10_000 | 30_000;

const AUTO_REFRESH_OPTIONS: { value: string; labelKey: string }[] = [
  { value: "0", labelKey: "autoRefresh.off" },
  { value: "10000", labelKey: "autoRefresh.10s" },
  { value: "30000", labelKey: "autoRefresh.30s" },
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
  const { t } = useTranslation("metrics");
  const [timeRange, setTimeRangeRaw] = useState<TimeRange>("7d");
  const [sortField, setSortField] = useState<SortField>("requestCount");
  const [sortDirection, setSortDirection] = useState<SortDirection>("desc");
  // Multi-select filters: ANY-of within a dimension, AND across dimensions.
  const [selectedProviders, setSelectedProviders] = useState<string[]>([]);
  const [selectedModels, setSelectedModels] = useState<string[]>([]);
  const [filterModalOpen, setFilterModalOpen] = useState(false);
  const [costDialogOpen, setCostDialogOpen] = useState(false);

  // Series toggle + auto-refresh + drill-down window
  const [seriesMetric, setSeriesMetric] = useState<TimeSeriesMetric>("tokens");
  const [compareDim, setCompareDim] = useState<"none" | "provider" | "model">("none");
  const [autoRefreshMs, setAutoRefreshMs] = useState<AutoRefreshInterval>(0);
  const [customWindow, setCustomWindow] = useState<DrillDownWindow | null>(null);
  const recentSectionRef = useRef<HTMLDivElement>(null);

  // Preset name input
  const [presetName, setPresetName] = useState("");
  const { presets, savePreset, deletePreset } = useMetricsPresets();

  // Cost rate periods are re-read whenever the calculator dialog closes so
  // edits made there flow into every estimate immediately.
  const [costRates, setCostRates] = useState<RatePeriod[]>(() => loadCostRates());
  useEffect(() => {
    if (!costDialogOpen) setCostRates(loadCostRates());
  }, [costDialogOpen]);

  const anyRates = useMemo(() => hasAnyRates(costRates), [costRates]);

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
      ...(selectedProviders.length > 0 ? { providers: selectedProviders } : {}),
      ...(selectedModels.length > 0 ? { models: selectedModels } : {}),
    }),
    [rangeParams, selectedProviders, selectedModels],
  ) satisfies MetricsAnalyticsParams;

  // Same duration, immediately before the selected window (period comparison).
  const prevFilterParams = useMemo(() => {
    const prev = getPreviousRangeParams(timeRange);
    if (!prev.from || !prev.to) return null;
    return {
      ...prev,
      ...(selectedProviders.length > 0 ? { providers: selectedProviders } : {}),
      ...(selectedModels.length > 0 ? { models: selectedModels } : {}),
    };
  }, [timeRange, selectedProviders, selectedModels]);

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
    queryFn: () => client.getMetricsTotals(filterParams),
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
      client.getMetricsSummary({
        ...filterParams,
        granularity: timeRange === "24h" ? "hour" : "day",
      }),
    refetchInterval,
  });

  // Provider+model grouped buckets. Serves both the comparison split and the
  // date-ranged cost engine (each bucket carries provider/model/at + tokens).
  // Only fetched when rates exist or a split dimension is active.
  const granularity = timeRange === "24h" ? "hour" : "day";
  const { data: groupedSummary, isFetching: groupedSummaryLoading } = useQuery({
    queryKey: ["metrics", "summary", "provider_model", filterParams, granularity],
    queryFn: () =>
      client.getMetricsSummary({
        ...filterParams,
        granularity,
        groupBy: "provider_model",
      }),
    enabled: anyRates || compareDim !== "none",
    refetchInterval,
  });

  const {
    data: models,
    isLoading: modelsLoading,
    error: modelsError,
    refetch: refetchModels,
  } = useQuery({
    queryKey: ["metrics", "models", filterParams],
    queryFn: () => client.getMetricsModels(filterParams),
    refetchInterval,
  });

  const {
    data: providers,
    isLoading: providersLoading,
    error: providersError,
    refetch: refetchProviders,
  } = useQuery({
    queryKey: ["metrics", "providers", rangeParams],
    queryFn: () => client.getMetricsProviders(rangeParams),
    refetchInterval,
  });

  // Latency distribution + API-key attribution for the current window.
  const {
    data: latencyBands,
    isLoading: latencyBandsLoading,
    isError: latencyBandsError,
    refetch: refetchLatencyBands,
  } = useQuery({
    queryKey: ["metrics", "latency-bands", filterParams],
    queryFn: () => client.getMetricsLatencyBands(filterParams),
    refetchInterval,
  });

  const {
    data: apiKeyUsage,
    isLoading: apiKeyUsageLoading,
    isError: apiKeyUsageError,
    refetch: refetchApiKeys,
  } = useQuery({
    // Endpoint is time-window scoped (no provider/model split server-side).
    queryKey: ["metrics", "api-keys", rangeParams],
    queryFn: () => client.getMetricsApiKeys(rangeParams),
    refetchInterval,
  });

  // Previous equivalent window, for % deltas on the summary cards.
  const { data: prevTotals } = useQuery({
    queryKey: ["metrics", "totals", "previous", prevFilterParams],
    queryFn: () => client.getMetricsTotals(prevFilterParams!),
    enabled: prevFilterParams !== null,
    refetchInterval,
  });

  // Month-to-date usage per provider, for budget progress bars.
  const { data: monthProviders, isLoading: monthProvidersLoading } = useQuery({
    queryKey: ["metrics", "providers", "month", monthParams],
    queryFn: () => client.getMetricsProviders(monthParams),
    refetchInterval,
  });

  // Fetch the full unfiltered models list once to populate the filter modal.
  // Static reference data: never auto-refreshed (manual refresh / remount
  // suffices); staleTime keeps react-query from refetching on every focus.
  const { data: allModels } = useQuery({
    queryKey: ["metrics", "models", "all"],
    queryFn: () => client.getMetricsModels(),
    staleTime: 5 * 60 * 1000,
    refetchInterval: false,
  });

  // Provider catalog for the filter modal + cost calculator's provider picker.
  const { data: providerCatalog } = useQuery({
    queryKey: ["metrics", "provider-catalog"],
    queryFn: () => client.getMetricsProviderCatalog(),
    staleTime: 5 * 60 * 1000,
    refetchInterval: false,
  });

  // Static UI preferences — same exclusion from auto-refresh.
  const { data: settings } = useQuery({
    queryKey: ["settings"],
    queryFn: () => client.getSettings(),
    staleTime: 5 * 60 * 1000,
    refetchInterval: false,
  });

  // Model registry — for resolving user-managed display names and runtime names.
  const { data: registryModels } = useQuery({
    queryKey: ["models"],
    queryFn: () => client.listModels(),
    staleTime: 5 * 60 * 1000,
    refetchInterval: false,
  });

  // Lookup map: model name → {displayName, sourceRuntimeName}
  const registryByName = useMemo(() => {
    const map = new Map<string, Pick<Model, "displayName" | "sourceRuntimeName">>();
    for (const m of registryModels ?? []) {
      map.set(m.name, m);
    }
    return map;
  }, [registryModels]);

  // ── Derived: filter-modal option lists ──────────────────────

  // Cloud provider names known from the model registry (models whose origin is
  // "cloud" expose their provider via `providerName`). Local usage providers
  // are now agent/host names, so anything not confirmed as a known cloud
  // provider is treated as an agent — an agent whose runtime/model was deleted
  // must not be mislabeled as cloud.
  const knownCloudProviderNames = useMemo(() => {
    const names = new Set<string>();
    for (const m of registryModels ?? []) {
      if (m.origin === "cloud" && m.providerName) names.add(m.providerName);
    }
    return names;
  }, [registryModels]);

  const providerOptions = useMemo<ProviderCatalogEntry[]>(() => {
    if (providerCatalog) return providerCatalog;
    // Catalog not loaded yet — fall back to providers seen in usage.
    if (!allModels) return [];
    return [...new Set(allModels.map((m) => m.provider))]
      .sort()
      .map((name) => ({
        name,
        kind: knownCloudProviderNames.has(name)
          ? ("cloud" as const)
          : ("agent" as const),
      }));
  }, [providerCatalog, allModels, knownCloudProviderNames]);

  const modelOptions = useMemo(() => {
    if (!allModels) return [];
    const byModel = new Map<string, Set<string>>();
    for (const m of allModels) {
      const set = byModel.get(m.model);
      if (set) set.add(m.provider);
      else byModel.set(m.model, new Set([m.provider]));
    }
    return [...byModel.entries()]
      .sort(([a], [b]) => a.localeCompare(b))
      .map(([model, providerSet]) => ({
        model,
        providers: [...providerSet].sort(),
      }));
  }, [allModels]);

  const modelLabel = useCallback(
    (model: string) => {
      const reg = registryByName.get(model);
      return formatModelName(
        model,
        allModels?.find((m) => m.model === model)?.provider ?? "",
        settings?.hideOriginPrefix ?? false,
        settings?.agentDisplayNames ?? {},
        reg?.sourceRuntimeName ?? undefined,
        reg?.displayName ?? undefined,
      );
    },
    [allModels, settings, registryByName],
  );

  const hasActiveFilters =
    selectedProviders.length > 0 || selectedModels.length > 0;
  const activeFilterCount = selectedProviders.length + selectedModels.length;

  const clearFilters = useCallback(() => {
    setSelectedProviders([]);
    setSelectedModels([]);
  }, []);

  /** Toggle one provider in the selection (chart bar / chip clicks). */
  const toggleProvider = useCallback((provider: string) => {
    setSelectedProviders((prev) =>
      prev.includes(provider)
        ? prev.filter((p) => p !== provider)
        : [...prev, provider],
    );
  }, []);

  // ── Cost estimates ───────────────────────────────────────────

  // Page-level bucket timeline: one CostBucket per grouped provider+model
  // bucket. Everything cost-related derives from this single array.
  const costTimeline = useMemo<CostBucket[]>(() => {
    if (!groupedSummary) return [];
    return groupedSummary.map((b) => ({
      provider: b.provider ?? b.group ?? "",
      model: b.model ?? "",
      at: b.bucketStart,
      promptTokens: b.promptTokens,
      completionTokens: b.completionTokens,
      cachedTokens: b.cachedTokens,
    }));
  }, [groupedSummary]);

  // Reused by the per-model table/sort and the comparison table so each
  // (provider, model) timeline is built once.
  const bucketsByProviderModel = useMemo(() => {
    const map = new Map<string, CostBucket[]>();
    for (const b of costTimeline) {
      const key = `${b.provider}|${b.model}`;
      const list = map.get(key);
      if (list) list.push(b);
      else map.set(key, [b]);
    }
    return map;
  }, [costTimeline]);

  const modelCostByKey = useMemo(() => {
    const map = new Map<string, ModelCost>();
    for (const [key, buckets] of bucketsByProviderModel) {
      map.set(key, modelCost(buckets, costRates));
    }
    return map;
  }, [bucketsByProviderModel, costRates]);

  /** Bucket-start → summed per-token cost, for the cost time series. */
  const costByBucket = useMemo(() => {
    const map = new Map<string, number>();
    for (const b of costTimeline) {
      const c = bucketCost(b, costRates);
      if (c !== null) map.set(b.at, (map.get(b.at) ?? 0) + c);
    }
    return map;
  }, [costTimeline, costRates]);

  /** Active calendar months ("YYYY-MM", UTC) per provider, for flat proration. */
  const activeMonthsByProvider = useMemo(() => {
    const map = new Map<string, Set<string>>();
    for (const b of costTimeline) {
      const d = new Date(b.at);
      const month = `${d.getUTCFullYear()}-${String(d.getUTCMonth() + 1).padStart(2, "0")}`;
      let set = map.get(b.provider);
      if (!set) {
        set = new Set();
        map.set(b.provider, set);
      }
      set.add(month);
    }
    return map;
  }, [costTimeline]);

  // Effective window bounds: prefer the server-reported totals window and fall
  // back to the summary extent (needed for flat proration on "all time").
  const windowBounds = useMemo<{ from: Date; to: Date }>(() => {
    if (totals) return { from: new Date(totals.from), to: new Date(totals.to) };
    const starts = (summary ?? []).map((b) => b.bucketStart).sort();
    const ends = (summary ?? []).map((b) => b.bucketEnd).sort();
    return {
      from: new Date(starts[0] ?? 0),
      to: new Date(ends.length > 0 ? ends[ends.length - 1]! : 0),
    };
  }, [totals, summary]);

  const { estCostTotal } = useMemo(() => {
    if (costTimeline.length === 0) return { estCostTotal: null as number | null };
    return { estCostTotal: sumBucketCosts(costTimeline, costRates).cost };
  }, [costTimeline, costRates]);

  const flatTotals = useMemo(
    () => flatCostTotals(costRates, activeMonthsByProvider, windowBounds),
    [costRates, activeMonthsByProvider, windowBounds],
  );

  const missingRateCount = useMemo(() => {
    if (!models) return 0;
    let missing = 0;
    for (const m of models) {
      const mc = modelCostByKey.get(`${m.provider}|${m.model}`);
      if (!mc) missing += 1;
      else if (mc.basis === "per-token" && mc.cost === null) missing += 1;
    }
    return missing;
  }, [models, modelCostByKey]);

  const savingsEstimate = useMemo(
    () =>
      totals
        ? totals.totalCachedTokens > 0
          ? cacheSavings(costTimeline, costRates)
          : 0
        : null,
    [totals, costTimeline, costRates],
  );

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

  // Stable so the memoized ProviderBreakdownChart skips re-renders.
  const handleProviderSelect = useCallback(
    (provider: string) => {
      toggleProvider(provider);
    },
    [toggleProvider],
  );

  // ── Comparison series pivot ─────────────────────────────────

  /** Cap comparison lines so the chart stays legible; ranked by requests. */
  const MAX_COMPARE_SERIES = 8;

  const compare = useMemo(() => {
    if (compareDim === "none" || !groupedSummary) return null;

    // Grouped buckets are provider+model; derive the active split's key.
    const seriesKeyOf = (b: MetricsTimeBucket) =>
      compareDim === "provider"
        ? (b.provider ?? b.group ?? "")
        : (b.model ?? b.group ?? "");

    // Rank groups by total request count in the window; keep the top N.
    const totalsByGroup = new Map<string, number>();
    for (const b of groupedSummary) {
      const key = seriesKeyOf(b);
      if (!key) continue;
      totalsByGroup.set(key, (totalsByGroup.get(key) ?? 0) + b.requestCount);
    }
    const topGroups = [...totalsByGroup.entries()]
      .sort((a, b) => b[1] - a[1])
      .slice(0, MAX_COMPARE_SERIES)
      .map(([g]) => g);
    if (topGroups.length === 0) return null;
    const topSet = new Set(topGroups);

    interface Cell {
      requestCount: number;
      promptTokens: number;
      completionTokens: number;
      cachedTokens: number;
      latencyWeighted: number;
      cost: number;
    }
    const byBucket = new Map<
      string,
      { row: CompareDataPoint; cells: Map<string, Cell> }
    >();
    const order: string[] = [];

    for (const b of [...groupedSummary].sort((a, b2) =>
      a.bucketStart.localeCompare(b2.bucketStart),
    )) {
      const key = seriesKeyOf(b);
      if (!key || !topSet.has(key)) continue;
      let entry = byBucket.get(b.bucketStart);
      if (!entry) {
        entry = {
          row: {
            bucketStart: b.bucketStart,
            bucketEnd: b.bucketEnd,
            time: new Date(b.bucketStart).toLocaleDateString(undefined, {
              month: "short",
              day: "numeric",
              hour: granularity === "hour" ? "2-digit" : undefined,
            }),
          },
          cells: new Map(),
        };
        byBucket.set(b.bucketStart, entry);
        order.push(b.bucketStart);
      }
      let cell = entry.cells.get(key);
      if (!cell) {
        cell = {
          requestCount: 0,
          promptTokens: 0,
          completionTokens: 0,
          cachedTokens: 0,
          latencyWeighted: 0,
          cost: 0,
        };
        entry.cells.set(key, cell);
      }
      cell.requestCount += b.requestCount;
      cell.promptTokens += b.promptTokens;
      cell.completionTokens += b.completionTokens;
      cell.cachedTokens += b.cachedTokens;
      cell.latencyWeighted += b.avgLatencyMs * b.requestCount;
      // Cost is resolved at each bucket's timestamp with the date-ranged
      // engine, so a mid-window rate change splits across the series.
      cell.cost +=
        bucketCost(
          {
            provider: b.provider ?? b.group ?? "",
            model: b.model ?? "",
            at: b.bucketStart,
            promptTokens: b.promptTokens,
            completionTokens: b.completionTokens,
            cachedTokens: b.cachedTokens,
          },
          costRates,
        ) ?? 0;
    }

    const metricValue = (cell: Cell | undefined): number => {
      if (!cell) return 0;
      switch (seriesMetric) {
        case "tokens":
          return cell.promptTokens + cell.completionTokens;
        case "requests":
          return cell.requestCount;
        case "latency":
          return cell.requestCount > 0
            ? Math.round(cell.latencyWeighted / cell.requestCount)
            : 0;
        case "cached":
          return cell.cachedTokens;
        case "cost":
          return cell.cost;
      }
    };

    const data = order.map((bucketStart) => {
      const entry = byBucket.get(bucketStart)!;
      for (const g of topGroups) entry.row[g] = metricValue(entry.cells.get(g));
      return entry.row;
    });

    return {
      data,
      series: topGroups.map((g) => ({
        key: g,
        label: compareDim === "model" ? modelLabel(g) : g,
      })),
    };
  }, [compareDim, groupedSummary, costRates, seriesMetric, granularity, modelLabel]);

  // ── Comparison table (aggregated per entity from model rows) ──

  const compareRows = useMemo(() => {
    if (compareDim === "none" || !models || models.length === 0) return [];
    const groups = new Map<
      string,
      {
        name: string;
        providers: Set<string>;
        requestCount: number;
        promptTokens: number;
        completionTokens: number;
        cachedTokens: number;
        latencyWeightedSum: number;
        estCost: number;
        hasMissingRate: boolean;
      }
    >();
    for (const m of models) {
      const key = compareDim === "provider" ? m.provider : m.model;
      let row = groups.get(key);
      if (!row) {
        row = {
          name: key,
          providers: new Set(),
          requestCount: 0,
          promptTokens: 0,
          completionTokens: 0,
          cachedTokens: 0,
          latencyWeightedSum: 0,
          estCost: 0,
          hasMissingRate: false,
        };
        groups.set(key, row);
      }
      row.providers.add(m.provider);
      row.requestCount += m.requestCount;
      row.promptTokens += m.promptTokens;
      row.completionTokens += m.completionTokens;
      row.cachedTokens += m.cachedTokens;
      row.latencyWeightedSum += m.avgLatencyMs * m.requestCount;
    }
    return [...groups.values()]
      .map((row) => {
        // Cost for the whole group, resolved from the page timeline so each
        // bucket is priced at its own timestamp.
        const groupBuckets = costTimeline.filter((b) =>
          compareDim === "provider" ? b.provider === row.name : b.model === row.name,
        );
        const mc = modelCost(groupBuckets, costRates);
        // A group that is flat OR spans a flat↔per-token transition cannot be
        // shown as a single comparable dollar figure.
        if (mc.basis !== "per-token" || mc.cost === null) row.hasMissingRate = true;
        else row.estCost += mc.cost;
        return {
          ...row,
          providers: [...row.providers],
          avgLatencyMs:
            row.requestCount > 0 ? row.latencyWeightedSum / row.requestCount : 0,
        };
      })
      .sort((a, b) => b.requestCount - a.requestCount);
  }, [compareDim, models, costRates, costTimeline]);

  // ── Presets ──────────────────────────────────────────────────

  const applyPreset = useCallback(
    (preset: { providers: string[]; models: string[]; range: string }) => {
      const validRange = TIME_RANGE_OPTIONS.some((o) => o.value === preset.range)
        ? (preset.range as TimeRange)
        : "7d";
      setSelectedProviders(preset.providers);
      setSelectedModels(preset.models);
      setTimeRange(validRange);
    },
    [setTimeRange],
  );

  const handleSavePreset = useCallback(() => {
    const name =
      presetName.trim() ||
      [
        selectedProviders.length > 0 ? selectedProviders.join("+") : t("allProvidersLabel"),
        selectedModels.length > 0
          ? selectedModels.map((m) => modelLabel(m)).join("+")
          : t("allModelsLabel"),
        timeRange,
      ].join(" · ");
    savePreset({
      name,
      providers: selectedProviders,
      models: selectedModels,
      range: timeRange,
    });
    setPresetName("");
  }, [presetName, selectedProviders, selectedModels, timeRange, savePreset, modelLabel]);

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
          const costOf = (m: (typeof models)[number]) => {
            const mc = modelCostByKey.get(`${m.provider}|${m.model}`);
            if (!mc || mc.basis !== "per-token") return Number.POSITIVE_INFINITY;
            return mc.cost ?? -1;
          };
          const costA = costOf(a);
          const costB = costOf(b);
          return dir * (costA - costB);
        }
        default:
          return 0;
      }
    });
    return sorted;
  }, [models, sortField, sortDirection, modelCostByKey]);

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
      t("table.model"),
      t("table.provider"),
      t("table.requests"),
      t("table.promptTokens"),
      t("table.completionTokens"),
      t("table.cachedTokens"),
      t("table.cacheHitPercent"),
      t("csvHeaders.avgLatencyMs"),
      t("csvHeaders.estCostUsd"),
    ];
    const lines = sortedModels.map((m) => {
      const mc = modelCostByKey.get(`${m.provider}|${m.model}`);
      return [
        escape(modelLabel(m.model)),
        escape(m.provider),
        m.requestCount,
        m.promptTokens,
        m.completionTokens,
        m.cachedTokens,
        m.promptTokens > 0
          ? ((m.cachedTokens / m.promptTokens) * 100).toFixed(2)
          : "",
        Math.round(m.avgLatencyMs),
        mc?.basis === "flat"
          ? t("costCalcSection.incl")
          : mc?.basis === "mixed"
            ? t("costCalcSection.mixed")
            : (mc?.cost ?? "").toString(),
      ].join(",");
    });
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
  }, [sortedModels, modelCostByKey, modelLabel]);

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
          title={t("failedToLoad")}
          description={error.message}
          action={
            <Button variant="secondary" size="sm" onClick={refreshAll}>
              <RefreshCw className="size-3.5" />
              {t("retry", { ns: "common" })}
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
      ? t("summary.streaming", { count: totals.totalStreamingRequests.toLocaleString() })
      : undefined;

  const summaryCards: StatCard[] = totals
    ? [
        {
          label: t("summary.totalRequests"),
          value: totals.totalRequests.toLocaleString(),
          icon: Activity,
          color: "text-[var(--color-primary)]",
          sub: streamingSub,
          delta: prevTotals
            ? { current: totals.totalRequests, previous: prevTotals.totalRequests }
            : undefined,
        },
        {
          label: t("summary.promptTokens"),
          value: formatTokens(totals.totalPromptTokens),
          icon: Zap,
          color: "text-[var(--color-status-running)]",
          delta: prevTotals
            ? { current: totals.totalPromptTokens, previous: prevTotals.totalPromptTokens }
            : undefined,
        },
        {
          label: t("summary.completionTokens"),
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
          label: t("summary.cacheHitRate"),
          value: hitRate !== null ? `${hitRate.toFixed(1)}%` : "\u2014",
          icon: Database,
          color: "text-[var(--color-status-warning)]",
          delta:
            prevTotals && hitRate !== null
              ? { current: hitRate, previous: prevHitRate }
              : undefined,
        },
        {
          label: t("summary.estCost"),
          value: anyRates
            ? estCostTotal === null
              ? "\u2014"
              : formatCurrency(estCostTotal)
            : undefined,
          icon: Calculator,
          color: "text-[var(--color-status-error)]",
          sub:
            anyRates && (flatTotals.subscriptions > 0 || flatTotals.selfHosted > 0) ? (
              <>
                {flatTotals.subscriptions > 0 && (
                  <span className="block">
                    + {formatCurrency(flatTotals.subscriptions)} {t("summary.subscriptionsLabel")}
                  </span>
                )}
                {flatTotals.selfHosted > 0 && (
                  <span className="block">
                    + {formatCurrency(flatTotals.selfHosted)} {t("summary.selfHostedLabel")}
                  </span>
                )}
              </>
            ) : undefined,
        },
        {
          label: t("summary.cacheSavings"),
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
          {t("title")}
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
              {t(opt.labelKey)}
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
          <Button
            variant={hasActiveFilters ? "primary" : "secondary"}
            size="sm"
            onClick={() => setFilterModalOpen(true)}
            className="gap-1.5 shrink-0"
            title={t("filterBar.chooseProvidersModels")}
          >
            <SlidersHorizontal className="size-3.5" />
            {t("filterBar.filters")}
            {activeFilterCount > 0 && (
              <span className="inline-flex min-w-4 justify-center">
                <Badge variant={hasActiveFilters ? "outline" : "info"} size="sm">
                  {activeFilterCount}
                </Badge>
              </span>
            )}
          </Button>
          {/* Active-filter summary chips — each removable in place */}
          {hasActiveFilters ? (
            <div className="flex flex-wrap items-center gap-1.5 min-w-0">
              {selectedProviders.map((provider) => (
                <FilterChip
                  key={`p-${provider}`}
                  dimension={t("table.provider")}
                  label={provider}
                  onRemove={() => toggleProvider(provider)}
                />
              ))}
              {selectedModels.map((model) => (
                <FilterChip
                  key={`m-${model}`}
                  dimension={t("table.model")}
                  label={modelLabel(model)}
                  onRemove={() =>
                    setSelectedModels((prev) => prev.filter((m) => m !== model))
                  }
                />
              ))}
              <Button
                variant="ghost"
                size="sm"
                onClick={clearFilters}
                className="gap-1 text-[var(--color-text-muted)] shrink-0"
              >
                <X className="size-3" />
                {t("filterBar.clearAll")}
              </Button>
            </div>
          ) : (
            <p className="text-xs text-[var(--color-text-muted)] self-center min-w-0 truncate">
              {t("filterBar.allProvidersModels")}
            </p>
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
              placeholder={t("presets.namePlaceholder")}
              aria-label={t("presets.presetName")}
              className="h-8 w-36 rounded-[var(--radius-lg)] border bg-[var(--color-bg-surface)] px-2.5 text-xs text-[var(--color-text)] border-[var(--color-border)] placeholder:text-[var(--color-text-muted)] focus:outline-none focus:border-[var(--color-primary)] focus:ring-1 focus:ring-[var(--color-focus-ring)] transition-colors"
            />
            <Button
              variant="secondary"
              size="sm"
              onClick={handleSavePreset}
              className="gap-1 shrink-0"
              title={t("presets.savePreset")}
            >
              <BookmarkPlus className="size-3.5" />
              {t("filterBar.save")}
            </Button>
          </div>
          <Button
            variant="secondary"
            size="sm"
            onClick={() => setCostDialogOpen(true)}
            className="gap-1.5 shrink-0"
          >
            <Calculator className="size-3.5" />
            {t("costCalculator")}
          </Button>
        </div>

        {/* Saved preset chips */}
        {presets.length > 0 && (
          <div className="flex flex-wrap items-center gap-1.5 mt-2 px-1">
            <span className="text-xs text-[var(--color-text-muted)] mr-0.5 inline-flex items-center gap-1">
              <Bookmark className="size-3" />
              {t("filterBar.presets")}
            </span>
            {presets.map((p) => (
              <span
                key={p.name}
                className="inline-flex items-center rounded-full border border-[var(--color-border)] bg-[var(--color-bg-surface)] overflow-hidden transition-colors hover:border-[var(--color-primary)]"
              >
                <button
                  type="button"
                  onClick={() => applyPreset(p)}
                  title={`${p.providers.length > 0 ? p.providers.join("+") : t("allProvidersLabel")} · ${p.models.length > 0 ? p.models.map(modelLabel).join("+") : t("allModelsLabel")} · ${p.range}`}
                  className="pl-2.5 pr-1.5 py-1 text-xs text-[var(--color-text)] cursor-pointer max-w-[220px] truncate"
                >
                  {p.name}
                </button>
                <button
                  type="button"
                  onClick={() => deletePreset(p.name)}
                  aria-label={t("presets.deletePreset", { name: p.name })}
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
              title={t("noUsage")}
              description={t("noUsageDesc")}
              action={
                <div className="flex flex-col items-center gap-3">
                  <ProxyUrlSnippet />
                  <p className="text-xs text-[var(--color-text-muted)] max-w-sm">
                    {t("onboarding.pointClient")}{" "}
                    ({t("onboarding.forExample")}{" "}
                    <code className="font-mono">base_url="{window.location.origin}/v1"</code>)
                    {" "}{t("onboarding.andSendRequest")}
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
              aria-label={t("aria.autoRefreshInterval")}
              options={AUTO_REFRESH_OPTIONS.map((o) => ({ ...o, label: t(o.labelKey) }))}
              value={String(autoRefreshMs)}
              onChange={(e) =>
                setAutoRefreshMs(Number(e.target.value) as AutoRefreshInterval)
              }
              className="w-[130px]"
            />
            <Tooltip content={t("refreshMetrics")} side="bottom">
              <Button
                variant="secondary"
                size="sm"
                onClick={refreshAll}
                className="gap-1.5"
                title={t("refreshMetrics")}
              >
                <RefreshCw className="size-3.5" />
                {t("refresh")}
              </Button>
            </Tooltip>
            <Button
              variant="secondary"
              size="sm"
              onClick={exportCsv}
              disabled={sortedModels.length === 0}
              className="gap-1.5"
              title={t("downloadBreakdown")}
            >
              <Download className="size-3.5" />
              {t("downloadCsv")}
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
                      {t("summary.setRates")}
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

          {/* Time-Series Chart with metric + comparison toggles */}
          <motion.div
            variants={fadeUp}
            initial="initial"
            animate="animate"
            transition={{ delay: 0.2 }}
          >
            <Card padding="lg">
              <div className="flex flex-col sm:flex-row sm:items-center sm:justify-between gap-3 mb-4">
                <p className="text-xs font-medium text-[var(--color-text-muted)] uppercase tracking-wider">
                  {compareDim === "none"
                    ? t(METRIC_TITLES_KEYS[seriesMetric])
                    : `${t(METRIC_TITLES_KEYS[seriesMetric])}${t("byEntity", { entity: compareDim })}`}
                </p>
                <div className="flex flex-wrap items-center gap-3">
                  <span className="text-[10px] text-[var(--color-text-muted)] hidden lg:inline">
                    {t("clickPoint")}
                  </span>
                  {/* Comparison dimension */}
                  <div
                    className="flex gap-1 bg-[var(--color-bg-muted)] rounded-[var(--radius-lg)] p-0.5"
                    role="group"
                    aria-label={t("aria.comparisonMode")}
                  >
                    {(
                      [
                        ["none", t("comparisonModes.combined")],
                        ["provider", t("comparisonModes.byProvider")],
                        ["model", t("comparisonModes.byModel")],
                      ] as const
                    ).map(([value, label]) => (
                      <button
                        key={value}
                        type="button"
                        onClick={() => setCompareDim(value)}
                        className={`
                          px-2.5 py-1 text-xs font-medium rounded-[var(--radius-md)]
                          transition-all duration-[var(--duration-fast)]
                          ${
                            compareDim === value
                              ? "bg-[var(--color-bg-surface)] text-[var(--color-text-heading)] shadow-sm cursor-pointer"
                              : "text-[var(--color-text-muted)] hover:text-[var(--color-text)] cursor-pointer"
                          }
                        `}
                      >
                        {label}
                      </button>
                    ))}
                  </div>
                  <div className="flex gap-1 bg-[var(--color-bg-muted)] rounded-[var(--radius-lg)] p-0.5">
                    {METRIC_OPTIONS.map((opt) => {
                      const disabled = opt.value === "cost" && !anyRates;
                      return (
                        <button
                          key={opt.value}
                          type="button"
                          disabled={disabled}
                          onClick={() => setSeriesMetric(opt.value)}
                          title={
                            disabled
                              ? t("setCostRates")
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
                          {t(opt.labelKey)}
                        </button>
                      );
                    })}
                  </div>
                </div>
              </div>
              {compareDim !== "none" ? (
                compare && compare.series.length > 0 ? (
                  <Suspense fallback={<ChartSkeleton />}>
                    <LazyMultiSeriesChart
                      data={compare.data}
                      series={compare.series}
                      metric={seriesMetric}
                      onPointClick={handlePointClick}
                    />
                  </Suspense>
                ) : (
                  <p className="text-sm text-[var(--color-text-muted)] py-8 text-center">
                    {groupedSummaryLoading
                      ? t("chart.loadingComparison")
                      : t("chart.noUsageData")}
                  </p>
                )
              ) : summary && summary.length > 0 ? (
                <Suspense fallback={<ChartSkeleton />}>
                  <LazyTokenUsageChart
                    summary={summary}
                    metric={seriesMetric}
                    costByBucket={costByBucket}
                    onPointClick={handlePointClick}
                  />
                </Suspense>
              ) : (
                <p className="text-sm text-[var(--color-text-muted)] py-8 text-center">
                  {t("chart.noUsageData")}
                </p>
              )}
              {seriesMetric === "cost" &&
                (flatTotals.subscriptions > 0 || flatTotals.selfHosted > 0) && (
                  <p className="text-[10px] text-[var(--color-text-muted)] mt-2">
                    {t("chart.excludes")}{" "}
                    {[
                      flatTotals.subscriptions > 0
                        ? t("chart.excludesSubscriptions", { amount: formatCurrency(flatTotals.subscriptions) })
                        : null,
                      flatTotals.selfHosted > 0
                        ? t("chart.excludesSelfHosted", { amount: formatCurrency(flatTotals.selfHosted) })
                        : null,
                    ]
                      .filter(Boolean)
                      .join(` ${t("chart.and")} `)}
                    {" — "}{t("chart.notTimeDistributed")}
                  </p>
                )}
            </Card>
          </motion.div>

          {/* Entity Comparison Table (visible while a split is active) */}
          {compareDim !== "none" && compareRows.length > 0 && (
            <motion.div
              variants={fadeUp}
              initial="initial"
              animate="animate"
              transition={{ delay: 0.22 }}
            >
              <Card padding="lg">
                <p className="text-xs font-medium text-[var(--color-text-muted)] uppercase tracking-wider mb-4">
                  {t("comparisonTable.providerComparison", { dim: compareDim === "provider" ? t("comparisonTable.provider") : t("comparisonTable.model") })}
                </p>
                <div className="overflow-x-auto">
                  <table className="w-full text-sm">
                    <thead>
                      <tr className="border-b border-[var(--color-border)]">
                        <th className="text-left py-2 pr-4 text-xs font-medium text-[var(--color-text-muted)]">
                          {compareDim === "provider" ? t("comparisonTable.provider") : t("comparisonTable.model")}
                        </th>
                        <th className="text-right py-2 px-4 text-xs font-medium text-[var(--color-text-muted)]">
                          {t("table.requests")}
                        </th>
                        <th className="text-right py-2 px-4 text-xs font-medium text-[var(--color-text-muted)] hidden sm:table-cell">
                          {t("table.promptTokens")}
                        </th>
                        <th className="text-right py-2 px-4 text-xs font-medium text-[var(--color-text-muted)] hidden sm:table-cell">
                          {t("table.completionTokens")}
                        </th>
                        <th className="text-right py-2 px-4 text-xs font-medium text-[var(--color-text-muted)]">
                          {t("table.cacheHitPercent")}
                        </th>
                        <th className="text-right py-2 px-4 text-xs font-medium text-[var(--color-text-muted)] hidden md:table-cell">
                          {t("table.avgLatency")}
                        </th>
                        {anyRates && (
                          <th className="text-right py-2 pl-4 text-xs font-medium text-[var(--color-text-muted)]">
                            {t("table.estCost")}
                          </th>
                        )}
                      </tr>
                    </thead>
                    <tbody>
                      {compareRows.map((row) => {
                        const hitPct =
                          row.promptTokens > 0
                            ? (row.cachedTokens / row.promptTokens) * 100
                            : null;
                        return (
                          <tr
                            key={row.name}
                            className="border-b border-[var(--color-border)] last:border-0"
                          >
                            <td className="py-2.5 pr-4">
                              <span className="font-medium text-[var(--color-text-heading)]">
                                {compareDim === "model" ? modelLabel(row.name) : row.name}
                              </span>
                              {row.providers.length > 1 && (
                                <Badge variant="outline" size="sm" className="ml-2">
                                  {t("comparisonTable.providersCount", { count: row.providers.length })}
                                </Badge>
                              )}
                            </td>
                            <td className="py-2.5 px-4 text-right font-mono text-[var(--color-text)]">
                              {row.requestCount.toLocaleString()}
                            </td>
                            <td className="py-2.5 px-4 text-right font-mono text-[var(--color-text)] hidden sm:table-cell">
                              {formatTokens(row.promptTokens)}
                            </td>
                            <td className="py-2.5 px-4 text-right font-mono text-[var(--color-text)] hidden sm:table-cell">
                              {formatTokens(row.completionTokens)}
                            </td>
                            <td className="py-2.5 px-4 text-right font-mono text-[var(--color-text)]">
                              {hitPct !== null ? `${hitPct.toFixed(1)}%` : "\u2014"}
                            </td>
                            <td className="py-2.5 px-4 text-right font-mono text-[var(--color-text)] hidden md:table-cell">
                              {formatMs(row.avgLatencyMs)}
                            </td>
                            {anyRates && (
                              <td className="py-2.5 pl-4 text-right font-mono text-[var(--color-text)]">
                                {row.estCost > 0 ? formatCurrency(row.estCost) : "\u2014"}
                                {row.hasMissingRate && (
                                  <span
                                    className="ml-1 text-[10px] text-[var(--color-text-muted)]"
                                    title={t("noRateConfigured")}
                                  >
                                    *
                                  </span>
                                )}
                              </td>
                            )}
                          </tr>
                        );
                      })}
                    </tbody>
                  </table>
                </div>
                {compareRows.some((r) => r.hasMissingRate) && anyRates && (
                  <p className="text-xs text-[var(--color-text-muted)] mt-3">
                    {t("comparisonTable.missingRateDisclaimer")}
                  </p>
                )}
              </Card>
            </motion.div>
          )}

          {/* Per-Model Breakdown Table */}
          <motion.div
            variants={fadeUp}
            initial="initial"
            animate="animate"
            transition={{ delay: 0.25 }}
          >
            <Card padding="lg">
              <p className="text-xs font-medium text-[var(--color-text-muted)] uppercase tracking-wider mb-4">
                {t("sections.perModelBreakdown")}
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
                            {t("table.model")}
                            <SortIcon field="model" />
                          </th>
                          <th
                            className="text-right py-2 px-4 text-xs font-medium text-[var(--color-text-muted)] cursor-pointer hover:text-[var(--color-text)] select-none"
                            onClick={() => handleSort("requestCount")}
                          >
                            {t("table.requests")}
                            <SortIcon field="requestCount" />
                          </th>
                          <th
                            className="text-right py-2 px-4 text-xs font-medium text-[var(--color-text-muted)] cursor-pointer hover:text-[var(--color-text)] select-none hidden sm:table-cell"
                            onClick={() => handleSort("promptTokens")}
                          >
                            {t("table.promptTokens")}
                            <SortIcon field="promptTokens" />
                          </th>
                          <th
                            className="text-right py-2 px-4 text-xs font-medium text-[var(--color-text-muted)] cursor-pointer hover:text-[var(--color-text)] select-none hidden md:table-cell"
                            onClick={() => handleSort("completionTokens")}
                          >
                            {t("table.completionTokens")}
                            <SortIcon field="completionTokens" />
                          </th>
                          <th
                            className="text-right py-2 px-4 text-xs font-medium text-[var(--color-text-muted)] cursor-pointer hover:text-[var(--color-text)] select-none hidden lg:table-cell"
                            onClick={() => handleSort("cacheHitRate")}
                          >
                            {t("table.cacheHitPercent")}
                            <SortIcon field="cacheHitRate" />
                          </th>
                          <th
                            className="text-right py-2 px-4 text-xs font-medium text-[var(--color-text-muted)] cursor-pointer hover:text-[var(--color-text)] select-none hidden lg:table-cell"
                            onClick={() => handleSort("estCost")}
                          >
                            {t("table.estCost")}
                            <SortIcon field="estCost" />
                          </th>
                          <th
                            className="text-right py-2 px-4 text-xs font-medium text-[var(--color-text-muted)] cursor-pointer hover:text-[var(--color-text)] select-none"
                            onClick={() => handleSort("avgLatencyMs")}
                          >
                            {t("table.avgLatency")}
                            <SortIcon field="avgLatencyMs" />
                          </th>
                          <th
                            className="text-right py-2 px-4 text-xs font-medium text-[var(--color-text-muted)] cursor-pointer hover:text-[var(--color-text)] select-none hidden xl:table-cell"
                            onClick={() => handleSort("p95LatencyMs")}
                          >
                            {t('table.p95')}
                            <SortIcon field="p95LatencyMs" />
                          </th>
                          <th
                            className="text-right py-2 pl-4 text-xs font-medium text-[var(--color-text-muted)] cursor-pointer hover:text-[var(--color-text)] select-none hidden xl:table-cell"
                            onClick={() => handleSort("maxLatencyMs")}
                          >
                            {t('table.max')}
                            <SortIcon field="maxLatencyMs" />
                          </th>
                        </tr>
                      </thead>
                      <tbody>
                        {sortedModels.map((m) => {
                          const mc = modelCostByKey.get(`${m.provider}|${m.model}`);
                          const mode = pricingModeAt(
                            costRates,
                            m.provider,
                            m.model,
                            windowBounds.to,
                          );
                          const resolved = rateAt(
                            costRates,
                            m.provider,
                            m.model,
                            windowBounds.to,
                          );
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
                                    registryByName.get(m.model)?.sourceRuntimeName ?? undefined,
                                    registryByName.get(m.model)?.displayName ?? undefined,
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
                                  mc?.basis === "mixed"
                                    ? t("costTooltips.mixed", { provider: m.provider })
                                    : mode === "subscription"
                                      ? t("costTooltips.subscription", {
                                          amount: formatCurrency(
                                            resolved?.monthlyPrice ?? 0,
                                          ),
                                          provider: m.provider,
                                        })
                                      : mode === "self-hosted"
                                        ? t("costTooltips.selfHosted", {
                                            amount: formatCurrency(
                                              resolved?.monthlyCost ?? 0,
                                            ),
                                            provider: m.provider,
                                          })
                                        : !mc || mc.cost === null
                                          ? t("costTooltips.noRate", { provider: m.provider })
                                          : undefined
                                }
                              >
                                {mc?.basis === "flat" ? (
                                  <Badge variant="outline" size="sm">
                                    {t("costCalcSection.incl")}
                                  </Badge>
                                ) : mc?.basis === "mixed" ? (
                                  <Badge variant="outline" size="sm">
                                    {t("costCalcSection.mixed")}
                                  </Badge>
                                ) : mc && mc.cost !== null ? (
                                  <span className="text-[var(--color-text)]">
                                    {formatCurrency(mc.cost)}
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
                      {t("perModelTable.modelsMissingRate", { count: missingRateCount })}{" "}
                      <button
                        type="button"
                        onClick={() => setCostDialogOpen(true)}
                        className="text-[var(--color-primary)] hover:underline underline-offset-2 cursor-pointer"
                      >
                        {t("perModelTable.openCostCalc")}
                      </button>{" "}
                      {t("perModelTable.toCompleteEstimates")}
                    </p>
                  )}
                </>
              ) : (
                <p className="text-sm text-[var(--color-text-muted)] py-8 text-center">
                  {t("perModelTable.noModelUsage")}
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
                {t("sections.requestsByHour")}
              </p>
              <HourlyHeatmap
                rangeIs24h={timeRange === "24h"}
                providers={selectedProviders.length > 0 ? selectedProviders : undefined}
                models={selectedModels.length > 0 ? selectedModels : undefined}
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
                error={latencyBandsError}
              />
            </motion.div>
            <motion.div
              variants={fadeUp}
              initial="initial"
              animate="animate"
              transition={{ delay: 0.3 }}
            >
              <ApiKeysCard
                rows={apiKeyUsage}
                loading={apiKeyUsageLoading}
                error={apiKeyUsageError}
              />
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
                  {t("sections.perProviderBreakdown")}
                </p>
                {providers && providers.length > 0 ? (
                  <Suspense fallback={<ChartSkeleton />}>
                    <LazyProviderBreakdownChart
                      providers={providers}
                      selectedProviders={selectedProviders}
                      onProviderSelect={handleProviderSelect}
                    />
                  </Suspense>
                ) : (
                  <p className="text-sm text-[var(--color-text-muted)] py-8 text-center">
                    {t("providerBreakdown.noProviderUsage")}
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
                  {t("sections.recentRequests")}
                </p>
                <RetentionControl onPurged={refreshAll} />
              </div>
              <RecentRequestsTable
                filterParams={filterParams}
                customWindow={customWindow}
                autoRefreshMs={autoRefreshMs}
                onClearCustomWindow={() => setCustomWindow(null)}
                modelLabel={modelLabel}
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
        catalog={providerCatalog}
        timeline={costTimeline}
        activeMonthsByProvider={activeMonthsByProvider}
        windowBounds={windowBounds}
        modelLabel={modelLabel}
      />

      {/* Filter Options Modal */}
      <FiltersModal
        open={filterModalOpen}
        onOpenChange={setFilterModalOpen}
        providerOptions={providerOptions}
        modelOptions={modelOptions}
        selectedProviders={selectedProviders}
        selectedModels={selectedModels}
        hideOriginPrefix={settings?.hideOriginPrefix ?? false}
        agentDisplayNames={settings?.agentDisplayNames ?? {}}
        modelDisplayNames={registryByName}
        onApply={(nextProviders, nextModels) => {
          setSelectedProviders(nextProviders);
          setSelectedModels(nextModels);
        }}
      />
    </div>
  );
}

// ─── Small shared pieces ─────────────────────────────────────────

/** Removable chip representing one active filter selection. */
function FilterChip({
  dimension,
  label,
  onRemove,
}: {
  dimension: string;
  label: string;
  onRemove: () => void;
}) {
  const { t } = useTranslation("metrics");
  return (
    <span className="inline-flex max-w-[240px] items-center overflow-hidden rounded-full border border-[var(--color-border)] bg-[var(--color-bg-surface)] text-xs">
      <span className="py-1 pl-2.5 pr-1 text-[var(--color-text-muted)]">{dimension}</span>
      <span className="max-w-[160px] truncate py-1 pr-1 font-medium text-[var(--color-text)]">
        {label}
      </span>
      <button
        type="button"
        onClick={onRemove}
        aria-label={t('filterBar.removeFilterAria', { dimension: dimension.toLowerCase(), label })}
        className="cursor-pointer py-1 pr-2 text-[var(--color-text-muted)] transition-colors hover:text-[var(--color-status-error)]"
      >
        <X className="size-3" />
      </button>
    </span>
  );
}

/** Colored % delta arrow comparing the current window to the previous one. */
function TrendDelta({
  current,
  previous,
}: {
  current: number;
  previous: number | null;
}) {
  const { t } = useTranslation("metrics");
  if (previous === null) {
    return (
      <span className="inline-flex items-center gap-1 text-[10px] text-[var(--color-text-muted)]">
        <Minus className="size-3" /> {t("costComparison.noData")}
      </span>
    );
  }
  if (previous === 0 && current === 0) {
    return (
      <span className="inline-flex items-center gap-1 text-[10px] text-[var(--color-text-muted)]">
        <Minus className="size-3" /> {t("costComparison.flat")}
      </span>
    );
  }
  const pct = previous === 0 ? 100 : ((current - previous) / previous) * 100;
  if (Math.abs(pct) < 0.5) {
    return (
      <span className="inline-flex items-center gap-1 text-[10px] text-[var(--color-text-muted)]">
        <Minus className="size-3" /> {t("costComparison.flat")}
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
      {t("costComparison.vsPrev", { pct: Math.abs(pct).toFixed(1) })}
    </span>
  );
}

/** Copyable proxy base-URL snippet used by the onboarding empty state. */
function ProxyUrlSnippet() {
  const { t } = useTranslation("metrics");
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
      title={t("copyToClipboard")}
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
  /** Provider catalog for the picker; undefined = fall back to free text. */
  catalog: ProviderCatalogEntry[] | undefined;
  /** Page bucket timeline (provider+model) reused for window tokens/costs. */
  timeline: CostBucket[];
  /** Active "YYYY-MM" months per provider (UTC), for flat proration. */
  activeMonthsByProvider: Map<string, Set<string>>;
  /** Effective page window bounds. */
  windowBounds: { from: Date; to: Date };
  /** Resolves a model id to its user-facing label. */
  modelLabel?: (model: string) => string;
}

interface RateRow {
  id: string;
  provider: string;
  /** "" = all models (provider-wide). */
  model: string;
  /** "" = open-ended. */
  from: string;
  to: string;
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

function newRateRowId(): string {
  const uuid = globalThis.crypto?.randomUUID?.();
  return uuid ?? `rp-${Date.now().toString(36)}-${Math.random().toString(36).slice(2)}`;
}

function toPeriod(row: RateRow): RatePeriod {
  return {
    id: row.id,
    provider: row.provider.trim(),
    model: row.model || null,
    from: row.from || null,
    // User-entered To is inclusive; storage/engine expects exclusive.
    to: inclusiveToExclusiveDate(row.to),
    mode: row.mode,
    promptPer1M: parseFloat(row.promptPer1M) || 0,
    completionPer1M: parseFloat(row.completionPer1M) || 0,
    monthlyPrice: parseFloat(row.monthlyPrice) || 0,
    monthlyCost: parseFloat(row.monthlyCost) || 0,
  };
}

function CostCalculatorDialog({
  open,
  onOpenChange,
  providers,
  catalog,
  timeline,
  activeMonthsByProvider,
  windowBounds,
  modelLabel = (m) => m,
}: CostCalculatorDialogProps) {
  const { t } = useTranslation("metrics");
  // Rate-period rows, seeded from saved data (or usage providers) on open.
  const [rows, setRows] = useState<RateRow[]>([]);

  useEffect(() => {
    if (!open) return;
    const saved = loadCostRates();
    const known = new Set((catalog ?? []).map((c) => c.name));
    const isCustom = (provider: string) =>
      catalog !== undefined && !known.has(provider);

    if (saved.length > 0) {
      setRows(
        saved.map((p) => ({
          id: p.id,
          provider: p.provider,
          model: p.model ?? "",
          from: p.from ?? "",
          // Stored `to` is exclusive; the field displays the inclusive day.
          to: p.to ? exclusiveToInclusiveDate(p.to) : "",
          mode: p.mode,
          promptPer1M: p.promptPer1M ? String(p.promptPer1M) : "",
          completionPer1M: p.completionPer1M ? String(p.completionPer1M) : "",
          monthlyPrice: p.monthlyPrice ? String(p.monthlyPrice) : "",
          monthlyCost: p.monthlyCost ? String(p.monthlyCost) : "",
          customProvider: isCustom(p.provider),
        })),
      );
    } else if (providers && providers.length > 0) {
      setRows(
        providers.map((p) => ({
          id: newRateRowId(),
          provider: p.provider,
          model: "",
          from: "",
          to: "",
          mode: "per-token",
          promptPer1M: "",
          completionPer1M: "",
          monthlyPrice: "",
          monthlyCost: "",
          customProvider: isCustom(p.provider),
        })),
      );
    } else {
      setRows([]);
    }
    // Re-seed only when the dialog opens, not on every prop change.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [open]);

  function updateRow(id: string, patch: Partial<RateRow>) {
    setRows((prev) => prev.map((r) => (r.id === id ? { ...r, ...patch } : r)));
  }

  function addRow() {
    setRows((prev) => [
      ...prev,
      {
        id: newRateRowId(),
        provider: "",
        model: "",
        from: "",
        to: "",
        mode: "per-token",
        promptPer1M: "",
        completionPer1M: "",
        monthlyPrice: "",
        monthlyCost: "",
      },
    ]);
  }

  function removeRow(id: string) {
    setRows((prev) => prev.filter((r) => r.id !== id));
  }

  // Display order: provider, then period start.
  const orderedRows = useMemo(
    () =>
      [...rows].sort(
        (a, b) =>
          a.provider.localeCompare(b.provider) ||
          (a.from || "").localeCompare(b.from || ""),
      ),
    [rows],
  );

  function rangeInvalid(r: RateRow): boolean {
    if (!r.from || !r.to) return false;
    const from = dayInstant(r.from, NaN);
    const to = inclusiveToExclusiveInstant(r.to);
    if (Number.isNaN(from) || to === null) return false;
    // Inclusive To → exclusive instant; a valid range needs from < to.
    return from >= to;
  }
  const hasInvalidRange = orderedRows.some(rangeInvalid);

  function overlaps(a: RateRow, b: RateRow): boolean {
    if (a.id === b.id) return false;
    if (a.provider !== b.provider) return false;
    const aModel = a.model || "";
    const bModel = b.model || "";
    // Same scope, or one side is provider-wide (covers the other's model).
    if (aModel !== bModel && aModel !== "" && bModel !== "") return false;
    const aFrom = dayInstant(a.from, -Infinity);
    const aTo = inclusiveToExclusiveInstant(a.to) ?? Infinity;
    const bFrom = dayInstant(b.from, -Infinity);
    const bTo = inclusiveToExclusiveInstant(b.to) ?? Infinity;
    return aFrom < bTo && bFrom < aTo;
  }

  function hasOverlap(r: RateRow): boolean {
    return rows.some((o) => overlaps(r, o));
  }

  function saveRates() {
    if (hasInvalidRange) return;
    // All value sets are always written so switching a period's mode later
    // never loses its previously entered rates.
    saveCostRates(
      orderedRows.filter((r) => r.provider.trim()).map(toPeriod),
    );
  }

  /** Models seen for a provider on the page timeline (for the scope select). */
  const modelsByProvider = useMemo(() => {
    const map = new Map<string, string[]>();
    for (const b of timeline) {
      if (!b.model) continue;
      let list = map.get(b.provider);
      if (!list) {
        list = [];
        map.set(b.provider, list);
      }
      if (!list.includes(b.model)) list.push(b.model);
    }
    for (const list of map.values()) list.sort();
    return map;
  }, [timeline]);

  function bucketsForRow(r: RateRow): CostBucket[] {
    if (!r.provider.trim()) return [];
    const from = dayInstant(r.from, -Infinity);
    // Inclusive user To → exclusive lookup bound.
    const to = inclusiveToExclusiveInstant(r.to) ?? Infinity;
    return timeline.filter((b) => {
      if (b.provider !== r.provider.trim()) return false;
      if (r.model && b.model !== r.model) return false;
      const at = new Date(b.at).getTime();
      return at >= from && at < to;
    });
  }

  /** Flat (subscription/self-hosted) window cost for one period. */
  function flatRowWindowCost(r: RateRow): number {
    const period = toPeriod(r);
    const months = activeMonthsByProvider.get(r.provider.trim()) ?? new Set<string>();
    const { subscriptions, selfHosted } = flatCostTotals(
      [period],
      new Map([[r.provider.trim(), months]]),
      windowBounds,
    );
    return subscriptions + selfHosted;
  }

  /**
   * Derived effective $/1M for a self-hosted period, using only tokens inside
   * the period's own active slice of the current month. `active: false` means
   * the period doesn't cover "now".
   */
  function derivedSelfHostedRate(r: RateRow): { rate: number | null; active: boolean } {
    if (r.mode !== "self-hosted") return { rate: null, active: true };
    const cost = parseFloat(r.monthlyCost) || 0;
    if (cost <= 0 || !r.provider.trim()) return { rate: null, active: true };

    const now = Date.now();
    const periodFrom = dayInstant(r.from, -Infinity);
    const periodTo = inclusiveToExclusiveInstant(r.to) ?? Infinity;
    if (!(now >= periodFrom && now < periodTo)) return { rate: null, active: false };

    const nowDate = new Date();
    const monthStart = Date.UTC(nowDate.getUTCFullYear(), nowDate.getUTCMonth(), 1);
    const monthEnd = Date.UTC(nowDate.getUTCFullYear(), nowDate.getUTCMonth() + 1, 1);
    const lo = Math.max(periodFrom, monthStart);
    const hi = Math.min(periodTo, monthEnd);

    const tokens = timeline
      .filter((b) => {
        if (b.provider !== r.provider.trim()) return false;
        if (r.model && b.model !== r.model) return false;
        const at = new Date(b.at).getTime();
        return at >= lo && at < hi;
      })
      .reduce((sum, b) => sum + b.promptTokens + b.completionTokens, 0);

    if (tokens <= 0) return { rate: null, active: true };
    return { rate: (cost / tokens) * 1_000_000, active: true };
  }

  function amountLabel(r: RateRow): string {
    if (r.mode === "subscription") {
      return `${formatCurrency(parseFloat(r.monthlyPrice) || 0)}${t("monthlySuffix")}`;
    }
    if (r.mode === "self-hosted") {
      return `${formatCurrency(parseFloat(r.monthlyCost) || 0)}${t("monthlySuffix")}`;
    }
    return `${formatCurrency(parseFloat(r.promptPer1M) || 0)} / ${formatCurrency(
      parseFloat(r.completionPer1M) || 0,
    )} ${t("per1MTok")}`;
  }

  const breakdownRows = orderedRows.filter((r) => r.provider.trim());

  // Preview costing resolves rates across ALL rows, exactly like the page's
  // timeline engine: each bucket is priced by the single effective period
  // (model-specific beats provider-wide, later `from`, later row), so a
  // provider-wide row never also prices buckets already covered by a
  // model-scope override row.
  const previewPeriods = useMemo(
    () => breakdownRows.map(toPeriod),
    [breakdownRows],
  );

  const previewPerTokenByRow = useMemo(() => {
    const byId = new Map<string, number>();
    for (const b of timeline) {
      const rate = rateAt(previewPeriods, b.provider, b.model, new Date(b.at));
      if (!rate || rate.mode !== "per-token") continue;
      const cost = bucketCost(b, previewPeriods);
      if (cost === null) continue;
      byId.set(rate.id, (byId.get(rate.id) ?? 0) + cost);
    }
    return byId;
  }, [timeline, previewPeriods]);

  const previewFlat = useMemo(
    () => flatCostTotals(previewPeriods, activeMonthsByProvider, windowBounds),
    [previewPeriods, activeMonthsByProvider, windowBounds],
  );

  /** Row cost as shown in the breakdown table (effective, deduplicated). */
  function rowDisplayCost(r: RateRow): number {
    if (r.mode === "per-token") return previewPerTokenByRow.get(r.id) ?? 0;
    return flatRowWindowCost(r);
  }

  const grandTotal =
    [...previewPerTokenByRow.values()].reduce((sum, v) => sum + v, 0) +
    previewFlat.subscriptions +
    previewFlat.selfHosted;

  // Quiet form labels: keeps the many per-period controls legible without
  // competing with the values themselves.
  const fieldLabelClass =
    "text-[10px] font-medium uppercase tracking-wider text-[var(--color-text-muted)]";
  // Identity controls (provider + model scope) are a touch larger and heavier
  // so they read as the row's primary key; everything else stays secondary.
  const identitySelectClass =
    "h-9 w-full appearance-none cursor-pointer rounded-[var(--radius-lg)] border border-[var(--color-border)] bg-[var(--color-bg-surface)] pl-3 pr-8 text-sm font-medium text-[var(--color-text-heading)] transition-colors focus:border-[var(--color-primary)] focus:outline-none focus:ring-1 focus:ring-[var(--color-focus-ring)]";
  const identityInputClass =
    "h-9 w-full min-w-0 rounded-[var(--radius-lg)] border border-[var(--color-border)] bg-[var(--color-bg-surface)] px-3 text-sm font-medium text-[var(--color-text-heading)] transition-colors focus:border-[var(--color-primary)] focus:outline-none focus:ring-1 focus:ring-[var(--color-focus-ring)]";
  const numberInputClass =
    "h-8 w-full rounded-[var(--radius-lg)] border bg-[var(--color-bg-surface)] px-3 text-sm text-[var(--color-text)] border-[var(--color-border)] focus:outline-none focus:border-[var(--color-primary)] focus:ring-1 focus:ring-[var(--color-focus-ring)] transition-colors font-mono";
  const dateInputClass =
    "h-7 w-full min-w-0 border-0 bg-transparent px-0 text-xs text-[var(--color-text)] focus:outline-none";
  // Same chevron used by the Select primitive, so the two pickers match.
  const selectChevron =
    "bg-[url('data:image/svg+xml;charset=utf-8,%3Csvg%20xmlns%3D%22http%3A%2F%2Fwww.w3.org%2F2000%2Fsvg%22%20width%3D%2216%22%20height%3D%2216%22%20viewBox%3D%220%200%2024%2024%22%20fill%3D%22none%22%20stroke%3D%22%236b7280%22%20stroke-width%3D%222%22%3E%3Cpath%20d%3D%22m6%209%206%206%206-6%22%2F%3E%3C%2Fsvg%3E')] bg-[position:right_0.5rem_center] bg-no-repeat";
  // Breakdown table headers: quiet, compact, numeric columns right-aligned.
  const thClass =
    "whitespace-nowrap px-3 py-2.5 text-[10px] font-medium uppercase tracking-wider text-[var(--color-text-muted)]";
  const thRightClass = `${thClass} text-right`;

  return (
    <Dialog
      open={open}
      onOpenChange={onOpenChange}
      title={t("costCalculator")}
      className="sm:max-w-4xl"
    >
      <div className="px-5 py-4 space-y-5">
        <p className="text-xs text-[var(--color-text-muted)]">
          {t("costCalc.description")}
        </p>

        {/* Rate-period entry rows */}
        <div className="space-y-2.5">
          {orderedRows.map((row) => {
            const invalid = rangeInvalid(row);
            const overlap = !invalid && hasOverlap(row);
            const derived = derivedSelfHostedRate(row);
            const providerModels = modelsByProvider.get(row.provider) ?? [];
            return (
              <div
                key={row.id}
                className={[
                  "rounded-[var(--radius-lg)] border bg-[var(--color-bg-muted)]/30 p-3 transition-colors duration-[var(--duration-fast)]",
                  invalid
                    ? "border-[color-mix(in_srgb,var(--color-status-error)_40%,var(--color-border-subtle))]"
                    : overlap
                      ? "border-[color-mix(in_srgb,var(--color-status-warning)_45%,var(--color-border-subtle))]"
                      : "border-[var(--color-border-subtle)]",
                ].join(" ")}
              >
                {/* Identity band: provider + model scope read as the row's key */}
                <div className="flex flex-wrap items-end gap-3">
                  {/* Provider picker: catalog select with custom-entry fallback */}
                  <div className="flex min-w-[180px] flex-1 flex-col gap-1">
                    <span className={fieldLabelClass}>
                      {t("costCalcSection.provider")}
                    </span>
                    {!catalog || row.customProvider ? (
                      <div className="flex gap-1.5">
                        <input
                          type="text"
                          value={row.provider}
                          onChange={(e) =>
                            updateRow(row.id, {
                              provider: e.target.value,
                              customProvider: true,
                            })
                          }
                          placeholder={t("costCalc.providerPlaceholder")}
                          className={identityInputClass}
                        />
                        {catalog && (
                          <button
                            type="button"
                            onClick={() =>
                              updateRow(row.id, { provider: "", customProvider: false })
                            }
                            title={t("pickFromList")}
                            className="h-9 shrink-0 whitespace-nowrap rounded-[var(--radius-lg)] border border-[var(--color-border)] px-2.5 text-xs text-[var(--color-text-muted)] transition-colors hover:border-[var(--color-border-strong)] hover:text-[var(--color-text)] focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-[var(--color-focus-ring)] cursor-pointer"
                          >
                            {t("costCalcSection.listButton")}
                          </button>
                        )}
                      </div>
                    ) : (
                      <select
                        value={row.provider}
                        onChange={(e) => {
                          if (e.target.value === CUSTOM_PROVIDER) {
                            updateRow(row.id, {
                              provider: "",
                              model: "",
                              customProvider: true,
                            });
                          } else {
                            updateRow(row.id, {
                              provider: e.target.value,
                              model: "",
                              customProvider: false,
                            });
                          }
                        }}
                        className={`${identitySelectClass} ${selectChevron}`}
                      >
                        <option value="">{t("costCalcSection.selectProvider")}</option>
                        {/* Preserve a saved provider that isn't in the catalog */}
                        {row.provider !== "" &&
                          !catalog.some((c) => c.name === row.provider) && (
                            <option value={row.provider}>{row.provider}</option>
                          )}
                        {catalog.some((c) => c.kind === "cloud") && (
                          <optgroup label={t("costCalcSection.cloudProviders")}>
                            {catalog
                              .filter((c) => c.kind === "cloud")
                              .map((c) => (
                                <option key={`cloud-${c.name}`} value={c.name}>
                                  {c.name}
                                </option>
                              ))}
                          </optgroup>
                        )}
                        {catalog.some((c) => c.kind === "agent") && (
                          <optgroup label={t("costCalcSection.selfHostedAgents")}>
                            {catalog
                              .filter((c) => c.kind === "agent")
                              .map((c) => (
                                <option key={`agent-${c.name}`} value={c.name}>
                                  {c.name}
                                </option>
                              ))}
                          </optgroup>
                        )}
                        <option value={CUSTOM_PROVIDER}>
                          {t("costCalcSection.customOption")}
                        </option>
                      </select>
                    )}
                  </div>

                  {/* Model scope: whole provider or one model override */}
                  <div className="flex min-w-[160px] flex-1 flex-col gap-1">
                    <span className={fieldLabelClass}>
                      {t("costCalcSection.modelScope")}
                    </span>
                    <select
                      value={row.model}
                      onChange={(e) => updateRow(row.id, { model: e.target.value })}
                      className={`${identitySelectClass} ${selectChevron}`}
                    >
                      <option value="">{t("costCalcSection.allModels")}</option>
                      {providerModels.map((mm) => (
                        <option key={mm} value={mm}>
                          {modelLabel(mm)}
                        </option>
                      ))}
                    </select>
                  </div>

                  <button
                    type="button"
                    onClick={() => removeRow(row.id)}
                    className="mb-1 ml-auto flex size-8 shrink-0 cursor-pointer items-center justify-center rounded-[var(--radius-md)] text-[var(--color-text-muted)] transition-colors hover:bg-[color-mix(in_srgb,var(--color-status-error)_12%,transparent)] hover:text-[var(--color-status-error)] focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-[var(--color-focus-ring)]"
                    aria-label={t("costCalcSection.removeRow")}
                  >
                    <Trash2 className="size-4" />
                  </button>
                </div>

                {/* Secondary band: bounded period, pricing mode, amounts */}
                <div className="mt-2.5 flex flex-wrap items-start gap-x-3 gap-y-2 border-t border-[var(--color-border-subtle)] pt-2.5">
                  {/* Period: bounded From → To range; a blank side is open-ended */}
                  <div className="flex w-full flex-col gap-1 sm:w-auto sm:min-w-[280px] sm:flex-1">
                    <span className={fieldLabelClass}>
                      {t("costCalcSection.period")}
                    </span>
                    <div className="flex items-stretch gap-1 rounded-[var(--radius-lg)] border border-[var(--color-border)] bg-[var(--color-bg-surface)] p-1 transition-colors focus-within:border-[var(--color-primary)] focus-within:ring-1 focus-within:ring-[var(--color-focus-ring)]">
                      <div className="flex min-w-0 flex-1 flex-col gap-0.5">
                        <div className="flex items-center justify-between gap-1 px-1">
                          <span className="text-[10px] font-medium text-[var(--color-text-muted)]">
                            {t("costCalcSection.periodFrom")}
                          </span>
                          {!row.from && (
                            <span className="rounded-full bg-[var(--color-bg-muted)] px-1.5 text-[10px] leading-4 text-[var(--color-text-muted)]">
                              {t("costCalcSection.openEnded")}
                            </span>
                          )}
                        </div>
                        <input
                          type="date"
                          value={row.from}
                          onChange={(e) => updateRow(row.id, { from: e.target.value })}
                          className={dateInputClass}
                        />
                      </div>
                      <ArrowRight
                        className="size-3.5 shrink-0 self-center text-[var(--color-text-muted)]"
                        aria-hidden="true"
                      />
                      <div className="flex min-w-0 flex-1 flex-col gap-0.5">
                        <div className="flex items-center justify-between gap-1 px-1">
                          <span className="text-[10px] font-medium text-[var(--color-text-muted)]">
                            {t("costCalcSection.periodTo")}
                          </span>
                          {!row.to && (
                            <span className="rounded-full bg-[var(--color-bg-muted)] px-1.5 text-[10px] leading-4 text-[var(--color-text-muted)]">
                              {t("costCalcSection.openEnded")}
                            </span>
                          )}
                        </div>
                        <input
                          type="date"
                          value={row.to}
                          onChange={(e) => updateRow(row.id, { to: e.target.value })}
                          className={dateInputClass}
                        />
                      </div>
                    </div>
                  </div>

                  {/* Pricing mode toggle (segmented, unchanged visual language) */}
                  <div className="flex shrink-0 flex-col gap-1">
                    <span className={fieldLabelClass}>
                      {t("costCalcSection.pricing")}
                    </span>
                    <div
                      className="flex h-8 items-center gap-0.5 rounded-[var(--radius-md)] bg-[var(--color-bg-muted)] p-0.5"
                      role="group"
                      aria-label={t("costCalcSection.pricing")}
                    >
                      {(
                        [
                          ["per-token", t("costModes.perToken"), t("costCalcSection.perTokenHint")],
                          ["subscription", t("costModes.monthly"), t("costCalcSection.monthlyHint")],
                          [
                            "self-hosted",
                            t("costModes.selfHosted"),
                            t("costCalcSection.selfHostedHint"),
                          ],
                        ] as const
                      ).map(([mode, label, hint]) => (
                        <button
                          key={mode}
                          type="button"
                          onClick={() => updateRow(row.id, { mode })}
                          title={hint}
                          aria-pressed={row.mode === mode}
                          className={`
                            cursor-pointer whitespace-nowrap rounded-[calc(var(--radius-md)-2px)] px-2 py-1 text-xs font-medium
                            transition-all duration-[var(--duration-fast)]
                            focus-visible:outline-2 focus-visible:outline-offset-1 focus-visible:outline-[var(--color-focus-ring)]
                            ${
                              row.mode === mode
                                ? "bg-[var(--color-bg-surface)] text-[var(--color-text-heading)] shadow-sm"
                                : "text-[var(--color-text-muted)] hover:text-[var(--color-text)]"
                            }
                          `}
                        >
                          {label}
                        </button>
                      ))}
                    </div>
                  </div>

                  {/* Amount inputs for the active mode */}
                  {row.mode === "per-token" && (
                    <>
                      <div className="flex min-w-[110px] flex-1 flex-col gap-1">
                        <span className={fieldLabelClass}>
                          {t("costCalcSection.promptRate")}
                        </span>
                        <input
                          type="number"
                          value={row.promptPer1M}
                          onChange={(e) =>
                            updateRow(row.id, { promptPer1M: e.target.value })
                          }
                          placeholder="0.00"
                          min="0"
                          step="0.01"
                          className={numberInputClass}
                        />
                      </div>
                      <div className="flex min-w-[110px] flex-1 flex-col gap-1">
                        <span className={fieldLabelClass}>
                          {t("costCalcSection.completionRate")}
                        </span>
                        <input
                          type="number"
                          value={row.completionPer1M}
                          onChange={(e) =>
                            updateRow(row.id, { completionPer1M: e.target.value })
                          }
                          placeholder="0.00"
                          min="0"
                          step="0.01"
                          className={numberInputClass}
                        />
                      </div>
                    </>
                  )}
                  {row.mode === "subscription" && (
                    <div className="flex min-w-[140px] flex-1 flex-col gap-1">
                      <span className={fieldLabelClass}>
                        {t("costCalc.monthlySubscription")}
                      </span>
                      <input
                        type="number"
                        value={row.monthlyPrice}
                        onChange={(e) =>
                          updateRow(row.id, { monthlyPrice: e.target.value })
                        }
                        placeholder="0.00"
                        min="0"
                        step="0.01"
                        className={numberInputClass}
                      />
                    </div>
                  )}
                  {row.mode === "self-hosted" && (
                    <div className="flex min-w-[160px] flex-1 flex-col gap-1">
                      <span className={fieldLabelClass}>
                        {t("costCalc.monthlyCost")}
                      </span>
                      <input
                        type="number"
                        value={row.monthlyCost}
                        onChange={(e) =>
                          updateRow(row.id, { monthlyCost: e.target.value })
                        }
                        placeholder="0.00"
                        min="0"
                        step="0.01"
                        className={numberInputClass}
                      />
                      {!derived.active ? (
                        <span className="inline-flex items-center gap-1 text-[10px] text-[var(--color-text-muted)]">
                          <Info className="size-3 shrink-0" aria-hidden="true" />
                          {t("costCalcSection.periodNotActive")}
                        </span>
                      ) : derived.rate !== null ? (
                        <span className="text-[10px] text-[var(--color-text-muted)]">
                          ≈{" "}
                          <span className="font-mono text-[var(--color-text)]">
                            {formatCurrency(Number(derived.rate.toFixed(2)))}
                          </span>{" "}
                          {t("costCalcSection.per1MTokens")}{" "}
                          <span className="text-[var(--color-primary)]">
                            {t("costCalcSection.derived")}
                          </span>{" "}
                          {t("costCalcSection.proratedMonth")}
                        </span>
                      ) : (
                        <span className="text-[10px] italic text-[var(--color-text-muted)]">
                          {t("costCalcSection.noUsageThisMonth")}
                        </span>
                      )}
                    </div>
                  )}
                </div>

                {invalid && (
                  <p className="mt-2 flex items-start gap-1.5 rounded-[var(--radius-md)] bg-[color-mix(in_srgb,var(--color-status-error)_10%,transparent)] px-2 py-1.5 text-[11px] font-medium text-[var(--color-status-error)]">
                    <AlertCircle className="mt-px size-3.5 shrink-0" aria-hidden="true" />
                    {t("costCalcSection.invalidRange")}
                  </p>
                )}
                {!invalid && overlap && (
                  <p className="mt-2 flex items-start gap-1.5 rounded-[var(--radius-md)] bg-[color-mix(in_srgb,var(--color-status-warning)_12%,transparent)] px-2 py-1.5 text-[11px] font-medium text-[var(--color-status-warning)]">
                    <AlertTriangle className="mt-px size-3.5 shrink-0" aria-hidden="true" />
                    {t("costCalcSection.overlapWarning")}
                  </p>
                )}
              </div>
            );
          })}
        </div>

        <div className="flex flex-wrap items-center justify-between gap-3">
          <button
            type="button"
            onClick={addRow}
            className="flex cursor-pointer items-center gap-1.5 rounded-[var(--radius-md)] px-3 py-1.5 text-xs font-medium text-[var(--color-primary)] transition-colors hover:bg-[var(--color-bg-muted)] focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-[var(--color-focus-ring)]"
          >
            <Plus className="size-3.5" />
            {t("costCalcSection.addPeriod")}
          </button>
          <Button
            variant="primary"
            size="sm"
            onClick={saveRates}
            disabled={hasInvalidRange}
          >
            {t("costCalcSection.saveRates")}
          </Button>
        </div>

        {/* Cost Breakdown Table (per period) */}
        {breakdownRows.length > 0 && (
          <div className="overflow-x-auto rounded-[var(--radius-lg)] border border-[var(--color-border-subtle)]">
            <table className="w-full text-sm">
              <thead>
                <tr className="border-b border-[var(--color-border)] bg-[var(--color-bg-muted)]/40">
                  <th className={`${thClass} text-left`}>
                    {t("costCalcSection.provider")}
                  </th>
                  <th className={`${thClass} text-left`}>
                    {t("costCalcSection.modelScope")}
                  </th>
                  <th className={`${thClass} text-left`}>
                    {t("costCalcSection.period")}
                  </th>
                  <th className={thRightClass}>
                    {t("costCalcSection.pricing")}
                  </th>
                  <th className={`${thRightClass} hidden sm:table-cell`}>
                    {t("costCalcSection.promptTokens")}
                  </th>
                  <th className={`${thRightClass} hidden sm:table-cell`}>
                    {t("costCalcSection.completionTokens")}
                  </th>
                  <th className={thRightClass}>
                    {t("costCalcSection.totalCost")}
                  </th>
                </tr>
              </thead>
              <tbody>
                {breakdownRows.map((row) => {
                  const buckets = bucketsForRow(row);
                  const promptTokens = buckets.reduce(
                    (sum, b) => sum + b.promptTokens,
                    0,
                  );
                  const completionTokens = buckets.reduce(
                    (sum, b) => sum + b.completionTokens,
                    0,
                  );
                  return (
                    <tr
                      key={row.id}
                      className="border-b border-[var(--color-border-subtle)] last:border-0"
                    >
                      <td className="whitespace-nowrap px-3 py-2 font-medium text-[var(--color-text-heading)]">
                        {row.provider || (
                          <span className="italic text-[var(--color-text-muted)]">
                            {t("costCalcSection.unnamed")}
                          </span>
                        )}
                        {row.mode === "subscription" && (
                          <Badge variant="outline" size="sm" className="ml-2">
                            {t("costCalcSection.monthly")}
                          </Badge>
                        )}
                        {row.mode === "self-hosted" && (
                          <Badge variant="outline" size="sm" className="ml-2">
                            {t("costCalcSection.selfHosted")}
                          </Badge>
                        )}
                      </td>
                      <td className="whitespace-nowrap px-3 py-2 text-[var(--color-text)]">
                        {row.model ? (
                          modelLabel(row.model)
                        ) : (
                          <span className="italic text-[var(--color-text-muted)]">
                            {t("costCalcSection.allModels")}
                          </span>
                        )}
                      </td>
                      <td className="whitespace-nowrap px-3 py-2 text-xs">
                        <span className="inline-flex items-center gap-1.5">
                          <span
                            className={`font-mono ${
                              row.from
                                ? "text-[var(--color-text)]"
                                : "italic text-[var(--color-text-muted)]"
                            }`}
                          >
                            {row.from || t("costCalcSection.openEnded")}
                          </span>
                          <ArrowRight
                            className="size-3 shrink-0 text-[var(--color-text-muted)]"
                            aria-hidden="true"
                          />
                          <span
                            className={`font-mono ${
                              row.to
                                ? "text-[var(--color-text)]"
                                : "italic text-[var(--color-text-muted)]"
                            }`}
                          >
                            {row.to || t("costCalcSection.openEnded")}
                          </span>
                        </span>
                      </td>
                      <td className="whitespace-nowrap px-3 py-2 text-right font-mono text-[var(--color-text)]">
                        {amountLabel(row)}
                      </td>
                      <td className="hidden px-3 py-2 text-right font-mono text-[var(--color-text)] sm:table-cell">
                        {formatTokens(promptTokens)}
                      </td>
                      <td className="hidden px-3 py-2 text-right font-mono text-[var(--color-text)] sm:table-cell">
                        {formatTokens(completionTokens)}
                      </td>
                      <td className="px-3 py-2 text-right font-mono font-medium text-[var(--color-text-heading)]">
                        {formatCurrency(rowDisplayCost(row))}
                      </td>
                    </tr>
                  );
                })}
              </tbody>
              <tfoot>
                <tr className="border-t border-[var(--color-border-strong)] bg-[var(--color-bg-muted)]/50">
                  <td
                    colSpan={6}
                    className="px-3 py-2.5 text-right text-[10px] font-semibold uppercase tracking-wider text-[var(--color-text-muted)]"
                  >
                    {t("costCalcSection.grandTotal")}
                  </td>
                  <td className="px-3 py-2.5 text-right font-mono text-sm font-semibold text-[var(--color-text-heading)]">
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
