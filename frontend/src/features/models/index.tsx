import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useState } from "react";
import { motion } from "motion/react";
import { Box, Clock, ExternalLink, Gauge, Hash, Trash2 } from "lucide-react";
import { Link } from "react-router-dom";
import { client } from "../../lib/query-client";
import type { ReactNode } from "react";
import {
  Card,
  Badge,
  StatusDot,
  Button,
  Skeleton,
  EmptyState,
  Tooltip,
  ConfirmDialog,
} from "../../components/ui";
import type { Model, ModelStatus } from "../../lib/api/types";

// ─── Status semantics — identical to the Fleet page palette ───────

const MODEL_STATUS_VARIANT: Record<ModelStatus, "success" | "warning" | "error" | "default"> = {
  ready: "success",
  validating: "warning",
  invalid: "error",
  deprecated: "default",
};

const MODEL_STATUS_LABEL: Record<ModelStatus, string> = {
  ready: "ready",
  validating: "validating…",
  invalid: "invalid",
  deprecated: "deprecated",
};

function formatTokensPerSec(v: number): string {
  if (!v || v <= 0) return "n/a";
  return `${v.toFixed(1)} tok/s`;
}

function formatLatency(v: number): string {
  if (!v || v <= 0) return "n/a";
  return `${v}ms`;
}

function formatTokens(v: number | undefined): string {
  if (!v || v <= 0) return "n/a";
  return `${v.toLocaleString()} tok`;
}

function formatRelativeTime(iso: string): string {
  const diff = Date.now() - new Date(iso).getTime();
  if (diff < 60_000) return "just now";
  if (diff < 3_600_000) return `${Math.floor(diff / 60_000)}m ago`;
  if (diff < 86_400_000) return `${Math.floor(diff / 3_600_000)}h ago`;
  return `${Math.floor(diff / 86_400_000)}d ago`;
}

// ─── Model row ────────────────────────────────────────────────────

function MetricChip({
  icon,
  label,
  value,
  title,
}: {
  icon: ReactNode;
  label: string;
  value: string;
  title?: string;
}) {
  return (
    <span
      title={title ?? `${label}: ${value}`}
      className="inline-flex items-center gap-1.5 rounded-[var(--radius-md)] border border-[var(--color-border)] bg-[var(--color-bg-surface)] px-2 py-1"
    >
      <span className="flex items-center gap-1 text-[var(--color-text-muted)]">
        {icon}
        <span className="text-[9px] font-medium uppercase tracking-wider">{label}</span>
      </span>
      <span className="font-mono text-[11px] text-[var(--color-text-heading)]">{value}</span>
    </span>
  );
}

function ModelRow({ model, index }: { model: Model; index: number }) {
  const bench = model.lastBenchmark;
  const queryClient = useQueryClient();
  const [deleting, setDeleting] = useState(false);
  const [showConfirm, setShowConfirm] = useState(false);

  const handleDelete = async () => {
    setDeleting(true);
    try {
      await client.deleteModel(model.id);
      queryClient.invalidateQueries({ queryKey: ["models"] });
      setShowConfirm(false);
    } finally {
      setDeleting(false);
    }
  };

  return (
    <motion.div
      layout
      initial={{ opacity: 0, y: 8 }}
      animate={{ opacity: 1, y: 0 }}
      transition={{ duration: 0.2, delay: Math.min(index * 0.04, 0.3) }}
    >
      <div className="flex flex-wrap items-center gap-x-4 gap-y-3 px-4 py-3.5 border-b border-[var(--color-border-subtle)] last:border-0 hover:bg-[var(--color-bg-muted)] transition-colors">
        {/* Name + status */}
        <div className="flex min-w-0 flex-1 basis-52 items-center gap-2.5">
          <StatusDot status={model.status} size="sm" />
          <div className="min-w-0">
            <p className="truncate font-mono text-xs font-medium text-[var(--color-text-heading)]">
              {model.name}
            </p>
            <p className="mt-0.5 truncate text-[10px] text-[var(--color-text-muted)]">
              {model.family} · {model.parameterSize} · {model.quantization}
            </p>
          </div>
          <Badge variant={MODEL_STATUS_VARIANT[model.status]} className="shrink-0">
            {MODEL_STATUS_LABEL[model.status]}
          </Badge>
        </div>

        {/* Last benchmark */}
        <div className="flex min-w-0 basis-52 items-center gap-2">
          {bench ? (
            <div className="flex min-w-0 flex-wrap items-center gap-1.5">
              <MetricChip
                icon={<Gauge className="size-2.5" />}
                label="speed"
                value={formatTokensPerSec(bench.tokensPerSec)}
                title="Speed: tokens per second"
              />
              <MetricChip
                icon={<Clock className="size-2.5" />}
                label="processing"
                value={formatLatency(bench.latencyMs)}
                title="Processing: time to first token"
              />
              {/* tokensGenerated is not part of the backend wire (LastBenchmarkResponse);
                  it is populated by the frontend when full run data is available. */}
              {bench.tokensGenerated !== undefined && (
                <MetricChip
                  icon={<Hash className="size-2.5" />}
                  label="tokens"
                  value={formatTokens(bench.tokensGenerated)}
                  title="Tokens generated"
                />
              )}
              <MetricChip
                icon={<Clock className="size-2.5" />}
                label="ran"
                value={formatRelativeTime(bench.timestamp)}
                title={`Last run ${new Date(bench.timestamp).toLocaleString()}`}
              />
            </div>
          ) : (
            <p className="text-xs text-[var(--color-text-muted)]">Not benchmarked yet</p>
          )}
        </div>

        {/* Actions */}
        <div className="ml-auto flex shrink-0 items-center gap-1">
          {model.sourceRuntimeId ? (
            <Tooltip content={`View ${model.sourceRuntimeId} on the Fleet page`}>
              <Link
                to={`/fleet?focus=${encodeURIComponent(model.sourceRuntimeId)}`}
                aria-label={`View source runtime ${model.sourceRuntimeId} on the Fleet page`}
                className="flex size-7 items-center justify-center rounded-[var(--radius-md)] text-[var(--color-text-muted)] transition-colors hover:bg-[var(--color-bg-muted)] hover:text-[var(--color-text)] focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-[var(--color-focus-ring)]"
              >
                <ExternalLink className="size-3.5" />
              </Link>
            </Tooltip>
          ) : (
            <span className="text-[10px] italic text-[var(--color-text-muted)]">not registered</span>
          )}
          {model.status === "deprecated" && (
            <>
              <Tooltip content="Remove deprecated model">
                <button
                  onClick={() => setShowConfirm(true)}
                  disabled={deleting}
                  aria-label={`Delete ${model.name}`}
                  className="flex size-7 items-center justify-center rounded-[var(--radius-md)] text-[var(--color-status-stopped)] transition-colors hover:bg-[var(--color-bg-muted)] focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-[var(--color-focus-ring)] disabled:opacity-50"
                >
                  <Trash2 className="size-3.5" />
                </button>
              </Tooltip>
              <ConfirmDialog
                open={showConfirm}
                title={`Delete ${model.name}?`}
                description="This will permanently remove the deprecated model from the registry."
                confirmLabel="Delete"
                loading={deleting}
                onConfirm={handleDelete}
                onCancel={() => setShowConfirm(false)}
              />
            </>
          )}
        </div>
      </div>
    </motion.div>
  );
}

// ─── Main Models page ─────────────────────────────────────────────

export default function Models() {
  const {
    data: models,
    isLoading,
    error,
    refetch,
    isRefetching,
  } = useQuery({
    queryKey: ["models"],
    queryFn: () => client.listModels(),
  });

  if (isLoading) {
    return (
      <div className="max-w-5xl space-y-4 p-6">
        <Skeleton className="h-7 w-40" />
        <Skeleton className="h-4 w-72" />
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
          title="Failed to load models"
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
        <h2 className="text-lg font-semibold text-[var(--color-text-heading)]">Models</h2>
        <p className="mt-0.5 text-xs text-[var(--color-text-muted)]">
          Discovered models: inference endpoints registered across agents.
        </p>
      </div>

      {!models || models.length === 0 ? (
        <Card padding="none">
          <EmptyState
            icon={<Box className="size-12" strokeWidth={1.5} />}
            title="No models discovered yet"
            description="Register containers on the Fleet page to auto-discover their models."
          />
        </Card>
      ) : (
        <Card padding="none">
          {models.map((model, i) => (
            <ModelRow key={model.id} model={model} index={i} />
          ))}
        </Card>
      )}
    </div>
  );
}
