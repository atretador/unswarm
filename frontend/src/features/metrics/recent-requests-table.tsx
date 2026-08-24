// Paginated feed of raw usage records from GET /api/metrics/usage, plus a
// live-tail mode that streams /ws/metrics events into a capped buffer.
//
// Respects the page's provider/model/time filters and can additionally be
// narrowed to a custom window via drill-down (see index.tsx).

import { memo, useCallback, useEffect, useRef, useState } from "react";
import { useQuery, keepPreviousData } from "@tanstack/react-query";
import { ChevronLeft, ChevronRight, RadioTower, X } from "lucide-react";
import type { UsageRecordResponse } from "../../lib/api/types";
import { client } from "../../lib/query-client";
import { Badge, Button, Tooltip } from "../../components/ui";
import { useLiveTail, type LiveTailStatus } from "./use-live-tail";
import { formatMs, formatTimestamp, formatTokens } from "./format";

const PAGE_SIZE = 15;
const LIVE_BUFFER_CAP = 200;
const FRESH_MS = 1600;
/** Coalesce bursts of WS events into one render pass. */
const BATCH_FLUSH_MS = 100;

export interface RecentRequestsTableProps {
  /** Base filter params (from/to/provider/model) shared by all metrics queries. */
  filterParams: {
    from?: string;
    to?: string;
    provider?: string;
    model?: string;
  };
  /** Optional custom window from time-chart drill-down; overrides from/to. */
  customWindow: { from: string; to: string } | null;
  /** Polling interval in ms; 0 disables. */
  autoRefreshMs: number;
  onClearCustomWindow: () => void;
}

const LIVE_STATUS_LABELS: Record<LiveTailStatus, string> = {
  off: "",
  connecting: "Connecting…",
  open: "Streaming live",
  reconnecting: "Reconnecting…",
  unavailable: "Unavailable",
};

export function RecentRequestsTable({
  filterParams,
  customWindow,
  autoRefreshMs,
  onClearCustomWindow,
}: RecentRequestsTableProps) {
  const [page, setPage] = useState(0);
  const [live, setLive] = useState(false);
  const [wsFailedOnce, setWsFailedOnce] = useState(false);
  const [liveEvents, setLiveEvents] = useState<UsageRecordResponse[]>([]);
  const [freshIds, setFreshIds] = useState<Set<string>>(() => new Set());
  const freshTimers = useRef<Map<string, number>>(new Map());

  // Seen-id set mirrors liveEvents so dedupe is O(1) instead of a linear
  // scan; ids dropped by the cap trim are removed here too.
  const seenIds = useRef<Set<string>>(new Set());
  const pendingRef = useRef<UsageRecordResponse[]>([]);
  const flushTimer = useRef<number | undefined>(undefined);

  const markFresh = useCallback((id: string) => {
    setFreshIds((prev) => new Set(prev).add(id));
    const timer = window.setTimeout(() => {
      setFreshIds((prev) => {
        const next = new Set(prev);
        next.delete(id);
        return next;
      });
      freshTimers.current.delete(id);
    }, FRESH_MS);
    // Replace any pending timer for the same id.
    const existing = freshTimers.current.get(id);
    if (existing !== undefined) window.clearTimeout(existing);
    freshTimers.current.set(id, timer);
  }, []);

  const flushPending = useCallback(() => {
    flushTimer.current = undefined;
    const batch = pendingRef.current;
    if (batch.length === 0) return;
    pendingRef.current = [];

    setLiveEvents((prev) => {
      const fresh = batch.filter((e) => !seenIds.current.has(e.id));
      if (fresh.length === 0) return prev;
      for (const e of fresh) seenIds.current.add(e.id);
      const next = [...fresh.reverse(), ...prev].slice(0, LIVE_BUFFER_CAP);
      if (next.length === LIVE_BUFFER_CAP && prev.length === LIVE_BUFFER_CAP) {
        // Rows evicted by the trim leave the seen set so a re-delivered id
        // (after Clear → new stream) isn't wrongly ignored.
        const kept = new Set(next.map((e) => e.id));
        for (const id of seenIds.current) {
          if (!kept.has(id)) seenIds.current.delete(id);
        }
      }
      return next;
    });
    for (const e of batch) markFresh(e.id);
  }, [markFresh]);

  const handleEvent = useCallback(
    (event: UsageRecordResponse) => {
      pendingRef.current.push(event);
      if (flushTimer.current === undefined) {
        flushTimer.current = window.setTimeout(flushPending, BATCH_FLUSH_MS);
      }
    },
    [flushPending],
  );

  useEffect(
    () => () => {
      if (flushTimer.current !== undefined) window.clearTimeout(flushTimer.current);
    },
    [],
  );

  const liveStatus = useLiveTail(live, handleEvent);

  // A failed first connection backs out of live mode and disables the toggle
  // for this visit — no point spinning on a dead endpoint.
  useEffect(() => {
    if (liveStatus === "unavailable") {
      setWsFailedOnce(true);
      setLive(false);
    }
  }, [liveStatus]);

  // Tear down transient live state when switching modes.
  function toggleLive() {
    pendingRef.current = [];
    seenIds.current = new Set();
    setLiveEvents([]);
    setFreshIds(new Set());
    setLive((v) => !v);
  }

  const queryParams = customWindow
    ? { ...filterParams, from: customWindow.from, to: customWindow.to }
    : filterParams;

  // Reset to the first page whenever the effective query shape changes.
  const paramKey = JSON.stringify(queryParams);
  useEffect(() => {
    setPage(0);
  }, [paramKey]);

  const { data, isLoading, isError, isPlaceholderData } = useQuery({
    queryKey: ["metrics", "usage", queryParams, page],
    queryFn: () =>
      client.getMetricsUsage({ ...queryParams, page, pageSize: PAGE_SIZE }),
    placeholderData: keepPreviousData,
    refetchInterval: autoRefreshMs || false,
    enabled: !live,
  });

  const total = data?.total ?? 0;
  const totalPages = Math.max(1, Math.ceil(total / PAGE_SIZE));

  return (
    <div>
      {/* Table-level controls: drill-down banner · live tail */}
      <div className="flex flex-wrap items-center gap-2 mb-3 min-h-7">
        {customWindow && !live && (
          <>
            <span className="text-xs text-[var(--color-text-muted)]">
              Showing requests between
            </span>
            <Badge variant="info" size="md">
              {formatTimestamp(customWindow.from)} →{" "}
              {formatTimestamp(customWindow.to)}
            </Badge>
            <button
              type="button"
              onClick={onClearCustomWindow}
              className="inline-flex items-center gap-1 cursor-pointer rounded-[var(--radius-md)] px-1.5 py-0.5 hover:bg-[var(--color-bg-muted)] transition-colors text-[var(--color-text-muted)] hover:text-[var(--color-text)] text-xs"
            >
              <X className="size-3" />
              Clear window
            </button>
          </>
        )}
        <div className="flex-1" />
        {live && (
          <span
            className={`inline-flex items-center gap-1.5 text-xs ${
              liveStatus === "open"
                ? "text-[var(--color-status-running)]"
                : "text-[var(--color-text-muted)]"
            }`}
          >
            <span
              className={`size-1.5 rounded-full ${
                liveStatus === "open"
                  ? "bg-[var(--color-status-running)] animate-pulse"
                  : "bg-[var(--color-text-muted)] animate-pulse"
              }`}
            />
            {LIVE_STATUS_LABELS[liveStatus]} · {liveEvents.length} buffered
          </span>
        )}
        <Tooltip
          content={
            wsFailedOnce
              ? "Live tail unavailable — couldn't reach /ws/metrics"
              : "Stream incoming requests as they happen"
          }
          side="top"
        >
          <Button
            variant={live ? "primary" : "secondary"}
            size="sm"
            onClick={toggleLive}
            disabled={wsFailedOnce}
            className="gap-1.5"
            title="Toggle live request stream"
          >
            <RadioTower className="size-3.5" />
            Live
          </Button>
        </Tooltip>
      </div>

      {live ? (
        /* ── Live stream view ─────────────────────────────────── */
        <div>
          <table className="w-full text-sm">
            <thead>
              <tr className="border-b border-[var(--color-border)]">
                <th className="text-left py-2 pr-4 text-xs font-medium text-[var(--color-text-muted)]">
                  Time
                </th>
                <th className="text-left py-2 px-4 text-xs font-medium text-[var(--color-text-muted)]">
                  Model
                </th>
                <th className="text-left py-2 px-4 text-xs font-medium text-[var(--color-text-muted)] hidden md:table-cell">
                  Key
                </th>
                <th className="text-right py-2 px-4 text-xs font-medium text-[var(--color-text-muted)] hidden sm:table-cell">
                  Tokens In
                </th>
                <th className="text-right py-2 px-4 text-xs font-medium text-[var(--color-text-muted)] hidden sm:table-cell">
                  Tokens Out
                </th>
                <th className="text-right py-2 px-4 text-xs font-medium text-[var(--color-text-muted)] hidden md:table-cell">
                  Cached
                </th>
                <th className="text-center py-2 px-4 text-xs font-medium text-[var(--color-text-muted)] hidden lg:table-cell">
                  Mode
                </th>
                <th className="text-right py-2 pl-4 text-xs font-medium text-[var(--color-text-muted)]">
                  Latency
                </th>
              </tr>
            </thead>
            <tbody>
              {liveEvents.length === 0 && (
                <tr>
                  <td
                    colSpan={8}
                    className="py-8 text-center text-[var(--color-text-muted)]"
                  >
                    Waiting for incoming requests…
                  </td>
                </tr>
              )}
              {liveEvents.map((r) => (
                <RequestRow key={r.id} record={r} fresh={freshIds.has(r.id)} />
              ))}
            </tbody>
          </table>
          <p className="mt-3 pt-3 border-t border-[var(--color-border-subtle)] text-xs text-[var(--color-text-muted)]">
            Newest first · capped at {LIVE_BUFFER_CAP} rows
          </p>
        </div>
      ) : (
        /* ── Paginated view ───────────────────────────────────── */
        <>
          <div
            className={
              isPlaceholderData ? "opacity-60 transition-opacity" : "transition-opacity"
            }
          >
            <table className="w-full text-sm">
              <thead>
                <tr className="border-b border-[var(--color-border)]">
                  <th className="text-left py-2 pr-4 text-xs font-medium text-[var(--color-text-muted)]">
                    Time
                  </th>
                  <th className="text-left py-2 px-4 text-xs font-medium text-[var(--color-text-muted)]">
                    Model
                  </th>
                  <th className="text-left py-2 px-4 text-xs font-medium text-[var(--color-text-muted)] hidden md:table-cell">
                    Key
                  </th>
                  <th className="text-right py-2 px-4 text-xs font-medium text-[var(--color-text-muted)] hidden sm:table-cell">
                    Tokens In
                  </th>
                  <th className="text-right py-2 px-4 text-xs font-medium text-[var(--color-text-muted)] hidden sm:table-cell">
                    Tokens Out
                  </th>
                  <th className="text-right py-2 px-4 text-xs font-medium text-[var(--color-text-muted)] hidden md:table-cell">
                    Cached
                  </th>
                  <th className="text-center py-2 px-4 text-xs font-medium text-[var(--color-text-muted)] hidden lg:table-cell">
                    Mode
                  </th>
                  <th className="text-right py-2 pl-4 text-xs font-medium text-[var(--color-text-muted)]">
                    Latency
                  </th>
                </tr>
              </thead>
              <tbody>
                {isLoading && (
                  <tr>
                    <td
                      colSpan={8}
                      className="py-8 text-center text-[var(--color-text-muted)]"
                    >
                      Loading…
                    </td>
                  </tr>
                )}
                {isError && (
                  <tr>
                    <td
                      colSpan={8}
                      className="py-8 text-center text-[var(--color-status-error)]"
                    >
                      Couldn't load recent requests.
                    </td>
                  </tr>
                )}
                {!isError && !isLoading && (data?.items.length ?? 0) === 0 && (
                  <tr>
                    <td
                      colSpan={8}
                      className="py-8 text-center text-[var(--color-text-muted)]"
                    >
                      No requests in this window.
                    </td>
                  </tr>
                )}
                {data?.items.map((r) => (
                  <RequestRow key={r.id} record={r} fresh={false} />
                ))}
              </tbody>
            </table>
          </div>

          {/* Pagination controls */}
          <div className="flex items-center justify-between mt-4 pt-3 border-t border-[var(--color-border-subtle)]">
            <p className="text-xs text-[var(--color-text-muted)]">
              {total.toLocaleString()} request{total === 1 ? "" : "s"} · page{" "}
              {page + 1} of {totalPages}
            </p>
            <div className="flex gap-1.5">
              <Button
                variant="secondary"
                size="sm"
                onClick={() => setPage((p) => Math.max(0, p - 1))}
                disabled={page === 0}
                aria-label="Previous page"
              >
                <ChevronLeft className="size-3.5" />
              </Button>
              <Button
                variant="secondary"
                size="sm"
                onClick={() => setPage((p) => Math.min(totalPages - 1, p + 1))}
                disabled={page >= totalPages - 1 || isPlaceholderData}
                aria-label="Next page"
              >
                <ChevronRight className="size-3.5" />
              </Button>
            </div>
          </div>
        </>
      )}
    </div>
  );
}

interface RequestRowProps {
  record: UsageRecordResponse;
  fresh: boolean;
}

const RequestRow = memo(function RequestRow({
  record: r,
  fresh,
}: RequestRowProps) {
  return (
    <tr
      className={`border-b border-[var(--color-border)] last:border-0 transition-colors duration-700 ${
        fresh ? "bg-[color-mix(in_srgb,var(--color-primary)_10%,transparent)]" : ""
      }`}
    >
      <td className="py-2.5 pr-4 whitespace-nowrap font-mono text-xs text-[var(--color-text-muted)]">
        {formatTimestamp(r.timestamp)}
      </td>
      <td className="py-2.5 px-4 max-w-[220px] truncate" title={`${r.provider}/${r.model}`}>
        <span className="font-medium text-[var(--color-text)]">{r.model}</span>{" "}
        <Badge variant="outline" size="sm" className="ml-1">
          {r.provider}
        </Badge>
      </td>
      <td
        className="py-2.5 px-4 max-w-[140px] truncate text-xs text-[var(--color-text-muted)] hidden md:table-cell"
        title={r.apiKeyName ?? undefined}
      >
        {r.apiKeyName || "\u2014"}
      </td>
      <td className="py-2.5 px-4 text-right font-mono text-[var(--color-text)] hidden sm:table-cell">
        {formatTokens(r.promptTokens)}
      </td>
      <td className="py-2.5 px-4 text-right font-mono text-[var(--color-text)] hidden sm:table-cell">
        {formatTokens(r.completionTokens)}
      </td>
      <td className="py-2.5 px-4 text-right font-mono text-[var(--color-status-warning)] hidden md:table-cell">
        {r.cachedTokens > 0 ? formatTokens(r.cachedTokens) : "\u2014"}
      </td>
      <td className="py-2.5 px-4 text-center hidden lg:table-cell">
        {r.isStreaming ? (
          <Badge variant="success" size="sm">streaming</Badge>
        ) : (
          <Badge variant="default" size="sm">batch</Badge>
        )}
      </td>
      <td className="py-2.5 pl-4 text-right font-mono text-[var(--color-text)]">
        {formatMs(r.elapsedMs)}
      </td>
    </tr>
  );
});
