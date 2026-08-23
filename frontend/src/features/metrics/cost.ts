// Cost-rate storage + estimation helpers.
//
// Rates live in localStorage (same source CostCalculatorDialog reads/writes)
// keyed by provider. Three pricing modes coexist per provider:
// - "per-token": usage-based API pricing, dollars per 1M tokens
//   (promptPer1M / completionPer1M) — the original schema.
// - "subscription": one fixed monthly fee (monthlyPrice), currency-agnostic.
// - "self-hosted": flat monthly hardware/power cost (monthlyCost); the
//   equivalent $/1M figure is derived from month-to-date tokens, not stored.
// Legacy entries have no `mode` field and are treated as per-token, so old
// data keeps working unchanged.
//
// All cost figures on the metrics page are estimates derived from these rates
// — missing rates degrade gracefully to null so the UI can show a "set rates"
// hint instead of a number.

import type { ModelUsageSummary, MetricsTimeBucket } from "../../lib/api/types";

export const COST_RATES_KEY = "unswarm-cost-rates";

export type PricingMode = "per-token" | "subscription" | "self-hosted";

export interface ProviderCostRate {
  /** Absent on legacy entries — always treated as "per-token". */
  mode?: PricingMode;
  promptPer1M: number;
  completionPer1M: number;
  /** Fixed monthly subscription fee; only meaningful in "subscription" mode. */
  monthlyPrice?: number;
  /** Flat monthly power/hardware cost; only meaningful in "self-hosted" mode. */
  monthlyCost?: number;
}

export type CostRatesMap = Record<string, ProviderCostRate>;

/** Providers whose cost is a flat monthly figure ("subscription" | "self-hosted"). */
export type FlatPricingMode = Exclude<PricingMode, "per-token">;

export function loadCostRates(): CostRatesMap {
  try {
    const raw = localStorage.getItem(COST_RATES_KEY);
    if (raw) return JSON.parse(raw) as CostRatesMap;
  } catch {
    // corrupt data — start fresh
  }
  return {};
}

export function saveCostRates(rates: CostRatesMap): void {
  try {
    localStorage.setItem(COST_RATES_KEY, JSON.stringify(rates));
  } catch {
    // storage full or unavailable — silently ignore
  }
}

/** Effective pricing mode for a provider; legacy entries are per-token. */
export function getPricingMode(
  rates: CostRatesMap,
  provider: string,
): PricingMode {
  return rates[provider]?.mode ?? "per-token";
}

export function isSubscriptionProvider(
  rates: CostRatesMap,
  provider: string,
): boolean {
  return getPricingMode(rates, provider) === "subscription";
}

export function isSelfHostedProvider(
  rates: CostRatesMap,
  provider: string,
): boolean {
  return getPricingMode(rates, provider) === "self-hosted";
}

/** True when the provider's cost is a flat monthly figure, not per token. */
export function isFlatRateProvider(
  rates: CostRatesMap,
  provider: string,
): boolean {
  return getPricingMode(rates, provider) !== "per-token";
}

export function hasAnyRates(rates: CostRatesMap): boolean {
  return Object.values(rates).some(
    (r) =>
      r.promptPer1M > 0 ||
      r.completionPer1M > 0 ||
      (r.mode === "subscription" && (r.monthlyPrice ?? 0) > 0) ||
      (r.mode === "self-hosted" && (r.monthlyCost ?? 0) > 0),
  );
}

/**
 * Flat monthly costs across the given providers that are actually present
 * (any usage) in the current window. Neither kind is time-distributed, so
 * both are reported as lump sums, never per bucket or per token.
 */
export function flatCostTotals(
  rates: CostRatesMap,
  activeProviders: Iterable<string>,
): { subscriptions: number; selfHosted: number } {
  let subscriptions = 0;
  let selfHosted = 0;
  const seen = new Set<string>();
  for (const provider of activeProviders) {
    if (seen.has(provider)) continue;
    seen.add(provider);
    const mode = getPricingMode(rates, provider);
    if (mode === "subscription") {
      subscriptions += Math.max(0, rates[provider]?.monthlyPrice ?? 0);
    } else if (mode === "self-hosted") {
      selfHosted += Math.max(0, rates[provider]?.monthlyCost ?? 0);
    }
  }
  return { subscriptions, selfHosted };
}

/**
 * Estimated usage-based cost for one model-usage row using its provider's
 * per-token rates. Returns null when the provider has no rate configured OR
 * is in a flat-rate mode (subscription / self-hosted — their monthly costs
 * aren't attributable per token).
 *
 * Cached prompt tokens are billed at the full input rate here; the discount
 * they represent is reported separately as cache savings (see cacheSavings).
 */
export function modelCost(
  m: Pick<ModelUsageSummary, "provider" | "promptTokens" | "completionTokens">,
  rates: CostRatesMap,
): number | null {
  const rate = rates[m.provider];
  if (!rate || isFlatRateProvider(rates, m.provider)) return null;
  return (
    (m.promptTokens / 1_000_000) * rate.promptPer1M +
    (m.completionTokens / 1_000_000) * rate.completionPer1M
  );
}

export interface BlendedRates {
  promptPer1M: number;
  completionPer1M: number;
}

/**
 * Token-weighted average rates across the models in the current window.
 * Used to project a cost-over-time series when buckets don't carry a provider
 * split. Models without configured per-token rates (including flat-rate
 * providers — subscription and self-hosted costs aren't time-distributed)
 * are excluded from the weighting; returns null when no rated tokens exist.
 */
export function computeBlendedRates(
  models: ModelUsageSummary[],
  rates: CostRatesMap,
): BlendedRates | null {
  let promptWeighted = 0;
  let completionWeighted = 0;
  let promptTotal = 0;
  let completionTotal = 0;

  for (const m of models) {
    if (isFlatRateProvider(rates, m.provider)) continue;
    const rate = rates[m.provider];
    if (!rate) continue;
    promptWeighted += m.promptTokens * rate.promptPer1M;
    completionWeighted += m.completionTokens * rate.completionPer1M;
    promptTotal += m.promptTokens;
    completionTotal += m.completionTokens;
  }

  if (promptTotal === 0 && completionTotal === 0) return null;
  return {
    promptPer1M: promptTotal > 0 ? promptWeighted / promptTotal : 0,
    completionPer1M: completionTotal > 0 ? completionWeighted / completionTotal : 0,
  };
}

/** Estimated cost for a single time bucket using blended rates. */
export function bucketCost(bucket: MetricsTimeBucket, blended: BlendedRates): number {
  return (
    (bucket.promptTokens / 1_000_000) * blended.promptPer1M +
    (bucket.completionTokens / 1_000_000) * blended.completionPer1M
  );
}

/**
 * Estimated savings from cached prompt tokens: cached tokens that didn't have
 * to be billed at the full prompt-input rate. Returns null without a rate.
 * Only meaningful for per-token providers by construction (blended rates
 * exclude subscription and self-hosted providers).
 */
export function cacheSavings(
  cachedTokens: number,
  blended: BlendedRates | null,
): number | null {
  if (!blended) return null;
  return (cachedTokens / 1_000_000) * blended.promptPer1M;
}
