// Unit tests for the date-ranged cost-rate engine.

import { describe, it, expect, beforeEach } from "vitest";
import {
  COST_RATES_KEY,
  COST_RATES_V2_KEY,
  loadCostRates,
  saveCostRates,
  rateAt,
  pricingModeAt,
  isFlatRateAt,
  hasAnyRates,
  bucketCost,
  modelCost,
  sumBucketCosts,
  cacheSavings,
  flatCostTotals,
  inclusiveToExclusiveInstant,
  inclusiveToExclusiveDate,
  exclusiveToInclusiveDate,
  type RatePeriod,
  type CostBucket,
} from "../features/metrics/cost";

let idSeq = 0;

function period(overrides: Partial<RatePeriod> = {}): RatePeriod {
  return {
    id: `p-${++idSeq}`,
    provider: "openai",
    model: null,
    from: null,
    to: null,
    mode: "per-token",
    promptPer1M: 0,
    completionPer1M: 0,
    monthlyPrice: 0,
    monthlyCost: 0,
    ...overrides,
  };
}

function bucket(overrides: Partial<CostBucket> = {}): CostBucket {
  return {
    provider: "openai",
    model: "gpt-4o",
    at: "2024-01-15T00:00:00.000Z",
    promptTokens: 0,
    completionTokens: 0,
    cachedTokens: 0,
    ...overrides,
  };
}

beforeEach(() => {
  localStorage.clear();
});

describe("rateAt", () => {
  it("prefers a model override over the provider-wide period", () => {
    const wide = period({ promptPer1M: 1 });
    const override = period({ model: "gpt-4o", promptPer1M: 5 });
    const at = new Date("2024-03-01T00:00:00Z");

    expect(rateAt([wide, override], "openai", "gpt-4o", at)).toBe(override);
    expect(rateAt([wide, override], "openai", "gpt-4o-mini", at)).toBe(wide);
  });

  it("falls back to a provider-wide period when no override matches", () => {
    const wide = period({ promptPer1M: 2 });
    expect(rateAt([wide], "openai", "anything", new Date("2024-01-01T00:00:00Z"))).toBe(wide);
  });

  it("resolves overlapping periods by the later from", () => {
    const early = period({ from: "2024-01-01", promptPer1M: 1 });
    const late = period({ from: "2024-02-01", promptPer1M: 2 });

    expect(rateAt([early, late], "openai", null, new Date("2024-01-15T00:00:00Z"))).toBe(early);
    expect(rateAt([early, late], "openai", null, new Date("2024-03-15T00:00:00Z"))).toBe(late);
  });

  it("resolves identical from by the later array index", () => {
    const first = period({ from: "2024-01-01", promptPer1M: 1 });
    const second = period({ from: "2024-01-01", promptPer1M: 2 });
    expect(rateAt([first, second], "openai", null, new Date("2024-02-01T00:00:00Z"))).toBe(second);
  });

  it("treats null bounds as open-ended", () => {
    const open = period({ promptPer1M: 1 });
    const future = period({ from: "2024-06-01", promptPer1M: 2 });

    expect(rateAt([open, future], "openai", null, new Date("2000-01-01T00:00:00Z"))).toBe(open);
    expect(rateAt([open, future], "openai", null, new Date("2025-01-01T00:00:00Z"))).toBe(future);
  });

  it("uses from inclusive and to exclusive bounds", () => {
    const p = period({ from: "2024-01-01", to: "2024-01-11", promptPer1M: 1 });

    expect(rateAt([p], "openai", null, new Date("2024-01-01T00:00:00Z"))).toBe(p);
    expect(rateAt([p], "openai", null, new Date("2024-01-10T23:59:59Z"))).toBe(p);
    expect(rateAt([p], "openai", null, new Date("2024-01-11T00:00:00Z"))).toBeNull();
  });

  it("returns null inside a gap", () => {
    const before = period({ from: "2024-01-01", to: "2024-01-11", promptPer1M: 1 });
    const after = period({ from: "2024-01-20", promptPer1M: 2 });
    expect(rateAt([before, after], "openai", null, new Date("2024-01-15T00:00:00Z"))).toBeNull();
  });
});

describe("pricing helpers", () => {
  it("defaults to per-token when nothing matches and reports flat modes", () => {
    const flat = period({ mode: "self-hosted", monthlyCost: 50, from: "2024-01-01" });
    const at = new Date("2024-02-01T00:00:00Z");

    expect(pricingModeAt([flat], "openai", null, at)).toBe("self-hosted");
    expect(isFlatRateAt([flat], "openai", null, at)).toBe(true);
    expect(pricingModeAt([flat], "openai", null, new Date("2023-01-01T00:00:00Z"))).toBe("per-token");
    expect(isFlatRateAt([flat], "openai", null, new Date("2023-01-01T00:00:00Z"))).toBe(false);
  });

  it("hasAnyRates only counts positive mode-appropriate amounts", () => {
    expect(hasAnyRates([])).toBe(false);
    expect(hasAnyRates([period({ mode: "per-token", promptPer1M: 0 })])).toBe(false);
    expect(hasAnyRates([period({ mode: "per-token", completionPer1M: 0.5 })])).toBe(true);
    expect(
      hasAnyRates([period({ mode: "subscription", monthlyPrice: 10, monthlyCost: 999 })]),
    ).toBe(true);
    expect(
      hasAnyRates([period({ mode: "self-hosted", monthlyCost: 10, monthlyPrice: 999 })]),
    ).toBe(true);
  });
});

describe("storage migration", () => {
  it("migrates v2 entries to v3 and keeps the v2 key", () => {
    localStorage.setItem(
      COST_RATES_V2_KEY,
      JSON.stringify({
        openai: { promptPer1M: 3, completionPer1M: 6 },
        anthropic: { mode: "subscription", promptPer1M: 0, completionPer1M: 0, monthlyPrice: 20 },
      }),
    );

    const periods = loadCostRates();
    expect(periods).toHaveLength(2);

    const openai = periods.find((p) => p.provider === "openai")!;
    expect(openai.mode).toBe("per-token");
    expect(openai.model).toBeNull();
    expect(openai.from).toBeNull();
    expect(openai.to).toBeNull();
    expect(openai.promptPer1M).toBe(3);
    expect(openai.completionPer1M).toBe(6);
    expect(openai.monthlyPrice).toBe(0);
    expect(openai.monthlyCost).toBe(0);
    expect(typeof openai.id).toBe("string");
    expect(openai.id.length).toBeGreaterThan(0);

    const anthropic = periods.find((p) => p.provider === "anthropic")!;
    expect(anthropic.mode).toBe("subscription");
    expect(anthropic.monthlyPrice).toBe(20);
    expect(anthropic.monthlyCost).toBe(0);

    // v3 was written; v2 retained for rollback safety.
    expect(localStorage.getItem(COST_RATES_KEY)).not.toBeNull();
    expect(localStorage.getItem(COST_RATES_V2_KEY)).not.toBeNull();
    expect(loadCostRates()).toEqual(periods);
  });

  it("prefers v3 when both keys exist", () => {
    localStorage.setItem(COST_RATES_V2_KEY, JSON.stringify({ openai: { promptPer1M: 1 } }));
    localStorage.setItem(
      COST_RATES_KEY,
      JSON.stringify([period({ provider: "agent-x", promptPer1M: 9 })]),
    );

    const periods = loadCostRates();
    expect(periods).toHaveLength(1);
    expect(periods[0]!.provider).toBe("agent-x");
  });

  it("returns [] when no storage is present", () => {
    expect(loadCostRates()).toEqual([]);
  });

  it("returns [] for corrupt v3 JSON", () => {
    localStorage.setItem(COST_RATES_KEY, "{not json");
    expect(loadCostRates()).toEqual([]);
  });

  it("falls back to the v2 migration when v3 is corrupt", () => {
    localStorage.setItem(COST_RATES_KEY, "{not json");
    localStorage.setItem(
      COST_RATES_V2_KEY,
      JSON.stringify({ openai: { promptPer1M: 7 } }),
    );

    const periods = loadCostRates();
    expect(periods).toHaveLength(1);
    expect(periods[0]!.provider).toBe("openai");
    expect(periods[0]!.promptPer1M).toBe(7);
    // The valid v2 payload was migrated and written to v3.
    expect(JSON.parse(localStorage.getItem(COST_RATES_KEY)!)).toHaveLength(1);
  });

  it("falls back to the v2 migration when v3 holds a non-array payload", () => {
    localStorage.setItem(COST_RATES_KEY, JSON.stringify({ openai: {} }));
    localStorage.setItem(
      COST_RATES_V2_KEY,
      JSON.stringify({ anthropic: { promptPer1M: 3 } }),
    );
    const periods = loadCostRates();
    expect(periods).toHaveLength(1);
    expect(periods[0]!.provider).toBe("anthropic");
  });

  it("saveCostRates writes the v3 key", () => {
    saveCostRates([period({ provider: "openai", promptPer1M: 4 })]);
    const raw = localStorage.getItem(COST_RATES_KEY);
    expect(raw).not.toBeNull();
    expect(JSON.parse(raw!)).toHaveLength(1);
  });
});

describe("bucketCost", () => {
  it("prices per-token buckets at their timestamp", () => {
    const p = period({ promptPer1M: 10, completionPer1M: 20 });
    const b = bucket({ promptTokens: 1_000_000, completionTokens: 500_000 });
    expect(bucketCost(b, [p])).toBe(20);
  });

  it("returns null for flat-mode and unrated buckets", () => {
    const flat = period({ mode: "subscription", monthlyPrice: 100 });
    expect(bucketCost(bucket(), [flat])).toBeNull();
    expect(bucketCost(bucket(), [])).toBeNull();
  });
});

describe("modelCost", () => {
  it("sums different rates across a mid-window rate change", () => {
    const early = period({ from: "2024-01-01", promptPer1M: 1 });
    const late = period({ from: "2024-01-15", promptPer1M: 2 });
    const buckets = [
      bucket({ at: "2024-01-10T00:00:00.000Z", promptTokens: 1_000_000 }),
      bucket({ at: "2024-01-20T00:00:00.000Z", promptTokens: 1_000_000 }),
    ];

    const result = modelCost(buckets, [early, late]);
    expect(result.cost).toBeCloseTo(3, 9);
    expect(result.missingBuckets).toBe(0);
    expect(result.flat).toBe(false);
  });

  it("counts missing buckets but still prices rated ones", () => {
    const rated = period({ model: "gpt-4o", promptPer1M: 10 });
    const buckets = [
      bucket({ model: "gpt-4o", promptTokens: 1_000_000 }),
      bucket({ model: "other-model", promptTokens: 1_000_000 }),
    ];

    const result = modelCost(buckets, [rated]);
    expect(result.cost).toBeCloseTo(10, 9);
    expect(result.missingBuckets).toBe(1);
  });

  it("returns null cost when every bucket is unpriced", () => {
    const result = modelCost([bucket(), bucket({ model: "other" })], []);
    expect(result.cost).toBeNull();
    expect(result.missingBuckets).toBe(2);
  });

  it("flags flat when a flat period is in effect at the earliest bucket", () => {
    const flat = period({ mode: "self-hosted", monthlyCost: 40 });
    const result = modelCost([bucket()], [flat]);
    expect(result.cost).toBeNull();
    expect(result.missingBuckets).toBe(1);
    expect(result.flat).toBe(true);
    expect(result.basis).toBe("flat");
  });

  it("reports a mixed basis across a flat→per-token transition", () => {
    const flat = period({ mode: "self-hosted", monthlyCost: 40, to: "2024-01-15" });
    const perToken = period({ from: "2024-01-15", promptPer1M: 2 });
    const buckets = [
      bucket({ at: "2024-01-10T00:00:00.000Z", promptTokens: 1_000_000 }),
      bucket({ at: "2024-01-20T00:00:00.000Z", promptTokens: 1_000_000 }),
    ];

    const result = modelCost(buckets, [flat, perToken]);
    // The earliest bucket is flat, but the window also contains a priced
    // per-token bucket, so the basis must not be reported as pure flat.
    expect(result.basis).toBe("mixed");
    expect(result.mixed).toBe(true);
    expect(result.flat).toBe(false);
    expect(result.cost).toBeCloseTo(2, 9);
    expect(result.missingBuckets).toBe(1);
  });

  it("reports a mixed basis across a per-token→flat transition", () => {
    const perToken = period({ promptPer1M: 2, to: "2024-01-15" });
    const flat = period({ from: "2024-01-15", mode: "subscription", monthlyPrice: 50 });
    const buckets = [
      bucket({ at: "2024-01-10T00:00:00.000Z", promptTokens: 1_000_000 }),
      bucket({ at: "2024-01-20T00:00:00.000Z", promptTokens: 1_000_000 }),
    ];

    const result = modelCost(buckets, [perToken, flat]);
    expect(result.basis).toBe("mixed");
    expect(result.cost).toBeCloseTo(2, 9);
  });
});

describe("sumBucketCosts", () => {
  it("sums priced buckets and reports missing ones", () => {
    const p = period({ promptPer1M: 1 });
    const buckets = [
      bucket({ at: "2024-01-01T00:00:00.000Z", promptTokens: 1_000_000 }),
      bucket({ at: "2024-01-02T00:00:00.000Z", promptTokens: 2_000_000 }),
      bucket({ provider: "unrated-provider", promptTokens: 5_000_000 }),
    ];

    const result = sumBucketCosts(buckets, [p]);
    expect(result.cost).toBeCloseTo(3, 9);
    expect(result.missingBuckets).toBe(1);
  });

  it("returns null when no bucket is covered by a rate", () => {
    const p = period({ promptPer1M: 1 });
    const uncovered = sumBucketCosts(
      [bucket({ provider: "unrated-provider", promptTokens: 1_000_000 })],
      [p],
    );
    expect(uncovered.cost).toBeNull();
    expect(uncovered.missingBuckets).toBe(1);

    const empty = sumBucketCosts([], [p]);
    expect(empty.cost).toBeNull();
    expect(empty.missingBuckets).toBe(0);
  });
});

describe("inclusive to-date mapping", () => {
  it("treats From=To as a single inclusive day", () => {
    const from = Date.parse("2024-01-15T00:00:00Z");
    const toExclusive = inclusiveToExclusiveInstant("2024-01-15");
    expect(toExclusive).not.toBeNull();
    expect(toExclusive! - from).toBe(86_400_000);
  });

  it("includes the last day for a month-end To", () => {
    expect(inclusiveToExclusiveDate("2024-01-31")).toBe("2024-02-01");
    expect(inclusiveToExclusiveDate("2024-02-29")).toBe("2024-03-01");
    expect(inclusiveToExclusiveDate("2024-12-31")).toBe("2025-01-01");
  });

  it("round-trips exclusive storage back to the inclusive display day", () => {
    expect(exclusiveToInclusiveDate("2024-02-01")).toBe("2024-01-31");
    expect(
      exclusiveToInclusiveDate(inclusiveToExclusiveDate("2024-01-31")!),
    ).toBe("2024-01-31");
    expect(inclusiveToExclusiveDate("")).toBeNull();
    expect(inclusiveToExclusiveInstant("")).toBeNull();
    expect(exclusiveToInclusiveDate("")).toBe("");
  });
});

describe("cacheSavings", () => {
  it("credits cached prompt tokens at each bucket's per-token rate", () => {
    const p = period({ promptPer1M: 10 });
    const buckets = [
      bucket({ cachedTokens: 2_000_000 }),
      bucket({ provider: "other", cachedTokens: 9_000_000 }),
    ];
    expect(cacheSavings(buckets, [p])).toBeCloseTo(20, 9);
  });

  it("excludes flat-mode buckets", () => {
    const flat = period({ mode: "subscription", monthlyPrice: 50 });
    expect(cacheSavings([bucket({ cachedTokens: 1_000_000 })], [flat])).toBe(0);
  });
});

describe("flatCostTotals", () => {
  it("charges a full 3-month window once per active month", () => {
    const sub = period({ mode: "subscription", monthlyPrice: 200 });
    const months = new Map([["openai", new Set(["2024-01", "2024-02", "2024-03"])]]);

    const result = flatCostTotals([sub], months, {
      from: new Date(Date.UTC(2024, 0, 1)),
      to: new Date(Date.UTC(2024, 3, 1)),
    });

    expect(result.subscriptions).toBeCloseTo(600, 6);
    expect(result.selfHosted).toBe(0);
    expect(result.details).toHaveLength(3);
    expect(result.details.map((d) => d.month).sort()).toEqual(["2024-01", "2024-02", "2024-03"]);
  });

  it("prorates a partial month by days", () => {
    const sub = period({ mode: "subscription", monthlyPrice: 200 });
    const months = new Map([["openai", new Set(["2024-01"])]]);

    const result = flatCostTotals([sub], months, {
      from: new Date(Date.UTC(2024, 0, 1)),
      to: new Date(Date.UTC(2024, 0, 16)),
    });

    expect(result.subscriptions).toBeCloseTo((200 * 15) / 31, 6);
  });

  it("clips a period that intersects only part of a month", () => {
    const sub = period({
      mode: "subscription",
      monthlyPrice: 200,
      from: "2024-01-10",
      to: "2024-01-20",
    });
    const months = new Map([["openai", new Set(["2024-01"])]]);

    const result = flatCostTotals([sub], months, {
      from: new Date(Date.UTC(2024, 0, 1)),
      to: new Date(Date.UTC(2024, 1, 1)),
    });

    expect(result.subscriptions).toBeCloseTo((200 * 10) / 31, 6);
  });

  it("only counts months present in the active-months map", () => {
    const sub = period({ mode: "subscription", monthlyPrice: 200 });
    const months = new Map([["openai", new Set(["2024-02"])]]);

    const result = flatCostTotals([sub], months, {
      from: new Date(Date.UTC(2024, 0, 1)),
      to: new Date(Date.UTC(2024, 3, 1)),
    });

    expect(result.subscriptions).toBeCloseTo(200, 6);
    expect(result.details).toHaveLength(1);
    expect(result.details[0]!.month).toBe("2024-02");
  });

  it("ignores model-specific flat periods (provider-wide only)", () => {
    const wide = period({ mode: "subscription", monthlyPrice: 200 });
    const modelSpecific = period({
      model: "gpt-4o",
      mode: "self-hosted",
      monthlyCost: 100,
    });
    const months = new Map([["openai", new Set(["2024-01"])]]);

    const result = flatCostTotals([wide, modelSpecific], months, {
      from: new Date(Date.UTC(2024, 0, 1)),
      to: new Date(Date.UTC(2024, 1, 1)),
    });

    expect(result.subscriptions).toBeCloseTo(200, 6);
    expect(result.selfHosted).toBe(0);
    expect(result.details).toHaveLength(1);
  });

  it("counts subscription and self-hosted independently for one provider", () => {
    const sub = period({ mode: "subscription", monthlyPrice: 200 });
    const self = period({ mode: "self-hosted", monthlyCost: 100 });
    const months = new Map([["openai", new Set(["2024-01"])]]);

    const result = flatCostTotals([sub, self], months, {
      from: new Date(Date.UTC(2024, 0, 1)),
      to: new Date(Date.UTC(2024, 1, 1)),
    });

    expect(result.subscriptions).toBeCloseTo(200, 6);
    expect(result.selfHosted).toBeCloseTo(100, 6);
    expect(result.details).toHaveLength(2);
    expect(result.details.reduce((s, d) => s + d.amount, 0)).toBeCloseTo(300, 6);
  });

  it("ignores providers without usage months", () => {
    const sub = period({ mode: "subscription", monthlyPrice: 200 });
    const result = flatCostTotals([sub], new Map(), {
      from: new Date(Date.UTC(2024, 0, 1)),
      to: new Date(Date.UTC(2024, 3, 1)),
    });
    expect(result.subscriptions).toBe(0);
    expect(result.details).toEqual([]);
  });

  it("charges the effective period per segment when flat periods overlap", () => {
    // Old $200/mo open-ended plus a newer $250/mo from mid-January. The naive
    // per-period sum would report $450 for January; the effective engine must
    // charge $200 for Jan 1–15 and $250 for Jan 16–31.
    const oldRate = period({ mode: "subscription", monthlyPrice: 200 });
    const newRate = period({
      mode: "subscription",
      monthlyPrice: 250,
      from: "2024-01-16",
    });
    const months = new Map([["openai", new Set(["2024-01"])]]);

    const result = flatCostTotals([oldRate, newRate], months, {
      from: new Date(Date.UTC(2024, 0, 1)),
      to: new Date(Date.UTC(2024, 1, 1)),
    });

    expect(result.subscriptions).toBeCloseTo((200 * 15 + 250 * 16) / 31, 6);
    expect(result.details).toHaveLength(1);
    expect(result.details[0]!.amount).toBeCloseTo((200 * 15 + 250 * 16) / 31, 6);
  });

  it("does not double-charge two overlapping self-hosted periods", () => {
    // The reported S1 scenario: a $200/mo self-hosted period superseded by a
    // $250/mo one mid-January must never sum to $450.
    const oldRate = period({ mode: "self-hosted", monthlyCost: 200, provider: "agent-a" });
    const newRate = period({
      mode: "self-hosted",
      monthlyCost: 250,
      provider: "agent-a",
      from: "2024-01-16",
    });
    const months = new Map([["agent-a", new Set(["2024-01"])]]);

    const result = flatCostTotals([oldRate, newRate], months, {
      from: new Date(Date.UTC(2024, 0, 1)),
      to: new Date(Date.UTC(2024, 1, 1)),
    });

    expect(result.selfHosted).toBeCloseTo((200 * 15 + 250 * 16) / 31, 6);
    expect(result.selfHosted).toBeLessThan(450);
  });

  it("only charges each period across its own active range", () => {
    const early = period({
      mode: "subscription",
      monthlyPrice: 200,
      from: "2024-01-01",
      to: "2024-01-16",
    });
    const late = period({
      mode: "subscription",
      monthlyPrice: 250,
      from: "2024-01-20",
    });
    const months = new Map([["openai", new Set(["2024-01"])]]);

    const result = flatCostTotals([early, late], months, {
      from: new Date(Date.UTC(2024, 0, 1)),
      to: new Date(Date.UTC(2024, 1, 1)),
    });

    // Jan 1–15 at $200; Jan 20–31 at $250; Jan 16–19 gap is uncharged.
    expect(result.subscriptions).toBeCloseTo((200 * 15 + 250 * 12) / 31, 6);
  });

  it("resolves overlapping flat periods by later array index on a from tie", () => {
    const first = period({ mode: "subscription", monthlyPrice: 100, from: "2024-01-01" });
    const second = period({ mode: "subscription", monthlyPrice: 300, from: "2024-01-01" });
    const months = new Map([["openai", new Set(["2024-01"])]]);

    const result = flatCostTotals([first, second], months, {
      from: new Date(Date.UTC(2024, 0, 1)),
      to: new Date(Date.UTC(2024, 1, 1)),
    });

    expect(result.subscriptions).toBeCloseTo(300, 6);
  });
});
