import { useState, useEffect } from "react";
import { useTranslation } from "react-i18next";
import { useQuery, useMutation, useQueryClient } from "@tanstack/react-query";
import { motion, AnimatePresence } from "motion/react";
import {
  ArrowDown,
  ArrowRight,
  ArrowUp,
  Clock,
  ChevronDown,
  ChevronRight,
  Pause,
  Server,
  Monitor,
  SkipForward,
  X,
} from "lucide-react";
import { client } from "../../lib/query-client";
import { formatMs, formatTokensPerSec } from "../../i18n/format";
import {
  Card,
  Badge,
  StatusDot,
  Button,
  Skeleton,
  EmptyState,
} from "../../components/ui";
import type { QueueItem } from "../../lib/api/types";
import { formatModelName } from "../../lib/format-model-name";

/** Parse targetId into an icon kind and agent name (label is translated via t()). */
function parseTarget(
  targetId: string | null,
): { kind: "host" | "agent"; agentName: string } {
  if (!targetId || targetId === "host") {
    return { kind: "host", agentName: "" };
  }
  const name = targetId.startsWith("agent:") ? targetId.slice(6) : targetId;
  return { kind: "agent", agentName: name };
}

/**
 * Live countdown of a conversation hold, ticking every second.
 * Clamps at 0s and shows "expiring…" until the next poll refreshes.
 * Same per-second tick pattern as the processing elapsed timer below.
 */
function HoldCountdown({ expiresAt }: { expiresAt: string }) {
  const { t } = useTranslation("queue");
  const [now, setNow] = useState(() => Date.now());
  useEffect(() => {
    const id = setInterval(() => setNow(Date.now()), 1000);
    return () => clearInterval(id);
  }, []);

  const remainingMs = new Date(expiresAt).getTime() - now;
  const remaining = Math.max(0, Math.ceil(remainingMs / 1000));

  return (
    <span
      className="rounded-full bg-[color-mix(in_srgb,var(--color-status-warning)_18%,transparent)] px-1 font-mono"
      data-testid="hold-countdown"
    >
      {remaining > 0 ? t("holdCountdown", { remaining }) : t("holdExpiring")}
    </span>
  );
}

// ─── Target Section ─────────────────────────────────────────────────

function TargetSection({
  targetId,
  processing,
  waiting,
  cancelMutation,
  releaseHoldMutation,
  settings,
}: {
  targetId: string;
  processing: QueueItem[];
  waiting: QueueItem[];
  cancelMutation: ReturnType<typeof useMutation<void, Error, string>>;
  releaseHoldMutation: ReturnType<typeof useMutation<void, Error, string>>;
  settings?: { hideOriginPrefix: boolean; agentDisplayNames: Record<string, string> };
}) {
  const { t } = useTranslation("queue");
  const idle = processing.length === 0 && waiting.length === 0;
  const [expanded, setExpanded] = useState(!idle);
  const { kind, agentName } = parseTarget(targetId);
  const label = kind === "host" ? t("hostLocal") : t("agentLabel", { name: agentName });

  // Live elapsed timer — ticks every second while anything is processing
  const [now, setNow] = useState(() => Date.now());
  useEffect(() => {
    if (processing.length === 0) return;
    const id = setInterval(() => setNow(Date.now()), 1000);
    return () => clearInterval(id);
  }, [processing.length]);

  return (
    <Card padding="none" className="overflow-hidden">
      {/* Header */}
      <div className="group flex items-center gap-2 py-1.5">
        <button
          type="button"
          onClick={() => setExpanded((p) => !p)}
          aria-expanded={expanded}
          aria-label={t("toggleSection", { label })}
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
              status={processing.length > 0 ? "processing" : "waiting"}
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
            {waiting.length + processing.length}
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
                  {t("idleNoRequests")}
                </div>
              )}

              {/* Processing items — one per runtime lane */}
              {processing.map((item) => {
                const liveElapsedMs = now - new Date(item.createdAt).getTime();
                return (
                  <div
                    key={item.id}
                    className="flex items-center justify-between gap-3 rounded-[var(--radius-md)] bg-[color-mix(in_srgb,var(--color-status-running)_6%,transparent)] px-3 py-2 mb-1"
                  >
                    <div className="flex items-center gap-3 text-xs min-w-0">
                      <StatusDot status="processing" size="sm" />
                      <span className="font-mono text-[var(--color-text-heading)] truncate">
                        {formatModelName(item.modelAssigned ?? item.modelRequested, "queue", settings?.hideOriginPrefix ?? false, settings?.agentDisplayNames ?? {})}
                      </span>
                      {item.runtimeId && (
                        <span
                          className="shrink-0 rounded-full border border-[var(--color-border-subtle)] px-1.5 py-px font-mono text-[10px] text-[var(--color-text-muted)]"
                          title={t("runningOnRuntime", { id: item.runtimeId })}
                        >
                          {item.runtimeId}
                        </span>
                      )}
                      {item.generationTokensPerSec > 0 && (
                        <span className="text-[var(--color-text-muted)] font-mono shrink-0" title={t("tokensSpeed")}>
                          {formatTokensPerSec(item.generationTokensPerSec)}
                        </span>
                      )}
                      {item.promptTokensPerSec > 0 && (
                        <span className="text-[var(--color-text-muted)] font-mono shrink-0" title={t("promptSpeed")}>
                          {t("promptLabel")} {formatTokensPerSec(item.promptTokensPerSec)}
                        </span>
                      )}
                      {item.tokensGenerated > 0 ? (
                        <span className="text-[var(--color-text-muted)] font-mono shrink-0">
                          {item.tokensGenerated.toLocaleString()} /{" "}
                          {item.tokensRequested.toLocaleString()}
                        </span>
                      ) : (
                        <span className="text-[var(--color-text-muted)] font-mono shrink-0 animate-pulse">
                          ...
                        </span>
                      )}
                      <span className="text-[var(--color-text-muted)] font-mono shrink-0">
                        {formatMs(liveElapsedMs)}
                      </span>
                    </div>
                    <Button
                      variant="ghost"
                      size="sm"
                      className="shrink-0 text-[var(--color-text-muted)] hover:text-[var(--color-status-error)]"
                      onClick={() => cancelMutation.mutate(item.id)}
                      disabled={cancelMutation.isPending}
                      aria-label={t("cancelProcessing")}
                      title={t("cancel")}
                    >
                      <X className="size-3.5" />
                    </Button>
                  </div>
                );
              })}

              {/* Waiting items */}
              {waiting.length > 0 && (
                <div className="divide-y divide-[var(--color-border-subtle)]">
                  {waiting.map((item, i) => {
                    const blocked = item.blockedByRuntimeIds.length > 0;
                    const held = item.heldByConversation ?? null;

                      return (
                      <div
                        key={item.id}
                        className="flex items-center justify-between gap-3 px-3 py-2 text-xs"
                      >
                        <div className="flex min-w-0 flex-wrap items-center gap-x-3 gap-y-1">
                          <span className="text-[var(--color-text-muted)] font-mono w-5 text-center shrink-0">
                            #{i + 1}
                          </span>
                          <StatusDot status="waiting" size="sm" />
                          <span className="font-mono text-[var(--color-text-heading)] truncate">
                            {formatModelName(item.modelRequested, "queue", settings?.hideOriginPrefix ?? false, settings?.agentDisplayNames ?? {})}
                          </span>
                          {!blocked && !held && i === 0 && (
                            <span className="shrink-0 text-[10px] uppercase tracking-wider text-[var(--color-primary)]">
                              {t("nextUp")}
                            </span>
                          )}
                          {blocked && !held && (
                            <span
                              className="shrink-0 rounded-full border border-[var(--color-border-subtle)] px-1.5 py-px text-[10px] text-[var(--color-text-muted)]"
                              title={t("blockedByTitle", { runtimes: item.blockedByRuntimeIds.join(", ") })}
                            >
                              {item.blockedByRuntimeIds.length === 1
                                ? t("blockedBy", { runtimeIds: item.blockedByRuntimeIds[0] })
                                : t("blockedByCount", { count: item.blockedByRuntimeIds.length })}
                            </span>
                          )}
                          {held && (
                            <span
                              className="inline-flex shrink-0 items-center gap-1 rounded-full border border-[color-mix(in_srgb,var(--color-status-warning)_35%,transparent)] bg-[color-mix(in_srgb,var(--color-status-warning)_12%,transparent)] px-1.5 py-px text-[10px] text-[var(--color-status-warning)]"
                              title={t("heldByTitle", { runtimeId: held.runtimeId, requestCount: held.requestCount })}
                              data-testid="conversation-hold"
                            >
                              <Pause className="size-2.5 shrink-0" aria-hidden />
                              {t("heldByConversation")}
                              <span className="font-mono">{held.model}</span>
                              <span className="rounded-full bg-[color-mix(in_srgb,var(--color-status-warning)_18%,transparent)] px-1 font-mono">
                                {t("requestCount", { count: held.requestCount })}
                              </span>
                              <HoldCountdown expiresAt={held.holdExpiresAt} />
                            </span>
                          )}
                        </div>
                        <div className="flex items-center gap-4 text-[var(--color-text-muted)] shrink-0">
                          <span className="flex items-center gap-1">
                            <Clock className="size-3" />
                            {formatMs(item.waitMs)}
                          </span>
                          <Badge variant="outline">P{item.priority}</Badge>
                          {held && (
                            <Button
                              variant="ghost"
                              size="sm"
                              className="text-[var(--color-text-muted)] hover:text-[var(--color-primary)]"
                              onClick={() =>
                                releaseHoldMutation.mutate(item.targetId ?? "host")
                              }
                              disabled={releaseHoldMutation.isPending}
                              aria-label={t("releaseHoldFor", { model: item.modelRequested })}
                              title={t("releaseHold")}
                            >
                              <SkipForward className="size-3.5" />
                            </Button>
                          )}
                          <Button
                            variant="ghost"
                            size="sm"
                            className="text-[var(--color-text-muted)] hover:text-[var(--color-status-error)]"
                            onClick={() => cancelMutation.mutate(item.id)}
                            disabled={cancelMutation.isPending}
                            aria-label={t("cancelRequestFor", { model: item.modelRequested })}
                            title={t("cancel")}
                          >
                            <X className="size-3.5" />
                          </Button>
                        </div>
                      </div>
                    );
                  })}
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
  const { t } = useTranslation("queue");
  const { t: tc } = useTranslation("common");
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

  const { data: settings } = useQuery({
    queryKey: ["settings"],
    queryFn: () => client.getSettings(),
  });

  const cancelMutation = useMutation({
    mutationFn: (itemId: string) => client.cancelQueueItem(itemId),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ["queue"] }),
  });

  const releaseHoldMutation = useMutation({
    mutationFn: (targetId: string) => client.releaseTargetHold(targetId),
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
          title={t("failedToLoad")}
          description={error.message}
          action={
            <Button
              variant="secondary"
              size="sm"
              onClick={() => refetch()}
              loading={isRefetching}
            >
              {tc("retry")}
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

  const {
    processing: processingRaw,
    currentSlot,
    waiting,
    recentCompleted,
    activeTransitions,
    skipsUsed,
    skipsRemaining,
  } = snapshot;

  // New contract: `processing` holds all in-flight items across lanes.
  // Fall back to the legacy single-slot alias when absent.
  const processing =
    processingRaw ?? (currentSlot ? [currentSlot] : []);

  // Skip-budget indicator is only meaningful when the feature has been used
  // or has budget available; fully hidden when skip is off and unused.
  const showSkipBudget = skipsRemaining > 0 || skipsUsed > 0;

  const TRANSITION_LABELS: Record<string, string> = {
    draining: t('transition.draining'),
    switching: t('transition.switching'),
    starting: t('transition.starting'),
    complete: t('transition.complete'),
  };

  // Transition tallies for the accessible live region.
  const stoppingCount = activeTransitions.reduce(
    (n, t) => n + t.stopping.length,
    0,
  );
  const startingCount = activeTransitions.length;

  // Waiting items currently held by an active conversation.
  const heldCount = waiting.filter((w) => w.heldByConversation).length;

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
    { processing: QueueItem[]; waiting: QueueItem[] }
  >();
  for (const target of allTargets) {
    grouped.set(target, { processing: [], waiting: [] });
  }

  for (const item of processing) {
    const key = item.targetId ?? "host";
    if (!grouped.has(key)) grouped.set(key, { processing: [], waiting: [] });
    grouped.get(key)!.processing.push(item);
  }

  for (const item of waiting) {
    const key = item.targetId ?? "host";
    if (!grouped.has(key)) grouped.set(key, { processing: [], waiting: [] });
    grouped.get(key)!.waiting.push(item);
  }

  return (
    <div className="p-6 space-y-6 max-w-5xl">
      {/* Header with live indicator */}
      <div className="flex items-start justify-between gap-4">
        <div>
          <h2 className="text-lg font-semibold text-[var(--color-text-heading)]">
            {t("title")}
          </h2>
          <p className="text-xs text-[var(--color-text-muted)] mt-0.5">
            {t("subtitle")}
          </p>
        </div>
        <div className="flex items-center gap-4 text-xs text-[var(--color-text-muted)] shrink-0 pt-0.5">
          <div className="flex items-center gap-1.5">
            <span className="size-1.5 rounded-full bg-[var(--color-status-running)] animate-pulse" />
            <span>{t("livePolling")}</span>
          </div>
          {showSkipBudget && (
            <div
              className="flex items-center gap-1.5"
              title={t("skipBudget", { used: skipsUsed })}
            >
              <ArrowRight className="size-3" />
              <span>
                {skipsUsed > 0
                  ? t("skipBudgetDisplayUsed", { remaining: skipsRemaining, used: skipsUsed })
                  : t("skipBudgetDisplay", { remaining: skipsRemaining })}
              </span>
            </div>
          )}
          {activeTransitions.length > 0 && (
            <div className="flex items-center gap-1.5">
              <ArrowRight className="size-3" />
              <span>
                {t("srActiveTransitions", { count: activeTransitions.length })}
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
              {t("modelTransitions")}
            </span>
          </div>
          <div className="divide-y divide-[var(--color-border-subtle)]">
            {activeTransitions.map((tr) => (
              <div
                key={tr.id}
                className="flex items-center justify-between gap-3 px-4 py-2.5 text-xs"
              >
                <div className="flex min-w-0 flex-wrap items-center gap-x-2 gap-y-1">
                  {tr.stopping.length > 0 && (
                    <>
                      <div className="flex flex-wrap items-center gap-x-1.5 gap-y-1">
                        <span
                          className="flex items-center gap-0.5 text-[10px] uppercase tracking-wider text-[var(--color-status-error)]"
                          title={t("runtimesGoingDown", { count: tr.stopping.length })}
                        >
                          <ArrowDown className="size-3" />
                          {t("down")}
                        </span>
                        {tr.stopping.map((s) => (
                          <span
                            key={s.runtimeId}
                            className="flex items-center gap-1"
                            title={t("stoppingOnRuntime", { id: s.runtimeId })}
                          >
                            <span className="font-mono text-[var(--color-text-muted)] line-through decoration-[var(--color-border-subtle)]">
                              {s.model}
                            </span>
                            <span className="rounded-full border border-[var(--color-border-subtle)] px-1.5 py-px font-mono text-[10px] text-[var(--color-text-muted)]">
                              {s.runtimeId}
                            </span>
                          </span>
                        ))}
                      </div>
                      <ArrowRight className="size-3 shrink-0 text-[var(--color-text-muted)]" />
                    </>
                  )}
                  <div className="flex items-center gap-1.5">
                    <span
                      className="flex items-center gap-0.5 text-[10px] uppercase tracking-wider text-[var(--color-primary)]"
                      title={t("modelComingUp")}
                    >
                      <ArrowUp className="size-3" />
                      {t("up")}
                    </span>
                    <span className="font-mono font-medium text-[var(--color-text-heading)]">
                      {tr.toModel}
                    </span>
                    {tr.runtimeId && (
                      <span
                        className="rounded-full border border-[var(--color-primary)] bg-[var(--color-primary-soft)] px-1.5 py-px font-mono text-[10px] text-[var(--color-primary)]"
                        title={t("startingOnRuntime", { id: tr.runtimeId })}
                      >
                        {tr.runtimeId}
                      </span>
                    )}
                  </div>
                </div>
                <Badge variant={tr.status === "complete" ? "success" : "info"}>
                  {TRANSITION_LABELS[tr.status] ?? tr.status}
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
            releaseHoldMutation={releaseHoldMutation}
            settings={settings}
          />
        ))}
      </div>

      {/* Recent completed */}
      {recentCompleted.length > 0 && (
        <Card padding="none">
          <div className="px-4 py-2.5 border-b border-[var(--color-border)]">
            <span className="text-xs font-medium text-[var(--color-text-muted)] uppercase tracking-wider">
              {t("recentCompleted", { count: recentCompleted.length })}
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
                    {formatModelName(item.modelAssigned ?? item.modelRequested, "queue", settings?.hideOriginPrefix ?? false, settings?.agentDisplayNames ?? {})}
                  </span>
                  <Badge variant="success">{t("completedBadge")}</Badge>
                </div>
                <div className="flex items-center gap-4 text-[var(--color-text-muted)]">
                  <span>{t("tokensGenerated", { count: item.tokensGenerated.toLocaleString() })}</span>
                  <span>{formatMs(item.elapsedMs)}</span>
                </div>
              </div>
            ))}
          </div>
        </Card>
      )}

      {/* Accessible live region for screen readers */}
      <div className="sr-only" aria-live="polite" aria-atomic="true">
        {processing.length > 0
          ? t("srQueueStatus", { waiting: waiting.length, processing: processing.length })
          : t("srQueueIdle", { waiting: waiting.length })}
        {activeTransitions.length > 0 &&
          `, ${t("srActiveTransitions", { count: activeTransitions.length })}`}
        {heldCount > 0 &&
          `, ${t("srHeldItems", { count: heldCount })}`}
        {stoppingCount > 0 && `, ${t("srStopping", { count: stoppingCount })}`}
        {startingCount > 0 && `, ${t("srStarting", { count: startingCount })}`}
      </div>
    </div>
  );
}
