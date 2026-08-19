import { useState } from "react";
import { useQuery, useMutation, useQueryClient } from "@tanstack/react-query";
import { motion, AnimatePresence } from "motion/react";
import {
  Activity,
  AlertTriangle,
  ChevronDown,
  ChevronRight,
  Play,
  Zap,
} from "lucide-react";
import { client } from "../../lib/query-client";
import {
  Card,
  Badge,
  Button,
  Skeleton,
  EmptyState,
  Input,
  Select,
  Tooltip,
} from "../../components/ui";
import type { BenchmarkResult, Model } from "../../lib/api/types";

// ─── Formatting helpers ───────────────────────────────────────────

function formatTokensPerSec(v: number): string {
  if (!v || v <= 0) return "n/a";
  return `${v.toFixed(1)} tok/s`;
}

function formatTimestamp(iso: string): string {
  const d = new Date(iso);
  return d.toLocaleString(undefined, {
    month: "short",
    day: "numeric",
    hour: "2-digit",
    minute: "2-digit",
  });
}

/** Model statuses that are safe to benchmark — matches the fleet card semantics. */
function benchmarkDisabledReason(model: Model | undefined): string | null {
  if (!model) return "No models available yet";
  if (model.status === "validating") return `${model.name} is still validating — not ready to benchmark`;
  if (model.status === "invalid") return `${model.name} is invalid — cannot benchmark`;
  if (model.status === "deprecated") return `${model.name} is deprecated — cannot benchmark`;
  return null;
}

// ─── Benchmark row ────────────────────────────────────────────────

function BenchmarkRow({ result, index }: { result: BenchmarkResult; index: number }) {
  const [expanded, setExpanded] = useState(false);
  const isError = result.status === "error";

  return (
    <motion.div
      layout
      initial={{ opacity: 0, y: 8 }}
      animate={{ opacity: 1, y: 0 }}
      transition={{ duration: 0.2, delay: Math.min(index * 0.04, 0.3) }}
    >
      <div
        role="button"
        tabIndex={0}
        aria-expanded={expanded}
        onClick={() => setExpanded((p) => !p)}
        onKeyDown={(e) => {
          if (e.key === "Enter" || e.key === " ") {
            e.preventDefault();
            setExpanded((p) => !p);
          }
        }}
        className="flex cursor-pointer flex-wrap items-center gap-x-4 gap-y-3 border-b border-[var(--color-border-subtle)] px-4 py-3.5 transition-colors last:border-0 hover:bg-[var(--color-bg-muted)] focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-[var(--color-focus-ring)]"
      >
        {/* Model + timestamp */}
        <div className="flex min-w-0 flex-1 basis-48 items-center gap-2">
          <span className="text-[var(--color-text-muted)]">
            {expanded ? (
              <ChevronDown className="size-3.5" />
            ) : (
              <ChevronRight className="size-3.5" />
            )}
          </span>
          <div className="min-w-0">
            <p className="truncate font-mono text-xs font-medium text-[var(--color-text-heading)]">
              {result.modelName}
            </p>
            <p className="text-[10px] text-[var(--color-text-muted)]">
              {formatTimestamp(result.timestamp)}
            </p>
          </div>
        </div>

        {/* Metrics */}
        <div className="flex min-w-0 basis-40 items-center gap-3">
          <span className="flex items-center gap-1 font-mono text-xs text-[var(--color-text-heading)]">
            <Zap className="size-3 shrink-0 text-[var(--color-text-muted)]" />
            {formatTokensPerSec(result.tokensPerSec)}
          </span>
          <span className="font-mono text-xs text-[var(--color-text-muted)]">
            {result.latencyMs > 0 ? `${result.latencyMs}ms` : "—"}
          </span>
          <span className="font-mono text-[10px] text-[var(--color-text-muted)]">
            {result.tokensGenerated > 0 ? `${result.tokensGenerated} tok` : "n/a"}
          </span>
        </div>

        {/* Status */}
        <div className="ml-auto shrink-0">
          {isError ? (
            <Badge variant="error">error</Badge>
          ) : (
            <Badge variant="success">completed</Badge>
          )}
        </div>
      </div>

      <AnimatePresence>
        {expanded && (
          <motion.div
            initial={{ height: 0, opacity: 0 }}
            animate={{ height: "auto", opacity: 1 }}
            exit={{ height: 0, opacity: 0 }}
            transition={{ duration: 0.2 }}
            className="overflow-hidden"
          >
            <div className="space-y-2.5 border-b border-[var(--color-border-subtle)] bg-[var(--color-bg-muted)] px-4 py-3">
              <p className="text-[10px] font-medium uppercase tracking-wider text-[var(--color-text-muted)]">
                Prompt
              </p>
              <p className="text-xs leading-relaxed text-[var(--color-text)]">
                {result.prompt || "—"}
              </p>
              {isError && result.errorMessage && (
                <div className="flex items-start gap-1.5 rounded-[var(--radius-md)] bg-[color-mix(in_srgb,var(--color-status-error)_8%,transparent)] px-2 py-1.5 text-[10px] text-[var(--color-status-error)]">
                  <AlertTriangle className="mt-0.5 size-3 shrink-0" />
                  <span className="leading-relaxed">{result.errorMessage}</span>
                </div>
              )}
            </div>
          </motion.div>
        )}
      </AnimatePresence>
    </motion.div>
  );
}

// ─── Run benchmark control ────────────────────────────────────────

function RunBenchmarkBar() {
  const queryClient = useQueryClient();
  const [modelId, setModelId] = useState("");
  const [prompt, setPrompt] = useState("");

  const { data: models } = useQuery({
    queryKey: ["models"],
    queryFn: () => client.listModels(),
  });

  const runMutation = useMutation({
    mutationFn: ({ modelId, prompt }: { modelId: string; prompt?: string }) =>
      client.runBenchmark(modelId, prompt),
    onSuccess: () => {
      // New run lands at the top of the history; the model's lastBenchmark refreshes too.
      queryClient.invalidateQueries({ queryKey: ["benchmarks"] });
      queryClient.invalidateQueries({ queryKey: ["models"] });
      setPrompt("");
    },
  });

  const readyModels = (models ?? []).filter((m) => m.status === "ready");
  const options = (models ?? []).map((m) => ({
    value: m.id,
    label: m.status === "ready" ? m.name : `${m.name} (${m.status})`,
  }));

  const selected = (models ?? []).find((m) => m.id === modelId);
  const disabledReason = benchmarkDisabledReason(selected ?? undefined);
  const canRun = !!selected && selected.status === "ready";

  const run = () => {
    if (!canRun || runMutation.isPending) return;
    // Empty prompt → backend default. Trim so whitespace-only input also falls back.
    runMutation.mutate({ modelId, prompt: prompt.trim() || undefined });
  };

  return (
    <Card padding="md" className="flex flex-col gap-3 lg:flex-row lg:items-end">
      <div className="min-w-0 flex-1">
        <Select
          label="Target model"
          aria-label="Target model"
          value={modelId}
          onChange={(e) => setModelId(e.target.value)}
          options={[
            { value: "", label: "Select a model…" },
            ...options,
          ]}
        />
      </div>
      <div className="min-w-0 flex-[1.6]">
        <Input
          label="Prompt (optional)"
          aria-label="Prompt (optional)"
          value={prompt}
          onChange={(e) => setPrompt(e.target.value)}
          maxLength={2000}
          placeholder="Describe what the model should do — empty uses the default benchmark prompt"
        />
      </div>
      <Tooltip content={disabledReason ?? "Run a benchmark against the selected model"}>
        <span className="inline-flex sm:shrink-0">
          <Button
            size="md"
            disabled={!canRun || runMutation.isPending}
            loading={runMutation.isPending}
            onClick={run}
          >
            <Play className="size-3.5" />
            Run benchmark
          </Button>
        </span>
      </Tooltip>
      {readyModels.length > 0 && (
        <p className="text-[10px] text-[var(--color-text-muted)] sm:shrink-0 sm:pb-2">
          {readyModels.length} ready model{readyModels.length !== 1 ? "s" : ""}
        </p>
      )}
    </Card>
  );
}

// ─── Main Benchmarks page ─────────────────────────────────────────

export default function Benchmarks() {
  const {
    data: results,
    isLoading,
    error,
    refetch,
    isRefetching,
  } = useQuery({
    queryKey: ["benchmarks"],
    queryFn: () => client.listBenchmarks(),
  });

  if (isLoading) {
    return (
      <div className="max-w-5xl space-y-4 p-6">
        <Skeleton className="h-7 w-40" />
        <Skeleton className="h-4 w-72" />
        <Card padding="md">
          <Skeleton className="h-8 w-full" />
        </Card>
        <Card padding="none">
          {Array.from({ length: 4 }, (_, i) => (
            <div key={i} className="border-b border-[var(--color-border-subtle)] px-4 py-3.5">
              <Skeleton className="h-4 w-48" />
              <Skeleton className="mt-2 h-3 w-64" />
            </div>
          ))}
        </Card>
      </div>
    );
  }

  if (error) {
    return (
      <div className="max-w-5xl p-6">
        <EmptyState
          title="Failed to load benchmarks"
          description={error.message}
          action={
            <Button variant="secondary" size="sm" onClick={() => refetch()} loading={isRefetching}>
              Retry
            </Button>
          }
        />
      </div>
    );
  }

  return (
    <div className="max-w-5xl space-y-6 p-6">
      {/* Header */}
      <div>
        <h2 className="text-lg font-semibold text-[var(--color-text-heading)]">Benchmarks</h2>
        <p className="mt-0.5 text-xs text-[var(--color-text-muted)]">
          Benchmark runs: measured throughput and latency per model.
        </p>
      </div>

      <RunBenchmarkBar />

      {!results || results.length === 0 ? (
        <Card padding="none">
          <EmptyState
            icon={<Activity className="size-12" strokeWidth={1.5} />}
            title="No benchmark runs yet"
            description="Run one from the Benchmarks or Fleet page."
          />
        </Card>
      ) : (
        <Card padding="none">
          {results.map((r, i) => (
            <BenchmarkRow key={r.id} result={r} index={i} />
          ))}
        </Card>
      )}
    </div>
  );
}
