import { useQuery } from "@tanstack/react-query";
import { motion, AnimatePresence } from "motion/react";
import { ListOrdered, ArrowRight, Clock } from "lucide-react";
import { client } from "../../lib/query-client";
import { Card, Badge, StatusDot, Button, Skeleton, EmptyState } from "../../components/ui";

function formatMs(ms: number): string {
  if (ms >= 60000) return `${(ms / 60000).toFixed(1)}m`;
  if (ms >= 1000) return `${(ms / 1000).toFixed(1)}s`;
  return `${ms}ms`;
}

export default function Queue() {
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
          action={<Button variant="secondary" size="sm" onClick={() => refetch()} loading={isRefetching}>Retry</Button>}
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

  return (
    <div className="p-6 space-y-6 max-w-5xl">
      <div>
        <h2 className="text-lg font-semibold text-[var(--color-text-heading)]">
          Queue
        </h2>
        <p className="text-xs text-[var(--color-text-muted)] mt-0.5">
          Live request queue: current slot, waiting requests, and model transitions.
        </p>
      </div>

      {/* Current slot */}
      <Card padding="lg">
        <div className="flex items-center gap-2 mb-4">
          <span className="text-xs font-medium text-[var(--color-text-muted)] uppercase tracking-wider">
            Current slot
          </span>
          {currentSlot ? (
            <Badge variant="success">processing</Badge>
          ) : (
            <Badge variant="default">idle</Badge>
          )}
        </div>
        {currentSlot ? (
          <div className="grid grid-cols-2 sm:grid-cols-5 gap-4 text-xs">
            <div>
              <p className="text-[var(--color-text-muted)] mb-0.5">Model</p>
              <p className="font-mono text-[var(--color-text-heading)]">{currentSlot.modelAssigned ?? currentSlot.modelRequested}</p>
            </div>
            <div>
              <p className="text-[var(--color-text-muted)] mb-0.5">Tokens</p>
              <p className="font-mono text-[var(--color-text-heading)]">
                {currentSlot.tokensGenerated.toLocaleString()} / {currentSlot.tokensRequested.toLocaleString()}
              </p>
            </div>
            <div>
              <p className="text-[var(--color-text-muted)] mb-0.5">Elapsed</p>
              <p className="font-mono text-[var(--color-text-heading)]">{formatMs(currentSlot.elapsedMs)}</p>
            </div>
            <div>
              <p className="text-[var(--color-text-muted)] mb-0.5">Wait</p>
              <p className="font-mono text-[var(--color-text-heading)]">{formatMs(currentSlot.waitMs)}</p>
            </div>
            <div>
              <p className="text-[var(--color-text-muted)] mb-0.5">Priority</p>
              <p className="font-mono text-[var(--color-text-heading)]">P{currentSlot.priority}</p>
            </div>
          </div>
        ) : (
          <p className="text-sm text-[var(--color-text-muted)]">No active request.</p>
        )}
      </Card>

      {/* Waiting requests */}
      <Card padding="none">
        <div className="px-4 py-2.5 border-b border-[var(--color-border)]">
          <span className="text-xs font-medium text-[var(--color-text-muted)] uppercase tracking-wider">
            Waiting ({waiting.length})
          </span>
        </div>
        {waiting.length > 0 ? (
          <div className="divide-y divide-[var(--color-border-subtle)]">
            <AnimatePresence>
              {waiting.map((item, i) => (
                <motion.div
                  key={item.id}
                  initial={{ opacity: 0, x: -10 }}
                  animate={{ opacity: 1, x: 0 }}
                  transition={{ delay: i * 0.05 }}
                  className="flex items-center justify-between px-4 py-2.5 text-xs"
                >
                  <div className="flex items-center gap-3">
                    <span className="text-[var(--color-text-muted)] font-mono w-5 text-center">#{i + 1}</span>
                    <StatusDot status="waiting" size="sm" />
                    <span className="font-mono text-[var(--color-text-heading)]">{item.modelRequested}</span>
                  </div>
                  <div className="flex items-center gap-4 text-[var(--color-text-muted)]">
                    <span className="flex items-center gap-1">
                      <Clock className="size-3" />
                      {formatMs(item.waitMs)}
                    </span>
                    <span>{item.tokensRequested.toLocaleString()} tokens</span>
                    <Badge variant="outline">P{item.priority}</Badge>
                  </div>
                </motion.div>
              ))}
            </AnimatePresence>
          </div>
        ) : (
          <div className="px-4 py-6 text-center text-sm text-[var(--color-text-muted)]">
            Queue is empty
          </div>
        )}
      </Card>

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
              <div key={item.id} className="flex items-center justify-between px-4 py-2.5 text-xs">
                <div className="flex items-center gap-2">
                  <ArrowRight className="size-3 text-[var(--color-status-running)]" />
                  <span className="font-mono text-[var(--color-text-heading)]">{item.modelAssigned ?? item.modelRequested}</span>
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

      {/* Model transitions */}
      {activeTransitions.length > 0 && (
        <Card padding="none">
          <div className="px-4 py-2.5 border-b border-[var(--color-border)]">
            <span className="text-xs font-medium text-[var(--color-text-muted)] uppercase tracking-wider">
              Model transitions
            </span>
          </div>
          <div className="divide-y divide-[var(--color-border-subtle)]">
            {activeTransitions.map((t) => (
              <div key={t.id} className="flex items-center justify-between px-4 py-2.5 text-xs">
                <div className="flex items-center gap-2">
                  <span className="font-mono text-[var(--color-text-heading)]">{t.fromModel}</span>
                  <ArrowRight className="size-3 text-[var(--color-primary)]" />
                  <span className="font-mono text-[var(--color-text-heading)]">{t.toModel}</span>
                </div>
                <Badge variant={t.status === "complete" ? "success" : "info"}>{t.status}</Badge>
              </div>
            ))}
          </div>
        </Card>
      )}

      {/* Summary */}
      <div className="flex items-center gap-4 text-xs text-[var(--color-text-muted)]">
        <div className="flex items-center gap-1.5">
          <span className="size-1.5 rounded-full bg-[var(--color-status-running)] animate-pulse" />
          <span>Live — polling every 2s</span>
        </div>
        {snapshot.activeTransitions.length > 0 && (
          <div className="flex items-center gap-1.5">
            <ListOrdered className="size-3" />
            <span>{snapshot.activeTransitions.length} active transition(s)</span>
          </div>
        )}
      </div>

      {/* Accessible live region for screen readers */}
      <div className="sr-only" aria-live="polite" aria-atomic="true">
        Queue: {waiting.length} waiting, {currentSlot ? "1 processing" : "idle"}
        {snapshot.activeTransitions.length > 0 && `, ${snapshot.activeTransitions.length} active transition(s)`}
      </div>
    </div>
  );
}
