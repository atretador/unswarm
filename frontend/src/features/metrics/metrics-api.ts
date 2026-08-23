// Extended metrics API surface for the newer backend lane.
//
// These calls live outside the shared UnswarmClient interface on purpose: the
// mock client doesn't implement them, and extending the interface would break
// its type-check. Everything here speaks to the real HTTP backend via
// BASE_URL with cookie credentials, mirroring httpClient's conventions.

import { ApiError, BASE_URL } from "../../lib/api/httpClient";
import type {
  MetricsTimeBucket,
  ModelUsageSummary,
  ProviderUsageSummary,
  UsageRecordResponse,
  UsageTotalsResponse,
} from "../../lib/api/types";

// ─── Response types (backend lane 2 additions) ───────────────────

export interface MetricsTotals extends UsageTotalsResponse {
  totalStreamingRequests?: number;
  p50LatencyMs?: number;
  p95LatencyMs?: number;
  p99LatencyMs?: number;
  maxLatencyMs?: number;
}

export interface ModelUsageRow extends ModelUsageSummary {
  streamingRequests?: number;
  p50LatencyMs?: number;
  p95LatencyMs?: number;
  p99LatencyMs?: number;
  maxLatencyMs?: number;
}

export interface MetricsBucket extends MetricsTimeBucket {
  streamingRequests?: number;
}

export interface ProviderUsageRow extends ProviderUsageSummary {
  streamingRequests?: number;
}

/** One bucket of the latency distribution histogram. */
export interface LatencyBand {
  label: string;
  minMs: number;
  /** Exclusive upper bound in ms; null = unbounded (">10s"). */
  maxMs: number | null;
  count: number;
}

/** Per-API-key usage attribution row. */
export interface ApiKeyUsageRow {
  apiKeyId: string;
  keyName: string;
  requestCount: number;
  streamingRequests: number;
  promptTokens: number;
  completionTokens: number;
  cachedTokens: number;
}

/** One entry of the provider catalog (usage + configured + registered). */
export interface ProviderCatalogEntry {
  name: string;
  kind: "cloud" | "local";
}

export interface UsagePageResponse {
  items: UsageRecordResponse[];
  total: number;
  page: number;
  pageSize: number;
}

// ─── Request helper (mirrors httpClient conventions) ─────────────

async function request<T>(path: string, init?: RequestInit): Promise<T> {
  const res = await fetch(`${BASE_URL}${path}`, {
    ...init,
    credentials: "include",
    headers: {
      "Content-Type": "application/json",
      Accept: "application/json",
      ...init?.headers,
    },
  });
  if (!res.ok) {
    let message = `HTTP ${res.status}`;
    try {
      const body = (await res.json()) as { message?: unknown; error?: unknown };
      if (body && typeof body === "object" && "message" in body) {
        message = String(body.message);
      } else if (body && typeof body === "object" && "error" in body) {
        message = String(body.error);
      }
    } catch {
      // body wasn't JSON — fall back to status text
      message = res.statusText || message;
    }
    throw new ApiError(res.status, message);
  }
  const text = await res.text();
  return text ? (JSON.parse(text) as T) : (undefined as T);
}

function qs(params: Record<string, string | number | undefined>): string {
  const search = new URLSearchParams();
  for (const [key, value] of Object.entries(params)) {
    if (value !== undefined && value !== "") search.set(key, String(value));
  }
  const s = search.toString();
  return s ? `?${s}` : "";
}

export interface MetricsFilterParams {
  from?: string;
  to?: string;
  provider?: string;
  model?: string;
}

// ─── Endpoints ───────────────────────────────────────────────────

export function getMetricsUsage(opts?: MetricsFilterParams & {
  page?: number;
  pageSize?: number;
  /** Cursor: only records with timestamp > this epoch-ms tick are returned. */
  since?: number;
}): Promise<UsagePageResponse> {
  return request(`/api/metrics/usage${qs({ ...opts })}`);
}

export function getMetricsSummary(opts?: MetricsFilterParams & {
  granularity?: "hour" | "day" | "week" | "month";
}): Promise<MetricsBucket[]> {
  return request(`/api/metrics/summary${qs({ ...opts })}`);
}

export function getMetricsModels(opts?: MetricsFilterParams): Promise<ModelUsageRow[]> {
  return request(`/api/metrics/models${qs({ ...opts })}`);
}

export function getMetricsProviders(opts?: Pick<MetricsFilterParams, "from" | "to">): Promise<ProviderUsageRow[]> {
  return request(`/api/metrics/providers${qs({ ...opts })}`);
}

export function getMetricsTotals(opts?: MetricsFilterParams): Promise<MetricsTotals> {
  return request(`/api/metrics/totals${qs({ ...opts })}`);
}

export function getMetricsLatencyBands(
  opts?: MetricsFilterParams,
): Promise<LatencyBand[]> {
  return request(`/api/metrics/latency-bands${qs({ ...opts })}`);
}

export function getMetricsApiKeys(
  opts?: Pick<MetricsFilterParams, "from" | "to">,
): Promise<ApiKeyUsageRow[]> {
  return request(`/api/metrics/api-keys${qs({ ...opts })}`);
}

export function purgeMetricsUsage(olderThanDays: number): Promise<{ deleted: number }> {
  return request(`/api/metrics/usage/purge${qs({ olderThanDays })}`, {
    method: "DELETE",
  });
}

/**
 * Provider catalog: union of providers seen in usage, configured cloud
 * providers, and registered runtimes/agents. Falls back to distinct providers
 * from /api/metrics/providers (kind inferred) when the catalog endpoint isn't
 * available yet (e.g. backend not restarted).
 */
export async function getMetricsProviderCatalog(): Promise<ProviderCatalogEntry[]> {
  try {
    return await request<ProviderCatalogEntry[]>("/api/metrics/provider-catalog");
  } catch {
    const usage = await getMetricsProviders({});
    const distinct = [...new Set(usage.map((p) => p.provider))].sort();
    return distinct.map((name) => ({
      name,
      kind: name === "cloud" ? ("cloud" as const) : ("local" as const),
    }));
  }
}

// ─── WebSocket ───────────────────────────────────────────────────

/**
 * WebSocket URL for `/ws/metrics`, derived from the same base-URL logic as
 * HTTP calls: VITE_API_URL when set (http→ws rewrite), otherwise same-origin.
 */
export function metricsWsUrl(): string {
  const base = BASE_URL || window.location.origin;
  const url = new URL(`${base}/ws/metrics`, window.location.origin);
  url.protocol = url.protocol === "https:" ? "wss:" : "ws:";
  return url.toString();
}
