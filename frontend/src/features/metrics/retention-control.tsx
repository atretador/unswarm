// Usage-retention control for the metrics toolbar area.
//
// Shows the current usageRetentionDays setting in a small popover with an
// editable number input (saved via PUT /api/settings) and an Admin-only
// "Purge old records" action (DELETE /api/metrics/usage/purge). Non-admins
// hit a 403, which disables the purge button for the rest of the visit.

import { useEffect, useRef, useState } from "react";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { Check, History, Trash2 } from "lucide-react";
import { ApiError } from "../../lib/api/httpClient";
import { client } from "../../lib/query-client";
import { Button, ConfirmDialog } from "../../components/ui";

export interface RetentionControlProps {
  /** Called after a successful purge so metrics can be refetched. */
  onPurged: () => void;
}

export function RetentionControl({ onPurged }: RetentionControlProps) {
  const queryClient = useQueryClient();
  const [open, setOpen] = useState(false);
  const [daysInput, setDaysInput] = useState<string>("");
  const [saving, setSaving] = useState(false);
  const [savedTick, setSavedTick] = useState(false);
  const [confirmOpen, setConfirmOpen] = useState(false);
  const [purging, setPurging] = useState(false);
  const [purgeResult, setPurgeResult] = useState<string | null>(null);
  const [purgeForbidden, setPurgeForbidden] = useState(false);
  const popRef = useRef<HTMLDivElement>(null);

  const { data: settings } = useQuery({
    queryKey: ["settings"],
    queryFn: () => client.getSettings(),
  });

  const retentionDays = settings?.usageRetentionDays ?? 30;

  // Seed the input each time the popover opens.
  useEffect(() => {
    if (open) {
      setDaysInput(String(retentionDays));
      setSavedTick(false);
      setPurgeResult(null);
    }
  }, [open, retentionDays]);

  // Close on outside click / Escape while open.
  useEffect(() => {
    if (!open) return;
    function onPointerDown(e: PointerEvent) {
      if (popRef.current && !popRef.current.contains(e.target as Node)) {
        setOpen(false);
      }
    }
    function onKeyDown(e: KeyboardEvent) {
      if (e.key === "Escape") setOpen(false);
    }
    document.addEventListener("pointerdown", onPointerDown);
    document.addEventListener("keydown", onKeyDown);
    return () => {
      document.removeEventListener("pointerdown", onPointerDown);
      document.removeEventListener("keydown", onKeyDown);
    };
  }, [open]);

  async function saveRetention() {
    const days = Math.max(0, Math.round(Number(daysInput)));
    if (Number.isNaN(days)) return;
    setSaving(true);
    try {
      await client.updateSettings({ usageRetentionDays: days });
      await queryClient.invalidateQueries({ queryKey: ["settings"] });
      setSavedTick(true);
      setTimeout(() => setSavedTick(false), 1500);
    } finally {
      setSaving(false);
    }
  }

  async function handlePurge() {
    setConfirmOpen(false);
    const days = Math.max(0, Math.round(Number(daysInput)));
    setPurging(true);
    try {
      const result = await client.purgeMetricsUsage(Number.isNaN(days) ? retentionDays : days);
      setPurgeResult(`Deleted ${result.deleted.toLocaleString()} record${result.deleted === 1 ? "" : "s"}`);
      setTimeout(() => setPurgeResult(null), 4000);
      onPurged();
    } catch (err) {
      if (err instanceof ApiError && err.status === 403) {
        setPurgeForbidden(true);
        setPurgeResult("Admin role required");
        setTimeout(() => setPurgeResult(null), 4000);
      } else {
        setPurgeResult(err instanceof Error ? err.message : "Purge failed");
        setTimeout(() => setPurgeResult(null), 4000);
      }
    } finally {
      setPurging(false);
    }
  }

  return (
    <div className="relative" ref={popRef}>
      <button
        type="button"
        onClick={() => setOpen((o) => !o)}
        aria-expanded={open}
        title="Usage retention & purge"
        className={`inline-flex items-center gap-1.5 h-8 px-2.5 rounded-[var(--radius-lg)] text-xs font-medium border transition-colors cursor-pointer ${
          open
            ? "border-[var(--color-primary)] text-[var(--color-primary)] bg-[var(--color-bg-surface)]"
            : "border-[var(--color-border)] text-[var(--color-text-muted)] hover:text-[var(--color-text)] bg-[var(--color-bg-surface)] hover:border-[var(--color-border-strong)]"
        }`}
      >
        <History className="size-3.5" />
        <span className="hidden sm:inline">Retention</span>
      </button>

      {open && (
        <div className="absolute right-0 top-full mt-2 z-30 w-72 rounded-[var(--radius-xl)] border border-[var(--color-border)] bg-[var(--color-bg-elevated)] shadow-lg p-4 space-y-3">
          <p className="text-xs font-semibold text-[var(--color-text-heading)]">
            Usage retention
          </p>
          <p className="text-xs text-[var(--color-text-muted)]">
            Raw request records older than this many days are eligible for
            cleanup.
          </p>

          <div className="flex items-end gap-2">
            <label className="flex flex-col gap-1 flex-1">
              <span className="text-[10px] font-medium text-[var(--color-text-muted)]">
                Keep records (days)
              </span>
              <input
                type="number"
                min="0"
                step="1"
                value={daysInput}
                onChange={(e) => setDaysInput(e.target.value)}
                className="h-8 rounded-[var(--radius-lg)] border bg-[var(--color-bg-surface)] px-2.5 text-sm font-mono text-[var(--color-text)] border-[var(--color-border)] focus:outline-none focus:border-[var(--color-primary)] focus:ring-1 focus:ring-[var(--color-focus-ring)] transition-colors w-full"
              />
            </label>
            <Button
              variant="secondary"
              size="sm"
              onClick={saveRetention}
              disabled={saving}
              className="gap-1 shrink-0"
            >
              {savedTick ? (
                <>
                  <Check className="size-3.5 text-[var(--color-status-running)]" />
                  Saved
                </>
              ) : (
                "Save"
              )}
            </Button>
          </div>

          <div className="pt-2 border-t border-[var(--color-border-subtle)]">
            <Button
              variant="danger"
              size="sm"
              onClick={() => setConfirmOpen(true)}
              disabled={purging || purgeForbidden}
              className="gap-1.5 w-full justify-center"
              title={
                purgeForbidden
                  ? "Requires an Admin role"
                  : `Delete all records older than ${daysInput || retentionDays} days`
              }
            >
              <Trash2 className="size-3.5" />
              {purgeForbidden ? "Purge unavailable" : "Purge old records"}
            </Button>
            {purgeResult && (
              <p
                className={`text-xs mt-2 ${
                  purgeResult.startsWith("Deleted")
                    ? "text-[var(--color-status-running)]"
                    : "text-[var(--color-status-error)]"
                }`}
              >
                {purgeResult}
              </p>
            )}
          </div>
        </div>
      )}

      <ConfirmDialog
        open={confirmOpen}
        title="Purge old usage records?"
        description={`This permanently deletes all raw request records older than ${
          daysInput || retentionDays
        } days. Aggregated metrics may shift accordingly. This cannot be undone.`}
        confirmLabel={purging ? "Purging…" : "Purge"}
        loading={purging}
        variant="danger"
        onConfirm={handlePurge}
        onCancel={() => setConfirmOpen(false)}
      />
    </div>
  );
}
