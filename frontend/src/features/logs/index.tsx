import { useState, useEffect, useRef, useCallback, useMemo } from "react";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { motion, AnimatePresence } from "motion/react";
import { Pause, Play, Trash2, ArrowDown } from "lucide-react";
import { client } from "../../lib/query-client";
import type { LogEntry, LogLevel } from "../../lib/api/types";
import { Card, Badge, Button, Skeleton, EmptyState, Select } from "../../components/ui";

const LEVEL_VARIANT: Record<LogLevel, "default" | "info" | "warning" | "error"> = {
  info: "info",
  warn: "warning",
  error: "error",
  debug: "default",
};

const LEVEL_OPTIONS = [
  { value: "", label: "All levels" },
  { value: "info", label: "Info" },
  { value: "warn", label: "Warn" },
  { value: "error", label: "Error" },
  { value: "debug", label: "Debug" },
];

// Source options are derived dynamically from loaded log entries

function LogLine({ entry }: { entry: LogEntry }) {
  const time = new Date(entry.timestamp);
  const timeStr = time.toLocaleTimeString("en-US", { hour12: false, hour: "2-digit", minute: "2-digit", second: "2-digit" });

  return (
    <motion.div
      initial={{ opacity: 0, y: 4 }}
      animate={{ opacity: 1, y: 0 }}
      transition={{ duration: 0.15 }}
      className="flex items-start gap-3 px-4 py-1.5 text-xs font-mono border-b border-[var(--color-border-subtle)] hover:bg-[var(--color-bg-muted)] transition-colors"
    >
      <span className="text-[var(--color-text-muted)] shrink-0 w-16">{timeStr}</span>
      <Badge variant={LEVEL_VARIANT[entry.level]} size="sm" className="shrink-0">
        {entry.level}
      </Badge>
      <span className="text-[var(--color-text-muted)] shrink-0 w-16 truncate">{entry.source}</span>
      <span className="text-[var(--color-text)] min-w-0 break-all">{entry.message}</span>
    </motion.div>
  );
}

export default function Logs() {
  const queryClient = useQueryClient();
  const [filterLevel, setFilterLevel] = useState("");
  const [filterSource, setFilterSource] = useState("");
  const [autoScroll, setAutoScroll] = useState(true);
  const [paused, setPaused] = useState(false);
  const [localEntries, setLocalEntries] = useState<LogEntry[]>([]);
  const [streamDisconnected, setStreamDisconnected] = useState(false);
  const scrollRef = useRef<HTMLDivElement>(null);
  const logIdSet = useRef(new Set<string>());
  const wasDisconnected = useRef(false);
  const pausedBuffer = useRef<LogEntry[]>([]);
  // Latest `paused` without re-running (and thus tearing down) the stream
  // effect on every pause/resume toggle.
  const pausedRef = useRef(paused);
  pausedRef.current = paused;

  // Fetch historical logs
  const { data: history, isLoading, error, refetch, isRefetching } = useQuery({
    queryKey: ["logs"],
    queryFn: () => client.getLogs(),
  });

  // Initialize from history
  useEffect(() => {
    if (history) {
      logIdSet.current.clear();
      setLocalEntries(history);
      history.forEach((e) => logIdSet.current.add(e.id));
    }
  }, [history]);

  // Subscribe to live stream (stays open even when paused — entries are buffered)
  useEffect(() => {
    const unsub = client.subscribeLogs(
      (entry) => {
        // Detect reconnect: if we were previously disconnected, invalidate
        // the history query to fill any gaps that occurred during the outage.
        if (wasDisconnected.current) {
          wasDisconnected.current = false;
          setStreamDisconnected(false);
          queryClient.invalidateQueries({ queryKey: ["logs"] });
        }

        if (pausedRef.current) {
          // Buffer entries while paused so they aren't lost
          pausedBuffer.current.push(entry);
          return;
        }

        if (logIdSet.current.has(entry.id)) return;
        logIdSet.current.add(entry.id);
        setLocalEntries((prev) => {
          const next = [...prev, entry];
          if (next.length > 500) {
            const trimmed = next.slice(-500);
            logIdSet.current = new Set(trimmed.map((e) => e.id));
            return trimmed;
          }
          return next;
        });
      },
      () => {
        wasDisconnected.current = true;
        setStreamDisconnected(true);
      },
    );

    return unsub;
  }, [queryClient]);

  // Flush paused buffer when resuming
  useEffect(() => {
    if (paused) return;
    const buf = pausedBuffer.current;
    if (buf.length === 0) return;
    pausedBuffer.current = [];

    setLocalEntries((prev) => {
      let next = prev;
      for (const entry of buf) {
        if (logIdSet.current.has(entry.id)) continue;
        logIdSet.current.add(entry.id);
        next = [...next, entry];
      }
      if (next.length > 500) {
        const trimmed = next.slice(-500);
        logIdSet.current = new Set(trimmed.map((e) => e.id));
        return trimmed;
      }
      return next;
    });
  }, [paused]);

  // Auto-scroll
  useEffect(() => {
    if (autoScroll && scrollRef.current) {
      scrollRef.current.scrollTop = scrollRef.current.scrollHeight;
    }
  }, [localEntries, autoScroll]);

  const clearLogs = useCallback(() => {
    logIdSet.current.clear();
    setLocalEntries([]);
  }, []);

  // Derive source filter options dynamically from loaded entries
  const sourceOptions = useMemo(() => {
    const sources = [...new Set(localEntries.map((e) => e.source))].sort();
    const options: Array<{ value: string; label: string }> = [
      { value: "", label: "All sources" },
    ];
    for (const src of sources) {
      options.push({ value: src, label: src });
    }
    return options;
  }, [localEntries]);

  // Filter
  const filtered = localEntries.filter((e) => {
    if (filterLevel && e.level !== filterLevel) return false;
    if (filterSource && e.source !== filterSource) return false;
    return true;
  });

  if (isLoading) {
    return (
      <div className="p-6 space-y-4 max-w-5xl">
        <Skeleton className="h-6 w-24" />
        <Card padding="none">
          <div className="px-4 py-3 space-y-2">
            {Array.from({ length: 5 }, (_, i) => (
              <Skeleton key={i} className="h-4 w-full" />
            ))}
          </div>
        </Card>
      </div>
    );
  }

  if (error) {
    return (
      <div className="p-6 max-w-5xl">
        <EmptyState
          title="Failed to load logs"
          description={error.message}
          action={<Button variant="secondary" size="sm" onClick={() => refetch()} loading={isRefetching}>Retry</Button>}
        />
      </div>
    );
  }

  return (
    <div className="p-6 space-y-4 max-w-5xl flex flex-col h-full">
      <div>
        <h2 className="text-lg font-semibold text-[var(--color-text-heading)]">
          Logs
        </h2>
        <p className="text-xs text-[var(--color-text-muted)] mt-0.5">
          Streaming container and scheduler logs.
        </p>
      </div>

      {/* Toolbar */}
      <div className="flex flex-wrap items-center gap-3">
        <Select
          label="Filter by level"
          options={LEVEL_OPTIONS}
          value={filterLevel}
          onChange={(e) => setFilterLevel(e.target.value)}
        />
        <Select
          label="Filter by source"
          options={sourceOptions}
          value={filterSource}
          onChange={(e) => setFilterSource(e.target.value)}
        />
        <div className="flex-1" />
        <Button
          variant={paused ? "primary" : "ghost"}
          size="sm"
          onClick={() => setPaused((p) => !p)}
        >
          {paused ? <Play className="size-3" /> : <Pause className="size-3" />}
          {paused ? "Resume" : "Pause"}
        </Button>
        <Button
          variant={autoScroll ? "secondary" : "ghost"}
          size="sm"
          onClick={() => setAutoScroll((p) => !p)}
        >
          <ArrowDown className="size-3" />
          Follow
        </Button>
        <Button variant="ghost" size="sm" onClick={clearLogs}>
          <Trash2 className="size-3" />
          Clear
        </Button>
      </div>

      {/* Log viewer */}
      <Card padding="none" className="flex-1 min-h-0">
        <div className="px-4 py-2 border-b border-[var(--color-border)] flex items-center gap-2">
          <span className="text-[10px] text-[var(--color-text-muted)] uppercase tracking-wider font-medium">
            {filtered.length} entries
          </span>
          {!paused && !streamDisconnected && (
            <div className="flex items-center gap-1.5 ml-auto">
              <span className="size-1.5 rounded-full bg-[var(--color-status-running)] animate-pulse" />
              <span className="text-[10px] text-[var(--color-text-muted)]">streaming</span>
            </div>
          )}
          {streamDisconnected && (
            <Badge variant="error" size="sm" className="ml-auto">
              stream disconnected
            </Badge>
          )}
          {paused && (
            <span className="text-[10px] text-[var(--color-status-warning)] ml-auto">paused</span>
          )}
        </div>
        <div
          ref={scrollRef}
          className="overflow-y-auto max-h-[60vh] font-mono text-xs divide-y divide-[var(--color-border-subtle)]"
          role="log"
          aria-label="Log entries"
        >
          <AnimatePresence initial={false}>
            {filtered.length > 0 ? (
              filtered.map((entry) => <LogLine key={entry.id} entry={entry} />)
            ) : (
              <div className="px-4 py-8 text-center text-sm text-[var(--color-text-muted)]">
                {localEntries.length === 0 ? "No logs yet — waiting for events..." : "No logs match the current filter"}
              </div>
            )}
          </AnimatePresence>
        </div>
      </Card>

      {/* Accessible live region for screen readers */}
      <div className="sr-only" aria-live="polite" aria-atomic="true">
        {filtered.length} log entries
        {!paused && " — streaming"}
      </div>
    </div>
  );
}
