import { useState, useEffect, useCallback } from "react";
import { useQuery, useMutation, useQueryClient } from "@tanstack/react-query";
import { Cloud, Plus, Pencil, Trash2, Loader2, Check, AlertCircle } from "lucide-react";
import { client } from "../../lib/query-client";
import {
  Card,
  Skeleton,
  Input,
  Button,
  EmptyState,
  ConfirmDialog,
  Dialog,
  TriCheckbox,
} from "../../components/ui";
import { getProviderModelCatalog } from "../api-keys/api-keys-api";
import type {
  CloudProvider,
  CloudProviderInput,
  CloudProviderUpdateInput,
} from "../../lib/api/types";

// ─── Helpers ─────────────────────────────────────────────────────

function formatRelativeTime(iso: string): string {
  const diff = Date.now() - new Date(iso).getTime();
  const seconds = Math.floor(diff / 1000);
  if (seconds < 60) return "just now";
  const minutes = Math.floor(seconds / 60);
  if (minutes < 60) return `${minutes}m ago`;
  const hours = Math.floor(minutes / 60);
  if (hours < 24) return `${hours}h ago`;
  const days = Math.floor(hours / 24);
  return `${days}d ago`;
}

// ─── Provider Row ────────────────────────────────────────────────

function ProviderRow({
  provider: p,
  onEdit,
  onDelete,
}: {
  provider: CloudProvider;
  onEdit: (provider: CloudProvider) => void;
  onDelete: (provider: CloudProvider) => void;
}) {
  return (
    <div className="flex items-center gap-4 px-4 py-3 border-b border-[var(--color-border-subtle)] last:border-b-0 hover:bg-[var(--color-bg-muted)]/50 transition-colors duration-[var(--duration-fast)]">
      {/* Name */}
      <div className="flex items-center gap-3 min-w-0 flex-1">
        <div className="flex items-center justify-center size-8 rounded-full bg-[var(--color-primary-soft)] text-[var(--color-primary)] shrink-0">
          <Cloud className="size-4" />
        </div>
        <span className="text-sm text-[var(--color-text)] truncate font-medium">
          {p.name}
        </span>
      </div>

      {/* Base URL */}
      <div className="shrink-0 w-[220px] min-w-0">
        <span className="text-xs text-[var(--color-text-muted)] truncate block" title={p.baseUrl}>
          {p.baseUrl}
        </span>
      </div>

      {/* API Key Hint */}
      <div className="shrink-0 w-[120px] min-w-0">
        <span className="text-xs text-[var(--color-text-muted)] font-mono">
          {p.apiKeyHint || "\u2014"}
        </span>
      </div>

      {/* Model Count */}
      <div className="shrink-0 w-[80px] text-right">
        <span className="text-xs text-[var(--color-text)]">
          {p.modelCount} {p.modelCount === 1 ? "model" : "models"}
        </span>
      </div>

      {/* Updated */}
      <div className="shrink-0 w-[100px] text-right">
        <span className="text-xs text-[var(--color-text-muted)]">
          {formatRelativeTime(p.updatedAt)}
        </span>
      </div>

      {/* Actions */}
      <div className="flex items-center gap-1 shrink-0">
        <Button variant="ghost" size="sm" onClick={() => onEdit(p)}>
          <Pencil className="size-3.5" />
          Edit
        </Button>
        <Button variant="danger" size="sm" onClick={() => onDelete(p)}>
          <Trash2 className="size-3.5" />
        </Button>
      </div>
    </div>
  );
}

// ─── Add / Edit Dialog ──────────────────────────────────────────

function ProviderDialog({
  open,
  onClose,
  editProvider,
}: {
  open: boolean;
  onClose: () => void;
  editProvider: CloudProvider | null;
}) {
  const queryClient = useQueryClient();
  const isEdit = editProvider !== null;

  const [name, setName] = useState("");
  const [baseUrl, setBaseUrl] = useState("");
  const [apiKey, setApiKey] = useState("");
  const [error, setError] = useState<string | null>(null);

  // Fetch models state
  const [fetchedModels, setFetchedModels] = useState<string[] | null>(null);
  const [fetchModelsPending, setFetchModelsPending] = useState(false);
  const [fetchModelsError, setFetchModelsError] = useState<string | null>(null);

  // Model selection state
  const [selectedModels, setSelectedModels] = useState<Set<string>>(new Set());
  const [savedModelIds, setSavedModelIds] = useState<Set<string> | null>(null);

  // Reset state on open / editProvider change
  useEffect(() => {
    if (open) {
      if (editProvider) {
        setName(editProvider.name);
        setBaseUrl(editProvider.baseUrl);
        setApiKey("");
      } else {
        setName("");
        setBaseUrl("");
        setApiKey("");
      }
      setError(null);
      setFetchedModels(null);
      setFetchModelsError(null);
      setSelectedModels(new Set());
      setSavedModelIds(null);
    }
  }, [open, editProvider]);

  // Fetch saved models from catalog when editing an existing provider
  useEffect(() => {
    if (!open || !editProvider) return;

    let cancelled = false;
    getProviderModelCatalog()
      .then((catalog) => {
        if (cancelled) return;
        const entry = catalog.find(
          (e) => e.name === editProvider.name && e.kind === "cloud",
        );
        setSavedModelIds(new Set(entry?.models ?? []));
      })
      .catch(() => {
        if (!cancelled) setSavedModelIds(new Set());
      });

    return () => { cancelled = true; };
  }, [open, editProvider]);

  const createMutation = useMutation({
    mutationFn: (data: CloudProviderInput) => client.createCloudProvider(data),
  });

  const updateMutation = useMutation({
    mutationFn: ({ id, data }: { id: string; data: CloudProviderUpdateInput }) =>
      client.updateCloudProvider(id, data),
  });

  const handleFetchModels = async () => {
    setFetchModelsPending(true);
    setFetchModelsError(null);
    setFetchedModels(null);
    try {
      let result: { modelIds: string[] };
      if (isEdit && editProvider) {
        result = await client.fetchCloudProviderModels(editProvider.id);
        queryClient.invalidateQueries({ queryKey: ["cloud-providers"] });
      } else {
        result = await client.testAndFetchModels(baseUrl.trim(), apiKey);
      }
      setFetchedModels(result.modelIds);

      // Populate selection: for edit, pre-check previously saved models; otherwise select all
      if (isEdit && savedModelIds && savedModelIds.size > 0) {
        setSelectedModels(new Set(result.modelIds.filter((id) => savedModelIds.has(id))));
      } else {
        setSelectedModels(new Set(result.modelIds));
      }
    } catch (err) {
      setFetchModelsError(err instanceof Error ? err.message : "Failed to fetch models");
    } finally {
      setFetchModelsPending(false);
    }
  };

  // Select all / deselect all
  const allModelsSelected =
    fetchedModels !== null &&
    fetchedModels.length > 0 &&
    selectedModels.size === fetchedModels.length;
  const someModelsSelected =
    fetchedModels !== null && selectedModels.size > 0 && !allModelsSelected;

  const toggleSelectAll = useCallback(() => {
    if (allModelsSelected) {
      setSelectedModels(new Set());
    } else if (fetchedModels) {
      setSelectedModels(new Set(fetchedModels));
    }
  }, [allModelsSelected, fetchedModels]);

  const toggleModel = useCallback((modelId: string) => {
    setSelectedModels((prev) => {
      const next = new Set(prev);
      if (next.has(modelId)) {
        next.delete(modelId);
      } else {
        next.add(modelId);
      }
      return next;
    });
  }, []);

  const handleSubmit = async (e: React.FormEvent) => {
    e.preventDefault();
    setError(null);
    setFetchModelsError(null);

    if (!name.trim()) {
      setError("Provider name is required.");
      return;
    }
    if (!baseUrl.trim()) {
      setError("Base URL is required.");
      return;
    }
    if (!isEdit && !apiKey) {
      setError("API key is required for new providers.");
      return;
    }

    try {
      if (isEdit && editProvider) {
        const patch: CloudProviderUpdateInput = {
          baseUrl: baseUrl.trim(),
          apiKey: apiKey || null,
        };
        await updateMutation.mutateAsync({ id: editProvider.id, data: patch });

        // Persist model selection if the user fetched and curated models
        if (fetchedModels !== null) {
          await client.saveCloudProviderModels(
            editProvider.id,
            Array.from(selectedModels),
          );
        }
      } else {
        const input: CloudProviderInput = {
          name: name.trim(),
          baseUrl: baseUrl.trim(),
          apiKey,
        };
        const result = await createMutation.mutateAsync(input);

        if (fetchedModels !== null && selectedModels.size > 0) {
          // Save the user's curated selection
          await client.saveCloudProviderModels(
            result.id,
            Array.from(selectedModels),
          );
        } else {
          // No models fetched — auto-fetch all so they appear on the Models page
          try {
            await client.fetchCloudProviderModels(result.id);
          } catch {
            // Non-critical — models can be fetched later via Edit
          }
        }
      }

      queryClient.invalidateQueries({ queryKey: ["cloud-providers"] });
      queryClient.invalidateQueries({ queryKey: ["models"] });
      queryClient.invalidateQueries({ queryKey: ["provider-model-catalog"] });
      onClose();
    } catch (err) {
      setError(err instanceof Error ? err.message : "Failed to save");
    }
  };

  const isPending = createMutation.isPending || updateMutation.isPending;

  return (
    <Dialog open={open} onOpenChange={(o) => !o && onClose()} title={isEdit ? "Edit Provider" : "Add Provider"}>
      <form onSubmit={handleSubmit} className="p-5 space-y-4">
        <Input
          label="Name"
          value={name}
          onChange={(e) => setName(e.target.value)}
          disabled={isEdit}
          placeholder="e.g. OpenAI"
          autoFocus={!isEdit}
        />
        <Input
          label="Base URL"
          value={baseUrl}
          onChange={(e) => setBaseUrl(e.target.value)}
          placeholder="https://api.openai.com/v1"
        />
        <Input
          label={isEdit ? "API Key (leave blank to keep existing)" : "API Key"}
          type="password"
          value={apiKey}
          onChange={(e) => setApiKey(e.target.value)}
          placeholder={isEdit ? "sk-..." : ""}
          autoComplete="off"
        />

        {/* Fetch Models — available in both add and edit mode */}
        <div className="space-y-2 pt-1">
          <div className="flex items-center gap-3">
            <Button
              type="button"
              variant="secondary"
              size="sm"
              onClick={handleFetchModels}
              disabled={fetchModelsPending || !baseUrl.trim() || (!isEdit && !apiKey)}
            >
              {fetchModelsPending ? (
                <Loader2 className="size-3.5 animate-spin" />
              ) : (
                <Cloud className="size-3.5" />
              )}
              Fetch Models
            </Button>
            {fetchedModels !== null && (
              <span className="flex items-center gap-1.5 text-xs text-[var(--color-status-success)]">
                <Check className="size-3.5" />
                {fetchedModels.length} {fetchedModels.length === 1 ? "model" : "models"} found
              </span>
            )}
            {fetchModelsError && (
              <span className="flex items-center gap-1.5 text-xs text-[var(--color-status-error)]">
                <AlertCircle className="size-3.5" />
                {fetchModelsError}
              </span>
            )}
          </div>
          {fetchedModels !== null && fetchedModels.length > 0 && (
            <div className="space-y-2">
              {/* Select All / Deselect All + count */}
              <div className="flex items-center justify-between px-1">
                <TriCheckbox
                  checked={allModelsSelected}
                  indeterminate={someModelsSelected}
                  onChange={toggleSelectAll}
                  label={allModelsSelected ? "Deselect all models" : "Select all models"}
                />
                <span className="text-xs text-[var(--color-text-muted)]">
                  {selectedModels.size} of {fetchedModels.length} models selected
                </span>
              </div>

              {/* Scrollable model list */}
              <div className="max-h-48 overflow-y-auto rounded-md border border-[var(--color-border-subtle)] bg-[var(--color-bg-muted)]/30">
                <div className="p-2 space-y-0.5">
                  {fetchedModels.map((modelId) => (
                    <label
                      key={modelId}
                      className="flex items-center gap-2 px-2 py-1 text-xs text-[var(--color-text)] hover:bg-[var(--color-bg-muted)]/50 rounded cursor-pointer select-none transition-colors duration-[var(--duration-fast)]"
                    >
                      <input
                        type="checkbox"
                        checked={selectedModels.has(modelId)}
                        onChange={() => toggleModel(modelId)}
                        className="size-3.5 rounded accent-[var(--color-primary)] cursor-pointer"
                      />
                      <span className="font-mono truncate">{modelId}</span>
                    </label>
                  ))}
                </div>
              </div>
            </div>
          )}
        </div>

        {error && (
          <p className="text-sm text-[var(--color-status-error)]">{error}</p>
        )}

        <div className="flex justify-end gap-2 pt-1">
          <Button variant="secondary" size="sm" onClick={onClose} disabled={isPending}>
            Cancel
          </Button>
          <Button type="submit" variant="primary" size="sm" loading={isPending}>
            {isEdit ? "Save Changes" : "Add Provider"}
          </Button>
        </div>
      </form>
    </Dialog>
  );
}

// ─── Main Providers Page ─────────────────────────────────────────

export default function Providers() {
  const queryClient = useQueryClient();

  const {
    data: providers,
    isLoading,
  } = useQuery({
    queryKey: ["cloud-providers"],
    queryFn: () => client.listCloudProviders(),
  });

  const deleteMutation = useMutation({
    mutationFn: (id: string) => client.deleteCloudProvider(id),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ["cloud-providers"] }),
  });

  const [dialogOpen, setDialogOpen] = useState(false);
  const [editTarget, setEditTarget] = useState<CloudProvider | null>(null);
  const [deleteTarget, setDeleteTarget] = useState<CloudProvider | null>(null);

  const handleEdit = (provider: CloudProvider) => {
    setEditTarget(provider);
    setDialogOpen(true);
  };

  const handleAdd = () => {
    setEditTarget(null);
    setDialogOpen(true);
  };

  const handleCloseDialog = () => {
    setDialogOpen(false);
    setEditTarget(null);
  };

  return (
    <div className="p-6 space-y-6 max-w-5xl">
      {/* Page header */}
      <div>
        <h2 className="text-lg font-semibold text-[var(--color-text-heading)]">
          Cloud Providers
        </h2>
        <p className="text-xs text-[var(--color-text-muted)] mt-0.5">
          Cloud LLM providers route through the OpenAI-compatible endpoint without local scheduling.
        </p>
      </div>

      {/* Providers card */}
      <Card padding="none">
        {/* Header */}
        <div className="flex items-center justify-between px-4 py-3 border-b border-[var(--color-border-subtle)]">
          <div className="flex items-center gap-2">
            <Cloud className="size-4 text-[var(--color-text-muted)]" />
            <p className="text-xs font-medium text-[var(--color-text-muted)] uppercase tracking-wider">
              Cloud Providers
            </p>
          </div>
          <Button variant="primary" size="sm" onClick={handleAdd}>
            <Plus className="size-3.5" />
            Add Provider
          </Button>
        </div>

        {/* Content */}
        {isLoading ? (
          <div className="p-4 space-y-3">
            {Array.from({ length: 2 }, (_, i) => (
              <Skeleton key={i} className="h-14 w-full" />
            ))}
          </div>
        ) : providers && providers.length > 0 ? (
          <div>
            {/* Column headers */}
            <div className="flex items-center gap-4 px-4 py-2 border-b border-[var(--color-border)] bg-[var(--color-bg-muted)]/30">
              <span className="text-[10px] font-medium text-[var(--color-text-muted)] uppercase tracking-wider min-w-0 flex-1">
                Provider
              </span>
              <span className="text-[10px] font-medium text-[var(--color-text-muted)] uppercase tracking-wider shrink-0 w-[220px]">
                Base URL
              </span>
              <span className="text-[10px] font-medium text-[var(--color-text-muted)] uppercase tracking-wider shrink-0 w-[120px]">
                API Key
              </span>
              <span className="text-[10px] font-medium text-[var(--color-text-muted)] uppercase tracking-wider shrink-0 w-[80px] text-right">
                Models
              </span>
              <span className="text-[10px] font-medium text-[var(--color-text-muted)] uppercase tracking-wider shrink-0 w-[100px] text-right">
                Updated
              </span>
              <span className="text-[10px] font-medium text-[var(--color-text-muted)] uppercase tracking-wider shrink-0 w-[120px] text-right">
                Actions
              </span>
            </div>

            {providers.map((p) => (
              <ProviderRow
                key={p.id}
                provider={p}
                onEdit={handleEdit}
                onDelete={setDeleteTarget}
              />
            ))}
          </div>
        ) : (
          <EmptyState
            icon={<Cloud className="size-12" strokeWidth={1.5} />}
            title="No cloud providers"
            description="Add a cloud LLM provider to route requests through an OpenAI-compatible endpoint."
            action={
              <Button variant="primary" size="sm" onClick={handleAdd}>
                <Plus className="size-3.5" />
                Add Provider
              </Button>
            }
          />
        )}
      </Card>

      {/* Add / Edit Dialog */}
      <ProviderDialog
        open={dialogOpen}
        onClose={handleCloseDialog}
        editProvider={editTarget}
      />

      {/* Delete Confirmation */}
      <ConfirmDialog
        open={deleteTarget !== null}
        title="Delete provider"
        description={`Delete provider "${deleteTarget?.name ?? ""}"? This cannot be undone.`}
        confirmLabel="Delete"
        variant="danger"
        loading={deleteMutation.isPending}
        onConfirm={() => {
          if (deleteTarget) {
            deleteMutation.mutate(deleteTarget.id);
            setDeleteTarget(null);
          }
        }}
        onCancel={() => setDeleteTarget(null)}
      />
    </div>
  );
}
