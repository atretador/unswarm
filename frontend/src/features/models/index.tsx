import { useQuery, useMutation, useQueryClient } from "@tanstack/react-query";
import { useState, useMemo, useEffect, useRef } from "react";
import { motion } from "motion/react";
import {
  Box,
  Cloud,
  Clock,
  ExternalLink,
  Gauge,
  Hash,
  MessageSquare,
  Pencil,
  Search,
  Server,
  Trash2,
} from "lucide-react";
import { Link, useSearchParams } from "react-router-dom";
import { useTranslation } from "react-i18next";
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
  Dialog,
  Input,
} from "../../components/ui";
import type { Model, ModelStatus, Settings } from "../../lib/api/types";
import { formatModelName } from "../../lib/format-model-name";
import { enumLabel } from "../../i18n/enum-map";
import { formatTokensPerSec, formatLatency, formatTokens } from "../../i18n/format";
import { TestChatDrawer } from "./test-chat-drawer";
import { CloudModelSelector } from "./cloud-model-selector";

// ─── Status semantics — identical to the Swarm page palette ───────

const MODEL_STATUS_VARIANT: Record<ModelStatus, "success" | "warning" | "error" | "default"> = {
  ready: "success",
  validating: "warning",
  invalid: "error",
  deprecated: "default",
  conflict: "error",
};

// MODEL_STATUS_LABEL is now provided via enumLabel('modelStatus', status)

function formatContextWindow(n: number | undefined): string {
  if (!n || n <= 0) return "";
  if (n >= 1_000_000) return `${(n / 1_000_000).toFixed(n % 1_000_000 === 0 ? 0 : 1)}M`;
  if (n >= 1_000) return `${Math.round(n / 1_000)}K`;
  return `${n}`;
}

function formatRelativeTime(iso: string, t: (key: string, options?: Record<string, unknown>) => string): string {
  const diff = Date.now() - new Date(iso).getTime();
  if (diff < 60_000) return t('time.justNow');
  if (diff < 3_600_000) return t('time.minutesAgo', { count: Math.floor(diff / 60_000) });
  if (diff < 86_400_000) return t('time.hoursAgo', { count: Math.floor(diff / 3_600_000) });
  return t('time.daysAgo', { count: Math.floor(diff / 86_400_000) });
}

// ─── Test-chat trigger — shared by Managed and Cloud rows ─────────

function TestChatButton({ model, onChat }: { model: Model; onChat: (model: Model) => void }) {
  const { t } = useTranslation('models');
  const invalid = model.status === "invalid";
  const conflicted = model.status === "conflict";
  const modelName = model.displayName || model.name;
  return (
    <Tooltip
      content={invalid ? t('tooltips.modelInvalid') : conflicted ? t('tooltips.modelConflict') : t('tooltips.testChat', { name: modelName })}
    >
      <button
        type="button"
        onClick={() => !invalid && onChat(model)}
        disabled={invalid}
        aria-label={t('tooltips.testChat', { name: modelName })}
        className="flex size-7 items-center justify-center rounded-[var(--radius-md)] text-[var(--color-text-muted)] transition-colors hover:bg-[var(--color-bg-muted)] hover:text-[var(--color-primary)] focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-[var(--color-focus-ring)] disabled:pointer-events-none disabled:opacity-40"
      >
        <MessageSquare className="size-3.5" />
      </button>
    </Tooltip>
  );
}

// ─── Model row (Managed / Swarm) ─────────────────────────────────

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

function ManagedModelRow({ model, index, settings, isSelected, onChat }: { model: Model; index: number; settings?: Settings; isSelected?: boolean; onChat: (model: Model) => void }) {
  const { t } = useTranslation('models');
  const { t: tCommon } = useTranslation('common');
  const bench = model.lastBenchmark;
  const queryClient = useQueryClient();
  const [deleting, setDeleting] = useState(false);
  const [showConfirm, setShowConfirm] = useState(false);
  const [showWarning, setShowWarning] = useState(false);

  // A model with no source runtime is orphaned — treat as deprecated for display
  const effectiveStatus: ModelStatus = (!model.sourceRuntimeId && model.status === "ready") ? "deprecated" : model.status;

  // Edit state
  const [editing, setEditing] = useState(false);
  const [editName, setEditName] = useState("");
  const [editDisplayName, setEditDisplayName] = useState("");
  const [editFamily, setEditFamily] = useState("");
  const [editParamSize, setEditParamSize] = useState("");
  const [editQuant, setEditQuant] = useState("");
  const [editCtxWindow, setEditCtxWindow] = useState("");
  const [editMaxOutputTokens, setEditMaxOutputTokens] = useState("");
  const [editThinkingEfforts, setEditThinkingEfforts] = useState("");
  const [editError, setEditError] = useState<string | null>(null);

  const updateMutation = useMutation({
    mutationFn: (data: { displayName: string | null; family: string; parameterSize: string; quantization: string; contextWindow: number; maxOutputTokens?: number | null; supportedThinkingEffortsJson: string | null }) =>
      client.updateModel(model.id, data as unknown as Partial<Model>),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["models"] });
      setEditing(false);
    },
    onError: (err: Error) => setEditError(err.message),
  });

  const handleEdit = () => {
    setEditName(model.name);
    setEditDisplayName(model.displayName ?? "");
    setEditFamily(model.family ?? "");
    setEditParamSize(model.parameterSize ?? "");
    setEditQuant(model.quantization ?? "");
    setEditCtxWindow(model.contextWindow?.toString() ?? "");
    setEditMaxOutputTokens(model.maxOutputTokens?.toString() ?? "");
    setEditThinkingEfforts(model.supportedThinkingEfforts?.join(", ") ?? "");
    setEditError(null);
    setEditing(true);
  };

  const handleSaveEdit = () => {
    const ctxWindow = editCtxWindow ? parseInt(editCtxWindow, 10) : 0;
    if (editCtxWindow && (isNaN(ctxWindow) || ctxWindow <= 0 || ctxWindow > 10_000_000)) {
      setEditError(t('errors.contextWindowInvalid'));
      return;
    }
    const maxOut = editMaxOutputTokens ? parseInt(editMaxOutputTokens, 10) : undefined;
    if (editMaxOutputTokens && (maxOut === undefined || isNaN(maxOut) || maxOut <= 0 || maxOut > 10_000_000)) {
      setEditError(t('errors.maxOutputTokensInvalid'));
      return;
    }
    setEditError(null);
    updateMutation.mutate({
      displayName: editDisplayName.trim() || null,
      family: editFamily.trim(),
      parameterSize: editParamSize.trim(),
      quantization: editQuant.trim(),
      contextWindow: ctxWindow,
      maxOutputTokens: maxOut,
      supportedThinkingEffortsJson: editThinkingEfforts.trim()
        ? JSON.stringify(editThinkingEfforts.split(",").map(s => s.trim()).filter(Boolean))
        : null,
    });
  };

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
      data-model-id={model.id}
    >
      <div
        className="flex flex-wrap items-center gap-x-4 gap-y-3 px-4 py-3.5 border-b border-[var(--color-border-subtle)] last:border-0 hover:bg-[var(--color-bg-muted)] transition-colors"
        style={isSelected ? {
          boxShadow: "0 0 0 2px var(--color-primary), 0 0 12px 2px color-mix(in srgb, var(--color-primary) 25%, transparent)",
          borderLeft: "3px solid var(--color-primary)",
          backgroundColor: "color-mix(in srgb, var(--color-primary) 5%, transparent)",
        } : undefined}
      >
        {/* Name + status */}
        <div className="flex min-w-0 flex-1 basis-52 items-center gap-2.5">
          <StatusDot status={model.status} size="sm" />
          <div className="min-w-0">
            <p className="truncate font-mono text-xs font-medium text-[var(--color-text-heading)]">
              {formatModelName(model.name, model.sourceRuntimeAgent ?? "local", settings?.hideOriginPrefix ?? false, settings?.agentDisplayNames ?? {}, undefined, model.displayName)}
            </p>
            <p className="mt-0.5 truncate text-[10px] text-[var(--color-text-muted)]">
              {[
                model.family,
                model.parameterSize,
                model.quantization,
                model.contextWindow ? `${formatContextWindow(model.contextWindow)} ctx` : null,
              ].filter(Boolean).join(" · ")}
            </p>
          </div>
          <Badge variant={MODEL_STATUS_VARIANT[effectiveStatus]} className="shrink-0">
            {enumLabel('modelStatus', effectiveStatus)}
          </Badge>
          {effectiveStatus === "conflict" && (
            <span className="text-[10px] text-[var(--color-status-error)] leading-tight">
              {t('conflictHint')}
            </span>
          )}
          {effectiveStatus === "deprecated" && model.sourceRuntimeId === null && (
            <span className="text-[10px] text-[var(--color-status-stopped)] leading-tight">
              {t('deprecatedHint')}
            </span>
          )}
        </div>

        {/* Last benchmark */}
        <div className="flex min-w-0 basis-52 items-center gap-2">
          {bench ? (
            <div className="flex min-w-0 flex-wrap items-center gap-1.5">
              <MetricChip
                icon={<Gauge className="size-2.5" />}
                label={t('benchmarks.speedLabel')}
                value={formatTokensPerSec(bench.tokensPerSec)}
                title={t('tooltips.tokensPerSecond')}
              />
              <MetricChip
                icon={<Clock className="size-2.5" />}
                label={t('benchmarks.processingLabel')}
                value={formatLatency(bench.latencyMs)}
                title={t('tooltips.timeToFirstToken')}
              />
              {bench.tokensGenerated !== undefined && (
                <MetricChip
                  icon={<Hash className="size-2.5" />}
                  label={t('benchmarks.tokensLabel')}
                  value={formatTokens(bench.tokensGenerated)}
                  title={t('tooltips.tokensGenerated')}
                />
              )}
              <MetricChip
                icon={<Clock className="size-2.5" />}
                label={t('benchmarks.ran')}
                value={formatRelativeTime(bench.timestamp, t)}
                title={t('benchmarks.lastRun', { date: new Date(bench.timestamp).toLocaleString() })}
              />
              {bench.promptName && (
                <span className="truncate text-[10px] text-[var(--color-text-muted)]">
                  {bench.promptName}{bench.promptVersion != null ? ` v${bench.promptVersion}` : ""}
                </span>
              )}
            </div>
          ) : (
            <p className="text-xs text-[var(--color-text-muted)]">{t('benchmarks.notBenchmarked')}</p>
          )}
        </div>

        {/* Actions */}
        <div className="ml-auto flex shrink-0 items-center gap-2">
          <TestChatButton model={model} onChat={onChat} />
          {model.sourceRuntimeId ? (
            <Link
              to={`/swarm?focus=${encodeURIComponent(model.sourceRuntimeId)}`}
              aria-label={t('viewRuntime')}
              className="inline-flex items-center gap-1.5 rounded-[var(--radius-md)] border border-[var(--color-border)] bg-[var(--color-bg-surface)] px-2 py-1 text-[10px] text-[var(--color-text-muted)] transition-colors hover:bg-[var(--color-bg-muted)] hover:text-[var(--color-text)] focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-[var(--color-focus-ring)]"
            >
              <ExternalLink className="size-2.5" />
              <span className="truncate max-w-[140px]">{model.sourceRuntimeName || model.sourceRuntimeId}</span>
            </Link>
          ) : (
            <span className="text-[10px] italic text-[var(--color-text-muted)]">{t('notRegistered')}</span>
          )}
          <Tooltip content={t('editDetails')}>
            <button
              onClick={handleEdit}
              aria-label={t('editModelAria', { name: model.displayName || model.name })}
              className="flex size-7 items-center justify-center rounded-[var(--radius-md)] text-[var(--color-text-muted)] transition-colors hover:bg-[var(--color-bg-muted)] hover:text-[var(--color-text)] focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-[var(--color-focus-ring)]"
            >
              <Pencil className="size-3.5" />
            </button>
          </Tooltip>
          <Tooltip content={tCommon('delete')}>
            <button
              onClick={() => {
                if (effectiveStatus === "deprecated" || !model.sourceRuntimeId) {
                  setShowConfirm(true);
                } else {
                  setShowWarning(true);
                }
              }}
              disabled={deleting}
              aria-label={t('deleteModelAria', { name: model.displayName || model.name })}
              className="flex size-7 items-center justify-center rounded-[var(--radius-md)] text-[var(--color-status-stopped)] transition-colors hover:bg-[var(--color-bg-muted)] focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-[var(--color-focus-ring)] disabled:opacity-50"
            >
              <Trash2 className="size-3.5" />
            </button>
          </Tooltip>
          {/* Direct delete confirm — for deprecated / orphaned models */}
          <ConfirmDialog
            open={showConfirm}
            title={t('deleteTitle', { name: model.displayName || model.name })}
            description={t('deleteModelDesc')}
            confirmLabel={tCommon('delete')}
            loading={deleting}
            onConfirm={handleDelete}
            onCancel={() => setShowConfirm(false)}
          />
          {/* Warning dialog — for active runtime-tied models */}
          <ConfirmDialog
            open={showWarning}
            title={t('removeTitle', { name: model.displayName || model.name })}
            description={t('removeFromRegistryDesc')}
            confirmLabel={t('removeFromRegistry')}
            loading={deleting}
            onConfirm={handleDelete}
            onCancel={() => setShowWarning(false)}
          />
        </div>
      </div>

      {/* Edit Model Dialog */}
      <Dialog open={editing} onOpenChange={(o) => !o && setEditing(false)} title={t('editModel')}>
        <div className="p-5 space-y-4">
          {model.sourceRuntimeAgent && (
            <p className="text-xs text-[var(--color-text-muted)] mb-1">
              {model.sourceRuntimeName
                ? `${model.sourceRuntimeName} / managed/${model.sourceRuntimeAgent}/`
                : `managed/${model.sourceRuntimeAgent}/`}
            </p>
          )}
          <Input
            label={t('form.displayName')}
            value={editDisplayName}
            onChange={(e) => setEditDisplayName(e.target.value)}
            placeholder={t('placeholders.displayName')}
          />
          <Input
            label={t('form.internalName')}
            value={editName}
            readOnly
            className="opacity-60 cursor-not-allowed"
          />
          <div className="grid grid-cols-2 gap-3">
            <Input
              label={t('form.family')}
              value={editFamily}
              onChange={(e) => setEditFamily(e.target.value)}
              placeholder={t('placeholders.family')}
            />
            <Input
              label={t('form.parameterSize')}
              value={editParamSize}
              onChange={(e) => setEditParamSize(e.target.value)}
              placeholder={t('placeholders.parameterSize')}
            />
            <Input
              label={t('form.quantization')}
              value={editQuant}
              onChange={(e) => setEditQuant(e.target.value)}
              placeholder={t('placeholders.quantization')}
            />
            <Input
              label={t('form.contextWindow')}
              value={editCtxWindow}
              onChange={(e) => setEditCtxWindow(e.target.value)}
              placeholder={t('placeholders.contextWindow')}
              type="number"
              min={1}
              max={10000000}
            />
            <Input
              label={t('form.maxOutputTokens')}
              value={editMaxOutputTokens}
              onChange={(e) => setEditMaxOutputTokens(e.target.value)}
              placeholder={t('placeholders.maxOutputTokens')}
              type="number"
              min={1}
              max={10000000}
            />
          </div>
          <div className="grid gap-2">
            <Input
              label="Supported Thinking Efforts"
              value={editThinkingEfforts}
              onChange={(e) => setEditThinkingEfforts(e.target.value)}
              placeholder="e.g. none, low, medium, high"
            />
            <p className="text-xs text-[var(--color-text-muted)]">
              Comma-separated effort levels this model supports
            </p>
          </div>
          {editError && (
            <p className="text-sm text-[var(--color-status-error)]">{editError}</p>
          )}
          <div className="flex justify-end gap-2 pt-1">
            <Button
              variant="secondary"
              size="sm"
              onClick={() => setEditing(false)}
              disabled={updateMutation.isPending}
            >
              {tCommon('cancel')}
            </Button>
            <Button
              variant="primary"
              size="sm"
              loading={updateMutation.isPending}
              onClick={handleSaveEdit}
            >
              {tCommon('save')}
            </Button>
          </div>
        </div>
      </Dialog>
    </motion.div>
  );
}

// ─── Main Models page ─────────────────────────────────────────────

type Tab = "managed" | "cloud";

const TABS: { key: Tab; icon: typeof Server }[] = [
  { key: "managed", icon: Server },
  { key: "cloud", icon: Cloud },
];

export default function Models() {
  const { t } = useTranslation('models');
  const [searchParams, setSearchParams] = useSearchParams();
  const selectedModelId = searchParams.get("selected");
  const clearTimerRef = useRef<ReturnType<typeof setTimeout> | null>(null);

  const [filter, setFilter] = useState("");
  const [activeTab, setActiveTab] = useState<Tab>("managed");
  const [agentFilter, setAgentFilter] = useState("");
  // Test-chat drawer: track the selected model ID so the row object stays fresh
  // (status may flip to invalid while the drawer is open).
  const [chatModelId, setChatModelId] = useState<string | null>(null);
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

  const { data: settings } = useQuery({
    queryKey: ["settings"],
    queryFn: () => client.getSettings(),
  });

  // Auto-switch tab based on selected model ID
  useEffect(() => {
    if (!selectedModelId) return;
    const isCloud = selectedModelId.startsWith("cloud/");
    setActiveTab(isCloud ? "cloud" : "managed");
    setFilter("");
    setAgentFilter("");
  }, [selectedModelId]);

  // Auto-scroll to selected model and clear URL param after delay
  useEffect(() => {
    if (!selectedModelId || !models) return;

    // Wait for the model to appear in the DOM, then scroll
    const timer = setTimeout(() => {
      const el = document.querySelector(`[data-model-id="${CSS.escape(selectedModelId)}"]`) as HTMLDivElement | null;
      if (el) {
        el.scrollIntoView({ behavior: "smooth", block: "center" });
      }
    }, 100);

    // Clear the ?selected= param after 3 seconds
    if (clearTimerRef.current) clearTimeout(clearTimerRef.current);
    clearTimerRef.current = setTimeout(() => {
      setSearchParams((prev) => {
        prev.delete("selected");
        return prev;
      }, { replace: true });
    }, 3000);

    return () => {
      clearTimeout(timer);
      if (clearTimerRef.current) clearTimeout(clearTimerRef.current);
    };
  }, [selectedModelId, models, setSearchParams]);

  const { managedModels, cloudModels } = useMemo(() => {
    const all = models ?? [];
    return {
      managedModels: all.filter((m) => m.origin !== "cloud"),
      cloudModels: all.filter((m) => m.origin === "cloud"),
    };
  }, [models]);

  const chatModel = chatModelId ? (models ?? []).find((m) => m.id === chatModelId) ?? null : null;

  const activeModels = activeTab === "managed" ? managedModels : cloudModels;

  const uniqueAgents = useMemo(() => {
    if (managedModels.length === 0) return [];
    const names = [...new Set(managedModels.map((m) => m.sourceRuntimeAgent).filter(Boolean))];
    return names as string[];
  }, [managedModels]);

  const filteredModels = useMemo(() => {
    let result = activeModels;
    const q = filter.trim().toLowerCase();
    if (q && activeTab === "managed") {
      result = result.filter((m) =>
        m.name.toLowerCase().includes(q) ||
        m.family.toLowerCase().includes(q) ||
        m.parameterSize.toLowerCase().includes(q) ||
        (m.sourceRuntimeName ?? "").toLowerCase().includes(q),
      );
    }
    if (q && activeTab === "cloud") {
      result = result.filter((m) =>
        m.name.toLowerCase().includes(q) ||
        (m.providerName ?? "").toLowerCase().includes(q),
      );
    }
    if (activeTab === "managed" && agentFilter) {
      result = result.filter((m) => m.sourceRuntimeAgent === agentFilter);
    }
    return result;
  }, [activeModels, filter, activeTab, agentFilter]);

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
          title={t('failedToLoad')}
          description={error.message}
          action={
            <Button variant="secondary" size="sm" onClick={() => refetch()} loading={isRefetching}>
              {t('retry')}
            </Button>
          }
        />
      </div>
    );
  }

  const totalModels = (models ?? []).length;
  const hasAnyModels = totalModels > 0;

  return (
    <div className="max-w-5xl space-y-6 p-6">
      {/* Header */}
      <div>
        <h2 className="text-lg font-semibold text-[var(--color-text-heading)]">{t('title')}</h2>
        <p className="mt-0.5 text-xs text-[var(--color-text-muted)]">
          {t('subtitle')}
        </p>
      </div>

      {/* Tab bar */}
      {hasAnyModels && (
        <div role="tablist" aria-label={t('tabAriaLabel')} className="flex border-b border-[var(--color-border-subtle)]">
          {TABS.map((tab) => {
            const count = tab.key === "managed" ? managedModels.length : cloudModels.length;
            return (
              <button
                key={tab.key}
                type="button"
                role="tab"
                id={`models-tab-${tab.key}`}
                aria-selected={activeTab === tab.key}
                aria-controls="models-panel"
                onClick={() => { setActiveTab(tab.key); setFilter(""); setAgentFilter(""); }}
                className={`
                  flex items-center gap-1.5 px-4 py-2 text-xs font-medium transition-colors
                  border-b-2 -mb-px
                  ${
                    activeTab === tab.key
                      ? "border-b-2 border-[var(--color-primary)] text-[var(--color-primary)]"
                      : "border-b-2 border-transparent text-[var(--color-text-muted)] hover:text-[var(--color-text)]"
                  }
                `}
              >
                <tab.icon className="size-3.5" />
                {t(`tabs.${tab.key}`)}
                <Badge variant={activeTab === tab.key ? "info" : "default"} size="sm">
                  {count}
                </Badge>
              </button>
            );
          })}
        </div>
      )}

      {/* Search */}
      {hasAnyModels && (
        <div className="relative">
          <Search className="pointer-events-none absolute left-3 top-1/2 size-3.5 -translate-y-1/2 text-[var(--color-text-muted)]" />
          <input
            type="search"
            value={filter}
            onChange={(e) => setFilter(e.target.value)}
            placeholder={
              activeTab === "managed"
                ? t('searchManaged')
                : t('searchCloud')
            }
            aria-label={t('searchAriaLabel')}
            className="h-8 w-full rounded-[var(--radius-lg)] border border-[var(--color-border)] bg-[var(--color-bg-surface)] pl-8 pr-3 text-sm text-[var(--color-text)] placeholder:text-[var(--color-text-muted)] outline-none transition-colors focus:border-[var(--color-focus-ring)] focus:ring-1 focus:ring-[var(--color-focus-ring)]"
          />
        </div>
      )}

      {/* Agent filter (Managed tab) */}
      {activeTab === "managed" && uniqueAgents.length > 0 && (
        <div className="flex items-center gap-2">
          <label className="text-xs text-[var(--color-text-muted)]">{t('agentFilterLabel')}</label>
          <select
            value={agentFilter}
            onChange={(e) => setAgentFilter(e.target.value)}
            className="h-8 rounded-[var(--radius-lg)] border border-[var(--color-border)] bg-[var(--color-bg-surface)] px-2 text-sm text-[var(--color-text)] outline-none focus:border-[var(--color-focus-ring)]"
          >
            <option value="">{t('allAgents')}</option>
            {uniqueAgents.map((a) => (
              <option key={a} value={a}>{a}</option>
            ))}
          </select>
        </div>
      )}

      {/* Tab content */}
      <div role="tabpanel" id="models-panel" aria-labelledby={`models-tab-${activeTab}`}>
        {totalModels === 0 ? (
          <Card padding="none">
            <EmptyState
              icon={<Box className="size-12" strokeWidth={1.5} />}
              title={t('empty.noModels')}
              description={t('empty.noModelsDesc')}
            />
          </Card>
        ) : filteredModels.length === 0 ? (
          <Card padding="none">
            <EmptyState
              icon={<Search className="size-12" strokeWidth={1.5} />}
              title={t('empty.noMatch')}
              description={t('empty.noMatchDesc')}
            />
          </Card>
        ) : (
          <>
            {activeTab === "cloud" && (
              <CloudModelSelector filter={filter} cloudModels={cloudModels} onChatModel={(modelName) => {
                const found = (models ?? []).find((m) => m.name === modelName);
                if (found) setChatModelId(found.id);
              }} />
            )}
            {activeTab === "managed" && (
              <Card padding="none">
                {filteredModels.map((model, i) => (
                  <ManagedModelRow key={model.id} model={model} index={i} settings={settings} isSelected={model.id === selectedModelId} onChat={(m) => setChatModelId(m.id)} />
                ))}
              </Card>
            )}
          </>
        )}
      </div>

      {/* Test chat drawer */}
      <TestChatDrawer
        model={chatModel}
        open={chatModel !== null}
        settings={settings}
        onClose={() => setChatModelId(null)}
      />
    </div>
  );
}
