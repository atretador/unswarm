import { useQuery, useMutation, useQueryClient } from "@tanstack/react-query";
import { useState, useMemo, useEffect, useCallback } from "react";
import {
  Cloud,
  ChevronDown,
  ChevronRight,
  MessageSquare,
  TriangleAlert,
  RefreshCw,
  Save,
} from "lucide-react";
import { TriCheckbox } from "../../components/ui";
import { Button, Badge, Tooltip } from "../../components/ui";
import { getProviderModelCatalog } from "../api-keys/api-keys-api";
import { client } from "../../lib/query-client";

/**
 * Curates which cloud models are active. Model selection is saved per-provider
 * to ModelsJson via PUT /api/cloudproviders/{id}/models.
 */
export function CloudModelSelector({ onSaved, onChatModel, filter }: { onSaved?: () => void; onChatModel?: (modelId: string) => void; filter?: string }) {
  const queryClient = useQueryClient();

  // Fetch cloud providers (for ID ↔ name mapping)
  const providersQuery = useQuery({
    queryKey: ["cloud-providers"],
    queryFn: () => client.listCloudProviders(),
  });

  // Fetch the provider-model catalog
  const catalogQuery = useQuery({
    queryKey: ["provider-model-catalog"],
    queryFn: () => getProviderModelCatalog(),
  });

  // Build name → id map
  const nameToId = useMemo(() => {
    const map = new Map<string, string>();
    for (const p of providersQuery.data ?? []) {
      map.set(p.name, p.id);
    }
    return map;
  }, [providersQuery.data]);

  // Cloud-only catalog entries
  const cloudProviders = useMemo(() => {
    return (catalogQuery.data ?? []).filter((e) => e.kind === "cloud");
  }, [catalogQuery.data]);

  // Filtered providers/models based on search string
  const filteredProviders = useMemo(() => {
    const q = (filter ?? "").trim().toLowerCase();
    if (!q) return cloudProviders;
    return cloudProviders
      .map((p) => ({
        ...p,
        models: p.models.filter(
          (m) =>
            m.toLowerCase().includes(q) ||
            p.name.toLowerCase().includes(q),
        ),
      }))
      .filter((p) => p.models.length > 0 || p.name.toLowerCase().includes(q));
  }, [cloudProviders, filter]);

  // Selection state: Map<providerName, Set<modelId>>
  const [selection, setSelection] = useState<Map<string, Set<string>>>(
    new Map(),
  );

  // Track whether selection has been initialized from catalog
  const [initialized, setInitialized] = useState(false);

  // Initialize selection from catalog (all models checked by default)
  useEffect(() => {
    if (initialized || cloudProviders.length === 0) return;
    const initial = new Map<string, Set<string>>();
    for (const p of cloudProviders) {
      initial.set(p.name, new Set(p.models));
    }
    setSelection(initial);
    setInitialized(true);
  }, [cloudProviders, initialized]);

  // Snapshot of initial selection for dirty tracking
  const [initialSelection, setInitialSelection] = useState<
    Map<string, Set<string>>
  >(new Map());
  useEffect(() => {
    if (initialized && initialSelection.size === 0 && selection.size > 0) {
      setInitialSelection(new Map(selection));
    }
  }, [initialized, selection, initialSelection]);

  // Expanded providers: Set<providerName>
  const [expanded, setExpanded] = useState<Set<string>>(new Set());
  useEffect(() => {
    if (expanded.size > 0 || cloudProviders.length === 0) return;
    // Providers with ≤5 models start expanded
    const initial = new Set<string>();
    for (const p of cloudProviders) {
      if (p.models.length <= 5) initial.add(p.name);
    }
    setExpanded(initial);
  }, [cloudProviders]);

  // Per-provider refresh state: track which providers are currently refreshing
  const [refreshing, setRefreshing] = useState<Set<string>>(new Set());
  // Per-provider error state
  const [errors, setErrors] = useState<Map<string, string>>(new Map());

  const clearError = useCallback((name: string) => {
    setErrors((prev) => {
      const next = new Map(prev);
      next.delete(name);
      return next;
    });
  }, []);

  // Dirty tracking
  const isDirty = useMemo(() => {
    if (initialSelection.size !== selection.size) return true;
    for (const [name, models] of selection) {
      const initial = initialSelection.get(name);
      if (!initial) return true;
      if (initial.size !== models.size) return true;
      for (const m of models) {
        if (!initial.has(m)) return true;
      }
    }
    return false;
  }, [selection, initialSelection]);

  // Selection helpers
  const getProviderState = (name: string, models: string[]) => {
    const sel = selection.get(name) ?? new Set(models);
    const all = models.every((m) => sel.has(m));
    const some = models.some((m) => sel.has(m));
    return { checked: all, indeterminate: !all && some, selected: sel };
  };

  const toggleProvider = (name: string, models: string[], checked: boolean) => {
    setSelection((prev) => {
      const next = new Map(prev);
      next.set(name, checked ? new Set(models) : new Set());
      return next;
    });
  };

  const toggleModel = (providerName: string, modelId: string, checked: boolean) => {
    setSelection((prev) => {
      const next = new Map(prev);
      const set = new Set(next.get(providerName) ?? []);
      if (checked) {
        set.add(modelId);
      } else {
        set.delete(modelId);
      }
      next.set(providerName, set);
      return next;
    });
  };

  const toggleExpanded = (name: string) => {
    setExpanded((prev) => {
      const next = new Set(prev);
      if (next.has(name)) {
        next.delete(name);
      } else {
        next.add(name);
      }
      return next;
    });
  };

  // Save mutation
  const saveMutation = useMutation({
    mutationFn: async () => {
      const providers = cloudProviders;
      await Promise.all(
        providers.map(async (p) => {
          const providerId = nameToId.get(p.name);
          if (!providerId) return;
          const selected = selection.get(p.name) ?? new Set(p.models);
          return client.saveCloudProviderModels(providerId, [...selected]);
        }),
      );
    },
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["models"] });
      queryClient.invalidateQueries({ queryKey: ["provider-model-catalog"] });
      onSaved?.();
    },
  });

  // Check all providers for new models
  const fetchAllMutation = useMutation({
    mutationFn: async () => {
      const providers = providersQuery.data ?? [];
      await Promise.all(
        providers.map((p) => client.fetchCloudProviderModels(p.id)),
      );
    },
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ["provider-model-catalog"] });
      queryClient.invalidateQueries({ queryKey: ["models"] });
      // Reset initialized so selection picks up any new models
      setInitialized(false);
      setInitialSelection(new Map());
      setErrors(new Map());
    },
  });

  const isLoading =
    catalogQuery.isLoading || providersQuery.isLoading;

  if (isLoading) return null;

  const hasCloudProviders = cloudProviders.length > 0;
  if (!hasCloudProviders) return null;

  return (
    <div className="space-y-3">
      {/* Warning banner */}
      <div className="rounded-[var(--radius-lg)] border border-[color-mix(in_srgb,var(--color-status-warning)_35%,transparent)] bg-[color-mix(in_srgb,var(--color-status-warning)_8%,transparent)] px-3.5 py-2.5">
        <div className="flex items-start gap-2.5">
          <TriangleAlert className="size-4 shrink-0 text-[var(--color-status-warning)] mt-0.5" />
          <p className="text-xs text-[var(--color-text)]">
            Models selected here determine what&apos;s available via the API. To use a
            model, it must also be granted to an API key in the{" "}
            <span className="font-medium">API Keys</span> page.
          </p>
        </div>
      </div>

      {/* Action bar — always visible at top */}
      <div className="flex items-center gap-2">
        <Button
          variant="secondary"
          size="sm"
          onClick={() => fetchAllMutation.mutate()}
          loading={fetchAllMutation.isPending}
        >
          <RefreshCw className="size-3.5" />
          Check all providers
        </Button>
        <Button
          variant="primary"
          size="sm"
          onClick={() => saveMutation.mutate()}
          loading={saveMutation.isPending}
          disabled={!isDirty}
        >
          <Save className="size-3.5" />
          Save
        </Button>
      </div>

      {/* Per-provider sections */}
      <div className="space-y-2">
        {filteredProviders.map((provider) => {
          const { checked, indeterminate } = getProviderState(
            provider.name,
            provider.models,
          );
          const isExpanded = expanded.has(provider.name);
          const isRefreshing = refreshing.has(provider.name);
          const error = errors.get(provider.name);

          return (
            <div
              key={provider.name}
              className="rounded-[var(--radius-lg)] border border-[var(--color-border-subtle)] px-3 py-2.5"
            >
              {/* Provider header */}
              <div className="flex items-center justify-between gap-2">
                {/* Left: expand + icon + checkbox + name + badge */}
                <div className="flex items-center gap-2 min-w-0">
                  {/* Expand toggle */}
                  <button
                    type="button"
                    onClick={() => toggleExpanded(provider.name)}
                    className="flex size-5 shrink-0 items-center justify-center rounded text-[var(--color-text-muted)] transition-colors hover:bg-[var(--color-bg-muted)] hover:text-[var(--color-text)]"
                    aria-label={isExpanded ? "Collapse" : "Expand"}
                  >
                    {isExpanded ? (
                      <ChevronDown className="size-3.5" />
                    ) : (
                      <ChevronRight className="size-3.5" />
                    )}
                  </button>

                  {/* Provider icon */}
                  <Cloud className="size-3.5 shrink-0 text-[var(--color-primary)]" />

                  {/* TriCheckbox (select-all) */}
                  <TriCheckbox
                    checked={checked}
                    indeterminate={indeterminate}
                    onChange={(c) =>
                      toggleProvider(provider.name, provider.models, c)
                    }
                    label={`Select all ${provider.name} models`}
                  />

                  {/* Provider name */}
                  <span className="text-xs font-medium text-[var(--color-text-heading)] truncate">
                    {provider.name}
                  </span>

                  {/* Model count badge */}
                  <Badge variant="default" size="sm">
                    {provider.models.length}
                  </Badge>
                </div>

                {/* Right: error indicator + refresh button */}
                <div className="flex shrink-0 items-center gap-2">
                  {/* Error indicator */}
                  {error && (
                    <button
                      type="button"
                      onClick={() => clearError(provider.name)}
                      className="flex items-center gap-1 text-xs text-[var(--color-status-error)] hover:text-[var(--color-text)] transition-colors"
                      title={error}
                      aria-label="Dismiss error"
                    >
                      <TriangleAlert className="size-3.5" />
                      Error
                    </button>
                  )}

                  {/* Per-provider refresh */}
                  <button
                    type="button"
                    onClick={() => {
                      const providerId = nameToId.get(provider.name);
                      if (!providerId) return;
                      clearError(provider.name);
                      setRefreshing((prev) => new Set(prev).add(provider.name));
                      client.fetchCloudProviderModels(providerId)
                        .then(() => {
                          queryClient.invalidateQueries({ queryKey: ["provider-model-catalog"] });
                          queryClient.invalidateQueries({ queryKey: ["models"] });
                          setInitialized(false);
                          setInitialSelection(new Map());
                        })
                        .catch((err: Error) => {
                          setErrors((prev) => {
                            const next = new Map(prev);
                            next.set(provider.name, err.message);
                            return next;
                          });
                        })
                        .finally(() => {
                          setRefreshing((prev) => {
                            const next = new Set(prev);
                            next.delete(provider.name);
                            return next;
                          });
                        });
                    }}
                    disabled={isRefreshing}
                    className={`flex shrink-0 items-center gap-1 rounded-[var(--radius-md)] px-1.5 py-1 text-xs transition-colors disabled:opacity-40 ${
                      isRefreshing
                        ? "cursor-wait text-[var(--color-text-muted)]"
                        : "text-[var(--color-text-muted)] hover:bg-[var(--color-bg-muted)] hover:text-[var(--color-text)]"
                    }`}
                    aria-label={`Refresh models for ${provider.name}`}
                  >
                    <RefreshCw className={`size-3.5 ${isRefreshing ? "animate-spin" : ""}`} />
                    <span>{isRefreshing ? "Fetching…" : "Refresh"}</span>
                  </button>
                </div>
              </div>

              {/* Model checkboxes (when expanded) */}
              {isExpanded && (
                <div className="max-h-[180px] overflow-y-auto flex flex-col gap-y-1 mt-2 ml-6">
                  {provider.models.map((modelId) => (
                    <div
                      key={modelId}
                      className="flex items-center gap-2 rounded-[var(--radius-md)] px-2 py-1 hover:bg-[var(--color-bg-muted)] transition-colors"
                    >
                      <input
                        type="checkbox"
                        checked={
                          selection.get(provider.name)?.has(modelId) ?? true
                        }
                        onChange={(e) =>
                          toggleModel(provider.name, modelId, e.target.checked)
                        }
                        className="size-3.5 rounded accent-[var(--color-primary)] cursor-pointer"
                      />
                      <span className="flex-1 text-xs text-[var(--color-text)] font-mono">
                        {modelId}
                      </span>
                      <Badge variant="info" size="sm">
                        {provider.name}
                      </Badge>
                      {onChatModel && (
                        <Tooltip content={`Test chat with ${modelId}`}>
                          <button
                            type="button"
                            onClick={() => onChatModel(modelId)}
                            aria-label={`Test chat with ${modelId}`}
                            className="flex size-6 items-center justify-center rounded-[var(--radius-md)] text-[var(--color-text-muted)] transition-colors hover:bg-[var(--color-bg-surface)] hover:text-[var(--color-primary)] focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-[var(--color-focus-ring)]"
                          >
                            <MessageSquare className="size-3" />
                          </button>
                        </Tooltip>
                      )}
                    </div>
                  ))}
                </div>
              )}
            </div>
          );
        })}
      </div>
    </div>
  );
}
