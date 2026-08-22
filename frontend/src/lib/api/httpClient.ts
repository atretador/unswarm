import type {
  Agent,
  AgentAvailableScript,
  ApiKeyCreateResponse,
  ApiKeyItem,
  AgentScriptStatus,
  BenchmarkResult,
  Container,
  LogEntry,
  Model,
  Prompt,
  PromptInput,
  PromptVersion,
  QueueSnapshot,
  RegisterRuntimePayload,
  RegisteredRuntime,
  Settings,
  StatsSummary,
  ToggleConcurrencyPayload,
  ToggleConcurrencyResponse,
  UpdateRuntimeConcurrencyPayload,
  User,
} from "./types";
import type { UnswarmClient } from "./client";

/**
 * Base URL for the API. Defaults to '' (same-origin relative paths) so the SPA
 * can be served by (or proxied to) the backend without cross-origin cookies.
 * Set VITE_API_URL to target a different origin explicitly.
 */
export const BASE_URL =
  (import.meta.env.VITE_API_URL as string | undefined) || "";

// ─── Error class ─────────────────────────────────────────────────

/**
 * Typed HTTP error that carries the status code for reliable
 * downstream matching (e.g. 403 admin gating).
 */
export class ApiError extends Error {
  status: number;
  constructor(
    status: number,
    message: string,
  ) {
    super(message);
    this.status = status;
    this.name = "ApiError";
  }
}

// ─── Helpers ──────────────────────────────────────────────────────

/**
 * Encode a path-like ID (e.g. `/models/Ling/Ling-3.0-tiny-Q4_0.gguf`)
 * so that slashes are preserved but individual segments are safe for URLs.
 */
function encodePathId(id: string): string {
  return id
    .split("/")
    .map((seg) => (seg ? encodeURIComponent(seg) : seg))
    .join("/");
}

// ─── Request helper ──────────────────────────────────────────────

/**
 * Paths that are expected to return 401 as part of normal auth flows
 * (login attempts, session probes) — never trigger a login redirect.
 */
const AUTH_EXEMPT_PATHS = ["/api/auth/login", "/api/auth/me"];

/** Guards against firing more than one login redirect per page load. */
let redirectingToLogin = false;

function handleUnauthorized(path: string) {
  if (redirectingToLogin || AUTH_EXEMPT_PATHS.some((p) => path.startsWith(p))) {
    return;
  }
  if (typeof window === "undefined") return;
  // Already on the login route — nothing to do (avoids redirect loops).
  if (window.location.pathname === "/login") return;
  redirectingToLogin = true;
  window.location.assign("/login");
}

async function request<T>(
  path: string,
  init?: RequestInit,
): Promise<T> {
  const url = `${BASE_URL}${path}`;
  const res = await fetch(url, {
    ...init,
    credentials: "include",
    headers: {
      "Content-Type": "application/json",
      Accept: "application/json",
      ...init?.headers,
    },
  });

  if (res.status === 204) {
    return undefined as T;
  }

  if (!res.ok) {
    if (res.status === 401) {
      handleUnauthorized(path);
    }
    let message = `HTTP ${res.status}`;
    try {
      const body = await res.json();
      if (body && typeof body === "object" && "message" in body) {
        message = String((body as { message: unknown }).message);
      } else if (body && typeof body === "object" && "error" in body) {
        message = String((body as { error: unknown }).error);
      }
    } catch {
      // response body wasn't JSON — use status text
      message = res.statusText || message;
    }
    throw new ApiError(res.status, message);
  }

  // 2xx with no body (safety net beyond 204)
  const text = await res.text();
  if (!text) return undefined as T;
  return JSON.parse(text) as T;
}

// ─── HTTP Client ─────────────────────────────────────────────────

export const httpClient: UnswarmClient = {
  // ── Models ────────────────────────────────────────────────────
  listModels() {
    return request<Model[]>("/api/models");
  },

  getModel(id: string) {
    return request<Model>(`/api/models/${encodePathId(id)}`);
  },

  createModel(data) {
    return request<Model>("/api/models", {
      method: "POST",
      body: JSON.stringify(data),
    });
  },

  updateModel(id, data) {
    return request<Model>(`/api/models/${encodePathId(id)}`, {
      method: "PUT",
      body: JSON.stringify(data),
    });
  },

  deleteModel(id) {
    return request<void>(`/api/models/${encodePathId(id)}`, {
      method: "DELETE",
    });
  },

  // ── Container Registration ───────────────────────────────────
  registerRuntime(data: RegisterRuntimePayload) {
    return request<RegisteredRuntime>("/api/containers/register", {
      method: "POST",
      body: JSON.stringify(data),
    });
  },

  listRegisteredRuntimes() {
    return request<RegisteredRuntime[]>("/api/containers/registered");
  },

  getRegisteredRuntime(id: string) {
    return request<RegisteredRuntime>(
      `/api/containers/registered/${encodeURIComponent(id)}`,
    );
  },

  rediscoverRuntime(id: string) {
    return request<RegisteredRuntime>(
      `/api/containers/registered/${encodeURIComponent(id)}/rediscover`,
      { method: "POST" },
    );
  },

  startRegisteredRuntime(id: string) {
    return request<RegisteredRuntime>(
      `/api/containers/registered/${encodeURIComponent(id)}/start`,
      { method: "POST" },
    );
  },

  stopRegisteredRuntime(id: string) {
    return request<RegisteredRuntime>(
      `/api/containers/registered/${encodeURIComponent(id)}/stop`,
      { method: "POST" },
    );
  },

  deleteRuntime(id: string, deleteModels = false) {
    const qs = deleteModels ? "?deleteModels=true" : "";
    return request<void>(
      `/api/containers/registered/${encodeURIComponent(id)}${qs}`,
      { method: "DELETE" },
    );
  },

  updateRuntimeConcurrency(id: string, payload: UpdateRuntimeConcurrencyPayload) {
    return request<RegisteredRuntime>(
      `/api/containers/registered/${encodeURIComponent(id)}/concurrency`,
      { method: "PUT", body: JSON.stringify(payload) },
    );
  },

  toggleRuntimeConcurrency(payload: ToggleConcurrencyPayload) {
    return request<ToggleConcurrencyResponse>(
      "/api/containers/registered/concurrency",
      { method: "POST", body: JSON.stringify(payload) },
    );
  },

  // ── Fleet / Containers ────────────────────────────────────────
  listContainers() {
    return request<Container[]>("/api/containers");
  },

  startContainer(modelId: string) {
    return request<Container>("/api/containers/start", {
      method: "POST",
      body: JSON.stringify({ modelId }),
    });
  },

  stopContainer(containerId: string) {
    return request<void>(
      `/api/containers/${encodeURIComponent(containerId)}/stop`,
      { method: "POST" },
    );
  },

  restartContainer(containerId: string) {
    return request<Container>(
      `/api/containers/${encodeURIComponent(containerId)}/restart`,
      { method: "POST" },
    );
  },

  listAgentContainers(agentName: string) {
    return request<Container[]>(
      `/api/agents/${encodeURIComponent(agentName)}/containers`,
    );
  },

  listAgentScripts(agentName: string) {
    return request<AgentScriptStatus[]>(
      `/api/agents/${encodeURIComponent(agentName)}/scripts`,
    );
  },

  listAvailableScripts(agentName: string) {
    return request<AgentAvailableScript[]>(
      `/api/agents/${encodeURIComponent(agentName)}/scripts/available`,
    );
  },

  runBenchmark(modelId: string, opts?: { promptId?: string }) {
    return request<BenchmarkResult>(
      `/api/benchmarks?modelId=${encodeURIComponent(modelId)}`,
      {
        method: "POST",
        body: JSON.stringify(opts?.promptId ? { promptId: opts.promptId } : {}),
      },
    );
  },

  listBenchmarks(modelId?: string) {
    const qs = modelId ? `?modelId=${encodeURIComponent(modelId)}` : "";
    return request<BenchmarkResult[]>(`/api/benchmarks${qs}`);
  },

  // ── Agents ────────────────────────────────────────────────────
  listAgents() {
    return request<Agent[]>("/api/agents");
  },

  // ── Queue ─────────────────────────────────────────────────────
  getQueueSnapshot() {
    return request<QueueSnapshot>("/api/queue/snapshot");
  },
  cancelQueueItem(itemId: string) {
    return request<void>(`/api/queue/${itemId}`, { method: "DELETE" });
  },

  // ── Stats ─────────────────────────────────────────────────────
  getStats() {
    return request<StatsSummary>("/api/stats");
  },

  // ── Logs ──────────────────────────────────────────────────────
  getLogs(opts) {
    const params = new URLSearchParams();
    if (opts?.source) params.set("source", opts.source);
    if (opts?.level) params.set("level", opts.level);
    if (opts?.limit !== undefined) params.set("limit", String(opts.limit));
    if (opts?.since) params.set("since", opts.since);
    const qs = params.toString();
    return request<LogEntry[]>(`/api/logs${qs ? `?${qs}` : ""}`);
  },

  subscribeLogs(callback, onError) {
    const es = new EventSource(`${BASE_URL}/api/logs/stream`, {
      withCredentials: true,
    });
    es.onmessage = (event) => {
      try {
        const entry: LogEntry = JSON.parse(event.data);
        callback(entry);
      } catch {
        // ignore malformed frames
      }
    };
    es.onerror = () => {
      // Connection failed or dropped (EventSource auto-reconnects).
      onError?.();
    };
    return () => {
      es.close();
    };
  },

  // ── Prompt Library ────────────────────────────────────────────
  listPrompts() {
    return request<Prompt[]>("/api/prompts");
  },

  createPrompt(input: PromptInput) {
    return request<Prompt>("/api/prompts", {
      method: "POST",
      body: JSON.stringify(input),
    });
  },

  updatePrompt(id: string, input: PromptInput) {
    return request<Prompt>(`/api/prompts/${encodeURIComponent(id)}`, {
      method: "PUT",
      body: JSON.stringify(input),
    });
  },

  deletePrompt(id: string) {
    return request<void>(`/api/prompts/${encodeURIComponent(id)}`, {
      method: "DELETE",
    });
  },

  setDefaultPrompt(id: string) {
    return request<Prompt>(`/api/prompts/${encodeURIComponent(id)}/default`, {
      method: "POST",
    });
  },

  listPromptVersions(promptId: string) {
    return request<PromptVersion[]>(`/api/prompts/${encodeURIComponent(promptId)}/versions`);
  },

  getPromptVersion(promptId: string, version: number) {
    return request<PromptVersion>(
      `/api/prompts/${encodeURIComponent(promptId)}/versions/${version}`,
    );
  },

  rollbackPrompt(promptId: string, version: number) {
    return request<Prompt>(`/api/prompts/${encodeURIComponent(promptId)}/rollback`, {
      method: "POST",
      body: JSON.stringify({ version }),
    });
  },

  // ── Settings ──────────────────────────────────────────────────
  getSettings() {
    return request<Settings>("/api/settings");
  },

  updateSettings(data) {
    return request<Settings>("/api/settings", {
      method: "PUT",
      body: JSON.stringify(data),
    });
  },

  // ── Auth ─────────────────────────────────────────────────────
  login(username: string, password: string) {
    return request<{ username: string; isTempPassword: boolean }>(
      "/api/auth/login",
      {
        method: "POST",
        body: JSON.stringify({ username, password }),
      },
    );
  },

  logout() {
    return request<void>("/api/auth/logout", { method: "POST" });
  },

  getMe() {
    return request<{ username: string; isTempPassword: boolean }>(
      "/api/auth/me",
    );
  },

  changePassword(currentPassword: string, newPassword: string) {
    return request<void>("/api/auth/change-password", {
      method: "POST",
      body: JSON.stringify({ currentPassword, newPassword }),
    });
  },

  // ── User Management ────────────────────────────────────────
  listUsers() {
    return request<User[]>("/api/users");
  },

  createUser(username: string, password: string) {
    return request<User>("/api/users", {
      method: "POST",
      body: JSON.stringify({ username, password }),
    });
  },

  deleteUser(id: string) {
    return request<void>(`/api/users/${encodeURIComponent(id)}`, {
      method: "DELETE",
    });
  },

  resetPassword(id: string, newPassword: string) {
    return request<void>(`/api/users/${encodeURIComponent(id)}/reset-password`, {
      method: "POST",
      body: JSON.stringify({ newPassword }),
    });
  },

  // ── API Keys ────────────────────────────────────────────────
  createApiKey(name: string) {
    return request<ApiKeyCreateResponse>("/api/api-keys", {
      method: "POST",
      body: JSON.stringify({ name }),
    });
  },

  createAgentApiKey(name: string) {
    return request<ApiKeyCreateResponse>("/api/api-keys/agent", {
      method: "POST",
      body: JSON.stringify({ name }),
    });
  },

  listApiKeys() {
    return request<ApiKeyItem[]>("/api/api-keys");
  },

  getApiKey(id: string) {
    return request<ApiKeyItem>(`/api/api-keys/${encodeURIComponent(id)}`);
  },

  revokeApiKey(id: string) {
    return request<void>(`/api/api-keys/${encodeURIComponent(id)}`, {
      method: "DELETE",
    });
  },

  rotateApiKey(id: string) {
    return request<ApiKeyCreateResponse>(
      `/api/api-keys/${encodeURIComponent(id)}/rotate`,
      { method: "POST" },
    );
  },
};
