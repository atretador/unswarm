// Manage-key modal: per-key access control editor + usage summary.
//
// Selection model (see AccessDraft below): a key is either unrestricted
// ("Full access"), or grants whole providers and/or individual models.
// The wire shape is {providers:[], models:[]} where an entry in `providers`
// grants ALL of that provider's models and `models` carries flat per-model
// grants. Empty lists = unrestricted.
//
// Fallbacks: if /api/provider-model-catalog or the access endpoints aren't
// deployed yet (404/error), the affected section degrades to a non-blocking
// notice instead of crashing. If the usage endpoint 404s, that section hides.

import { useEffect, useMemo, useRef, useState } from "react";
import { useMutation, useQuery } from "@tanstack/react-query";
import {
  Check,
  ChevronDown,
  ChevronRight,
  Cloud,
  Globe,
  HardDrive,
  Info,
  Route,
  Search,
  Server,
  ShieldCheck,
  SlidersHorizontal,
  TriangleAlert,
} from "lucide-react";
import { ApiError } from "../../lib/api/httpClient";
import { Badge, Button, ConfirmDialog, Dialog, Select, Switch, TriCheckbox } from "../../components/ui";
import type {
  ApiKeyAccess,
  ApiKeyItem,
  ProviderModelCatalogEntry,
} from "../../lib/api/types";
import {
  getApiKeyAccess,
  getApiKeyUsage,
  getProviderModelCatalog,
  putApiKeyAccess,
} from "./api-keys-api";
import { formatTokens } from "../metrics/format";

// ─── Selection-state model ───────────────────────────────────────

interface ProviderSelection {
  /** Grant every model of this provider. */
  all: boolean;
  /** Individually granted models (only meaningful when all=false). */
  models: Set<string>;
}

interface AccessDraft {
  fullAccess: boolean;
  providers: Map<string, ProviderSelection>;
  /** Saved grants referencing models absent from the catalog. */
  unmatchedModels: string[];
}

function emptyDraft(catalog: ProviderModelCatalogEntry[]): AccessDraft {
  const providers = new Map<string, ProviderSelection>();
  for (const entry of catalog) {
    providers.set(entry.name, { all: false, models: new Set() });
  }
  return { fullAccess: false, providers, unmatchedModels: [] };
}

/** Wire shape: providers[] grant all their models; models[] are flat grants. */
function serializeDraft(draft: AccessDraft): ApiKeyAccess {
  if (draft.fullAccess) return { providers: [], models: [] };
  const providers: string[] = [];
  const models = new Set<string>();
  for (const [name, sel] of draft.providers) {
    if (sel.all) {
      providers.push(name);
      continue;
    }
    for (const m of sel.models) models.add(m);
  }
  for (const m of draft.unmatchedModels) models.add(m);
  return { providers, models: [...models] };
}

function parseAccess(
  access: ApiKeyAccess,
  catalog: ProviderModelCatalogEntry[],
): AccessDraft {
  const draft = emptyDraft(catalog);
  const isEmpty =
    (access.providers?.length ?? 0) === 0 && (access.models?.length ?? 0) === 0;
  if (isEmpty) return { ...draft, fullAccess: true };

  for (const name of access.providers ?? []) {
    const sel = draft.providers.get(name) ?? { all: false, models: new Set<string>() };
    sel.all = true;
    draft.providers.set(name, sel);
  }

  const ownersByModel = new Map<string, string[]>();
  for (const entry of catalog) {
    for (const model of entry.models) {
      const owners = ownersByModel.get(model) ?? [];
      owners.push(entry.name);
      ownersByModel.set(model, owners);
    }
  }
  for (const model of access.models ?? []) {
    const owners = ownersByModel.get(model);
    if (!owners || owners.length === 0) {
      draft.unmatchedModels.push(model);
      continue;
    }
    for (const owner of owners) {
      draft.providers.get(owner)?.models.add(model);
    }
  }
  return draft;
}

function countGranted(draft: AccessDraft): number {
  let n = 0;
  for (const [, sel] of draft.providers) {
    n += sel.all ? Number.POSITIVE_INFINITY : sel.models.size;
  }
  return n;
}

function is404(err: unknown): boolean {
  return err instanceof ApiError && err.status === 404;
}

// ─── Small pieces ────────────────────────────────────────────────

function SectionHeader({
  icon: Icon,
  title,
}: {
  icon: typeof Globe;
  title: string;
}) {
  return (
    <div className="flex items-center gap-2">
      <Icon className="size-3.5 text-[var(--color-text-muted)]" />
      <p className="text-xs font-medium text-[var(--color-text-muted)] uppercase tracking-wider">
        {title}
      </p>
    </div>
  );
}

function NoticeBanner({ children }: { children: React.ReactNode }) {
  return (
    <div className="flex items-start gap-2.5 rounded-[var(--radius-lg)] border border-[color-mix(in_srgb,var(--color-status-warning)_35%,transparent)] bg-[color-mix(in_srgb,var(--color-status-warning)_10%,transparent)] px-3.5 py-3">
      <TriangleAlert className="size-4 shrink-0 mt-0.5 text-[var(--color-status-warning)]" />
      <div className="text-xs text-[var(--color-text-muted)] leading-relaxed space-y-1">
        {children}
      </div>
    </div>
  );
}

// ─── Usage section ────────────────────────────────────────────────

type UsageRange = "7d" | "30d" | "90d";

const USAGE_RANGE_OPTIONS = [
  { value: "7d", label: "Last 7 days" },
  { value: "30d", label: "Last 30 days" },
  { value: "90d", label: "Last 90 days" },
];

const USAGE_RANGE_MS: Record<UsageRange, number> = {
  "7d": 7 * 86_400_000,
  "30d": 30 * 86_400_000,
  "90d": 90 * 86_400_000,
};

function UsageSection({ keyId }: { keyId: string }) {
  const [range, setRange] = useState<UsageRange>("30d");

  const window = useMemo(() => {
    const now = new Date();
    return {
      from: new Date(now.getTime() - USAGE_RANGE_MS[range]).toISOString(),
      to: now.toISOString(),
    };
  }, [range]);

  const { data, isLoading, error } = useQuery({
    queryKey: ["api-key-usage", keyId, range],
    queryFn: () => getApiKeyUsage(keyId, window),
    enabled: !!keyId,
    staleTime: 60_000,
  });

  // Endpoint not deployed yet — hide the section entirely.
  if (error && is404(error)) return null;

  return (
    <div className="space-y-3">
      <div className="flex items-center justify-between gap-3 flex-wrap">
        <SectionHeader icon={HardDrive} title="Usage" />
        <Select
          aria-label="Usage time range"
          options={USAGE_RANGE_OPTIONS}
          value={range}
          onChange={(e) => setRange(e.target.value as UsageRange)}
          className="h-7 w-[130px] text-xs"
        />
      </div>

      {error && !is404(error) && (
        <p className="text-xs text-[var(--color-status-error)]">
          {(error as Error).message}
        </p>
      )}

      {isLoading && (
        <p className="text-xs text-[var(--color-text-muted)] py-3">Loading…</p>
      )}

      {data && (
        <>
          {/* Headline numbers */}
          <div className="grid grid-cols-2 sm:grid-cols-4 gap-2">
            {[
              { label: "Requests", value: data.totals.requestCount.toLocaleString() },
              { label: "Tokens in", value: formatTokens(data.totals.promptTokens) },
              { label: "Tokens out", value: formatTokens(data.totals.completionTokens) },
              { label: "Cached", value: formatTokens(data.totals.cachedTokens) },
            ].map((stat) => (
              <div
                key={stat.label}
                className="rounded-[var(--radius-lg)] border border-[var(--color-border-subtle)] bg-[var(--color-bg-muted)]/40 px-3 py-2"
              >
                <p className="text-[10px] text-[var(--color-text-muted)]">
                  {stat.label}
                </p>
                <p className="text-sm font-semibold font-mono text-[var(--color-text-heading)]">
                  {stat.value}
                </p>
              </div>
            ))}
          </div>

          {/* Per-model breakdown */}
          {(data.models?.length ?? 0) > 0 ? (
            <table className="w-full text-sm">
              <thead>
                <tr className="border-b border-[var(--color-border)]">
                  <th className="text-left py-1.5 pr-4 text-xs font-medium text-[var(--color-text-muted)]">
                    Model
                  </th>
                  <th className="text-right py-1.5 px-3 text-xs font-medium text-[var(--color-text-muted)]">
                    Requests
                  </th>
                  <th className="text-right py-1.5 px-3 text-xs font-medium text-[var(--color-text-muted)] hidden sm:table-cell">
                    In
                  </th>
                  <th className="text-right py-1.5 px-3 text-xs font-medium text-[var(--color-text-muted)] hidden sm:table-cell">
                    Out
                  </th>
                  <th className="text-right py-1.5 pl-3 text-xs font-medium text-[var(--color-text-muted)]">
                    Cached
                  </th>
                </tr>
              </thead>
              <tbody>
                {data.models!.map((m) => (
                  <tr
                    key={`${m.provider ?? ""}-${m.model}`}
                    className="border-b border-[var(--color-border)] last:border-0"
                  >
                    <td className="py-2 pr-4 max-w-[200px] truncate" title={m.model}>
                      <span className="font-medium text-[var(--color-text)]">
                        {m.model}
                      </span>
                      {m.provider && (
                        <Badge variant="outline" size="sm" className="ml-2">
                          {m.provider}
                        </Badge>
                      )}
                    </td>
                    <td className="py-2 px-3 text-right font-mono text-[var(--color-text)]">
                      {m.requestCount.toLocaleString()}
                    </td>
                    <td className="py-2 px-3 text-right font-mono text-[var(--color-text)] hidden sm:table-cell">
                      {formatTokens(m.promptTokens)}
                    </td>
                    <td className="py-2 px-3 text-right font-mono text-[var(--color-text)] hidden sm:table-cell">
                      {formatTokens(m.completionTokens)}
                    </td>
                    <td className="py-2 pl-3 text-right font-mono text-[var(--color-status-warning)]">
                      {m.cachedTokens > 0 ? formatTokens(m.cachedTokens) : "\u2014"}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          ) : (
            !isLoading && (
              <p className="text-xs text-[var(--color-text-muted)] py-3 text-center">
                No usage recorded for this key in the selected window.
              </p>
            )
          )}
        </>
      )}
    </div>
  );
}

// ─── Main modal ──────────────────────────────────────────────────

export interface ManageKeyModalProps {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  apiKey: ApiKeyItem;
}

export function ManageKeyModal({ open, onOpenChange, apiKey }: ManageKeyModalProps) {
  const [draft, setDraft] = useState<AccessDraft | null>(null);
  const [initialSerialized, setInitialSerialized] = useState<string | null>(null);
  const [savedTick, setSavedTick] = useState(false);
  const [confirmDiscard, setConfirmDiscard] = useState(false);
  const [expandedProviders, setExpandedProviders] = useState<Set<string>>(new Set());
  const [modelSearch, setModelSearch] = useState("");
  const expandedInit = useRef(false);

  // AccessJson grants are only enforced on /v1 inference; agent-scope keys
  // can't call /v1, so the access editor is hidden for them entirely.
  const isAgentKey = apiKey.scope === "agent";

  function toggleExpand(name: string) {
    setExpandedProviders((prev) => {
      const next = new Set(prev);
      if (next.has(name)) next.delete(name);
      else next.add(name);
      return next;
    });
  }

  const catalogQuery = useQuery({
    queryKey: ["provider-model-catalog"],
    queryFn: () => getProviderModelCatalog(),
    enabled: open && !isAgentKey,
    staleTime: 5 * 60 * 1000,
  });

  const accessQuery = useQuery({
    queryKey: ["api-key-access", apiKey.id],
    queryFn: () => getApiKeyAccess(apiKey.id),
    enabled: open && !isAgentKey,
  });

  const catalog = catalogQuery.data;
  const access = accessQuery.data;

  // Default expansion: providers with ≤5 models start expanded.
  const defaultExpanded = useMemo(() => {
    if (!catalog) return new Set<string>();
    return new Set(catalog.filter((e) => e.models.length <= 5).map((e) => e.name));
  }, [catalog]);

  // Seed expandedProviders from defaults once when catalog loads.
  useEffect(() => {
    if (catalog && !expandedInit.current) {
      setExpandedProviders(defaultExpanded);
      expandedInit.current = true;
    }
  }, [catalog, defaultExpanded]);

  // Reset transient state then build the editable draft.
  // Must be a single effect so init always runs after reset, preventing
  // the race where React batches both setDraft calls and the null wins.
  useEffect(() => {
    if (!open) return;
    // Reset first
    setDraft(null);
    setInitialSerialized(null);
    setSavedTick(false);
    setConfirmDiscard(false);
    setModelSearch("");
    expandedInit.current = false;
    setExpandedProviders(new Set());
    // Then init if data is already available
    if (catalog && access) {
      const parsed = parseAccess(access, catalog);
      setDraft(parsed);
      setInitialSerialized(JSON.stringify(serializeDraft(parsed)));
    }
  }, [open, apiKey.id, catalog, access]);

  const saveMutation = useMutation({
    mutationFn: (next: ApiKeyAccess) => putApiKeyAccess(apiKey.id, next),
    onSuccess: (saved) => {
      const effective = saved ?? serializeDraft(draft!);
      setInitialSerialized(JSON.stringify(effective));
      setSavedTick(true);
      setTimeout(() => setSavedTick(false), 1800);
    },
  });

  const catalogUnavailable = catalogQuery.isError;
  const accessUnavailable = accessQuery.isError;
  const editorReady = open && !!draft && !catalogUnavailable && !accessUnavailable;

  const serialized = draft ? JSON.stringify(serializeDraft(draft)) : null;
  const dirty =
    editorReady &&
    serialized !== null &&
    initialSerialized !== null &&
    serialized !== initialSerialized;

  function patchProvider(name: string, patch: Partial<ProviderSelection>) {
    setDraft((prev) => {
      if (!prev) return prev;
      const providers = new Map(prev.providers);
      const sel = providers.get(name) ?? { all: false, models: new Set<string>() };
      providers.set(name, { ...sel, ...patch });
      return { ...prev, providers };
    });
  }

  function toggleModel(providerName: string, model: string, granted: boolean) {
    setDraft((prev) => {
      if (!prev) return prev;
      const providers = new Map(prev.providers);
      const sel = providers.get(providerName) ?? { all: false, models: new Set<string>() };
      const models = new Set(sel.models);
      if (granted) models.add(model);
      else models.delete(model);
      providers.set(providerName, { ...sel, models });
      return { ...prev, providers };
    });
  }

  function requestClose(next: boolean) {
    if (!next && dirty) {
      setConfirmDiscard(true);
      return;
    }
    onOpenChange(next);
  }

  const nothingGranted = draft !== null && !draft.fullAccess && countGranted(draft) === 0;

  return (
    <>
      <Dialog
        open={open}
        onOpenChange={requestClose}
        title={`Manage "${apiKey.name}"`}
        className="sm:max-w-[680px]"
      >
        <div className="px-5 py-4 space-y-6">
          {/* ── Access control ───────────────────────────────── */}
          {/* Hidden for agent-scope keys: AccessJson is only enforced on
              /v1 inference, which agent keys cannot call. */}
          {!isAgentKey && (
          <section className="space-y-4">
            <SectionHeader icon={ShieldCheck} title="Access control" />

            {catalogUnavailable && (
              <NoticeBanner>
                <p>
                  The provider/model catalog isn't available right now
                  ({(catalogQuery.error as Error).message}). The backend may need
                  restarting — access editing is paused until then.
                </p>
              </NoticeBanner>
            )}
            {!catalogUnavailable && accessUnavailable && (
              <NoticeBanner>
                <p>
                  Couldn't load this key's current access ({(accessQuery.error as Error).message}).
                  Saving is paused until it loads.
                </p>
              </NoticeBanner>
            )}

            {editorReady && draft && (
              <>
                {/* Full access toggle */}
                <div className="flex items-start justify-between gap-4 rounded-[var(--radius-lg)] border border-[var(--color-border-subtle)] bg-[var(--color-bg-muted)]/40 px-3.5 py-3">
                  <div>
                    <p className="text-sm font-medium text-[var(--color-text-heading)] flex items-center gap-1.5">
                      Full access
                      <Globe className="size-3.5 text-[var(--color-text-muted)]" />
                    </p>
                    <p className="text-xs text-[var(--color-text-muted)] mt-0.5">
                      This key can call every provider and model. Turn off to
                      restrict it below.
                    </p>
                  </div>
                  <Switch
                    checked={draft.fullAccess}
                    onCheckedChange={(checked) =>
                      setDraft((prev) =>
                        prev ? { ...prev, fullAccess: checked } : prev,
                      )
                    }
                  />
                </div>

                {!draft.fullAccess && (
                  <>
                    <p className="text-xs text-[var(--color-text-muted)]">
                      Tick a provider to grant <em>all</em> of its models, or pick
                      individual models for a narrower grant.
                    </p>

                    <div className="relative">
                      <Search className="absolute left-3 top-1/2 -translate-y-1/2 size-3.5 text-[var(--color-text-muted)]" />
                      <input
                        type="text"
                        placeholder="Search models…"
                        value={modelSearch}
                        onChange={(e) => setModelSearch(e.target.value)}
                        className="w-full rounded-[var(--radius-lg)] border border-[var(--color-border-subtle)] bg-[var(--color-bg)] pl-9 pr-3 py-1.5 text-xs text-[var(--color-text)] placeholder:text-[var(--color-text-muted)] focus:outline-none focus:ring-1 focus:ring-[var(--color-primary)]"
                      />
                    </div>

                    {catalogQuery.data && (["cloud", "local", "router"] as const).map((kind) => {
                      const entries = catalogQuery.data
                        .filter((c) => c.kind === kind)
                        .map((e) => ({
                          ...e,
                          models: modelSearch
                            ? e.models.filter((m) => m.toLowerCase().includes(modelSearch.toLowerCase()))
                            : e.models,
                        }))
                        .filter((e) => (modelSearch ? e.models.length > 0 : true));
                      if (entries.length === 0) return null;
                      return (
                        <div key={kind} className="space-y-2">
                          <div className="flex items-center gap-2 pt-1">
{kind === "cloud" ? (
  <Cloud className="size-3.5 text-[var(--color-text-muted)]" />
) : kind === "router" ? (
  <Route className="size-3.5 text-[var(--color-text-muted)]" />
) : (
  <Server className="size-3.5 text-[var(--color-text-muted)]" />
)}
<p className="text-xs font-semibold text-[var(--color-text-heading)]">
  {kind === "cloud" ? "Cloud providers" : kind === "router" ? "Router profiles" : "Self-hosted agents"}
</p>
                          </div>

                          <div className="space-y-2">
                            {entries.map((entry) => {
                              const sel =
                                draft.providers.get(entry.name) ??
                                { all: false, models: new Set<string>() };
                              const picked = sel.models.size;
                              const some = picked > 0 && !sel.all;
                              const isExpanded = expandedProviders.has(entry.name);
                              return (
                                <div
                                  key={entry.name}
                                  className="rounded-[var(--radius-lg)] border border-[var(--color-border-subtle)] px-3 py-2.5"
                                >
                                  <div className="flex items-center gap-2.5">
                                    <TriCheckbox
                                      checked={sel.all}
                                      indeterminate={some}
                                      disabled={saveMutation.isPending}
                                      label={`Grant all ${entry.name} models`}
                                      onChange={(checked) =>
                                        patchProvider(entry.name, {
                                          all: checked,
                                          // Granting all clears per-model picks;
                                          // they come back implied anyway.
                                          models: checked ? new Set<string>() : sel.models,
                                        })
                                      }
                                    />
                                    <span className="text-sm font-medium text-[var(--color-text-heading)]">
                                      {entry.name}
                                    </span>
                                    {sel.all ? (
                                      <Badge variant="info" size="sm">
                                        all models
                                      </Badge>
                                    ) : (
                                      picked > 0 && (
                                        <Badge variant="default" size="sm">
                                          {picked} of {entry.models.length}
                                        </Badge>
                                      )
                                    )}
                                    <span className="flex-1" />
                                    {entry.models.length > 0 && (
                                      <button
                                        type="button"
                                        onClick={() => toggleExpand(entry.name)}
                                        className="p-1 rounded hover:bg-[var(--color-bg-muted)] transition-colors"
                                        aria-label={isExpanded ? `Collapse ${entry.name} models` : `Expand ${entry.name} models`}
                                      >
                                        {isExpanded ? (
                                          <ChevronDown className="size-3.5 text-[var(--color-text-muted)]" />
                                        ) : (
                                          <ChevronRight className="size-3.5 text-[var(--color-text-muted)]" />
                                        )}
                                      </button>
                                    )}
                                  </div>

                                  {isExpanded && entry.models.length > 0 && (
                                    <div className="max-h-[180px] overflow-y-auto flex flex-wrap gap-x-4 gap-y-1.5 mt-2 ml-6">
                                      {entry.models.map((model) => {
                                        const granted = sel.all || sel.models.has(model);
                                        return (
                                          <label
                                            key={model}
                                            className={`inline-flex items-center gap-1.5 text-xs select-none ${
                                              sel.all
                                                ? "text-[var(--color-text-muted)]"
                                                : "text-[var(--color-text)] cursor-pointer"
                                            }`}
                                          >
                                            <input
                                              type="checkbox"
                                              checked={granted}
                                              disabled={sel.all || saveMutation.isPending}
                                              onChange={(e) =>
                                                toggleModel(entry.name, model, e.target.checked)
                                              }
                                              className="size-3 rounded accent-[var(--color-primary)] cursor-pointer disabled:cursor-not-allowed"
                                            />
                                            <span className="max-w-[220px] truncate" title={model}>
                                              {model}
                                            </span>
                                          </label>
                                        );
                                      })}
                                    </div>
                                  )}
                                </div>
                              );
                            })}
                          </div>
                        </div>
                      );
                    })}

                    {/* Unmatched legacy grants stay visible, read-only */}
                    {draft.unmatchedModels.length > 0 && (
                      <div className="flex items-start gap-2 text-xs text-[var(--color-text-muted)]">
                        <Info className="size-3.5 mt-0.5 shrink-0" />
                        <p>
                          Also granted (no longer in the catalog):{" "}
                          {draft.unmatchedModels.map((m) => (
                            <code
                              key={m}
                              className="font-mono text-[var(--color-text)] bg-[var(--color-bg-muted)] rounded px-1 mr-1"
                            >
                              {m}
                            </code>
                          ))}
                        </p>
                      </div>
                    )}

                    {nothingGranted && (
                      <div className="flex items-start gap-2 rounded-[var(--radius-lg)] border border-[color-mix(in_srgb,var(--color-status-error)_35%,transparent)] bg-[color-mix(in_srgb,var(--color-status-error)_8%,transparent)] px-3.5 py-2.5">
                        <TriangleAlert className="size-4 shrink-0 mt-0.5 text-[var(--color-status-error)]" />
                        <p className="text-xs text-[var(--color-status-error)]">
                          Nothing selected — this key can't call any provider or
                          model. Pick at least one, or re-enable Full access.
                        </p>
                      </div>
                    )}

                    {/* Save bar */}
                    <div className="flex items-center justify-end gap-3 pt-1">
                      {dirty && (
                        <span className="inline-flex items-center gap-1.5 text-xs text-[var(--color-status-warning)]">
                          <span className="size-1.5 rounded-full bg-[var(--color-status-warning)]" />
                          Unsaved changes
                        </span>
                      )}
                      {!dirty && savedTick && (
                        <span className="inline-flex items-center gap-1 text-xs text-[var(--color-status-running)]">
                          <Check className="size-3.5" />
                          Saved
                        </span>
                      )}
                      <Button
                        variant="primary"
                        size="sm"
                        disabled={!dirty || nothingGranted}
                        loading={saveMutation.isPending}
                        onClick={() => saveMutation.mutate(serializeDraft(draft))}
                        className="gap-1.5"
                      >
                        <SlidersHorizontal className="size-3.5" />
                        {saveMutation.isPending ? "Saving…" : "Save access"}
                      </Button>
                    </div>
                  </>
                )}
              </>
            )}
          </section>
          )}

          {/* ── Usage ────────────────────────────────────────── */}
          <section className="pt-5 border-t border-[var(--color-border-subtle)]">
            <UsageSection keyId={apiKey.id} />
          </section>
        </div>
      </Dialog>

      <ConfirmDialog
        open={confirmDiscard}
        title="Discard unsaved changes?"
        description="Access changes for this key haven't been saved yet."
        confirmLabel="Discard"
        variant="danger"
        onConfirm={() => {
          setConfirmDiscard(false);
          onOpenChange(false);
        }}
        onCancel={() => setConfirmDiscard(false)}
      />
    </>
  );
}
