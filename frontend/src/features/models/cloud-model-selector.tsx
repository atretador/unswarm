import { useQuery, useMutation, useQueryClient } from "@tanstack/react-query";
import { useState, useMemo, useEffect } from "react";
import {
  Cloud,
  Search,
  ChevronDown,
  ChevronRight,
  TriangleAlert,
  RefreshCw,
  Save,
} from "lucide-react";
import { TriCheckbox } from "../../components/ui";
import { Button, Badge } from "../../components/ui";
import { getProviderModelCatalog } from "../api-keys/api-keys-api";
import { client } from "../../lib/query-client";

/**
 * Curates which cloud models are active. Model selection is saved per-provider
 * to ModelsJson via PUT /api/cloudproviders/{id}/models.
 */
export function CloudModelSelector({ onSaved }: { onSaved?: () => void }) {
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
  }, [cloudProviders, expanded]);

  // Search
  const [search, setSearch] = useState("");

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

  // Filter providers by search
  const filteredProviders = useMemo(() => {
    const q = search.trim().toLowerCase();
    if (!q) return cloudProviders;
    return cloudProviders.filter(
      (p) =>
        p.name.toLowerCase().includes(q) ||
        p.models.some((m) => m.toLowerCase().includes(q)),
    );
  }, [cloudProviders, search]);

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

  // Check for new models (fetch from each provider)
  const fetchModelsMutation = useMutation({
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

      {/* Search input */}
      <div className="relative">
        <Search className="pointer-events-none absolute left-3 top-1/2 size-3.5 -translate-y-1/2 text-[var(--color-text-muted)]" />
        <input
          type="search"
          value={search}
          onChange={(e) => setSearch(e.target.value)}
          placeholder="Search providers or models..."
          aria-label="Search cloud providers"
          className="h-8 w-full rounded-[var(--radius-lg)] border border-[var(--color-border)] bg-[var(--color-bg-surface)] pl-8 pr-3 text-sm text-[var(--color-text)] placeholder:text-[var(--color-text-muted)] outline-none transition-colors focus:border-[var(--color-focus-ring)] focus:ring-1 focus:ring-[var(--color-focus-ring)]"
        />
      </div>

      {/* Per-provider sections */}
      <div className="space-y-2">
        {filteredProviders.map((provider) => {
          const { checked, indeterminate } = getProviderState(
            provider.name,
            provider.models,
          );
          const isExpanded = expanded.has(provider.name);

          return (
            <div
              key={provider.name}
              className="rounded-[var(--radius-lg)] border border-[var(--color-border-subtle)] px-3 py-2.5"
            >
              {/* Provider header */}
              <div className="flex items-center gap-2">
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
                <span className="text-xs font-medium text-[var(--color-text-heading)]">
                  {provider.name}
                </span>

                {/* Model count badge */}
                <Badge variant="default" size="sm">
                  {provider.models.length}
                </Badge>
              </div>

              {/* Model checkboxes (when expanded) */}
              {isExpanded && (
                <div className="max-h-[180px] overflow-y-auto flex flex-wrap gap-x-4 gap-y-1.5 mt-2 ml-6">
                  {provider.models.map((modelId) => (
                    <label
                      key={modelId}
                      className="inline-flex items-center gap-1.5 cursor-pointer select-none"
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
                      <span className="text-xs text-[var(--color-text)] font-mono">
                        {modelId}
                      </span>
                    </label>
                  ))}
                </div>
              )}
            </div>
          );
        })}
      </div>

      {/* Bottom bar */}
      <div className="flex items-center gap-2">
        <Button
          variant="secondary"
          size="sm"
          onClick={() => fetchModelsMutation.mutate()}
          loading={fetchModelsMutation.isPending}
        >
          <RefreshCw className="size-3.5" />
          Check for new models
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
    </div>
  );
}
