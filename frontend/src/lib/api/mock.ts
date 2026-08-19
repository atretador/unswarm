import type {
  ApiKey,
  Container,
  LogEntry,
  Model,
  QueueSnapshot,
  Settings,
  StatsSummary,
} from "./types";
import type { UnswarmClient } from "./client";

// ─── Helpers ──────────────────────────────────────────────────────

/** When non-null, overrides all random delays with this fixed value (ms). */
let fixedLatency: number | null = null;

/** Set to 0 for instant tests, null to restore random delays. */
export function setMockLatency(ms: number | null) {
  fixedLatency = ms;
}

function delay(ms: number): Promise<void> {
  return new Promise((r) => setTimeout(r, ms));
}

function rand(min: number, max: number): number {
  return fixedLatency !== null ? fixedLatency : Math.random() * (max - min) + min;
}

let nextId = 100;
function id(): string {
  return String(++nextId);
}

const NOW = new Date().toISOString();

// ─── Seed Data ────────────────────────────────────────────────────

const MODELS: Model[] = [
  {
    id: "1",
    name: "llama-3.1-70b",
    family: "Llama",
    parameterSize: "70B",
    quantization: "Q4_K_M",
    status: "ready",
    lastBenchmark: { tokensPerSec: 42.3, latencyMs: 120, timestamp: NOW },
    contextWindow: 128000,
    containerImage: "unswarm/llama3.1:70b-q4km",
    createdAt: NOW,
    updatedAt: NOW,
  },
  {
    id: "2",
    name: "mistral-large-2",
    family: "Mistral",
    parameterSize: "123B",
    quantization: "Q5_K_M",
    status: "ready",
    lastBenchmark: { tokensPerSec: 28.7, latencyMs: 185, timestamp: NOW },
    contextWindow: 128000,
    containerImage: "unswarm/mistral-large:123b-q5km",
    createdAt: NOW,
    updatedAt: NOW,
  },
  {
    id: "3",
    name: "codestral-22b",
    family: "Mistral",
    parameterSize: "22B",
    quantization: "Q6_K",
    status: "validating",
    lastBenchmark: null,
    contextWindow: 32000,
    containerImage: "unswarm/codestral:22b-q6k",
    createdAt: NOW,
    updatedAt: NOW,
  },
  {
    id: "4",
    name: "phi-3.5-mini",
    family: "Phi",
    parameterSize: "3.8B",
    quantization: "FP16",
    status: "deprecated",
    lastBenchmark: { tokensPerSec: 98.1, latencyMs: 32, timestamp: NOW },
    contextWindow: 128000,
    containerImage: "unswarm/phi3.5-mini:fp16",
    createdAt: NOW,
    updatedAt: NOW,
  },
  {
    id: "5",
    name: "gemma-2-27b",
    family: "Gemma",
    parameterSize: "27B",
    quantization: "Q4_K_S",
    status: "ready",
    lastBenchmark: { tokensPerSec: 55.0, latencyMs: 95, timestamp: NOW },
    contextWindow: 8192,
    containerImage: "unswarm/gemma2:27b-q4ks",
    createdAt: NOW,
    updatedAt: NOW,
  },
];

const CONTAINERS: Container[] = [
  {
    id: "c1",
    modelId: "1",
    modelName: "llama-3.1-70b",
    status: "running",
    port: 8081,
    pid: 48291,
    memoryMb: 38400,
    cpuPercent: 12.4,
    uptime: 86400,
    lastHealthCheck: NOW,
    errorMessage: null,
    createdAt: NOW,
  },
  {
    id: "c2",
    modelId: "2",
    modelName: "mistral-large-2",
    status: "starting",
    port: null,
    pid: null,
    memoryMb: 0,
    cpuPercent: 0,
    uptime: 0,
    lastHealthCheck: null,
    errorMessage: null,
    createdAt: NOW,
  },
  {
    id: "c3",
    modelId: "5",
    modelName: "gemma-2-27b",
    status: "stopped",
    port: null,
    pid: null,
    memoryMb: 0,
    cpuPercent: 0,
    uptime: 0,
    lastHealthCheck: null,
    errorMessage: null,
    createdAt: NOW,
  },
];

const QUEUE_SNAPSHOT: QueueSnapshot = {
  currentSlot: {
    id: "q1",
    modelRequested: "llama-3.1-70b",
    modelAssigned: "llama-3.1-70b",
    status: "processing",
    priority: 1,
    tokensRequested: 4096,
    tokensGenerated: 1247,
    elapsedMs: 3400,
    waitMs: 120,
    createdAt: NOW,
  },
  waiting: [
    {
      id: "q2",
      modelRequested: "mistral-large-2",
      modelAssigned: null,
      status: "waiting",
      priority: 2,
      tokensRequested: 2048,
      tokensGenerated: 0,
      elapsedMs: 0,
      waitMs: 8200,
      createdAt: NOW,
    },
    {
      id: "q3",
      modelRequested: "llama-3.1-70b",
      modelAssigned: null,
      status: "waiting",
      priority: 3,
      tokensRequested: 1024,
      tokensGenerated: 0,
      elapsedMs: 0,
      waitMs: 5100,
      createdAt: NOW,
    },
  ],
  recentCompleted: [
    {
      id: "q4",
      modelRequested: "llama-3.1-70b",
      modelAssigned: "llama-3.1-70b",
      status: "completed",
      priority: 1,
      tokensRequested: 2048,
      tokensGenerated: 2048,
      elapsedMs: 12400,
      waitMs: 200,
      createdAt: NOW,
    },
  ],
  activeTransitions: [],
};

const STATS: StatsSummary = {
  totalRequests: 14287,
  activeRequests: 3,
  avgLatencyMs: 142,
  totalTokensProcessed: 24_891_000,
  uptimeSeconds: 86400 * 7 + 3600 * 4,
  modelsLoaded: 2,
  containersRunning: 1,
  queueDepth: 2,
  requestsPerMinute: [
    12, 8, 15, 22, 18, 9, 14, 26, 31, 28, 20, 16, 19, 24, 27, 33, 29, 18,
    14, 11, 8, 13, 17, 21,
  ],
  errorsLast24h: 3,
  tokensPerSecond: [
    420, 380, 510, 620, 580, 440, 490, 680, 720, 690, 550, 500, 530, 640, 700,
    750, 710, 520, 480, 430, 390, 460, 510, 590,
  ],
};

const LOGS: LogEntry[] = [
  {
    id: "l1",
    timestamp: new Date(Date.now() - 5000).toISOString(),
    level: "info",
    source: "c1",
    message: "Health check passed — 12ms response",
  },
  {
    id: "l2",
    timestamp: new Date(Date.now() - 12000).toISOString(),
    level: "info",
    source: "scheduler",
    message: "Request q1 assigned to llama-3.1-70b (slot 0)",
  },
  {
    id: "l3",
    timestamp: new Date(Date.now() - 30000).toISOString(),
    level: "warn",
    source: "c2",
    message: "Container startup slow: waiting for model weights to load",
  },
  {
    id: "l4",
    timestamp: new Date(Date.now() - 45000).toISOString(),
    level: "info",
    source: "proxy",
    message: "OpenAI-compatible endpoint ready on :8080",
  },
  {
    id: "l5",
    timestamp: new Date(Date.now() - 60000).toISOString(),
    level: "error",
    source: "c3",
    message: "OOM killed — model requires 32GB, container limit was 16GB",
  },
  {
    id: "l6",
    timestamp: new Date(Date.now() - 90000).toISOString(),
    level: "debug",
    source: "scheduler",
    message: "Queue depth: 2 — next available slot in ~8s",
  },
];

const SETTINGS: Settings = {
  maxConcurrentModels: 1,
  defaultModel: "llama-3.1-70b",
  requestTimeout: 120,
  healthCheckInterval: 10,
  autoShutdownIdle: true,
  idleTimeout: 300,
  logRetention: 168,
  enableBenchmarking: true,
  priorityMode: "priority",
  batchDrain: false,
  lazyStop: true,
  maxQueueDepth: 32,
};

const API_KEYS: ApiKey[] = [
  {
    id: "k1",
    name: "dev-local",
    keyPrefix: "usw-xxxx",
    permissions: ["models:read", "proxy:access"],
    rateLimit: 60,
    createdAt: NOW,
    lastUsedAt: NOW,
    expiresAt: null,
  },
  {
    id: "k2",
    name: "ci-pipeline",
    keyPrefix: "usw-yyyy",
    permissions: ["models:read", "models:write", "fleet:manage", "proxy:access"],
    rateLimit: null,
    createdAt: NOW,
    lastUsedAt: NOW,
    expiresAt: new Date(Date.now() + 86400 * 90).toISOString(),
  },
];

// ─── Log Streaming ────────────────────────────────────────────────

const STREAM_POOL: Array<{ level: LogEntry["level"]; source: string; message: string }> = [
  { level: "info", source: "scheduler", message: "Queue depth: 1 — next request in <2s" },
  { level: "info", source: "proxy", message: "POST /v1/chat/completions — 200 OK (138ms)" },
  { level: "debug", source: "c1", message: "KV cache utilization: 34%" },
  { level: "info", source: "c1", message: "Health check passed — 8ms response" },
  { level: "warn", source: "scheduler", message: "Request timeout approaching for q3 (115s / 120s)" },
  { level: "info", source: "proxy", message: "GET /v1/models — 200 OK (3ms)" },
  { level: "error", source: "c2", message: "CUDA OOM — retrying with reduced batch size" },
  { level: "info", source: "scheduler", message: "Container c1 CPU at 78% — high load" },
  { level: "debug", source: "proxy", message: "SSE stream opened — tokens flowing" },
  { level: "info", source: "c3", message: "Graceful shutdown complete" },
];

const logSubscribers = new Map<symbol, (entry: LogEntry) => void>();
let logInterval: ReturnType<typeof setInterval> | null = null;
let logCounter = 1000;

function startLogStream() {
  if (logInterval) return;
  logInterval = setInterval(() => {
    const template = STREAM_POOL[Math.floor(Math.random() * STREAM_POOL.length)];
    const entry: LogEntry = {
      id: `stream-${++logCounter}`,
      timestamp: new Date().toISOString(),
      level: template.level,
      source: template.source,
      message: template.message,
    };
    for (const cb of logSubscribers.values()) {
      cb(entry);
    }
  }, 3000);
}

function stopLogStreamIfIdle() {
  if (logSubscribers.size === 0 && logInterval) {
    clearInterval(logInterval);
    logInterval = null;
  }
}

// ─── Mutable state ────────────────────────────────────────────────

let models = [...MODELS];
let containers = [...CONTAINERS];
let settings = { ...SETTINGS };
let apiKeys = [...API_KEYS];

// ─── Mock Client ──────────────────────────────────────────────────

export const mockClient: UnswarmClient = {
  // Models
  async listModels() {
    await delay(rand(80, 200));
    return [...models];
  },
  async getModel(modelId) {
    await delay(rand(60, 150));
    const m = models.find((x) => x.id === modelId);
    if (!m) throw new Error(`Model ${modelId} not found`);
    return { ...m };
  },
  async createModel(data) {
    await delay(rand(100, 300));
    const m: Model = {
      ...data,
      id: id(),
      createdAt: new Date().toISOString(),
      updatedAt: new Date().toISOString(),
    };
    models.push(m);
    return { ...m };
  },
  async updateModel(modelId, data) {
    await delay(rand(80, 200));
    const idx = models.findIndex((x) => x.id === modelId);
    if (idx === -1) throw new Error(`Model ${modelId} not found`);
    models[idx] = { ...models[idx], ...data, updatedAt: new Date().toISOString() };
    return { ...models[idx] };
  },
  async deleteModel(modelId) {
    await delay(rand(60, 150));
    models = models.filter((x) => x.id !== modelId);
  },

  // Fleet
  async listContainers() {
    await delay(rand(80, 200));
    return containers.map((c) => ({ ...c }));
  },
  async startContainer(modelId) {
    await delay(rand(200, 500));
    const model = models.find((m) => m.id === modelId);
    const c: Container = {
      id: id(),
      modelId,
      modelName: model?.name ?? "unknown",
      status: "starting",
      port: null,
      pid: null,
      memoryMb: 0,
      cpuPercent: 0,
      uptime: 0,
      lastHealthCheck: null,
      errorMessage: null,
      createdAt: new Date().toISOString(),
    };
    containers.push(c);
    return { ...c };
  },
  async stopContainer(containerId) {
    await delay(rand(100, 300));
    const c = containers.find((x) => x.id === containerId);
    if (c) c.status = "stopped";
  },
  async restartContainer(containerId) {
    await delay(rand(200, 400));
    const c = containers.find((x) => x.id === containerId);
    if (c) c.status = "running";
    return { ...(c ?? CONTAINERS[0]) };
  },

  // Queue
  async getQueueSnapshot() {
    await delay(rand(60, 120));
    return {
      ...QUEUE_SNAPSHOT,
      waiting: QUEUE_SNAPSHOT.waiting.map((w) => ({ ...w })),
      recentCompleted: QUEUE_SNAPSHOT.recentCompleted.map((r) => ({ ...r })),
      activeTransitions: QUEUE_SNAPSHOT.activeTransitions.map((t) => ({ ...t })),
    };
  },

  // Stats
  async getStats() {
    await delay(rand(100, 250));
    return {
      ...STATS,
      requestsPerMinute: [...STATS.requestsPerMinute],
      tokensPerSecond: [...STATS.tokensPerSecond],
    };
  },

  // Logs — with filtering support
  async getLogs(opts) {
    await delay(rand(60, 120));
    let result = [...LOGS];

    if (opts?.source) {
      result = result.filter((l) => l.source === opts.source);
    }
    if (opts?.level) {
      result = result.filter((l) => l.level === opts.level);
    }
    if (opts?.since) {
      const sinceMs = new Date(opts.since).getTime();
      result = result.filter((l) => new Date(l.timestamp).getTime() > sinceMs);
    }
    if (opts?.limit) {
      result = result.slice(-opts.limit);
    }

    return result;
  },

  // Log streaming
  subscribeLogs(callback) {
    const key = Symbol();
    logSubscribers.set(key, callback);
    startLogStream();
    return () => {
      logSubscribers.delete(key);
      stopLogStreamIfIdle();
    };
  },

  // Settings
  async getSettings() {
    await delay(rand(50, 100));
    return { ...settings };
  },
  async updateSettings(data) {
    await delay(rand(80, 200));
    settings = { ...settings, ...data };
    return { ...settings };
  },

  // API Keys
  async listApiKeys() {
    await delay(rand(60, 120));
    return apiKeys.map((k) => ({ ...k }));
  },
  async createApiKey(data) {
    await delay(rand(100, 300));
    const k: ApiKey = {
      id: id(),
      name: data.name,
      keyPrefix: "usw-" + Math.random().toString(36).slice(2, 6),
      permissions: data.permissions,
      rateLimit: 60,
      createdAt: new Date().toISOString(),
      lastUsedAt: null,
      expiresAt: null,
    };
    apiKeys.push(k);
    return { ...k };
  },
  async revokeApiKey(keyId) {
    await delay(rand(50, 100));
    apiKeys = apiKeys.filter((k) => k.id !== keyId);
  },
};
