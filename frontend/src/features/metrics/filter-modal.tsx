// Dedicated filter-options modal for the Metrics page.
//
// Two searchable checkbox sections — Providers and Models. Selections are
// edited as a local draft while the modal is open; nothing touches the page's
// queries until "Apply". The model list narrows to the drafted providers so
// the two dimensions stay coherent (models are shown per provider).
//
// Within a dimension selections are ANY-of (union); across dimensions they
// AND together, matching the backend's analytics filtering semantics.

import { useEffect, useMemo, useState } from "react";
import { Check, Search } from "lucide-react";
import { Badge, Button, Dialog } from "../../components/ui";
import { formatModelName } from "../../lib/format-model-name";
import type { ProviderCatalogEntry } from "../../lib/api/types";

/** One selectable model entry: the model id plus the providers serving it. */
export interface ModelFilterOption {
  model: string;
  providers: string[];
}

export interface FiltersModalProps {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  /** Selectable providers, already unioned with providers seen in usage. */
  providerOptions: ProviderCatalogEntry[];
  /** Distinct models with the providers that served them. */
  modelOptions: ModelFilterOption[];
  selectedProviders: string[];
  selectedModels: string[];
  hideOriginPrefix?: boolean;
  agentDisplayNames?: Record<string, string>;
  /** Called with the final selection when Apply is pressed. */
  onApply: (providers: string[], models: string[]) => void;
}

interface CheckboxListProps<T extends string> {
  /** All selectable values in display order. */
  options: Array<{ value: T; label: string; badge?: string }>;
  selected: T[];
  onToggle: (value: T) => void;
  emptyText: string;
}

function CheckboxList<T extends string>({
  options,
  selected,
  onToggle,
  emptyText,
}: CheckboxListProps<T>) {
  if (options.length === 0) {
    return (
      <p className="py-6 text-center text-xs text-[var(--color-text-muted)]">
        {emptyText}
      </p>
    );
  }
  return (
    <div className="max-h-48 overflow-y-auto rounded-[var(--radius-lg)] border border-[var(--color-border-subtle)]">
      {options.map((opt) => {
        const checked = selected.includes(opt.value);
        return (
          <label
            key={opt.value}
            className="flex cursor-pointer items-center gap-2.5 px-3 py-2 text-sm transition-colors hover:bg-[var(--color-bg-muted)]"
          >
            <input
              type="checkbox"
              checked={checked}
              onChange={() => onToggle(opt.value)}
              className="size-3.5 shrink-0 cursor-pointer accent-[var(--color-primary)]"
            />
            <span
              className={`flex-1 truncate ${
                checked ? "text-[var(--color-text-heading)]" : "text-[var(--color-text)]"
              }`}
            >
              {opt.label}
            </span>
            {opt.badge && (
              <Badge variant={opt.badge === "local" ? "outline" : "info"} size="sm">
                {opt.badge}
              </Badge>
            )}
          </label>
        );
      })}
    </div>
  );
}

interface SearchBoxProps {
  value: string;
  onChange: (value: string) => void;
  placeholder: string;
}

function SearchBox({ value, onChange, placeholder }: SearchBoxProps) {
  return (
    <div className="relative">
      <Search className="pointer-events-none absolute left-2.5 top-1/2 size-3.5 -translate-y-1/2 text-[var(--color-text-muted)]" />
      <input
        type="text"
        role="searchbox"
        value={value}
        onChange={(e) => onChange(e.target.value)}
        placeholder={placeholder}
        className="h-8 w-full rounded-[var(--radius-lg)] border bg-[var(--color-bg-surface)] pl-8 pr-7 text-xs text-[var(--color-text)] border-[var(--color-border)] placeholder:text-[var(--color-text-muted)] focus:outline-none focus:border-[var(--color-primary)] focus:ring-1 focus:ring-[var(--color-focus-ring)] transition-colors"
      />
      {value && (
        <button
          type="button"
          onClick={() => onChange("")}
          aria-label="Clear search"
          className="absolute right-1.5 top-1/2 flex size-5 -translate-y-1/2 cursor-pointer items-center justify-center rounded-[var(--radius-md)] text-[var(--color-text-muted)] hover:text-[var(--color-text)]"
        >
          ×
        </button>
      )}
    </div>
  );
}

function matchSearch(haystack: string, needle: string): boolean {
  return haystack.toLowerCase().includes(needle.trim().toLowerCase());
}

export function FiltersModal({
  open,
  onOpenChange,
  providerOptions,
  modelOptions,
  selectedProviders,
  selectedModels,
  hideOriginPrefix = false,
  agentDisplayNames = {},
  onApply,
}: FiltersModalProps) {
  // Draft state — seeded from the live selection each time the modal opens.
  const [draftProviders, setDraftProviders] = useState<string[]>([]);
  const [draftModels, setDraftModels] = useState<string[]>([]);
  const [providerSearch, setProviderSearch] = useState("");
  const [modelSearch, setModelSearch] = useState("");

  useEffect(() => {
    if (open) {
      setDraftProviders([...selectedProviders]);
      setDraftModels([...selectedModels]);
      setProviderSearch("");
      setModelSearch("");
    }
  }, [open, selectedProviders, selectedModels]);

  const toggle = (
    list: string[],
    setList: (next: string[]) => void,
    value: string,
  ) => {
    setList(
      list.includes(value) ? list.filter((v) => v !== value) : [...list, value].sort(),
    );
  };

  // Providers matching the search box.
  const visibleProviders = useMemo(
    () =>
      providerOptions
        .filter((p) => matchSearch(p.name, providerSearch))
        .map((p) => ({ value: p.name, label: p.name, badge: p.kind })),
    [providerOptions, providerSearch],
  );

  // Models matching both the search box and the drafted provider selection.
  const visibleModels = useMemo(
    () =>
      modelOptions
        .filter((m) =>
          draftProviders.length === 0
            ? true
            : m.providers.some((p) => draftProviders.includes(p)),
        )
        .filter((m) => {
          const label = formatModelName(
            m.model,
            m.providers[0] ?? "",
            hideOriginPrefix,
            agentDisplayNames,
          );
          return (
            matchSearch(m.model, modelSearch) || matchSearch(label, modelSearch)
          );
        })
        .map((m) => ({
          value: m.model,
          label: formatModelName(
            m.model,
            m.providers[0] ?? "",
            hideOriginPrefix,
            agentDisplayNames,
          ),
          badge: m.providers.length > 1 ? `${m.providers.length} providers` : undefined,
        })),
    [modelOptions, draftProviders, modelSearch, hideOriginPrefix, agentDisplayNames],
  );

  const totalSelected = draftProviders.length + draftModels.length;

  function apply() {
    onApply(draftProviders, draftModels);
    onOpenChange(false);
  }

  return (
    <Dialog open={open} onOpenChange={onOpenChange} title="Filter data" className="sm:max-w-lg">
      <div className="px-5 py-4 space-y-5">
        {/* ── Providers ─────────────────────────────────────── */}
        <section className="space-y-2">
          <div className="flex items-center justify-between gap-2">
            <h4 className="text-xs font-semibold uppercase tracking-wider text-[var(--color-text-muted)]">
              Providers{" "}
              <span className="font-normal normal-case tracking-normal">
                ({draftProviders.length} selected)
              </span>
            </h4>
            <div className="flex gap-1">
              <button
                type="button"
                onClick={() => setDraftProviders(providerOptions.map((p) => p.name))}
                className="cursor-pointer rounded-[var(--radius-md)] px-1.5 py-0.5 text-[10px] font-medium text-[var(--color-primary)] hover:bg-[var(--color-bg-muted)] transition-colors"
              >
                All
              </button>
              <button
                type="button"
                onClick={() => setDraftProviders([])}
                className="cursor-pointer rounded-[var(--radius-md)] px-1.5 py-0.5 text-[10px] font-medium text-[var(--color-text-muted)] hover:bg-[var(--color-bg-muted)] transition-colors"
              >
                None
              </button>
            </div>
          </div>
          <SearchBox
            value={providerSearch}
            onChange={setProviderSearch}
            placeholder="Search providers…"
          />
          <CheckboxList
            options={visibleProviders}
            selected={draftProviders}
            onToggle={(v) => toggle(draftProviders, setDraftProviders, v)}
            emptyText={
              providerOptions.length === 0
                ? "No usage recorded yet."
                : `No providers match "${providerSearch}".`
            }
          />
        </section>

        {/* ── Models ────────────────────────────────────────── */}
        <section className="space-y-2">
          <div className="flex items-center justify-between gap-2">
            <h4 className="text-xs font-semibold uppercase tracking-wider text-[var(--color-text-muted)]">
              Models{" "}
              <span className="font-normal normal-case tracking-normal">
                ({draftModels.length} selected)
              </span>
            </h4>
            <div className="flex gap-1">
              <button
                type="button"
                onClick={() => setDraftModels(visibleModels.map((m) => m.value))}
                disabled={visibleModels.length === 0}
                className="cursor-pointer rounded-[var(--radius-md)] px-1.5 py-0.5 text-[10px] font-medium text-[var(--color-primary)] hover:bg-[var(--color-bg-muted)] transition-colors disabled:opacity-40 disabled:cursor-not-allowed"
              >
                All
              </button>
              <button
                type="button"
                onClick={() => setDraftModels([])}
                className="cursor-pointer rounded-[var(--radius-md)] px-1.5 py-0.5 text-[10px] font-medium text-[var(--color-text-muted)] hover:bg-[var(--color-bg-muted)] transition-colors"
              >
                None
              </button>
            </div>
          </div>
          <SearchBox
            value={modelSearch}
            onChange={setModelSearch}
            placeholder={
              draftProviders.length > 0
                ? "Search selected providers' models…"
                : "Search models…"
            }
          />
          <CheckboxList
            options={visibleModels}
            selected={draftModels}
            onToggle={(v) => toggle(draftModels, setDraftModels, v)}
            emptyText={
              visibleModels.length === 0 && modelSearch
                ? `No models match "${modelSearch}".`
                : "No model usage recorded for this selection."
            }
          />
        </section>

        {/* ── Footer ────────────────────────────────────────── */}
        <div className="flex items-center justify-between gap-3 pt-1">
          <Button
            variant="ghost"
            size="sm"
            onClick={() => {
              setDraftProviders([]);
              setDraftModels([]);
            }}
            disabled={totalSelected === 0}
            className="gap-1 text-[var(--color-text-muted)]"
          >
            <Check className="size-3 rotate-45" />
            Clear all
          </Button>
          <div className="flex gap-2">
            <Button variant="secondary" size="sm" onClick={() => onOpenChange(false)}>
              Cancel
            </Button>
            <Button variant="primary" size="sm" onClick={apply}>
              Apply{totalSelected > 0 ? ` (${totalSelected})` : ""}
            </Button>
          </div>
        </div>
      </div>
    </Dialog>
  );
}
