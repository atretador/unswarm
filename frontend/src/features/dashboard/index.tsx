import { Suspense, lazy, useState, useCallback } from "react";
import { useQuery } from "@tanstack/react-query";
import { motion } from "motion/react";
import { Activity, Zap, Clock, AlertTriangle, RefreshCw, ArrowLeftRight, Copy, Check } from "lucide-react";
import { client } from "../../lib/query-client";
import { BASE_URL } from "../../lib/api/httpClient";
import { Card, Badge, Skeleton, EmptyState, Button, Spinner } from "../../components/ui";

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

function ChartSkeleton() {
  return (
    <div className="h-48 flex items-center justify-center">
      <Spinner size="sm" />
    </div>
  );
}

function formatUptime(seconds: number): string {
  const d = Math.floor(seconds / 86400);
  const h = Math.floor((seconds % 86400) / 3600);
  return d > 0 ? `${d}d ${h}h` : `${h}h`;
}

function formatTokens(n: number): string {
  if (n >= 1_000_000) return `${(n / 1_000_000).toFixed(1)}M`;
  if (n >= 1_000) return `${(n / 1_000).toFixed(1)}K`;
  return String(n);
}

function formatMs(ms: number): string {
  return `${Math.round(ms)} ms`;
}

function CopyButton({ text, className = "" }: { text: string; className?: string }) {
  const [copied, setCopied] = useState(false);

  const handleCopy = useCallback(() => {
    navigator.clipboard.writeText(text).then(() => {
      setCopied(true);
      setTimeout(() => setCopied(false), 1500);
    });
  }, [text]);

  return (
    <button
      type="button"
      onClick={handleCopy}
      className={`
        inline-flex items-center gap-1 text-[10px] font-medium
        px-1.5 py-0.5 rounded-[var(--radius-sm)]
        text-[var(--color-text-muted)]
        hover:bg-[var(--color-bg-muted)] hover:text-[var(--color-text)]
        transition-colors duration-[var(--duration-fast)] cursor-pointer
        ${className}
      `}
    >
      {copied ? (
        <>
          <Check className="size-3 text-[var(--color-status-running)]" />
          <span className="text-[var(--color-status-running)]">Copied</span>
        </>
      ) : (
        <>
          <Copy className="size-3" />
          <span>Copy</span>
        </>
      )}
    </button>
  );
}

const PROXY_ENDPOINTS = [
  { method: "POST" as const, path: "/v1/chat/completions" },
  { method: "POST" as const, path: "/v1/completions" },
  { method: "GET" as const, path: "/v1/models" },
];

const fadeUp = {
  initial: { opacity: 0, y: 8 },
  animate: { opacity: 1, y: 0 },
};

function DashboardContent() {
  const {
    data: stats,
    isLoading,
    error,
    refetch,
    isRefetching,
  } = useQuery({
    queryKey: ["stats"],
    queryFn: () => client.getStats(),
    refetchInterval: 2000,
  });

  if (isLoading) {
    return (
      <div className="p-6 space-y-6 max-w-6xl">
        <div className="grid grid-cols-2 md:grid-cols-5 gap-4">
          {Array.from({ length: 5 }, (_, i) => (
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
      </div>
    );
  }

  if (error) {
    return (
      <div className="p-6 max-w-6xl">
        <EmptyState
          icon={<AlertTriangle className="size-12" strokeWidth={1.5} />}
          title="Failed to load dashboard"
          description={error.message}
          action={
            <Button
              variant="secondary"
              size="sm"
              onClick={() => refetch()}
              loading={isRefetching}
            >
              <RefreshCw className="size-3.5" />
              Retry
            </Button>
          }
        />
      </div>
    );
  }

  if (!stats) {
    return (
      <div className="p-6 space-y-6 max-w-6xl">
        <div className="grid grid-cols-2 md:grid-cols-5 gap-4">
          {Array.from({ length: 5 }, (_, i) => (
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
      </div>
    );
  }

  const statCards = [
    {
      label: "Total requests",
      value: stats.totalRequests.toLocaleString(),
      icon: Activity,
      color: "text-[var(--color-primary)]",
    },
    {
      label: "Tokens processed",
      value: formatTokens(stats.totalTokensProcessed),
      icon: Zap,
      color: "text-[var(--color-status-running)]",
    },
    {
      label: "Avg latency",
      value: formatMs(stats.avgLatencyMs),
      icon: Clock,
      color: "text-[var(--color-status-warning)]",
    },
    {
      label: "Queue depth",
      value: String(stats.queueDepth),
      icon: AlertTriangle,
      color: stats.queueDepth > 5 ? "text-[var(--color-status-error)]" : "text-[var(--color-text-muted)]",
    },
    {
      label: "Avg switch",
      value: stats.switchCount > 0 ? formatMs(stats.avgSwitchMs) : "—",
      icon: ArrowLeftRight,
      color: "text-[var(--color-text-muted)]",
    },
  ];

  const hourLabels = stats.requestsPerMinute.map((_, i) => `${i}m`);

  // BASE_URL is '' for same-origin deployments — show the current origin instead.
  const displayBaseUrl = BASE_URL || window.location.origin;

  return (
    <div className="p-6 space-y-6 max-w-6xl">
      {/* Stat cards */}
      <div className="grid grid-cols-2 md:grid-cols-5 gap-4">
        {statCards.map((stat, i) => (
          <motion.div key={stat.label} variants={fadeUp} initial="initial" animate="animate" transition={{ delay: i * 0.05 }}>
            <Card padding="md">
              <div className="flex items-center justify-between mb-1">
                <p className="text-xs text-[var(--color-text-muted)]">{stat.label}</p>
                <stat.icon className={`size-3.5 ${stat.color}`} />
              </div>
              <p className="text-xl font-semibold font-heading text-[var(--color-text-heading)]">
                {stat.value}
              </p>
            </Card>
          </motion.div>
        ))}
      </div>

      {/* API endpoint info */}
      <motion.div variants={fadeUp} initial="initial" animate="animate" transition={{ delay: 0.15 }}>
        <Card padding="lg">
          <p className="text-xs font-medium text-[var(--color-text-muted)] uppercase tracking-wider mb-3">
            API endpoint
          </p>
          <p className="text-xs text-[var(--color-text-muted)] mb-4">
            OpenAI-compatible proxy — point any OpenAI SDK client or harness at this base URL.
            No authentication required.
          </p>

          {/* Base URL (falls back to current origin when API is same-origin) */}
          <div className="flex items-center gap-2 mb-4 p-2.5 rounded-[var(--radius-md)] bg-[var(--color-bg-elevated)] border border-[var(--color-border)]">
            <code className="flex-1 text-sm font-mono text-[var(--color-text-heading)] truncate">
              {displayBaseUrl}
            </code>
            <CopyButton text={displayBaseUrl} />
          </div>

          {/* Endpoint rows */}
          <div className="space-y-1.5">
            {PROXY_ENDPOINTS.map((ep) => {
              const fullUrl = `${displayBaseUrl}${ep.path}`;
              return (
                <div
                  key={ep.path}
                  className="flex items-center gap-2 px-2.5 py-1.5 rounded-[var(--radius-md)] hover:bg-[var(--color-bg-elevated)] transition-colors duration-[var(--duration-fast)]"
                >
                  <Badge
                    variant={ep.method === "GET" ? "info" : "success"}
                    size="sm"
                    className="shrink-0 w-12 justify-center"
                  >
                    {ep.method}
                  </Badge>
                  <code className="flex-1 text-xs font-mono text-[var(--color-text)] truncate">
                    {ep.path}
                  </code>
                  <CopyButton text={fullUrl} />
                </div>
              );
            })}
          </div>
        </Card>
      </motion.div>

      {/* Requests per minute chart */}
      <motion.div variants={fadeUp} initial="initial" animate="animate" transition={{ delay: 0.2 }}>
        <Card padding="lg">
          <p className="text-xs font-medium text-[var(--color-text-muted)] uppercase tracking-wider mb-4">
            Requests per minute
          </p>
          <Suspense fallback={<ChartSkeleton />}>
            <LazyResponsiveContainer width="100%" height={200}>
              <LazyAreaChart data={stats.requestsPerMinute.map((v, i) => ({ time: hourLabels[i], value: v }))}>
                <defs>
                  <linearGradient id="rpmGrad" x1="0" y1="0" x2="0" y2="1">
                    <stop offset="0%" stopColor="var(--color-primary)" stopOpacity={0.3} />
                    <stop offset="100%" stopColor="var(--color-primary)" stopOpacity={0} />
                  </linearGradient>
                </defs>
                <LazyXAxis dataKey="time" tick={{ fontSize: 10, fill: "var(--color-text-muted)" }} tickLine={false} axisLine={false} />
                <LazyYAxis tick={{ fontSize: 10, fill: "var(--color-text-muted)" }} tickLine={false} axisLine={false} width={30} />
                <LazyTooltip
                  contentStyle={{
                    background: "var(--color-bg-elevated)",
                    border: "1px solid var(--color-border)",
                    borderRadius: "var(--radius-lg)",
                    fontSize: "12px",
                  }}
                />
                <LazyArea
                  type="monotone"
                  dataKey="value"
                  stroke="var(--color-primary)"
                  fill="url(#rpmGrad)"
                  strokeWidth={2}
                />
              </LazyAreaChart>
            </LazyResponsiveContainer>
          </Suspense>
        </Card>
      </motion.div>

      {/* Tokens per second chart */}
      <motion.div variants={fadeUp} initial="initial" animate="animate" transition={{ delay: 0.3 }}>
        <Card padding="lg">
          <p className="text-xs font-medium text-[var(--color-text-muted)] uppercase tracking-wider mb-4">
            Tokens per second
          </p>
          <Suspense fallback={<ChartSkeleton />}>
            <LazyResponsiveContainer width="100%" height={200}>
              <LazyAreaChart data={stats.tokensPerSecond.map((v, i) => ({ time: hourLabels[i], value: v }))}>
                <defs>
                  <linearGradient id="tpsGrad" x1="0" y1="0" x2="0" y2="1">
                    <stop offset="0%" stopColor="var(--color-status-running)" stopOpacity={0.3} />
                    <stop offset="100%" stopColor="var(--color-status-running)" stopOpacity={0} />
                  </linearGradient>
                </defs>
                <LazyXAxis dataKey="time" tick={{ fontSize: 10, fill: "var(--color-text-muted)" }} tickLine={false} axisLine={false} />
                <LazyYAxis tick={{ fontSize: 10, fill: "var(--color-text-muted)" }} tickLine={false} axisLine={false} width={30} />
                <LazyTooltip
                  contentStyle={{
                    background: "var(--color-bg-elevated)",
                    border: "1px solid var(--color-border)",
                    borderRadius: "var(--radius-lg)",
                    fontSize: "12px",
                  }}
                />
                <LazyArea
                  type="monotone"
                  dataKey="value"
                  stroke="var(--color-status-running)"
                  fill="url(#tpsGrad)"
                  strokeWidth={2}
                />
              </LazyAreaChart>
            </LazyResponsiveContainer>
          </Suspense>
        </Card>
      </motion.div>

      {/* Quick stats row */}
      <motion.div variants={fadeUp} initial="initial" animate="animate" transition={{ delay: 0.4 }}>
        <div className="grid grid-cols-2 md:grid-cols-5 gap-4 text-xs">
          <Card padding="sm">
            <p className="text-[var(--color-text-muted)] mb-0.5">Active requests</p>
            <p className="font-mono font-medium text-[var(--color-text-heading)]">{stats.activeRequests}</p>
          </Card>
          <Card padding="sm">
            <p className="text-[var(--color-text-muted)] mb-0.5">Cached prompt tokens</p>
            <p className="font-mono font-medium text-[var(--color-text-heading)]">{formatTokens(stats.totalPromptTokensCached)}</p>
          </Card>
          <Card padding="sm">
            <p className="text-[var(--color-text-muted)] mb-0.5">Models loaded</p>
            <p className="font-mono font-medium text-[var(--color-text-heading)]">{stats.modelsLoaded}</p>
          </Card>
          <Card padding="sm">
            <p className="text-[var(--color-text-muted)] mb-0.5">Uptime</p>
            <p className="font-mono font-medium text-[var(--color-text-heading)]">{formatUptime(stats.uptimeSeconds)}</p>
          </Card>
          <Card padding="sm">
            <p className="text-[var(--color-text-muted)] mb-0.5">Errors (24h)</p>
            <p className={`font-mono font-medium ${stats.errorsLast24h > 0 ? "text-[var(--color-status-error)]" : "text-[var(--color-text-heading)]"}`}>
              {stats.errorsLast24h}
            </p>
          </Card>
        </div>
      </motion.div>
    </div>
  );
}

export default function Dashboard() {
  return <DashboardContent />;
}
