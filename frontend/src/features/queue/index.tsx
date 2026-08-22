import { useState } from "react";
import { useQuery, useMutation, useQueryClient } from "@tanstack/react-query";
import { motion, AnimatePresence } from "motion/react";
import {
  ListOrdered,
  ArrowRight,
  Clock,
  ChevronDown,
  ChevronRight,
  Server,
  Monitor,
  X,
} from "lucide-react";
import { client } from "../../lib/query-client";
import {
  Card,
  Badge,
  StatusDot,
  Button,
  Skeleton,
  EmptyState,
} from "../../components/ui";
import type { QueueItem } from "../../lib/api/types";

// ─── Helpers ────────────────────────────────────────────────────────

function formatMs(ms: number): string {
  if (ms >= 60000) return `${(ms / 60000).toFixed(1)}m`;
  if (ms >= 1000) return `${(ms / 1000).toFixed(1)}s`;
  return `${ms}ms`;
}

/** Parse targetId into a display label and icon kind. */
function parseTarget(
  targetId: string | null,
): { label: string; kind: "host" | "agent" } {
  if (!targetId || targetId === "host") {
    return { label: "Host (local)", kind: "host" };
  }
  const name = targetId.startsWith("agent:") ? targetId.slice(6) : targetId;
  return { label: `Agent: ${name}`, kind: "agent" };
}

// ─── Target Section ─────────────────────────────────────────────────

function TargetSection({
  targetId,
  processing,
  waiting,
  cancelMutation,
}: {
  targetId: string;
  processing: QueueItem | null;
  waiting: QueueItem[];
  cancelMutation: ReturnType<typeof useMutation<void, Error, string>>;
}) {
  const idle = !processing && waiting.length === 0;
  const [expanded, setExpanded] = useState(!idle);
  const { label, kind } = parseTarget(targetId);

  return (
    <Card padding="none" className="overflow-hidden">
      {/* Header */}
      <div className="group flex items-center gap-2 py-1.5">
        <button
          type="button"
          onClick={() => setExpanded((p) => !p)}
          aria-expanded={expanded}
          aria-label={`Toggle ${label} section`}
          className="flex min-w-0 flex-1 cursor-pointer items-center gap-3 rounded-[var(--radius-lg)] px-3 py-2 text-left transition-colors hover:bg-[var(--color-bg-muted)]"
        >
          {expanded ? (
            <ChevronDown className="size-4 shrink-0 text-[var(--color-text-muted)]" />
          ) : (
            <ChevronRight className="size-4 shrink-0 text-[var(--color-text-muted)]" />
          )}

          {idle ? (
            <span className="inline-block size-2 rounded-full bg-[var(--color-text-muted)] opacity-40" />
          ) : (
            <StatusDot
              status={processing ? "processing" : "waiting"}
              size="md"
            />
          )}

          {kind === "host" ? (
            <Server className="size-3.5 shrink-0 text-[var(--color-text-muted)]" />
          ) : (
            <Monitor className="size-3.5 shrink-0 text-[var(--color-text-muted)]" />
          )}

          <span className="truncate text-sm font-semibold text-[var(--color-text-heading)] transition-colors group-hover:text-[var(--color-primary)]">
            {label}
          </span>

          <Badge variant={idle ? "default" : "outline"} className="shrink-0">
            {waiting.length + (processing ? 1 : 0)}
          </Badge>
        </button>
      </div>

      {/* Collapsible content */}
      <AnimatePresence initial={false}>
        {expanded && (
          <motion.div
            key="target-body"
            initial={{ height: 0, opacity: 0 }}
            animate={{ height: "auto", opacity: 1 }}
            exit={{ height: 0, opacity: 0 }}
            transition={{ duration: 0.22, ease: "easeOut" }}
            className="overflow-hidden"
          >
            <div className="pb-2 pl-9 pr-3">
              {/* Idle state */}
              {idle && (
                <div className="px-3 py-3 text-xs text-[var(--color-text-muted)]">
                  Idle — no queued requests
                </div>
              )}

              {/* Processing item */}
              {processing && (
                <div className="flex items-center justify-between gap-3 rounded-[var(--radius-md)] bg-[color-mix(in_srgb,var(--color-status-running)_6%,transparent)] px-3 py-2 mb-1">
                  <div className="flex items-center gap-3 text-xs min-w-0">
                    <StatusDot status="processing" size="sm" />
                    <span className="font-mono text-[var(--color-text-heading)] truncate">
                      {processing.modelAssigned ?? processing.modelRequested}
                    </span>
                    <span className="text-[var(--color-text-muted)] font-mono shrink-0">
                      {processing.tokensGenerated.toLocaleString()} /{" "}
                      {processing.tokensRequested.toLocaleString()}
                    </span>
                    <span className="text-[var(--color-text-muted)] font-mono shrink-0">
                      {formatMs(processing.elapsedMs)}
                    </span>
                  </div>
                  <Button
                    variant="ghost"
                    size="sm"
                    className="shrink-0 text-[var(--color-text-muted)] hover:text-[var(--color-status-error)]"
                    onClick={() => cancelMutation.mutate(processing.id)}
                    disabled={cancelMutation.isPending}
                    aria-label="Cancel processing request"
                    title="Cancel"
                  >
                    <X className="size-3.5" />
                  </Button>
                </div>
              )}

              {/* Waiting items */}
              {waiting.length > 0 && (
                <div className="divide-y divide-[var(--color-border-subtle)]">
                  {waiting.map((item, i) => (
                    <div
                      key={item.id}
                      className="flex items-center justify-between gap-3 px-3 py-2 text-xs"
                    >
                      <div className="flex items-center gap-3 min-w-0">
                        <span className="text-[var(--color-text-muted)] font-mono w-5 text-center shrink-0">
                          #{i + 1}
                        </span>
                        <StatusDot status="waiting" size="sm" />
                        <span className="font-mono text-[var(--color-text-heading)] truncate">
                          {item.modelRequested}
                        </span>
                      </div>
                      <div className="flex items-center gap-4 text-[var(--color-text-muted)] shrink-0">
                        <span className="flex items-center gap-1">
                          <Clock className="size-3" />
                          {formatMs(item.waitMs)}
                        </span>
                        <Badge variant="outline">P{item.priority}</Badge>
                        <Button
                          variant="ghost"
                          size="sm"
                          className="text-[var(--color-text-muted)] hover:text-[var(--color-status-error)]"
                          onClick={() => cancelMutation.mutate(item.id)}
                          disabled={cancelMutation.isPending}
                          aria-label={`Cancel ${item.modelRequested} request`}
                          title="Cancel"
                        >
                          <X className="size-3.5" />
                        </Button>
                      </div>
                    </div>
                  ))}
                </div>
              )}
            </div>
          </motion.div>
        )}
      </AnimatePresence>
    </Card>
  );
}

// ─── Main Page ──────────────────────────────────────────────────────

export default function Queue() {
  const queryClient = useQueryClient();

  const {
    data: snapshot,
    isLoading,
    error,
    refetch,
    isRefetching,
  } = useQuery({
    queryKey: ["queue"],
    queryFn: () => client.getQueueSnapshot(),
    refetchInterval: 2000,
    refetchIntervalInBackground: false,
  });

  const { data: agents } = useQuery({
    queryKey: ["agents"],
    queryFn: () => client.listAgents(),
    refetchInterval: 10000,
  });

  const cancelMutation = useMutation({
    mutationFn: (itemId: string) => client.cancelQueueItem(itemId),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ["queue"] }),
  });

  if (isLoading) {
    return (
      <div className="p-6 space-y-4 max-w-5xl">
        <Skeleton className="h-6 w-24" />
        <Card padding="md">
          <Skeleton className="h-4 w-32 mb-3" />
          <Skeleton className="h-16 w-full" />
        </Card>
      </div>
    );
  }

  if (error) {
    return (
      <div className="p-6 max-w-5xl">
        <EmptyState
          title="Failed to load queue"
          description={error.message}
          action={
            <Button
              variant="secondary"
              size="sm"
              onClick={() => refetch()}
              loading={isRefetching}
            >
              Retry
            </Button>
          }
        />
      </div>
    );
  }

  if (!snapshot) {
    return (
      <div className="p-6 space-y-4 max-w-5xl">
        <Skeleton className="h-6 w-24" />
        <Card padding="md">
          <Skeleton className="h-4 w-32 mb-3" />
          <Skeleton className="h-16 w-full" />
        </Card>
      </div>
    );
  }

  const { currentSlot, waiting, recentCompleted, activeTransitions } = snapshot;

  // Build full target list: host + all agents
  const allTargets = [
    "host",
    ...(agents ?? [])
      .filter((a) => a.name !== "host")
      .map((a) => `agent:${a.name}`),
  ];

  // Merge queue items onto targets
  const grouped = new Map<
    string,
    { processing: QueueItem | null; waiting: QueueItem[] }
  >();
  for (const target of allTargets) {
    grouped.set(target, { processing: null, waiting: [] });
  }

  if (currentSlot) {
    const key = currentSlot.targetId ?? "host";
    if (!grouped.has(key)) grouped.set(key, { processing: null, waiting: [] });
    grouped.get(key)!.processing = currentSlot;
  }

  for (const item of waiting) {
    const key = item.targetId ?? "host";
    if (!grouped.has(key)) grouped.set(key, { processing: null, waiting: [] });
    grouped.get(key)!.waiting.push(item);
  }

  return (
    <div className="p-6 space-y-6 max-w-5xl">
      {/* Header with live indicator */}
      <div className="flex items-start justify-between gap-4">
        <div>
          <h2 className="text-lg font-semibold text-[var(--color-text-heading)]">
            Queue
          </h2>
          <p className="text-xs text-[var(--color-text-muted)] mt-0.5">
            Live request queue grouped by execution target.
          </p>
        </div>
        <div className="flex items-center gap-4 text-xs text-[var(--color-text-muted)] shrink-0 pt-0.5">
          <div className="flex items-center gap-1.5">
            <span className="size-1.5 rounded-full bg-[var(--color-status-running)] animate-pulse" />
            <span>Live — polling every 2s</span>
          </div>
          {activeTransitions.length > 0 && (
            <div className="flex items-center gap-1.5">
              <ListOrdered className="size-3" />
              <span>
                {activeTransitions.length} active transition(s)
              </span>
            </div>
          )}
        </div>
      </div>

      {/* Active transitions */}
      {activeTransitions.length > 0 && (
        <Card padding="none">
          <div className="px-4 py-2.5 border-b border-[var(--color-border)]">
            <span className="text-xs font-medium text-[var(--color-text-muted)] uppercase tracking-wider">
              Model transitions
            </span>
          </div>
          <div className="divide-y divide-[var(--color-border-subtle)]">
            {activeTransitions.map((t) => (
              <div
                key={t.id}
                className="flex items-center justify-between px-4 py-2.5 text-xs"
              >
                <div className="flex items-center gap-2">
                  <span className="font-mono text-[var(--color-text-heading)]">
                    {t.fromModel}
                  </span>
                  <ArrowRight className="size-3 text-[var(--color-primary)]" />
                  <span className="font-mono text-[var(--color-text-heading)]">
                    {t.toModel}
                  </span>
                </div>
                <Badge variant={t.status === "complete" ? "success" : "info"}>
                  {t.status}
                </Badge>
              </div>
            ))}
          </div>
        </Card>
      )}

      {/* Grouped target sections */}
      <div className="space-y-3">
        {[...grouped.entries()].map(([targetId, group]) => (
          <TargetSection
            key={targetId}
            targetId={targetId}
            processing={group.processing}
            waiting={group.waiting}
            cancelMutation={cancelMutation}
          />
        ))}
      </div>

      {/* Recent completed */}
      {recentCompleted.length > 0 && (
        <Card padding="none">
          <div className="px-4 py-2.5 border-b border-[var(--color-border)]">
            <span className="text-xs font-medium text-[var(--color-text-muted)] uppercase tracking-wider">
              Recent completed ({recentCompleted.length})
            </span>
          </div>
          <div className="divide-y divide-[var(--color-border-subtle)]">
            {recentCompleted.map((item) => (
              <div
                key={item.id}
                className="flex items-center justify-between px-4 py-2.5 text-xs"
              >
                <div className="flex items-center gap-2">
                  <ArrowRight className="size-3 text-[var(--color-status-running)]" />
                  <span className="font-mono text-[var(--color-text-heading)]">
                    {item.modelAssigned ?? item.modelRequested}
                  </span>
                  <Badge variant="success">completed</Badge>
                </div>
                <div className="flex items-center gap-4 text-[var(--color-text-muted)]">
                  <span>{item.tokensGenerated.toLocaleString()} tokens</span>
                  <span>{formatMs(item.elapsedMs)}</span>
                </div>
              </div>
            ))}
          </div>
        </Card>
      )}

      {/* Accessible live region for screen readers */}
      <div className="sr-only" aria-live="polite" aria-atomic="true">
        Queue: {waiting.length} waiting, {currentSlot ? "1 processing" : "idle"}
        {activeTransitions.length > 0 &&
          `, ${activeTransitions.length} active transition(s)`}
      </div>
    </div>
  );
}
