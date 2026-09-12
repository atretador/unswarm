import { useState, useEffect, useRef, useCallback, useMemo } from "react";
import { useTranslation } from "react-i18next";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { motion, AnimatePresence } from "motion/react";
import { Pause, Play, Trash2, ArrowDown } from "lucide-react";
import { client } from "../../lib/query-client";
import i18n from "../../i18n";
import type { LogEntry, LogLevel } from "../../lib/api/types";
import { Card, Badge, Button, Skeleton, EmptyState, Select } from "../../components/ui";

const LEVEL_VARIANT: Record<LogLevel, "default" | "info" | "warning" | "error"> = {
  info: "info",
  warn: "warning",
  error: "error",
  debug: "default",
};

// Level options and source options are computed inside the component using t().

function LogLine({ entry }: { entry: LogEntry }) {
  const time = new Date(entry.timestamp);
  const timeStr = time.toLocaleTimeString(i18n.language === 'pt-BR' ? 'pt-BR' : 'en-US', { hour12: false, hour: "2-digit", minute: "2-digit", second: "2-digit" });

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
  const { t } = useTranslation("logs");
  const { t: tc } = useTranslation("common");
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
      setLocalEntries([...history].reverse());
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
      { value: "", label: t("allSources") },
    ];
    for (const src of sources) {
      options.push({ value: src, label: src });
    }
    return options;
  }, [localEntries, t]);

  // Level filter options translated
  const levelOptions = useMemo(() => [
    { value: "", label: t("allLevels") },
    { value: "info", label: t("levels.info") },
    { value: "warn", label: t("levels.warn") },
    { value: "error", label: t("levels.error") },
    { value: "debug", label: t("levels.debug") },
  ], [t]);

  // Filter and sort oldest→newest
  const filtered = useMemo(
    () =>
      localEntries
        .filter((e) => {
          if (filterLevel && e.level !== filterLevel) return false;
          if (filterSource && e.source !== filterSource) return false;
          return true;
        })
        .sort((a, b) => {
          const d = new Date(a.timestamp).getTime() - new Date(b.timestamp).getTime();
          return d !== 0 ? d : a.id.localeCompare(b.id);
        }),
    [localEntries, filterLevel, filterSource],
  );

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
          title={t("failedToLoad")}
          description={error.message}
          action={<Button variant="secondary" size="sm" onClick={() => refetch()} loading={isRefetching}>{tc("retry")}</Button>}
        />
      </div>
    );
  }

  return (
    <div className="p-6 space-y-4 max-w-5xl flex flex-col h-full">
      <div>
        <h2 className="text-lg font-semibold text-[var(--color-text-heading)]">
          {t("title")}
        </h2>
        <p className="text-xs text-[var(--color-text-muted)] mt-0.5">
          {t("subtitle")}
        </p>
      </div>

      {/* Toolbar */}
      <div className="flex flex-wrap items-center gap-3">
        <Select
          label={t("filterByLevel")}
          options={levelOptions}
          value={filterLevel}
          onChange={(e) => setFilterLevel(e.target.value)}
        />
        <Select
          label={t("filterBySource")}
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
          {paused ? t("resume") : t("pause")}
        </Button>
        <Button
          variant={autoScroll ? "secondary" : "ghost"}
          size="sm"
          onClick={() => setAutoScroll((p) => !p)}
        >
          <ArrowDown className="size-3" />
          {t("follow")}
        </Button>
        <Button variant="ghost" size="sm" onClick={clearLogs}>
          <Trash2 className="size-3" />
          {t("clear")}
        </Button>
      </div>

      {/* Log viewer */}
      <Card padding="none" className="flex-1 min-h-0 flex flex-col">
        <div className="px-4 py-2 border-b border-[var(--color-border)] flex items-center gap-2">
          <span className="text-[10px] text-[var(--color-text-muted)] uppercase tracking-wider font-medium">
            {t("entries", { count: filtered.length })}
          </span>
          {!paused && !streamDisconnected && (
            <div className="flex items-center gap-1.5 ml-auto">
              <span className="size-1.5 rounded-full bg-[var(--color-status-running)] animate-pulse" />
              <span className="text-[10px] text-[var(--color-text-muted)]">{t("streaming")}</span>
            </div>
          )}
          {streamDisconnected && (
            <Badge variant="error" size="sm" className="ml-auto">
              {t("streamDisconnected")}
            </Badge>
          )}
          {paused && (
            <span className="text-[10px] text-[var(--color-status-warning)] ml-auto">{t("paused")}</span>
          )}
        </div>
        <div
          ref={scrollRef}
          className="flex-1 min-h-0 overflow-y-auto font-mono text-xs divide-y divide-[var(--color-border-subtle)]"
          role="log"
          aria-label={t("entries", { count: filtered.length })}
        >
          <AnimatePresence initial={false}>
            {filtered.length > 0 ? (
              filtered.map((entry) => <LogLine key={entry.id} entry={entry} />)
            ) : (
              <div className="px-4 py-8 text-center text-sm text-[var(--color-text-muted)]">
                {localEntries.length === 0 ? t("noLogs") : t("noLogsFiltered")}
              </div>
            )}
          </AnimatePresence>
        </div>
      </Card>

      {/* Accessible live region for screen readers */}
      <div className="sr-only" aria-live="polite" aria-atomic="true">
        {t("srLogEntries", { count: filtered.length })}
        {!paused && t("srStreaming")}
      </div>
    </div>
  );
}
