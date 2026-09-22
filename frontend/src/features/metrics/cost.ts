// Date-ranged cost-rate engine for the Metrics page.
//
// Rates are modelled as an ordered set of `RatePeriod`s rather than one rate
// per provider, so a rate change mid-history is accounted for. Each period is
// scoped to a `provider` (the cost unit: cloud provider name, or agent/host
// for local usage) and optionally to a single `model` override. Period
// `from`/`to` bounds are UTC calendar days: `from` inclusive, `to` exclusive,
// both open-ended when null.
//
// Cost semantics:
// - per-token: resolved per time bucket at that bucket's timestamp, then summed.
// - subscription / self-hosted (flat): a fixed monthly amount charged once per
//   active calendar month in the window, day-prorated across the overlap of the
//   month, the window and the period.
//
// All figures are estimates; missing rates degrade gracefully to null so the UI
// can show a "set rates" hint instead of a number.

export const COST_RATES_KEY = "unswarm-cost-rates:v3";
/** Previous per-provider storage. Kept for one-way migration; never deleted. */
export const COST_RATES_V2_KEY = "unswarm-cost-rates:v2";

export type PricingMode = "per-token" | "subscription" | "self-hosted";

/** Providers whose cost is a flat monthly figure ("subscription" | "self-hosted"). */
export type FlatPricingMode = Exclude<PricingMode, "per-token">;

export interface RatePeriod {
  id: string;
  /** Cost unit: cloud provider name or agent/host. */
  provider: string;
  /** null/undefined = whole provider; else a model override. */
  model?: string | null;
  /** "YYYY-MM-DD" inclusive UTC day start; null = unbounded past. */
  from?: string | null;
  /** "YYYY-MM-DD" EXCLUSIVE UTC day start; null = unbounded future. */
  to?: string | null;
  mode: PricingMode;
  promptPer1M: number;
  completionPer1M: number;
  /** Meaningful in subscription mode. */
  monthlyPrice: number;
  /** Meaningful in self-hosted mode. */
  monthlyCost: number;
}

/** One aggregated usage bucket, addressed by provider + model + start time. */
export interface CostBucket {
  provider: string;
  model: string;
  /** Bucket start ISO timestamp. */
  at: string;
  promptTokens: number;
  completionTokens: number;
  cachedTokens: number;
}

/** Effective pricing basis across every bucket of a model in a window. */
export type CostBasis = "flat" | "per-token" | "mixed";

/**
 * Cost summary for one (provider, model) over a window.
 *
 * NOTE (shape extension): `basis` and `mixed` were added so consumers can tell
 * a window that spans a flat↔per-token pricing transition apart from a
 * uniformly flat or per-token window. `flat` is retained for compatibility and
 * is `true` only when `basis === "flat"`.
 */
export interface ModelCost {
  cost: number | null;
  missingBuckets: number;
  flat: boolean;
  /** "mixed" = the window contains both flat and per-token buckets. */
  basis: CostBasis;
  /** Convenience mirror of `basis === "mixed"`. */
  mixed: boolean;
}

export interface FlatCostDetail {
  provider: string;
  /** "YYYY-MM". */
  month: string;
  mode: FlatPricingMode;
  amount: number;
}

/** Legacy v2 entry shape (one rate per provider, no date range). */
interface LegacyProviderCostRate {
  mode?: PricingMode;
  promptPer1M?: number;
  completionPer1M?: number;
  monthlyPrice?: number;
  monthlyCost?: number;
}

function newPeriodId(): string {
  const uuid = globalThis.crypto?.randomUUID?.();
  return uuid ?? `rp-${Date.now().toString(36)}-${Math.random().toString(36).slice(2)}`;
}

// ── Storage ───────────────────────────────────────────────────────

/**
 * Load rate periods from localStorage. Prefers the current v3 format and
 * migrates a legacy v2 map in place (writing v3 but keeping v2 for rollback
 * safety). Corrupt/unavailable storage yields an empty list.
 */
export function loadCostRates(): RatePeriod[] {
  try {
    const rawV3 = localStorage.getItem(COST_RATES_KEY);
    if (rawV3) {
      // N1: a corrupt/non-array v3 payload must not hide a valid v2 fallback.
      try {
        const parsed = JSON.parse(rawV3) as unknown;
        if (Array.isArray(parsed)) return parsed as RatePeriod[];
      } catch {
        // fall through to the v2 migration below
      }
    }
  } catch {
    // storage unavailable — start fresh
    return [];
  }

  try {
    const rawV2 = localStorage.getItem(COST_RATES_V2_KEY);
    if (rawV2) {
      const parsed = JSON.parse(rawV2) as Record<string, LegacyProviderCostRate>;
      const migrated: RatePeriod[] = Object.entries(parsed ?? {}).map(
        ([provider, r]) => ({
          id: newPeriodId(),
          provider,
          model: null,
          from: null,
          to: null,
          mode: r.mode ?? "per-token",
          promptPer1M: r.promptPer1M ?? 0,
          completionPer1M: r.completionPer1M ?? 0,
          monthlyPrice: r.monthlyPrice ?? 0,
          monthlyCost: r.monthlyCost ?? 0,
        }),
      );
      saveCostRates(migrated);
      return migrated;
    }
  } catch {
    // corrupt data or storage unavailable — start fresh
  }
  return [];
}

export function saveCostRates(periods: RatePeriod[]): void {
  try {
    localStorage.setItem(COST_RATES_KEY, JSON.stringify(periods));
  } catch {
    // storage full or unavailable — silently ignore
  }
}

// ── Resolution ────────────────────────────────────────────────────

export const DAY_MS = 86_400_000;

/** UTC day-start instant, or the given fallback when absent/malformed. */
export function dayInstant(value: string | null | undefined, fallback: number): number {
  if (!value) return fallback;
  const t = Date.parse(`${value}T00:00:00Z`);
  return Number.isNaN(t) ? fallback : t;
}

// ── Inclusive-`to` mapping ────────────────────────────────────────
//
// The UI labels its `to` field "To" and its copy promises an inclusive range
// ("From must be on or before To"), so users mean "through this day". Storage
// and the rate engine, however, use an EXCLUSIVE `to` day start. These helpers
// are the single mapping between the two; every dialog path that reads or
// writes a `to` goes through them.

/**
 * Inclusive user-entered "YYYY-MM-DD" `to` → exclusive UTC instant (the next
 * midnight), or null when absent/malformed (open-ended).
 */
export function inclusiveToExclusiveInstant(
  value: string | null | undefined,
): number | null {
  if (!value) return null;
  const t = Date.parse(`${value}T00:00:00Z`);
  return Number.isNaN(t) ? null : t + DAY_MS;
}

/** Inclusive user-entered `to` → exclusive "YYYY-MM-DD" storage value. */
export function inclusiveToExclusiveDate(
  value: string | null | undefined,
): string | null {
  const t = inclusiveToExclusiveInstant(value);
  return t === null ? null : new Date(t).toISOString().slice(0, 10);
}

/** Exclusive stored `to` → inclusive "YYYY-MM-DD" for display ("" when absent). */
export function exclusiveToInclusiveDate(value: string | null | undefined): string {
  if (!value) return "";
  const t = Date.parse(`${value}T00:00:00Z`);
  if (Number.isNaN(t)) return value;
  return new Date(t - DAY_MS).toISOString().slice(0, 10);
}

function isActive(p: RatePeriod, at: number): boolean {
  return at >= dayInstant(p.from, -Infinity) && at < dayInstant(p.to, Infinity);
}

/**
 * Resolve the rate period in effect for `provider`/`model` at `at`.
 * Precedence: model-specific over provider-wide, then later `from`
 * (null = -Infinity), then later array index.
 */
export function rateAt(
  periods: RatePeriod[],
  provider: string,
  model: string | null,
  at: Date,
): RatePeriod | null {
  const t = at.getTime();
  let best: RatePeriod | null = null;
  let bestSpecificity = -1;
  let bestFrom = -Infinity;

  for (const p of periods) {
    if (p.provider !== provider) continue;
    if (!(p.model == null || p.model === model)) continue;
    if (!isActive(p, t)) continue;

    const specificity = p.model == null ? 0 : 1;
    const from = dayInstant(p.from, -Infinity);

    // Later index wins ties (iteration is in array order), so `>=` on `from`.
    if (specificity > bestSpecificity || (specificity === bestSpecificity && from >= bestFrom)) {
      best = p;
      bestSpecificity = specificity;
      bestFrom = from;
    }
  }
  return best;
}

/** Effective pricing mode at `at`; defaults to per-token when nothing matches. */
export function pricingModeAt(
  periods: RatePeriod[],
  provider: string,
  model: string | null,
  at: Date,
): PricingMode {
  return rateAt(periods, provider, model, at)?.mode ?? "per-token";
}

export function isFlatRateAt(
  periods: RatePeriod[],
  provider: string,
  model: string | null,
  at: Date,
): boolean {
  return pricingModeAt(periods, provider, model, at) !== "per-token";
}

/** True when at least one period carries a positive mode-appropriate amount. */
export function hasAnyRates(periods: RatePeriod[]): boolean {
  return periods.some(
    (p) =>
      p.promptPer1M > 0 ||
      p.completionPer1M > 0 ||
      (p.mode === "subscription" && p.monthlyPrice > 0) ||
      (p.mode === "self-hosted" && p.monthlyCost > 0),
  );
}

// ── Per-token cost ────────────────────────────────────────────────

/** Per-token cost for one bucket, or null when unrated or flat at that time. */
export function bucketCost(bucket: CostBucket, periods: RatePeriod[]): number | null {
  const rate = rateAt(periods, bucket.provider, bucket.model, new Date(bucket.at));
  if (!rate || rate.mode !== "per-token") return null;
  return (
    (bucket.promptTokens / 1_000_000) * rate.promptPer1M +
    (bucket.completionTokens / 1_000_000) * rate.completionPer1M
  );
}

/**
 * Sum the per-token cost of every bucket. `cost` is null when no bucket could
 * be priced; `missingBuckets` counts unrated/gap/flat buckets. `basis` reflects
 * the pricing mode across ALL buckets (not just the earliest), so a window that
 * spans a flat↔per-token transition reads as "mixed".
 */
export function modelCost(buckets: CostBucket[], periods: RatePeriod[]): ModelCost {
  let total = 0;
  let priced = 0;
  let missingBuckets = 0;
  let sawFlat = false;
  let sawPerToken = false;

  for (const b of buckets) {
    const rate = rateAt(periods, b.provider, b.model, new Date(b.at));
    if (rate) {
      if (rate.mode === "per-token") sawPerToken = true;
      else sawFlat = true;
    }
    if (!rate || rate.mode !== "per-token") {
      missingBuckets += 1;
      continue;
    }
    total +=
      (b.promptTokens / 1_000_000) * rate.promptPer1M +
      (b.completionTokens / 1_000_000) * rate.completionPer1M;
    priced += 1;
  }

  const basis: CostBasis =
    sawFlat && sawPerToken ? "mixed" : sawFlat ? "flat" : "per-token";

  return {
    cost: priced > 0 ? total : null,
    missingBuckets,
    flat: basis === "flat",
    basis,
    mixed: basis === "mixed",
  };
}

/**
 * Sum of per-token costs. `cost` is null when no bucket could be priced (rates
 * configured but zero covered/valid buckets), so callers can render "—" rather
 * than a misleading $0.00.
 */
export function sumBucketCosts(
  buckets: CostBucket[],
  periods: RatePeriod[],
): { cost: number | null; missingBuckets: number } {
  let cost = 0;
  let priced = 0;
  let missingBuckets = 0;
  for (const b of buckets) {
    const c = bucketCost(b, periods);
    if (c === null) missingBuckets += 1;
    else {
      cost += c;
      priced += 1;
    }
  }
  return { cost: priced > 0 ? cost : null, missingBuckets };
}

/**
 * Estimated savings from cached prompt tokens at each bucket's per-token rate.
 * Flat or unrated buckets contribute nothing.
 */
export function cacheSavings(buckets: CostBucket[], periods: RatePeriod[]): number {
  let total = 0;
  for (const b of buckets) {
    const rate = rateAt(periods, b.provider, b.model, new Date(b.at));
    if (!rate || rate.mode !== "per-token") continue;
    total += (b.cachedTokens / 1_000_000) * rate.promptPer1M;
  }
  return total;
}

// ── Flat (monthly) cost ───────────────────────────────────────────

function monthBounds(month: string): { start: number; end: number } | null {
  const m = /^(\d{4})-(\d{2})$/.exec(month);
  if (!m) return null;
  const year = Number(m[1]);
  const monthIndex = Number(m[2]) - 1;
  if (monthIndex < 0 || monthIndex > 11) return null;
  return { start: Date.UTC(year, monthIndex, 1), end: Date.UTC(year, monthIndex + 1, 1) };
}

function monthlyAmount(p: RatePeriod): number {
  return p.mode === "subscription" ? p.monthlyPrice : p.monthlyCost;
}

/**
 * Flat monthly costs for the given active months, day-prorated.
 *
 * Only provider-wide (`model == null`) flat periods count. For each active
 * `"YYYY-MM"`, the month ∩ window is split into segments at every flat-period
 * `from`/`to` boundary and ONE effective flat period is resolved per segment
 * and per mode (subscription / self-hosted) using the same precedence as
 * {@link rateAt} (later `from`, then later array index; all candidates here are
 * provider-wide). Each segment contributes `monthlyAmount * segmentMs / monthMs`
 * exactly once, so a mid-month price change charges the old price for the old
 * range and the new price for the new range without ever double-charging. A
 * period is only charged across its own active range. Amounts are summed per
 * (provider, month, mode); subscription and self-hosted periods are independent
 * (both can apply without double-counting each other).
 */
export function flatCostTotals(
  periods: RatePeriod[],
  activeMonthsByProvider: Map<string, Set<string>>,
  window: { from: Date; to: Date },
): { subscriptions: number; selfHosted: number; details: FlatCostDetail[] } {
  let subscriptions = 0;
  let selfHosted = 0;
  const details = new Map<string, FlatCostDetail>();

  const winStart = window.from.getTime();
  const winEnd = window.to.getTime();

  for (const [provider, months] of activeMonthsByProvider) {
    const flatPeriods = periods.filter(
      (p) => p.provider === provider && p.model == null && p.mode !== "per-token",
    );
    if (flatPeriods.length === 0) continue;

    for (const month of months) {
      const bounds = monthBounds(month);
      if (!bounds) continue;
      const monthMs = bounds.end - bounds.start;
      if (monthMs <= 0) continue;

      const winLo = Math.max(bounds.start, winStart);
      const winHi = Math.min(bounds.end, winEnd);
      if (winHi <= winLo) continue;

      // Segment the active slice at every period boundary that falls inside it.
      const boundaries = new Set<number>([winLo, winHi]);
      for (const p of flatPeriods) {
        const lo = dayInstant(p.from, -Infinity);
        const hi = dayInstant(p.to, Infinity);
        if (lo > winLo && lo < winHi) boundaries.add(lo);
        if (hi > winLo && hi < winHi) boundaries.add(hi);
      }
      const sorted = [...boundaries].sort((a, b) => a - b);

      for (let i = 0; i < sorted.length - 1; i += 1) {
        const segLo = sorted[i]!;
        const segHi = sorted[i + 1]!;
        if (segHi <= segLo) continue;

        // Resolve at the segment midpoint to avoid boundary-inclusivity edges.
        const at = new Date(Math.floor((segLo + segHi) / 2));
        // Subscription and self-hosted are independent cost concepts, so each
        // mode resolves its own effective period; within a mode the latest
        // `from` (then latest array index) wins, exactly like rateAt.
        for (const mode of ["subscription", "self-hosted"] as FlatPricingMode[]) {
          const candidates = flatPeriods.filter((p) => p.mode === mode);
          if (candidates.length === 0) continue;
          const effective = rateAt(candidates, provider, null, at);
          if (!effective) continue;

          const amount = monthlyAmount(effective) * ((segHi - segLo) / monthMs);
          const key = `${provider}\u0000${month}\u0000${mode}`;
          const existing = details.get(key);
          if (existing) existing.amount += amount;
          else details.set(key, { provider, month, mode, amount });
        }
      }
    }
  }

  for (const d of details.values()) {
    if (d.mode === "subscription") subscriptions += d.amount;
    else selfHosted += d.amount;
  }

  return { subscriptions, selfHosted, details: [...details.values()] };
}
