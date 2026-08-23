// API-key access-control + per-key usage endpoints.
//
// Follows the metrics-api.ts convention: cookie credentials, ApiError with
// status for downstream matching (e.g. 404 → graceful fallback), and a small
// local request helper so the shared UnswarmClient interface stays untouched.

import { ApiError, BASE_URL } from "../../lib/api/httpClient";
import type {
  ApiKeyAccess,
  ApiKeyUsageResponse,
  ProviderModelCatalogEntry,
} from "../../lib/api/types";

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

/** Union of providers (usage + configured cloud + registered runtimes). */
export function getProviderModelCatalog(): Promise<ProviderModelCatalogEntry[]> {
  return request("/api/provider-model-catalog");
}

/** Current access grants for a key. Empty lists = unrestricted. */
export function getApiKeyAccess(keyId: string): Promise<ApiKeyAccess> {
  return request(`/api/api-keys/${encodeURIComponent(keyId)}/access`);
}

export function putApiKeyAccess(
  keyId: string,
  access: ApiKeyAccess,
): Promise<ApiKeyAccess> {
  return request(`/api/api-keys/${encodeURIComponent(keyId)}/access`, {
    method: "PUT",
    body: JSON.stringify(access),
  });
}

/** Aggregate usage for one key over an ISO from/to window. */
export function getApiKeyUsage(
  keyId: string,
  opts?: { from?: string; to?: string },
): Promise<ApiKeyUsageResponse> {
  const params = new URLSearchParams();
  if (opts?.from) params.set("from", opts.from);
  if (opts?.to) params.set("to", opts.to);
  const qs = params.toString();
  return request(
    `/api/metrics/api-keys/${encodeURIComponent(keyId)}/usage${qs ? `?${qs}` : ""}`,
  );
}
