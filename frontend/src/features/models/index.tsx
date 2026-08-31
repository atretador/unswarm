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
import { TestChatDrawer } from "./test-chat-drawer";

// ─── Status semantics — identical to the Swarm page palette ───────

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

// ─── Test-chat trigger — shared by Managed and Cloud rows ─────────

function TestChatButton({ model, onChat }: { model: Model; onChat: (model: Model) => void }) {
  const invalid = model.status === "invalid";
  return (
    <Tooltip
      content={invalid ? "Model invalid — fix registration first" : `Test chat with ${model.name}`}
    >
      <button
        type="button"
        onClick={() => !invalid && onChat(model)}
        disabled={invalid}
        aria-label={`Test chat with ${model.name}`}
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
  const bench = model.lastBenchmark;
  const queryClient = useQueryClient();
  const [deleting, setDeleting] = useState(false);
  const [showConfirm, setShowConfirm] = useState(false);

  // Edit state
  const [editing, setEditing] = useState(false);
  const [editName, setEditName] = useState("");
  const [editError, setEditError] = useState<string | null>(null);

  const updateMutation = useMutation({
    mutationFn: ({ name }: { name: string }) =>
      client.updateModel(model.id, { name }),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["models"] });
      setEditing(false);
    },
    onError: (err: Error) => setEditError(err.message),
  });

  const handleEdit = () => {
    setEditName(model.name);
    setEditError(null);
    setEditing(true);
  };

  const handleSaveEdit = () => {
    if (!editName.trim()) {
      setEditError("Model name is required.");
      return;
    }
    updateMutation.mutate({ name: editName.trim() });
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
              {formatModelName(model.name, model.sourceRuntimeAgent ?? "local", settings?.hideOriginPrefix ?? false, settings?.agentDisplayNames ?? {})}
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
              {bench.promptName && (
                <span className="truncate text-[10px] text-[var(--color-text-muted)]">
                  {bench.promptName}{bench.promptVersion != null ? ` v${bench.promptVersion}` : ""}
                </span>
              )}
            </div>
          ) : (
            <p className="text-xs text-[var(--color-text-muted)]">Not benchmarked yet</p>
          )}
        </div>

        {/* Actions */}
        <div className="ml-auto flex shrink-0 items-center gap-2">
          <TestChatButton model={model} onChat={onChat} />
          {model.sourceRuntimeId ? (
            <Link
              to={`/swarm?focus=${encodeURIComponent(model.sourceRuntimeId)}`}
              aria-label={`View source runtime on the Swarm page`}
              className="inline-flex items-center gap-1.5 rounded-[var(--radius-md)] border border-[var(--color-border)] bg-[var(--color-bg-surface)] px-2 py-1 text-[10px] text-[var(--color-text-muted)] transition-colors hover:bg-[var(--color-bg-muted)] hover:text-[var(--color-text)] focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-[var(--color-focus-ring)]"
            >
              <ExternalLink className="size-2.5" />
              <span className="truncate max-w-[140px]">{model.sourceRuntimeName || model.sourceRuntimeId}</span>
            </Link>
          ) : (
            <span className="text-[10px] italic text-[var(--color-text-muted)]">not registered</span>
          )}
          <Tooltip content="Edit model name">
            <button
              onClick={handleEdit}
              aria-label={`Edit ${model.name}`}
              className="flex size-7 items-center justify-center rounded-[var(--radius-md)] text-[var(--color-text-muted)] transition-colors hover:bg-[var(--color-bg-muted)] hover:text-[var(--color-text)] focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-[var(--color-focus-ring)]"
            >
              <Pencil className="size-3.5" />
            </button>
          </Tooltip>
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

      {/* Edit Model Dialog */}
      <Dialog open={editing} onOpenChange={(o) => !o && setEditing(false)} title="Edit Model Name">
        <div className="p-5 space-y-4">
          {model.sourceRuntimeAgent && (
            <p className="text-xs text-[var(--color-text-muted)] mb-1">
              managed/{model.sourceRuntimeAgent}/
            </p>
          )}
          <Input
            label="Model Name"
            value={editName}
            onChange={(e) => setEditName(e.target.value)}
            placeholder="e.g. llama-3.1-8b"
            autoFocus
            onKeyDown={(e) => {
              if (e.key === "Enter") handleSaveEdit();
              if (e.key === "Escape") setEditing(false);
            }}
          />
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
              Cancel
            </Button>
            <Button
              variant="primary"
              size="sm"
              loading={updateMutation.isPending}
              onClick={handleSaveEdit}
            >
              Save
            </Button>
          </div>
        </div>
      </Dialog>
    </motion.div>
  );
}

// ─── Model row (Cloud) ───────────────────────────────────────────

function CloudModelRow({ model, index, settings, isSelected, onChat }: { model: Model; index: number; settings?: Settings; isSelected?: boolean; onChat: (model: Model) => void }) {
  return (
    <motion.div
      layout
      initial={{ opacity: 0, y: 8 }}
      animate={{ opacity: 1, y: 0 }}
      transition={{ duration: 0.2, delay: Math.min(index * 0.04, 0.3) }}
      data-model-id={model.id}
    >
      <div
        className="flex items-center gap-x-4 px-4 py-3.5 border-b border-[var(--color-border-subtle)] last:border-0 hover:bg-[var(--color-bg-muted)] transition-colors"
        style={isSelected ? {
          boxShadow: "0 0 0 2px var(--color-primary), 0 0 12px 2px color-mix(in srgb, var(--color-primary) 25%, transparent)",
          borderLeft: "3px solid var(--color-primary)",
          backgroundColor: "color-mix(in srgb, var(--color-primary) 5%, transparent)",
        } : undefined}
      >
        {/* Cloud icon + Name + Provider */}
        <div className="flex min-w-0 flex-1 items-center gap-2.5">
          <div className="flex size-7 shrink-0 items-center justify-center rounded-[var(--radius-md)] bg-[color-mix(in_srgb,var(--color-primary)_10%,transparent)]">
            <Cloud className="size-3.5 text-[var(--color-primary)]" />
          </div>
          <div className="min-w-0">
            <p className="truncate font-mono text-xs font-medium text-[var(--color-text-heading)]">
              {formatModelName(model.name, model.providerName ?? "cloud", settings?.hideOriginPrefix ?? false, settings?.agentDisplayNames ?? {})}
            </p>
            <p className="mt-0.5 truncate text-[10px] text-[var(--color-text-muted)]">
              {model.contextWindow.toLocaleString()} context
            </p>
          </div>
        </div>

        {/* Provider badge */}
        <Badge variant="info" className="shrink-0">
          {model.providerName}
        </Badge>

        {/* Status */}
        <div className="shrink-0">
          <Badge variant={MODEL_STATUS_VARIANT[model.status]} className="shrink-0">
            {MODEL_STATUS_LABEL[model.status]}
          </Badge>
        </div>

        {/* Actions */}
        <div className="ml-auto flex shrink-0 items-center gap-2">
          <TestChatButton model={model} onChat={onChat} />
        </div>
      </div>
    </motion.div>
  );
}

// ─── Main Models page ─────────────────────────────────────────────

type Tab = "managed" | "cloud";

const TABS: { key: Tab; label: string; icon: typeof Server }[] = [
  { key: "managed", label: "Managed", icon: Server },
  { key: "cloud", label: "Cloud", icon: Cloud },
];

export default function Models() {
  const [searchParams, setSearchParams] = useSearchParams();
  const selectedModelId = searchParams.get("selected");
  const clearTimerRef = useRef<ReturnType<typeof setTimeout> | null>(null);

  const [filter, setFilter] = useState("");
  const [activeTab, setActiveTab] = useState<Tab>("managed");
  const [providerFilter, setProviderFilter] = useState("");
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
    setProviderFilter("");
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

  const uniqueProviders = useMemo(() => {
    if (cloudModels.length === 0) return [];
    const names = [...new Set(cloudModels.map((m) => m.providerName).filter(Boolean))];
    return names as string[];
  }, [cloudModels]);

  const uniqueAgents = useMemo(() => {
    if (managedModels.length === 0) return [];
    const names = [...new Set(managedModels.map((m) => m.sourceRuntimeAgent).filter(Boolean))];
    return names as string[];
  }, [managedModels]);

  const filteredModels = useMemo(() => {
    let result = activeModels;
    const q = filter.trim().toLowerCase();
    if (q) {
      result = result.filter((m) => {
        if (activeTab === "cloud") {
          return (
            m.name.toLowerCase().includes(q) ||
            (m.providerName ?? "").toLowerCase().includes(q)
          );
        }
        return (
          m.name.toLowerCase().includes(q) ||
          m.family.toLowerCase().includes(q) ||
          m.parameterSize.toLowerCase().includes(q) ||
          (m.sourceRuntimeName ?? "").toLowerCase().includes(q)
        );
      });
    }
    if (activeTab === "cloud" && providerFilter) {
      result = result.filter((m) => m.providerName === providerFilter);
    }
    if (activeTab === "managed" && agentFilter) {
      result = result.filter((m) => m.sourceRuntimeAgent === agentFilter);
    }
    return result;
  }, [activeModels, filter, activeTab, providerFilter, agentFilter]);

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

  const totalModels = (models ?? []).length;
  const hasAnyModels = totalModels > 0;

  return (
    <div className="max-w-5xl space-y-6 p-6">
      {/* Header */}
      <div>
        <h2 className="text-lg font-semibold text-[var(--color-text-heading)]">Models</h2>
        <p className="mt-0.5 text-xs text-[var(--color-text-muted)]">
          Discovered models: inference endpoints registered across agents and cloud providers.
        </p>
      </div>

      {/* Tab bar */}
      {hasAnyModels && (
        <div role="tablist" aria-label="Model origin" className="flex border-b border-[var(--color-border-subtle)]">
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
                onClick={() => { setActiveTab(tab.key); setFilter(""); setProviderFilter(""); setAgentFilter(""); }}
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
                {tab.label}
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
                ? "Search models by name, family, or runtime..."
                : "Search cloud models by name or provider..."
            }
            aria-label="Search models"
            className="h-8 w-full rounded-[var(--radius-lg)] border border-[var(--color-border)] bg-[var(--color-bg-surface)] pl-8 pr-3 text-sm text-[var(--color-text)] placeholder:text-[var(--color-text-muted)] outline-none transition-colors focus:border-[var(--color-focus-ring)] focus:ring-1 focus:ring-[var(--color-focus-ring)]"
          />
        </div>
      )}

      {/* Provider filter (Cloud tab) */}
      {activeTab === "cloud" && uniqueProviders.length > 0 && (
        <div className="flex items-center gap-2">
          <label className="text-xs text-[var(--color-text-muted)]">Provider:</label>
          <select
            value={providerFilter}
            onChange={(e) => setProviderFilter(e.target.value)}
            className="h-8 rounded-[var(--radius-lg)] border border-[var(--color-border)] bg-[var(--color-bg-surface)] px-2 text-sm text-[var(--color-text)] outline-none focus:border-[var(--color-focus-ring)]"
          >
            <option value="">All providers</option>
            {uniqueProviders.map((p) => (
              <option key={p} value={p}>{p}</option>
            ))}
          </select>
        </div>
      )}

      {/* Agent filter (Managed tab) */}
      {activeTab === "managed" && uniqueAgents.length > 0 && (
        <div className="flex items-center gap-2">
          <label className="text-xs text-[var(--color-text-muted)]">Agent:</label>
          <select
            value={agentFilter}
            onChange={(e) => setAgentFilter(e.target.value)}
            className="h-8 rounded-[var(--radius-lg)] border border-[var(--color-border)] bg-[var(--color-bg-surface)] px-2 text-sm text-[var(--color-text)] outline-none focus:border-[var(--color-focus-ring)]"
          >
            <option value="">All agents</option>
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
              title="No models discovered yet"
              description="Register containers on the Swarm page to auto-discover their models, or add cloud providers to access hosted models."
            />
          </Card>
        ) : filteredModels.length === 0 ? (
          <Card padding="none">
            <EmptyState
              icon={<Search className="size-12" strokeWidth={1.5} />}
              title="No models match your search"
              description="Try a different search term."
            />
          </Card>
        ) : (
          <Card padding="none">
            {activeTab === "managed"
              ? filteredModels.map((model, i) => (
                  <ManagedModelRow key={model.id} model={model} index={i} settings={settings} isSelected={model.id === selectedModelId} onChat={(m) => setChatModelId(m.id)} />
                ))
              : filteredModels.map((model, i) => (
                  <CloudModelRow key={model.id} model={model} index={i} settings={settings} isSelected={model.id === selectedModelId} onChat={(m) => setChatModelId(m.id)} />
                ))}
          </Card>
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
