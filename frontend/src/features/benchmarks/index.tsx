import { useState, useCallback, useRef, useEffect } from "react";
import { useQuery, useMutation, useQueryClient } from "@tanstack/react-query";
import { motion } from "motion/react";
import {
  Activity,
  AlertTriangle,
  BookOpen,
  Check,
  Copy,
  Eye,
  FileText,
  History,
  Play,
  Plus,
  RotateCcw,
  Star,
  Trash2,
  Zap,
} from "lucide-react";
import { client } from "../../lib/query-client";
import { Dialog } from "../../components/ui/Dialog";
import {
  Card,
  Badge,
  Button,
  ConfirmDialog,
  Skeleton,
  EmptyState,
  Select,
  Tooltip,
} from "../../components/ui";
import type { BenchmarkResult, Model, Prompt, PromptVersion } from "../../lib/api/types";

// ─── Formatting helpers ───────────────────────────────────────────

function formatTokensPerSec(v: number): string {
  if (!v || v <= 0) return "n/a";
  return `${v.toFixed(1)} tok/s`;
}

function formatLatency(ms: number): string {
  if (!ms || ms <= 0) return "—";
  if (ms >= 1000) return `${(ms / 1000).toFixed(1)}s`;
  return `${Math.round(ms)}ms`;
}

function formatTokens(count: number): string {
  if (!count || count <= 0) return "—";
  return `${count.toLocaleString()} tok`;
}

function formatTimestamp(iso: string): string {
  const d = new Date(iso);
  return d.toLocaleString(undefined, {
    month: "short",
    day: "numeric",
    hour: "2-digit",
    minute: "2-digit",
  });
}

function formatRelativeTime(iso: string): string {
  const now = Date.now();
  const then = new Date(iso).getTime();
  const diffMs = now - then;
  const mins = Math.floor(diffMs / 60_000);
  if (mins < 1) return "just now";
  if (mins < 60) return `${mins}m ago`;
  const hours = Math.floor(mins / 60);
  if (hours < 24) return `${hours}h ago`;
  const days = Math.floor(hours / 24);
  if (days === 1) return "yesterday";
  if (days < 30) return `${days}d ago`;
  const months = Math.floor(days / 30);
  return `${months}mo ago`;
}

/** Model statuses that are safe to benchmark — matches the fleet card semantics. */
function benchmarkDisabledReason(model: Model | undefined): string | null {
  if (!model) return "No models available yet";
  if (model.status === "validating") return `${model.name} is still validating — not ready to benchmark`;
  if (model.status === "invalid") return `${model.name} is invalid — cannot benchmark`;
  if (model.status === "deprecated") return `${model.name} is deprecated — cannot benchmark`;
  return null;
}

// ─── Prompt Library Modal ─────────────────────────────────────────

function PromptLibraryModal({ open, onClose }: { open: boolean; onClose: () => void }) {
  const queryClient = useQueryClient();
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [draftName, setDraftName] = useState("");
  const [draftText, setDraftText] = useState("");
  const [confirmDeleteId, setConfirmDeleteId] = useState<string | null>(null);
  const [isCreating, setIsCreating] = useState(false);
  const [showHistory, setShowHistory] = useState(false);
  const [versions, setVersions] = useState<PromptVersion[]>([]);
  const [selectedVersion, setSelectedVersion] = useState<PromptVersion | null>(null);
  const [confirmRollbackVersion, setConfirmRollbackVersion] = useState<PromptVersion | null>(null);
  const saveRef = useRef<HTMLButtonElement>(null);


  const { data: prompts, isLoading } = useQuery({
    queryKey: ["prompts"],
    queryFn: () => client.listPrompts(),
    enabled: open,
  });

  const createMutation = useMutation({
    mutationFn: (input: { name: string; text: string }) => client.createPrompt(input),
    onSuccess: (created) => {
      queryClient.invalidateQueries({ queryKey: ["prompts"] });
      setSelectedId(created.id);
      setDraftName(created.name);
      setDraftText(created.text);
      setIsCreating(false);
    },
  });

  const updateMutation = useMutation({
    mutationFn: ({ id, ...input }: { id: string; name: string; text: string }) =>
      client.updatePrompt(id, input),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ["prompts"] }),
  });

  const deleteMutation = useMutation({
    mutationFn: (id: string) => client.deletePrompt(id),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["prompts"] });
      setConfirmDeleteId(null);
      if (selectedId) {
        setSelectedId(null);
        setDraftName("");
        setDraftText("");
      }
    },
  });

  const setDefaultMutation = useMutation({
    mutationFn: (id: string) => client.setDefaultPrompt(id),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ["prompts"] }),
  });

  const rollbackMutation = useMutation({
    mutationFn: ({ promptId, version }: { promptId: string; version: number }) =>
      client.rollbackPrompt(promptId, version),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["prompts"] });
      setConfirmRollbackVersion(null);
      setShowHistory(false);
      setSelectedVersion(null);
    },
  });

  // Fetch version history when history view is opened for a selected prompt
  useEffect(() => {
    if (!showHistory || !selectedId) {
      setVersions([]);
      setSelectedVersion(null);
      return;
    }
    let cancelled = false;
    client.listPromptVersions(selectedId).then((v) => {
      if (!cancelled) setVersions(v);
    }).catch(() => {
      if (!cancelled) setVersions([]);
    });
    return () => { cancelled = true; };
  }, [showHistory, selectedId]);

  const handleSelectPrompt = useCallback(
    (prompt: Prompt) => {
      setSelectedId(prompt.id);
      setDraftName(prompt.name);
      setDraftText(prompt.text);
      setIsCreating(false);
      setConfirmDeleteId(null);
      setShowHistory(false);
      setSelectedVersion(null);
    },
    [],
  );

  const handleShowHistory = useCallback(
    (e: React.MouseEvent, promptId: string) => {
      e.stopPropagation();
      setSelectedId(promptId);
      setShowHistory(true);
      setSelectedVersion(null);
    },
    [],
  );

  const handleAddNew = () => {
    setSelectedId(null);
    setDraftName("");
    setDraftText("");
    setIsCreating(true);
    setConfirmDeleteId(null);
  };

  const handleSave = () => {
    const name = draftName.trim();
    const text = draftText.trim();
    if (!name || !text) return;
    if (isCreating) {
      createMutation.mutate({ name, text });
    } else if (selectedId) {
      updateMutation.mutate({ id: selectedId, name, text });
    }
  };

  const handleDelete = (id: string) => {
    if (confirmDeleteId === id) {
      deleteMutation.mutate(id);
    } else {
      setConfirmDeleteId(id);
    }
  };

  return (
    <Dialog
      open={open}
      onOpenChange={(o) => { if (!o) onClose(); }}
      title="Prompt library"
      className="sm:max-w-4xl min-h-[420px] sm:min-h-[480px]"
    >
      {/* Body */}
        <div className="flex flex-1 min-h-0 flex-col sm:flex-row">
          {/* Left: prompt list */}
          <div className="w-full sm:w-56 shrink-0 border-b sm:border-b-0 sm:border-r border-[var(--color-border-subtle)] flex flex-col sm:max-h-[70vh]">
            <div className="border-b border-[var(--color-border-subtle)] px-3 py-2">
              <button
                type="button"
                onClick={handleAddNew}
                className="flex w-full items-center gap-2 rounded-[var(--radius-lg)] px-2.5 py-1.5 text-xs font-medium text-[var(--color-primary)] transition-colors hover:bg-[var(--color-primary-soft)] cursor-pointer"
              >
                <Plus className="size-3.5" />
                New prompt
              </button>
            </div>
            <div className="flex-1 overflow-y-auto">
              {isLoading ? (
                <div className="space-y-1 p-3">
                  {Array.from({ length: 4 }, (_, i) => (
                    <Skeleton key={i} className="h-9 w-full rounded-[var(--radius-lg)]" />
                  ))}
                </div>
              ) : prompts?.length === 0 ? (
                <p className="p-3 text-center text-xs text-[var(--color-text-muted)]">No saved prompts</p>
              ) : (
                prompts?.map((p) => {
                  const isSelected = p.id === selectedId;
                  const isConfirmingDelete = confirmDeleteId === p.id;
                  return (
                    <div
                      key={p.id}
                      className={`flex items-center gap-1 border-b border-[var(--color-border-subtle)] px-3 last:border-0 ${isSelected ? "bg-[var(--color-primary-soft)]" : ""}`}
                    >
                      <button
                        type="button"
                        onClick={() => handleSelectPrompt(p)}
                        className={`flex-1 min-w-0 py-2 text-left text-xs truncate cursor-pointer transition-colors ${
                          isSelected
                            ? "font-medium text-[var(--color-text-heading)]"
                            : "text-[var(--color-text-muted)] hover:text-[var(--color-text-heading)]"
                        }`}
                      >
                        {p.name}
                        {p.isDefault && (
                          <span className="ml-1 rounded px-1 py-0.5 text-[9px] font-medium uppercase tracking-wide text-[var(--color-primary)]">default</span>
                        )}
                        <span className="ml-1 font-mono text-[9px] text-[var(--color-text-muted)]">v{p.currentVersion ?? 1}</span>
                        {p.id === selectedId && showHistory && (
                          <span className="ml-1 rounded bg-[var(--color-primary-soft)] px-1 py-0.5 text-[8px] font-medium text-[var(--color-primary)]">history</span>
                        )}
                      </button>
                      <button
                        type="button"
                        onClick={(e) => handleShowHistory(e, p.id)}
                        className="shrink-0 rounded p-1 text-[var(--color-text-muted)] transition-colors hover:bg-[var(--color-bg-muted)] hover:text-[var(--color-primary)] cursor-pointer"
                        aria-label={`Version history for ${p.name}`}
                        title="Version history"
                      >
                        <History className="size-3" />
                      </button>
                      <button
                        type="button"
                        onClick={(e) => {
                          e.stopPropagation();
                          if (!p.isDefault) {
                            setDefaultMutation.mutate(p.id);
                          }
                        }}
                        disabled={setDefaultMutation.isPending}
                        className="shrink-0 rounded p-1 text-[var(--color-text-muted)] transition-colors hover:bg-[var(--color-bg-muted)] cursor-pointer"
                        aria-label={p.isDefault ? "Default benchmark prompt" : `Set ${p.name} as default benchmark prompt`}
                        title={p.isDefault ? "Default benchmark prompt" : `Set ${p.name} as default benchmark prompt`}
                      >
                        <Star
                          className="size-3"
                          fill={p.isDefault ? "var(--color-primary)" : "none"}
                          stroke={p.isDefault ? "var(--color-primary)" : "currentColor"}
                        />
                      </button>
                      {isConfirmingDelete ? (
                        <div className="flex shrink-0 items-center gap-0.5">
                          <button
                            type="button"
                            onClick={() => handleDelete(p.id)}
                            disabled={deleteMutation.isPending}
                            className="shrink-0 rounded px-1 py-0.5 text-[10px] font-medium text-[var(--color-status-error)] hover:bg-[color-mix(in_srgb,var(--color-status-error)_10%,transparent)] cursor-pointer"
                          >
                            Delete
                          </button>
                          <button
                            type="button"
                            onClick={() => setConfirmDeleteId(null)}
                            className="shrink-0 rounded px-1 py-0.5 text-[10px] font-medium text-[var(--color-text-muted)] hover:bg-[var(--color-bg-muted)] cursor-pointer"
                          >
                            Cancel
                          </button>
                        </div>
                      ) : (
                        <button
                          type="button"
                          onClick={(e) => {
                            e.stopPropagation();
                            handleDelete(p.id);
                          }}
                          className="shrink-0 rounded p-1 text-[var(--color-text-muted)] transition-colors hover:bg-[var(--color-bg-muted)] hover:text-[var(--color-status-error)] cursor-pointer"
                          aria-label={`Delete ${p.name}`}
                          title="Delete prompt"
                        >
                          <Trash2 className="size-3" />
                        </button>
                      )}
                    </div>
                  );
                })
              )}
            </div>
          </div>

          {/* Right: editor or version history */}
          <div className="flex flex-1 min-h-0 flex-col p-5 gap-4 overflow-y-auto">
            {!selectedId && !isCreating ? (
              <div className="flex flex-1 items-center justify-center">
                <p className="text-sm text-[var(--color-text-muted)]">Select a prompt to edit, or create a new one.</p>
              </div>
            ) : showHistory ? (
              /* ── Version history view ── */
              <>
                <div className="flex items-center justify-between">
                  <h3 className="font-heading text-xs font-semibold text-[var(--color-text-heading)]">
                    Version History — {draftName || "Untitled"}
                  </h3>
                  <button
                    type="button"
                    onClick={() => { setShowHistory(false); setSelectedVersion(null); }}
                    className="text-[10px] font-medium text-[var(--color-primary)] hover:underline cursor-pointer"
                  >
                    ← Back to Editor
                  </button>
                </div>

                {versions.length === 0 ? (
                  <div className="flex flex-1 items-center justify-center">
                    <p className="text-xs text-[var(--color-text-muted)]">No version history available.</p>
                  </div>
                ) : (
                  <>
                    {/* Version list */}
                    <div className="space-y-1">
                      {versions.map((v) => {
                        const isCurrent = v.version === (prompts?.find((p) => p.id === selectedId)?.currentVersion ?? 1);
                        const isPreviewing = selectedVersion?.id === v.id;
                        return (
                          <div
                            key={v.id}
                            className={`flex items-center gap-2 rounded-[var(--radius-md)] px-3 py-2 text-xs transition-colors ${
                              isPreviewing
                                ? "bg-[var(--color-primary-soft)] ring-1 ring-[var(--color-primary)]"
                                : "hover:bg-[var(--color-bg-muted)]"
                            }`}
                          >
                            <span className="shrink-0 font-mono font-semibold text-[var(--color-text-heading)]">
                              v{v.version}
                            </span>
                            {isCurrent && (
                              <span className="shrink-0 rounded bg-[var(--color-primary-soft)] px-1 py-0.5 text-[8px] font-medium text-[var(--color-primary)]">
                                current
                              </span>
                            )}
                            <span className="shrink-0 text-[10px] text-[var(--color-text-muted)]">
                              {formatRelativeTime(v.createdAt)}
                            </span>
                            <span className="min-w-0 flex-1 truncate text-[var(--color-text-muted)]">
                              {v.text.length > 60 ? v.text.slice(0, 60) + "…" : v.text}
                            </span>
                            <div className="flex shrink-0 items-center gap-0.5">
                              <button
                                type="button"
                                onClick={() => setSelectedVersion(isPreviewing ? null : v)}
                                className="rounded p-1 text-[var(--color-text-muted)] transition-colors hover:bg-[var(--color-bg-muted)] hover:text-[var(--color-text-heading)] cursor-pointer"
                                aria-label={`View version ${v.version}`}
                                title="View this version"
                              >
                                <Eye className="size-3" />
                              </button>
                              {!isCurrent && (
                                <button
                                  type="button"
                                  onClick={() => setConfirmRollbackVersion(v)}
                                  className="rounded p-1 text-[var(--color-text-muted)] transition-colors hover:bg-[var(--color-bg-muted)] hover:text-[var(--color-status-error)] cursor-pointer"
                                  aria-label={`Rollback to version ${v.version}`}
                                  title="Restore this version"
                                >
                                  <RotateCcw className="size-3" />
                                </button>
                              )}
                            </div>
                          </div>
                        );
                      })}
                    </div>

                    {/* Preview area */}
                    {selectedVersion && (
                      <div className="flex flex-1 min-h-[120px] flex-col space-y-1.5">
                        <label className="text-xs font-medium text-[var(--color-text-muted)]">
                          v{selectedVersion.version} text
                          {selectedVersion.version === (prompts?.find((p) => p.id === selectedId)?.currentVersion ?? 1) && (
                            <span className="ml-1.5 rounded bg-[var(--color-primary-soft)] px-1 py-0.5 text-[8px] font-medium text-[var(--color-primary)]">
                              ← current
                            </span>
                          )}
                        </label>
                        <div className="flex-1 min-h-[100px] w-full resize-y rounded-[var(--radius-lg)] border border-[var(--color-border)] bg-[var(--color-bg-surface)] px-3 py-2.5 text-sm leading-relaxed text-[var(--color-text)] overflow-y-auto whitespace-pre-wrap">
                          {selectedVersion.text}
                        </div>
                        <span className="text-[10px] text-[var(--color-text-muted)]">
                          {selectedVersion.text.length} characters · {formatRelativeTime(selectedVersion.createdAt)}
                        </span>
                      </div>
                    )}
                  </>
                )}
              </>
            ) : (
              /* ── Normal editor view ── */
              <>
                <div className="space-y-1.5">
                  <label htmlFor="prompt-name" className="text-xs font-medium text-[var(--color-text-muted)]">
                    Name
                  </label>
                  <input
                    id="prompt-name"
                    type="text"
                    value={draftName}
                    onChange={(e) => setDraftName(e.target.value)}
                    placeholder="Short, descriptive name"
                    className="h-9 w-full rounded-[var(--radius-lg)] border border-[var(--color-border)] bg-[var(--color-bg-surface)] px-3 text-sm text-[var(--color-text)] placeholder:text-[var(--color-text-muted)] focus:border-[var(--color-primary)] focus:outline-none focus:ring-1 focus:ring-[var(--color-focus-ring)] transition-colors"
                  />
                </div>
                <div className="flex flex-1 min-h-[160px] flex-col space-y-1.5">
                  <label htmlFor="prompt-text" className="text-xs font-medium text-[var(--color-text-muted)]">
                    Prompt text
                  </label>
                  <textarea
                    id="prompt-text"
                    value={draftText}
                    onChange={(e) => setDraftText(e.target.value)}
                    placeholder="Write the prompt the model will follow — instructions, context, format, or constraints."
                    rows={8}
                    className="flex-1 min-h-[140px] w-full resize-y rounded-[var(--radius-lg)] border border-[var(--color-border)] bg-[var(--color-bg-surface)] px-3 py-2.5 text-sm leading-relaxed text-[var(--color-text)] placeholder:text-[var(--color-text-muted)] focus:border-[var(--color-primary)] focus:outline-none focus:ring-1 focus:ring-[var(--color-focus-ring)] transition-colors"
                  />
                </div>
                <div className="flex items-center justify-between gap-3 pt-1 border-t border-[var(--color-border-subtle)]">
                  <span className="text-[10px] text-[var(--color-text-muted)]">
                    {draftText.length > 0 ? `${draftText.length} characters` : "No content yet"}
                  </span>
                  <div className="flex items-center gap-2">
                    {createMutation.isError || updateMutation.isError ? (
                      <span className="text-xs text-[var(--color-status-error)]">
                        {(createMutation.error ?? updateMutation.error)?.message ?? "Save failed"}
                      </span>
                    ) : null}
                    {createMutation.isSuccess || updateMutation.isSuccess ? (
                      <span className="text-xs text-[var(--color-status-running)] font-medium">Saved</span>
                    ) : null}
                    <Tooltip content={!draftName.trim() || !draftText.trim() ? "Name and prompt text are required" : undefined}>
                      <span className="inline-flex">
                        <Button
                          ref={saveRef}
                          size="sm"
                          disabled={!draftName.trim() || !draftText.trim() || createMutation.isPending || updateMutation.isPending}
                          loading={createMutation.isPending || updateMutation.isPending}
                          onClick={handleSave}
                        >
                          {isCreating ? "Create" : "Save"}
                        </Button>
                      </span>
                    </Tooltip>
                  </div>
                </div>
              </>
            )}
          </div>
        </div>

      <ConfirmDialog
        open={confirmRollbackVersion !== null}
        title="Restore this version?"
        description="This will create a new version with the previous text."
        confirmLabel="Restore"
        cancelLabel="Cancel"
        variant="primary"
        loading={rollbackMutation.isPending}
        onConfirm={() => {
          if (confirmRollbackVersion && selectedId) {
            rollbackMutation.mutate({ promptId: selectedId, version: confirmRollbackVersion.version });
          }
        }}
        onCancel={() => setConfirmRollbackVersion(null)}
      />
    </Dialog>
  );
}

// ─── Benchmark row ────────────────────────────────────────────────

function BenchmarkRow({ result, index, onShowHistory, onShowResults }: { result: BenchmarkResult; index: number; onShowHistory?: (modelId: string, modelName: string) => void; onShowResults?: (result: BenchmarkResult) => void }) {
  const [expanded, setExpanded] = useState(false);
  const isError = result.status === "error";

  return (
    <motion.div
      layout
      initial={{ opacity: 0, y: 8 }}
      animate={{ opacity: 1, y: 0 }}
      transition={{ duration: 0.2, delay: Math.min(index * 0.04, 0.3) }}
    >
      <div
        className="flex flex-wrap items-center gap-x-4 gap-y-2 border-b border-[var(--color-border-subtle)] px-4 py-3 transition-colors last:border-0 hover:bg-[var(--color-bg-muted)]"
      >
        {/* Model + timestamp + prompt info — clickable to expand/collapse details */}
        <button
          type="button"
          onClick={() => setExpanded((e) => !e)}
          aria-expanded={expanded}
          className="flex min-w-0 flex-1 basis-48 cursor-pointer items-center gap-2 text-left"
        >
          <div className="min-w-0">
            <p className="truncate font-mono text-xs font-medium text-[var(--color-text-heading)]">
              {result.modelName}
            </p>
            <p className="text-[10px] text-[var(--color-text-muted)]">
              {formatTimestamp(result.timestamp)}
            </p>
            <p className="truncate text-[10px] text-[var(--color-text-muted)]">
              {result.promptName ?? "Built-in prompt"}{result.promptVersion != null ? ` · v${result.promptVersion}` : ""}
            </p>
          </div>
        </button>

        {/* Metrics — throughput primary, latency + tokens secondary */}
        <div className="grid shrink-0 grid-cols-[auto_4.5rem_4.5rem] items-center gap-x-3">
          {/* Primary: throughput */}
          <div
            className="flex items-center gap-1.5 justify-self-start"
            title={`Throughput: ${result.tokensPerSec ? result.tokensPerSec.toFixed(2) : "n/a"} tokens/sec`}
          >
            <Zap className="size-3.5 shrink-0 text-[var(--color-primary)]" />
            <span className="whitespace-nowrap font-mono text-sm font-semibold tabular-nums text-[var(--color-primary)]">
              {formatTokensPerSec(result.tokensPerSec)}
            </span>
          </div>

          {/* Latency — secondary */}
          <span
            className="whitespace-nowrap text-right font-mono text-[11px] tabular-nums text-[var(--color-text-muted)]"
            title={result.latencyMs > 0 ? `Processing time: ${result.latencyMs.toLocaleString()} ms` : "No latency data"}
          >
            {formatLatency(result.latencyMs)}
          </span>

          {/* Tokens generated — secondary */}
          <span
            className="whitespace-nowrap text-right font-mono text-[11px] tabular-nums text-[var(--color-text-muted)]"
            title={result.tokensGenerated > 0 ? `${result.tokensGenerated.toLocaleString()} tokens generated` : "No token data"}
          >
            {formatTokens(result.tokensGenerated)}
          </span>
        </div>

        {/* Status + results + history */}
        <div className="ml-auto flex shrink-0 items-center gap-1.5">
          <button
            type="button"
            onClick={() => onShowResults?.(result)}
            className="rounded p-1 text-[var(--color-text-muted)] transition-colors hover:bg-[var(--color-bg-muted)] hover:text-[var(--color-primary)] cursor-pointer"
            aria-label={`Results for ${result.modelName} run`}
            title="View run results"
          >
            <FileText className="size-3.5" />
          </button>
          <button
            type="button"
            onClick={() => onShowHistory?.(result.modelId, result.modelName)}
            className="rounded p-1 text-[var(--color-text-muted)] transition-colors hover:bg-[var(--color-bg-muted)] hover:text-[var(--color-primary)] cursor-pointer"
            aria-label={`Historic results for ${result.modelName}`}
            title="View historic results"
          >
            <History className="size-3.5" />
          </button>
          {isError ? (
            <Badge variant="error">error</Badge>
          ) : (
            <Badge variant="success">completed</Badge>
          )}
        </div>
      </div>

      {/* Detail area — error message or prompt text, only when expanded */}
      {expanded && (
        <div className="border-b border-[var(--color-border-subtle)] px-4 py-2 last:border-0">
          {isError && result.errorMessage ? (
            <div className="flex items-start gap-1.5 bg-[color-mix(in_srgb,var(--color-status-error)_8%,transparent)] text-[10px] text-[var(--color-status-error)]">
              <AlertTriangle className="mt-0.5 size-3 shrink-0" />
              <span className="leading-relaxed">{result.errorMessage}</span>
            </div>
          ) : (
            <span className="block whitespace-pre-wrap text-[10px] leading-relaxed text-[var(--color-text-muted)]">
              {result.prompt}
            </span>
          )}
        </div>
      )}
    </motion.div>
  );
}

// ─── Run results modal ────────────────────────────────────────────

function StatTile({ label, value }: { label: string; value: string }) {
  return (
    <div className="rounded-[var(--radius-lg)] border border-[var(--color-border-subtle)] bg-[var(--color-bg-muted)] px-3 py-2">
      <p className="text-[10px] font-medium uppercase tracking-wide text-[var(--color-text-muted)]">
        {label}
      </p>
      <p className="mt-0.5 font-mono text-sm font-semibold tabular-nums text-[var(--color-text-heading)]">
        {value}
      </p>
    </div>
  );
}

function RunResultModal({ result, onClose }: { result: BenchmarkResult; onClose: () => void }) {
  const [copied, setCopied] = useState(false);
  const isError = result.status === "error";
  const hasResponse = typeof result.response === "string" && result.response.length > 0;

  const handleCopy = useCallback(() => {
    if (!hasResponse) return;
    navigator.clipboard
      .writeText(result.response as string)
      .then(() => {
        setCopied(true);
        window.setTimeout(() => setCopied(false), 1500);
      })
      .catch(() => {
        // Clipboard unavailable (permissions/insecure context) — nothing sensible to do.
      });
  }, [hasResponse, result.response]);

  return (
    <Dialog
      open
      onOpenChange={(o) => { if (!o) onClose(); }}
      title={`${result.modelName} — run details`}
      className="sm:max-w-3xl"
    >
      <div className="flex flex-col gap-4 p-5">
        {/* Header: status + when */}
        <div className="flex flex-wrap items-center gap-x-3 gap-y-1">
          {isError ? (
            <Badge variant="error">error</Badge>
          ) : (
            <Badge variant="success">completed</Badge>
          )}
          <span className="text-xs text-[var(--color-text-muted)]" title={new Date(result.timestamp).toLocaleString()}>
            {formatTimestamp(result.timestamp)} · {formatRelativeTime(result.timestamp)}
          </span>
          {(result.promptName || result.promptVersion != null) && (
            <span className="ml-auto truncate text-[10px] text-[var(--color-text-muted)]">
              {result.promptName ?? "Built-in prompt"}{result.promptVersion != null ? ` · v${result.promptVersion}` : ""}
            </span>
          )}
        </div>

        {/* Metrics */}
        <div className="grid grid-cols-3 gap-2">
          <StatTile label="Tokens generated" value={formatTokens(result.tokensGenerated)} />
          <StatTile label="Throughput" value={formatTokensPerSec(result.tokensPerSec)} />
          <StatTile label="Latency" value={formatLatency(result.latencyMs)} />
        </div>

        {/* Prompt */}
        <div className="flex min-h-0 flex-col gap-1.5">
          <span className="text-[10px] font-medium uppercase tracking-wide text-[var(--color-text-muted)]">
            Prompt
          </span>
          <div className="max-h-32 overflow-y-auto whitespace-pre-wrap rounded-[var(--radius-lg)] border border-[var(--color-border-subtle)] bg-[var(--color-bg-muted)] px-3 py-2.5 text-xs leading-relaxed text-[var(--color-text-muted)]">
            {result.prompt}
          </div>
        </div>

        {/* Response / error */}
        <div className="flex min-h-0 flex-col gap-1.5">
          <div className="flex items-center justify-between gap-2">
            <span className="text-[10px] font-medium uppercase tracking-wide text-[var(--color-text-muted)]">
              Response
            </span>
            {hasResponse && (
              <button
                type="button"
                onClick={handleCopy}
                className="flex items-center gap-1 rounded px-1.5 py-0.5 text-[10px] font-medium text-[var(--color-primary)] transition-colors hover:bg-[var(--color-primary-soft)] cursor-pointer"
                aria-label="Copy response to clipboard"
              >
                {copied ? (
                  <>
                    <Check className="size-3" />
                    Copied
                  </>
                ) : (
                  <>
                    <Copy className="size-3" />
                    Copy
                  </>
                )}
              </button>
            )}
          </div>

          {isError && result.errorMessage ? (
            <div className="flex items-start gap-1.5 rounded-[var(--radius-lg)] bg-[color-mix(in_srgb,var(--color-status-error)_8%,transparent)] px-3 py-2.5">
              <AlertTriangle className="mt-0.5 size-3.5 shrink-0 text-[var(--color-status-error)]" />
              <span className="whitespace-pre-wrap break-words text-xs leading-relaxed text-[var(--color-status-error)]">
                {result.errorMessage}
              </span>
            </div>
          ) : hasResponse ? (
            <pre className="max-h-72 overflow-y-auto whitespace-pre-wrap break-words rounded-[var(--radius-lg)] border border-[var(--color-border-subtle)] bg-[var(--color-bg-surface)] px-3 py-2.5 font-mono text-xs leading-relaxed text-[var(--color-text-heading)]">
              {result.response}
            </pre>
          ) : (
            <div className="rounded-[var(--radius-lg)] border border-dashed border-[var(--color-border)] px-3 py-4 text-center text-xs text-[var(--color-text-muted)]">
              No response captured
            </div>
          )}
        </div>
      </div>
    </Dialog>
  );
}

// ─── Model results modal ──────────────────────────────────────────

function ModelResultsModal({
  modelId,
  modelName,
  open,
  onClose,
}: {
  modelId: string;
  modelName: string;
  open: boolean;
  onClose: () => void;
}) {
  const { data: results, isLoading } = useQuery({
    queryKey: ["benchmarks", modelId],
    queryFn: () => client.listBenchmarks(modelId),
    enabled: open,
  });

  return (
    <Dialog
      open={open}
      onOpenChange={(o) => { if (!o) onClose(); }}
      title={`${modelName} — benchmark results`}
    >
        {/* Body */}
        <div className="flex-1 overflow-y-auto">
          {isLoading ? (
            <div className="space-y-1 p-3">
              {Array.from({ length: 4 }, (_, i) => (
                <Skeleton key={i} className="h-9 w-full rounded-[var(--radius-lg)]" />
              ))}
            </div>
          ) : !results || results.length === 0 ? (
            <div className="flex flex-1 items-center justify-center p-8">
              <EmptyState title="No benchmark runs for this model yet" />
            </div>
          ) : (
            <div>
              {results.map((r) => {
                const isError = r.status === "error";
                return (
                  <div
                    key={r.id}
                    className="flex flex-wrap items-center gap-x-4 gap-y-2 border-b border-[var(--color-border-subtle)] px-4 py-3 last:border-0"
                  >
                    {/* Prompt name + version */}
                    <div className="min-w-0 flex-1 basis-40 truncate">
                      <span className="text-xs font-medium text-[var(--color-text-heading)]">
                        {r.promptName ?? "Built-in prompt"}
                      </span>
                      {r.promptVersion != null && (
                        <span className="ml-1 font-mono text-[9px] text-[var(--color-text-muted)]">v{r.promptVersion}</span>
                      )}
                    </div>

                    {/* Metrics */}
                    <div className="flex shrink-0 items-center gap-3">
                      <span className="flex items-center gap-1 font-mono text-xs text-[var(--color-text-heading)]">
                        <Zap className="size-3 shrink-0 text-[var(--color-text-muted)]" />
                        {formatTokensPerSec(r.tokensPerSec)}
                      </span>
                      <span className="font-mono text-xs text-[var(--color-text-muted)]">
                        {r.latencyMs > 0 ? `${r.latencyMs}ms` : "—"}
                      </span>
                      <span className="font-mono text-[10px] text-[var(--color-text-muted)]">
                        {r.tokensGenerated > 0 ? `${r.tokensGenerated} tok` : "n/a"}
                      </span>
                    </div>

                    {/* Status + timestamp */}
                    <div className="ml-auto flex shrink-0 items-center gap-2">
                      {isError ? (
                        <Badge variant="error">error</Badge>
                      ) : (
                        <Badge variant="success">completed</Badge>
                      )}
                      <span className="text-[10px] text-[var(--color-text-muted)]">
                        {formatTimestamp(r.timestamp)}
                      </span>
                    </div>
                  </div>
                );
              })}
            </div>
          )}
        </div>
    </Dialog>
  );
}

// ─── Run benchmark control ────────────────────────────────────────

function RunBenchmarkBar({ onManagePrompts, onShowResults }: { onManagePrompts: () => void; onShowResults: (modelId: string, modelName: string) => void }) {
  const queryClient = useQueryClient();
  const [modelId, setModelId] = useState("");
  const [selectedPromptId, setSelectedPromptId] = useState("");

  const { data: models } = useQuery({
    queryKey: ["models"],
    queryFn: () => client.listModels(),
  });

  const { data: prompts } = useQuery({
    queryKey: ["prompts"],
    queryFn: () => client.listPrompts(),
  });

  const runMutation = useMutation({
    mutationFn: ({ modelId, promptId }: { modelId: string; promptId?: string }) =>
      client.runBenchmark(modelId, promptId ? { promptId } : undefined),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["benchmarks"] });
      queryClient.invalidateQueries({ queryKey: ["models"] });
      // Keep prompt selection — user may want to run the same prompt again.
    },
  });

  const readyModels = (models ?? []).filter((m) => m.status === "ready");
  const modelOptions = (models ?? []).map((m) => ({
    value: m.id,
    label: m.status === "ready" ? m.name : `${m.name} (${m.status})`,
  }));

  const promptOptions = (prompts ?? []).map((p) => ({
    value: p.id,
    label: p.name,
  }));

  const defaultPrompt = (prompts ?? []).find((p) => p.isDefault);

  const selected = (models ?? []).find((m) => m.id === modelId);
  const disabledReason = benchmarkDisabledReason(selected ?? undefined);
  const canRun = !!selected && selected.status === "ready";

  const run = () => {
    if (!canRun || runMutation.isPending) return;
    runMutation.mutate({
      modelId,
      promptId: selectedPromptId || undefined,
    });
  };

  return (
    <Card padding="md" className="flex flex-col gap-3 lg:flex-row lg:items-end">
      <div className="min-w-0 flex-1">
        <Select
          label="Target model"
          aria-label="Target model"
          value={modelId}
          onChange={(e) => setModelId(e.target.value)}
          options={[
            { value: "", label: "Select a model…" },
            ...modelOptions,
          ]}
        />
      </div>
      <div className="min-w-0 flex-1">
        <Select
          label="Prompt (optional)"
          aria-label="Prompt (optional)"
          value={selectedPromptId}
          onChange={(e) => setSelectedPromptId(e.target.value)}
          options={[
            { value: "", label: defaultPrompt ? `Default prompt (${defaultPrompt.name})` : "Default prompt" },
            ...promptOptions,
          ]}
        />
      </div>
      <Button
        variant="secondary"
        size="md"
        onClick={onManagePrompts}
        className="lg:self-end"
      >
        <BookOpen className="size-3.5" />
        Manage prompts
      </Button>
      <Button
        variant="secondary"
        size="md"
        disabled={!modelId}
        onClick={() => {
          if (modelId && selected) {
            onShowResults(modelId, selected.name);
          }
        }}
        className="lg:self-end"
      >
        <History className="size-3.5" />
        Results
      </Button>
      <Tooltip content={disabledReason ?? "Run a benchmark against the selected model"}>
        <span className="inline-flex sm:shrink-0">
          <Button
            size="md"
            disabled={!canRun || runMutation.isPending}
            loading={runMutation.isPending}
            onClick={run}
          >
            <Play className="size-3.5" />
            Run benchmark
          </Button>
        </span>
      </Tooltip>
      {readyModels.length > 0 && (
        <p className="text-[10px] text-[var(--color-text-muted)] sm:shrink-0 sm:pb-2">
          {readyModels.length} ready model{readyModels.length !== 1 ? "s" : ""}
        </p>
      )}
    </Card>
  );
}

// ─── Main Benchmarks page ─────────────────────────────────────────

export default function Benchmarks() {
  const [showPromptLibrary, setShowPromptLibrary] = useState(false);
  const [modelResultsModal, setModelResultsModal] = useState<{ modelId: string; modelName: string } | null>(null);
  const [runResult, setRunResult] = useState<BenchmarkResult | null>(null);

  const {
    data: results,
    isLoading,
    error,
    refetch,
    isRefetching,
  } = useQuery({
    queryKey: ["benchmarks"],
    queryFn: () => client.listBenchmarks(),
  });

  if (isLoading) {
    return (
      <div className="max-w-5xl space-y-4 p-6">
        <Skeleton className="h-7 w-40" />
        <Skeleton className="h-4 w-72" />
        <Card padding="md">
          <Skeleton className="h-8 w-full" />
        </Card>
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
          title="Failed to load benchmarks"
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
        <h2 className="text-lg font-semibold text-[var(--color-text-heading)]">Benchmarks</h2>
        <p className="mt-0.5 text-xs text-[var(--color-text-muted)]">
          Benchmark runs: measured throughput and latency per model.
        </p>
      </div>

      <RunBenchmarkBar
        onManagePrompts={() => setShowPromptLibrary(true)}
        onShowResults={(modelId, modelName) => setModelResultsModal({ modelId, modelName })}
      />

      {!results || results.length === 0 ? (
        <Card padding="none">
          <EmptyState
            icon={<Activity className="size-12" strokeWidth={1.5} />}
            title="No benchmark runs yet"
            description="Run one from the Benchmarks or Fleet page."
          />
        </Card>
      ) : (
        <Card padding="none">
          {results.map((r, i) => (
            <BenchmarkRow
              key={r.id}
              result={r}
              index={i}
              onShowHistory={(modelId, modelName) => setModelResultsModal({ modelId, modelName })}
              onShowResults={setRunResult}
            />
          ))}
        </Card>
      )}

      <PromptLibraryModal
        open={showPromptLibrary}
        onClose={() => setShowPromptLibrary(false)}
      />
      <ModelResultsModal
        modelId={modelResultsModal?.modelId ?? ""}
        modelName={modelResultsModal?.modelName ?? ""}
        open={modelResultsModal !== null}
        onClose={() => setModelResultsModal(null)}
      />
      {runResult && (
        <RunResultModal
          result={runResult}
          onClose={() => setRunResult(null)}
        />
      )}
    </div>
  );
}
