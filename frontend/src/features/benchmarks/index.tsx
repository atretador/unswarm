import { useState, useCallback, useRef, useEffect } from "react";
import { useQuery, useMutation, useQueryClient } from "@tanstack/react-query";
import { motion, AnimatePresence } from "motion/react";
import {
  Activity,
  AlertTriangle,
  BookOpen,
  ChevronDown,
  ChevronRight,
  Play,
  Plus,
  Trash2,
  X,
  Zap,
} from "lucide-react";
import { client } from "../../lib/query-client";
import {
  Card,
  Badge,
  Button,
  Skeleton,
  EmptyState,
  Select,
  Tooltip,
} from "../../components/ui";
import type { BenchmarkResult, Model, Prompt } from "../../lib/api/types";

// ─── Formatting helpers ───────────────────────────────────────────

function formatTokensPerSec(v: number): string {
  if (!v || v <= 0) return "n/a";
  return `${v.toFixed(1)} tok/s`;
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

/** Model statuses that are safe to benchmark — matches the fleet card semantics. */
function benchmarkDisabledReason(model: Model | undefined): string | null {
  if (!model) return "No models available yet";
  if (model.status === "validating") return `${model.name} is still validating — not ready to benchmark`;
  if (model.status === "invalid") return `${model.name} is invalid — cannot benchmark`;
  if (model.status === "deprecated") return `${model.name} is deprecated — cannot benchmark`;
  return null;
}

// ─── Prompt Library Modal ─────────────────────────────────────────

const FOCUSABLE =
  'a[href], button:not([disabled]), input:not([disabled]), select:not([disabled]), textarea:not([disabled]), [tabindex]:not([tabindex="-1"])';

function PromptLibraryModal({ open, onClose }: { open: boolean; onClose: () => void }) {
  const queryClient = useQueryClient();
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [draftName, setDraftName] = useState("");
  const [draftText, setDraftText] = useState("");
  const [confirmDeleteId, setConfirmDeleteId] = useState<string | null>(null);
  const [isCreating, setIsCreating] = useState(false);
  const saveRef = useRef<HTMLButtonElement>(null);
  const dialogRef = useRef<HTMLDivElement>(null);
  const closeRef = useRef<HTMLButtonElement>(null);
  const previousFocusRef = useRef<HTMLElement | null>(null);

  const handleKeyDown = useCallback(
    (e: KeyboardEvent) => {
      if (e.key === "Escape") {
        onClose();
        return;
      }
      if (e.key !== "Tab" || !dialogRef.current) return;
      const focusable = dialogRef.current.querySelectorAll<HTMLElement>(FOCUSABLE);
      if (focusable.length === 0) return;
      const first = focusable[0];
      const last = focusable[focusable.length - 1];
      if (e.shiftKey) {
        if (document.activeElement === first) {
          e.preventDefault();
          last.focus();
        }
      } else if (document.activeElement === last) {
        e.preventDefault();
        first.focus();
      }
    },
    [onClose],
  );

  useEffect(() => {
    if (!open) return;
    previousFocusRef.current = document.activeElement as HTMLElement;
    closeRef.current?.focus();
    document.addEventListener("keydown", handleKeyDown);

    // Lock background scroll while the dialog is open; restore on close.
    const previousOverflow = document.body.style.overflow;
    const previousTouchAction = document.body.style.touchAction;
    document.body.style.overflow = "hidden";
    document.body.style.touchAction = "none";

    return () => {
      document.removeEventListener("keydown", handleKeyDown);
      document.body.style.overflow = previousOverflow;
      document.body.style.touchAction = previousTouchAction;
      previousFocusRef.current?.focus();
    };
  }, [open, handleKeyDown]);


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

  const handleSelectPrompt = useCallback(
    (prompt: Prompt) => {
      setSelectedId(prompt.id);
      setDraftName(prompt.name);
      setDraftText(prompt.text);
      setIsCreating(false);
      setConfirmDeleteId(null);
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

  if (!open) return null;

  return (
    <div
      ref={dialogRef}
      className="fixed inset-0 z-50 flex items-end justify-center sm:items-center sm:p-6"
      role="dialog"
      aria-modal="true"
      aria-label="Prompt library"
    >
      <div className="absolute inset-0 bg-black/50 backdrop-blur-[2px]" onClick={onClose} aria-hidden="true" />
      <div className="relative z-10 flex w-full flex-col overflow-hidden rounded-t-2xl border border-[var(--color-border)] bg-[var(--color-bg-surface)] shadow-xl sm:max-w-4xl sm:rounded-2xl sm:max-h-[85vh] max-h-[92dvh]">
        {/* Header */}
        <div className="flex items-center justify-between border-b border-[var(--color-border-subtle)] px-5 py-4">
          <h2 className="font-heading text-sm font-semibold text-[var(--color-text-heading)]">Prompt Library</h2>
          <button
            ref={closeRef}
            type="button"
            onClick={onClose}
            className="flex size-7 items-center justify-center rounded-[var(--radius-md)] text-[var(--color-text-muted)] transition-colors hover:bg-[var(--color-bg-muted)] hover:text-[var(--color-text)] cursor-pointer"
            aria-label="Close"
          >
            <X className="size-4" />
          </button>
        </div>

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

          {/* Right: editor */}
          <div className="flex flex-1 min-h-0 flex-col p-5 gap-4 overflow-y-auto">
            {!selectedId && !isCreating ? (
              <div className="flex flex-1 items-center justify-center">
                <p className="text-sm text-[var(--color-text-muted)]">Select a prompt to edit, or create a new one.</p>
              </div>
            ) : (
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
      </div>
    </div>
  );
}

// ─── Benchmark row ────────────────────────────────────────────────

function BenchmarkRow({ result, index }: { result: BenchmarkResult; index: number }) {
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
        role="button"
        tabIndex={0}
        aria-expanded={expanded}
        onClick={() => setExpanded((p) => !p)}
        onKeyDown={(e) => {
          if (e.key === "Enter" || e.key === " ") {
            e.preventDefault();
            setExpanded((p) => !p);
          }
        }}
        className="flex cursor-pointer flex-wrap items-center gap-x-4 gap-y-3 border-b border-[var(--color-border-subtle)] px-4 py-3.5 transition-colors last:border-0 hover:bg-[var(--color-bg-muted)] focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-[var(--color-focus-ring)]"
      >
        {/* Model + timestamp */}
        <div className="flex min-w-0 flex-1 basis-48 items-center gap-2">
          <span className="text-[var(--color-text-muted)]">
            {expanded ? (
              <ChevronDown className="size-3.5" />
            ) : (
              <ChevronRight className="size-3.5" />
            )}
          </span>
          <div className="min-w-0">
            <p className="truncate font-mono text-xs font-medium text-[var(--color-text-heading)]">
              {result.modelName}
            </p>
            <p className="text-[10px] text-[var(--color-text-muted)]">
              {formatTimestamp(result.timestamp)}
            </p>
          </div>
        </div>

        {/* Metrics */}
        <div className="flex min-w-0 basis-40 items-center gap-3">
          <span className="flex items-center gap-1 font-mono text-xs text-[var(--color-text-heading)]">
            <Zap className="size-3 shrink-0 text-[var(--color-text-muted)]" />
            {formatTokensPerSec(result.tokensPerSec)}
          </span>
          <span className="font-mono text-xs text-[var(--color-text-muted)]">
            {result.latencyMs > 0 ? `${result.latencyMs}ms` : "—"}
          </span>
          <span className="font-mono text-[10px] text-[var(--color-text-muted)]">
            {result.tokensGenerated > 0 ? `${result.tokensGenerated} tok` : "n/a"}
          </span>
        </div>

        {/* Status */}
        <div className="ml-auto shrink-0">
          {isError ? (
            <Badge variant="error">error</Badge>
          ) : (
            <Badge variant="success">completed</Badge>
          )}
        </div>
      </div>

      <AnimatePresence>
        {expanded && (
          <motion.div
            initial={{ height: 0, opacity: 0 }}
            animate={{ height: "auto", opacity: 1 }}
            exit={{ height: 0, opacity: 0 }}
            transition={{ duration: 0.2 }}
            className="overflow-hidden"
          >
            <div className="space-y-2.5 border-b border-[var(--color-border-subtle)] bg-[var(--color-bg-muted)] px-4 py-3">
              <p className="text-[10px] font-medium uppercase tracking-wider text-[var(--color-text-muted)]">
                Prompt
              </p>
              <p className="text-xs leading-relaxed text-[var(--color-text)]">
                {result.prompt || "—"}
              </p>
              {isError && result.errorMessage && (
                <div className="flex items-start gap-1.5 rounded-[var(--radius-md)] bg-[color-mix(in_srgb,var(--color-status-error)_8%,transparent)] px-2 py-1.5 text-[10px] text-[var(--color-status-error)]">
                  <AlertTriangle className="mt-0.5 size-3 shrink-0" />
                  <span className="leading-relaxed">{result.errorMessage}</span>
                </div>
              )}
            </div>
          </motion.div>
        )}
      </AnimatePresence>
    </motion.div>
  );
}

// ─── Run benchmark control ────────────────────────────────────────

function RunBenchmarkBar({ onManagePrompts }: { onManagePrompts: () => void }) {
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
    mutationFn: ({ modelId, prompt }: { modelId: string; prompt?: string }) =>
      client.runBenchmark(modelId, prompt),
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

  const selected = (models ?? []).find((m) => m.id === modelId);
  const selectedPrompt = (prompts ?? []).find((p) => p.id === selectedPromptId);
  const disabledReason = benchmarkDisabledReason(selected ?? undefined);
  const canRun = !!selected && selected.status === "ready";

  const run = () => {
    if (!canRun || runMutation.isPending) return;
    runMutation.mutate({
      modelId,
      prompt: selectedPrompt?.text || undefined,
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
            { value: "", label: "Default prompt" },
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

      <RunBenchmarkBar onManagePrompts={() => setShowPromptLibrary(true)} />

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
            <BenchmarkRow key={r.id} result={r} index={i} />
          ))}
        </Card>
      )}

      <PromptLibraryModal
        open={showPromptLibrary}
        onClose={() => setShowPromptLibrary(false)}
      />
    </div>
  );
}
