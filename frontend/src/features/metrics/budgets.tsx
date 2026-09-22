// Per-provider monthly budget editor + progress bars.
//
// Budgets persist server-side via /api/settings `providerBudgetsJson`
// (`{"provider":{"tokenBudget":number,"costBudget":number}}`). Providers are
// now keyed by the metrics cost unit (cloud provider, or execution agent/host
// for local usage) rather than by runtime name. A backend migration owns the
// one-time cleanup of old runtime-keyed server entries; the frontend never
// clears server data. Stale entries are inert because budget rows come from
// usage `monthProviders` and look up `budgets[row.provider]` by the new agent
// key. Edits apply optimistically for a snappy feel and reconcile with the
// server response afterwards.
//
// Month-to-date usage per provider comes from /api/metrics/providers over the
// current calendar month.

import { useMemo, useState } from "react";
import { useTranslation } from "react-i18next";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { PiggyBank, Settings2 } from "lucide-react";
import { Card, Badge } from "../../components/ui";
import type { ProviderUsageSummary } from "../../lib/api/types";
import { client } from "../../lib/query-client";
import { formatCurrency, formatTokens } from "./format";
import { pricingModeAt, rateAt, type RatePeriod } from "./cost";

interface ProviderBudget {
  /** Monthly token budget. Undefined = no token budget set. */
  tokens?: number;
  /** Monthly cost budget (dollars). Undefined = no cost budget set. */
  cost?: number;
}

type BudgetsMap = Record<string, ProviderBudget>;

/** Server wire format: {"provider":{"tokenBudget":n,"costBudget":n}} */
type ServerBudgetsMap = Record<
  string,
  { tokenBudget?: number; costBudget?: number }
>;

function parseServerBudgets(json: string | undefined): BudgetsMap {
  if (!json) return {};
  try {
    const parsed = JSON.parse(json) as ServerBudgetsMap;
    const out: BudgetsMap = {};
    for (const [provider, value] of Object.entries(parsed ?? {})) {
      out[provider] = {
        tokens:
          typeof value?.tokenBudget === "number" ? value.tokenBudget : undefined,
        cost:
          typeof value?.costBudget === "number" ? value.costBudget : undefined,
      };
    }
    return out;
  } catch {
    // corrupt/foreign payload — treat as empty
    return {};
  }
}

function toServerBudgets(budgets: BudgetsMap): string {
  const out: ServerBudgetsMap = {};
  for (const [provider, b] of Object.entries(budgets)) {
    const entry: { tokenBudget?: number; costBudget?: number } = {};
    if (b.tokens !== undefined && b.tokens > 0) entry.tokenBudget = b.tokens;
    if (b.cost !== undefined && b.cost > 0) entry.costBudget = b.cost;
    if (entry.tokenBudget !== undefined || entry.costBudget !== undefined) {
      out[provider] = entry;
    }
  }
  return JSON.stringify(out);
}

export interface BudgetsPanelProps {
  /** Per-provider usage for the current calendar month. */
  monthProviders: ProviderUsageSummary[] | undefined;
  loading: boolean;
  costRates: RatePeriod[];
}

interface BarState {
  pct: number | null; // null = no budget set
  level: "ok" | "warn" | "danger" | "none";
}

function barState(used: number, budget: number | undefined): BarState {
  if (!budget || budget <= 0) return { pct: null, level: "none" };
  const pct = Math.min(100, (used / budget) * 100);
  return {
    pct,
    level: pct >= 100 ? "danger" : pct >= 80 ? "warn" : "ok",
  };
}

const LEVEL_COLORS: Record<BarState["level"], string> = {
  ok: "var(--color-primary)",
  warn: "var(--color-status-warning)",
  danger: "var(--color-status-error)",
  none: "var(--color-border-strong)",
};

export function BudgetsPanel({
  monthProviders,
  loading,
  costRates,
}: BudgetsPanelProps) {
  const { t } = useTranslation("metrics");
  const queryClient = useQueryClient();

  // Shares the ["settings"] cache entry warmed by the page-level query.
  const { data: settings } = useQuery({
    queryKey: ["settings"],
    queryFn: () => client.getSettings(),
  });

  const serverBudgets = useMemo(
    () => parseServerBudgets(settings?.providerBudgetsJson),
    [settings],
  );

  // ── Optimistic local overlay for snappy edits ─────────────────
  const [localBudgets, setLocalBudgets] = useState<BudgetsMap | null>(null);
  const budgets = localBudgets ?? serverBudgets;

  function updateBudget(provider: string, patch: ProviderBudget) {
    const next: BudgetsMap = {
      ...budgets,
      [provider]: { ...budgets[provider], ...patch },
    };
    setLocalBudgets(next);
    client
      .updateSettings({ providerBudgetsJson: toServerBudgets(next) })
      .then(() => queryClient.invalidateQueries({ queryKey: ["settings"] }))
      .then(() => setLocalBudgets(null))
      .catch(() => {
        // Keep the optimistic value visible on failure; the next successful
        // save (or reload) reconciles with the server.
      });
  }

  const [editing, setEditing] = useState(false);

  // Only show providers that have usage this month or an existing budget.
  // Cost is resolved at "now" with the date-ranged engine; a provider-wide
  // rate period (model scope null) applies to the month's aggregated tokens.
  const now = new Date();
  const rows = (monthProviders ?? [])
    .map((p) => {
      const rate = rateAt(costRates, p.provider, null, now);
      const costUsed =
        rate && rate.mode === "per-token"
          ? (p.promptTokens / 1_000_000) * rate.promptPer1M +
            (p.completionTokens / 1_000_000) * rate.completionPer1M
          : 0;
      return {
        provider: p.provider,
        tokensUsed: p.promptTokens + p.completionTokens,
        costUsed,
      };
    })
    .sort((a, b) => b.tokensUsed - a.tokensUsed);

  return (
    <Card padding="lg">
      <div className="flex items-center justify-between mb-4">
        <p className="text-xs font-medium text-[var(--color-text-muted)] uppercase tracking-wider">
          {t("budgets.monthlyBudgets")}
        </p>
        <button
          type="button"
          onClick={() => setEditing((e) => !e)}
          className="inline-flex items-center gap-1 text-xs text-[var(--color-text-muted)] hover:text-[var(--color-text)] cursor-pointer rounded-[var(--radius-md)] px-1.5 py-0.5 hover:bg-[var(--color-bg-muted)] transition-colors"
          aria-expanded={editing}
        >
          <Settings2 className="size-3.5" />
          {editing ? t("budgets.done") : t("budgets.edit")}
        </button>
      </div>

      {loading && (
        <p className="text-sm text-[var(--color-text-muted)] py-4">{t("loading", { ns: "common" })}…</p>
      )}

      {!loading && rows.length === 0 && (
        <div className="flex flex-col items-center py-6 text-center">
          <PiggyBank className="size-8 text-[var(--color-text-muted)] opacity-40 mb-2" />
          <p className="text-sm text-[var(--color-text-muted)] max-w-xs">
            {t("budgets.noUsageYet")}
          </p>
        </div>
      )}

      {/* Rows echo the Cost Calculator's bordered-row table language. */}
      <div className="space-y-2">
        {rows.map((row) => {
          const budget: ProviderBudget = budgets[row.provider] ?? {};
          const tokens = barState(row.tokensUsed, budget.tokens);
          // Flat-rate providers (subscription / self-hosted) pay a fixed
          // monthly amount, not usage-based cost — a cost budget would
          // trivially sit at 100%, so we only surface their token budget
          // plus the flat cost itself.
          const flatKind = pricingModeAt(costRates, row.provider, null, now);
          const isFlat = flatKind !== "per-token";
          const resolved = rateAt(costRates, row.provider, null, now);
          const flatCost =
            flatKind === "subscription"
              ? (resolved?.monthlyPrice ?? 0)
              : (resolved?.monthlyCost ?? 0);
          const cost = barState(row.costUsed, budget.cost);
          return (
            <div
              key={row.provider}
              className="p-2.5 rounded-[var(--radius-lg)] border border-[var(--color-border-subtle)] bg-[var(--color-bg-muted)]/30"
            >
              <div className="flex items-baseline justify-between gap-2">
                <span className="flex items-center gap-2 min-w-0 text-sm font-medium text-[var(--color-text-heading)]">
                  <span className="truncate">{row.provider}</span>
                  {flatKind === "subscription" && (
                    <Badge variant="outline" size="sm">
                      {t("costCalcSection.monthly")}
                    </Badge>
                  )}
                  {flatKind === "self-hosted" && (
                    <Badge variant="outline" size="sm">
                      {t("budgets.selfHosted")}
                    </Badge>
                  )}
                </span>
                <span className="text-xs font-mono text-[var(--color-text-muted)] shrink-0 tabular-nums">
                  {formatTokens(row.tokensUsed)} {t("tokSuffix")}
                  {!isFlat && budget.cost !== undefined && budget.cost > 0
                    ? ` · ${formatCurrency(row.costUsed)}`
                    : ""}
                </span>
              </div>

              {(tokens.pct !== null || isFlat || cost.pct !== null) && (
                <div className="mt-2 space-y-1.5">
                  {tokens.pct !== null && (
                    <ProgressBar
                      label={t("budgets.tokenBudgetProgress", { pct: Math.round(tokens.pct), amount: formatTokens(budget.tokens!) })}
                      state={tokens}
                    />
                  )}
                  {isFlat ? (
                    <p className="text-[10px] text-[var(--color-text-muted)]">
                      {t("budgets.flatCostLine", { amount: formatCurrency(flatCost) })}{" "}
                      {flatKind === "self-hosted"
                        ? t("budgets.hardwarePower")
                        : t("budgets.noUsageBasedCost")}
                    </p>
                  ) : (
                    cost.pct !== null && (
                      <ProgressBar
                        label={t("budgets.costBudgetProgress", { pct: Math.round(cost.pct), amount: formatCurrency(budget.cost!) })}
                        state={cost}
                      />
                    )
                  )}
                  {tokens.pct === null && (isFlat || cost.pct === null) && (
                    <p className="text-xs text-[var(--color-text-muted)] italic">
                      {t("budgets.noBudgetSet")}
                    </p>
                  )}
                </div>
              )}

              {editing && (
                <div className="flex flex-wrap gap-2 mt-3 pt-3 border-t border-[var(--color-border-subtle)]">
                  <BudgetInput
                    label={t("budgets.tokenBudget")}
                    value={budget.tokens?.toString() ?? ""}
                    onChange={(v) =>
                      updateBudget(row.provider, {
                        tokens: v === "" ? undefined : Math.max(0, Number(v) || 0),
                      })
                    }
                  />
                  {!isFlat && (
                    <BudgetInput
                      label={t("budgets.costBudget")}
                      value={budget.cost?.toString() ?? ""}
                      step="0.01"
                      onChange={(v) =>
                        updateBudget(row.provider, {
                          cost: v === "" ? undefined : Math.max(0, Number(v) || 0),
                        })
                      }
                    />
                  )}
                </div>
              )}
            </div>
          );
        })}

        {rows.length > 0 && (
          <div className="flex items-baseline justify-between pt-2 border-t border-[var(--color-border-strong)]">
            <span className="text-xs font-semibold text-[var(--color-text-heading)]">
              {t("budgets.monthToDate")}
            </span>
            <span className="text-xs font-mono font-semibold text-[var(--color-text-heading)] tabular-nums">
              {formatTokens(rows.reduce((sum, r) => sum + r.tokensUsed, 0))} {t("tokSuffix")} ·{" "}
              {formatCurrency(rows.reduce((sum, r) => sum + r.costUsed, 0))}
            </span>
          </div>
        )}
      </div>
    </Card>
  );
}

function ProgressBar({ label, state }: { label: string; state: BarState }) {
  return (
    <div>
      <div className="h-1.5 w-full rounded-full bg-[var(--color-bg-muted)] overflow-hidden">
        <div
          className="h-full rounded-full transition-all duration-500"
          style={{
            width: `${state.pct}%`,
            backgroundColor: LEVEL_COLORS[state.level],
          }}
        />
      </div>
      <p
        className={`text-[10px] mt-0.5 ${
          state.level === "danger"
            ? "text-[var(--color-status-error)]"
            : state.level === "warn"
              ? "text-[var(--color-status-warning)]"
              : "text-[var(--color-text-muted)]"
        }`}
      >
        {label}
      </p>
    </div>
  );
}

function BudgetInput({
  label,
  value,
  onChange,
  step,
}: {
  label: string;
  value: string;
  onChange: (value: string) => void;
  step?: string;
}) {
  return (
    <label className="flex flex-col gap-0.5 min-w-[130px] flex-1">
      <span className="text-xs font-medium text-[var(--color-text-muted)]">
        {label}
      </span>
      <input
        type="number"
        inputMode="decimal"
        min="0"
        step={step ?? "1"}
        value={value}
        placeholder="—"
        onChange={(e) => onChange(e.target.value)}
        className="h-8 rounded-[var(--radius-lg)] border bg-[var(--color-bg-surface)] px-3 text-sm font-mono text-[var(--color-text)] border-[var(--color-border)] focus:outline-none focus:border-[var(--color-primary)] focus:ring-1 focus:ring-[var(--color-focus-ring)] transition-colors w-full"
      />
    </label>
  );
}
