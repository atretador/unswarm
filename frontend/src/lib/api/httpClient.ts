import type {
  Agent,
  AgentScriptStatus,
  BenchmarkResult,
  Container,
  LogEntry,
  Model,
  Prompt,
  QueueSnapshot,
  RegisterRuntimePayload,
  RegisteredRuntime,
  Settings,
  StatsSummary,
} from "./types";
import type { UnswarmClient } from "./client";

export const BASE_URL =
  (import.meta.env.VITE_API_URL as string | undefined) ||
  "http://localhost:5014";

// ─── Request helper ──────────────────────────────────────────────

async function request<T>(
  path: string,
  init?: RequestInit,
): Promise<T> {
  const url = `${BASE_URL}${path}`;
  const res = await fetch(url, {
    ...init,
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
    throw new Error(message);
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
    return request<Model>(`/api/models/${encodeURIComponent(id)}`);
  },

  createModel(data) {
    return request<Model>("/api/models", {
      method: "POST",
      body: JSON.stringify(data),
    });
  },

  updateModel(id, data) {
    return request<Model>(`/api/models/${encodeURIComponent(id)}`, {
      method: "PUT",
      body: JSON.stringify(data),
    });
  },

  deleteModel(id) {
    return request<void>(`/api/models/${encodeURIComponent(id)}`, {
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

  deleteRuntime(id: string, deleteModels = false) {
    const qs = deleteModels ? "?deleteModels=true" : "";
    return request<void>(
      `/api/containers/registered/${encodeURIComponent(id)}${qs}`,
      { method: "DELETE" },
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

  runBenchmark(modelId: string, prompt?: string) {
    return request<BenchmarkResult>(
      `/api/benchmarks?modelId=${encodeURIComponent(modelId)}`,
      {
        method: "POST",
        body: JSON.stringify(prompt ? { prompt } : {}),
      },
    );
  },

  listBenchmarks() {
    return request<BenchmarkResult[]>("/api/benchmarks");
  },

  // ── Agents ────────────────────────────────────────────────────
  listAgents() {
    return request<Agent[]>("/api/agents");
  },

  // ── Queue ─────────────────────────────────────────────────────
  getQueueSnapshot() {
    return request<QueueSnapshot>("/api/queue/snapshot");
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

  subscribeLogs(callback) {
    const es = new EventSource(`${BASE_URL}/api/logs/stream`);
    es.onmessage = (event) => {
      try {
        const entry: LogEntry = JSON.parse(event.data);
        callback(entry);
      } catch {
        // ignore malformed frames
      }
    };
    return () => {
      es.close();
    };
  },

  // ── Prompt Library ────────────────────────────────────────────
  listPrompts() {
    return request<Prompt[]>("/api/prompts");
  },

  createPrompt(input: { name: string; text: string }) {
    return request<Prompt>("/api/prompts", {
      method: "POST",
      body: JSON.stringify(input),
    });
  },

  updatePrompt(id: string, input: { name: string; text: string }) {
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
};
