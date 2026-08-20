import type {
  Agent,
  BenchmarkResult,
  Container,
  LastBenchmarkResult,
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

// ─── Helpers for benchmark seeds ─────────────────────────────────

function bench(modelId: string, modelName: string, tokensPerSec: number, latencyMs: number, tokensGenerated = 512): BenchmarkResult {
  return {
    id: `b-${modelId}`,
    modelId,
    modelName,
    prompt: "Default benchmark prompt — explain the proxy architecture in three sentences.",
    tokensPerSec,
    latencyMs,
    tokensGenerated,
    timestamp: NOW,
    status: "completed",
    errorMessage: null,
  };
}

/** Model-level summary (Model.lastBenchmark). tokensGenerated is optional — the backend wire contract omits it. */
function lastBench(tokensPerSec: number, latencyMs: number, tokensGenerated?: number): LastBenchmarkResult {
  return {
    tokensPerSec,
    latencyMs,
    timestamp: NOW,
    ...(tokensGenerated !== undefined ? { tokensGenerated } : {}),
  };
}

const LONG_PROMPT =
  "You are an inference engineer. Given a workload of concurrent streaming requests with mixed context lengths, describe how you would size the KV cache, pick a batch size, and schedule model swaps so that time-to-first-token stays under 200ms while throughput degrades gracefully under memory pressure. Be concrete and keep the answer under 400 words.";

/** Seed benchmark history — newest first, mixed outcomes, varied timestamps. */
const BENCHMARKS: BenchmarkResult[] = [
  {
    id: "b8",
    modelId: "1",
    modelName: "llama-3.1-70b",
    prompt: LONG_PROMPT,
    tokensPerSec: 44.7,
    latencyMs: 108,
    tokensGenerated: 368,
    timestamp: new Date(Date.now() - 2 * 60_000).toISOString(),
    status: "completed",
    errorMessage: null,
  },
  {
    id: "b7",
    modelId: "2",
    modelName: "mistral-large-2",
    prompt: "Default benchmark prompt — explain the proxy architecture in three sentences.",
    tokensPerSec: 27.9,
    latencyMs: 194,
    tokensGenerated: 512,
    timestamp: new Date(Date.now() - 38 * 60_000).toISOString(),
    status: "completed",
    errorMessage: null,
  },
  {
    id: "b6",
    modelId: "3",
    modelName: "codestral-22b",
    prompt: "Write a type-safe middleware chain for an HTTP router.",
    tokensPerSec: 0,
    latencyMs: 0,
    tokensGenerated: 0,
    timestamp: new Date(Date.now() - 95 * 60_000).toISOString(),
    status: "error",
    errorMessage: "Model is still validating — refused to serve. No response tokens produced.",
  },
  {
    id: "b5",
    modelId: "5",
    modelName: "gemma-2-27b",
    prompt: "Summarize the tokenizer differences between Llama 3 and Gemma in two sentences.",
    tokensPerSec: 56.3,
    latencyMs: 91,
    tokensGenerated: 512,
    timestamp: new Date(Date.now() - 6 * 3600_000).toISOString(),
    status: "completed",
    errorMessage: null,
  },
  {
    id: "b4",
    modelId: "4",
    modelName: "phi-3.5-mini",
    prompt: "Default benchmark prompt — explain the proxy architecture in three sentences.",
    tokensPerSec: 101.2,
    latencyMs: 31,
    tokensGenerated: 640,
    timestamp: new Date(Date.now() - 26 * 3600_000).toISOString(),
    status: "completed",
    errorMessage: null,
  },
  {
    id: "b3",
    modelId: "1",
    modelName: "llama-3.1-70b",
    prompt: "Default benchmark prompt — explain the proxy architecture in three sentences.",
    tokensPerSec: 41.8,
    latencyMs: 124,
    tokensGenerated: 512,
    timestamp: new Date(Date.now() - 3 * 86400_000).toISOString(),
    status: "completed",
    errorMessage: null,
  },
  {
    id: "b2",
    modelId: "2",
    modelName: "mistral-large-2",
    prompt: "Default benchmark prompt — explain the proxy architecture in three sentences.",
    tokensPerSec: 0,
    latencyMs: 0,
    tokensGenerated: 0,
    timestamp: new Date(Date.now() - 5 * 86400_000).toISOString(),
    status: "error",
    errorMessage: "CUDA out of memory while loading weights — retry with a smaller quantization.",
  },
  {
    id: "b1",
    modelId: "5",
    modelName: "gemma-2-27b",
    prompt: "Default benchmark prompt — explain the proxy architecture in three sentences.",
    tokensPerSec: 54.2,
    latencyMs: 99,
    tokensGenerated: 448,
    timestamp: new Date(Date.now() - 9 * 86400_000).toISOString(),
    status: "completed",
    errorMessage: null,
  },
];

// ─── Prompt Library Seed ─────────────────────────────────────────

const PROMPTS: Prompt[] = [
  {
    id: "p1",
    name: "Concise summary",
    text: "Summarize the input in two sentences maximum, focusing on the key technical points. Use plain language without jargon.",
    createdAt: NOW,
    updatedAt: NOW,
  },
  {
    id: "p2",
    name: "Code review",
    text: "Review this code for bugs, performance issues, and readability. Be specific about line numbers and suggest concrete fixes. Keep suggestions actionable — no vague advice.",
    createdAt: NOW,
    updatedAt: NOW,
  },
  {
    id: "p3",
    name: "Creative rewrite",
    text: "Rewrite the following text in a more engaging, conversational tone while preserving all technical accuracy and the original structure. Use short sentences and active voice. Add one concrete example where it clarifies the point.",
    createdAt: NOW,
    updatedAt: NOW,
  },
  {
    id: "p4",
    name: "Long-form writing",
    text: "You are an experienced technical writer. Expand the following notes into a well-structured blog post with an introduction, three main sections, and a conclusion. Include practical tips, real-world examples, and potential pitfalls. Aim for approximately 800 words, maintain a professional but approachable tone, and ensure the content flows naturally from one section to the next.",
    createdAt: NOW,
    updatedAt: NOW,
  },
];

// ─── Seed Data ────────────────────────────────────────────────────

const MODELS: Model[] = [
  {
    id: "1",
    name: "llama-3.1-70b",
    family: "Llama",
    parameterSize: "70B",
    quantization: "Q4_K_M",
    status: "ready",
    lastBenchmark: lastBench(42.3, 120, 512),
    contextWindow: 128000,
    containerImage: "unswarm/llama3.1:70b-q4km",
    sourceRuntimeId: "rc1",
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
    lastBenchmark: lastBench(28.7, 185),
    contextWindow: 128000,
    containerImage: "unswarm/mistral-large:123b-q5km",
    sourceRuntimeId: null,
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
    sourceRuntimeId: null,
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
    lastBenchmark: lastBench(98.1, 32, 640),
    contextWindow: 128000,
    containerImage: "unswarm/phi3.5-mini:fp16",
    sourceRuntimeId: null,
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
    lastBenchmark: lastBench(55.0, 95, 448),
    contextWindow: 8192,
    containerImage: "unswarm/gemma2:27b-q4ks",
    sourceRuntimeId: "rc1",
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

/** Containers running on each agent (returned by listAgentContainers). */
const AGENT_CONTAINERS: Record<string, Container[]> = {
  host: [...CONTAINERS],
  "edge-node-1": [
    {
      id: "en-vllm",
      modelId: "",
      modelName: "vllm-serve",
      status: "running",
      port: 8000,
      pid: 77124,
      memoryMb: 10240,
      cpuPercent: 8.2,
      uptime: 21600,
      lastHealthCheck: NOW,
      errorMessage: null,
      createdAt: NOW,
    },
    {
      id: "en-sd",
      modelId: "",
      modelName: "stable-diffusion-api",
      status: "running",
      port: 7860,
      pid: 77140,
      memoryMb: 6144,
      cpuPercent: 3.1,
      uptime: 43200,
      lastHealthCheck: NOW,
      errorMessage: null,
      createdAt: NOW,
    },
    {
      id: "en-ray",
      modelId: "",
      modelName: "ray-worker",
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
  ],
};

const AGENTS: Agent[] = [
  {
    name: "host",
    connectionId: null,
    connectedAt: null,
    lastSeen: NOW,
    isConnected: true,
    dockerSocket: "/var/run/docker.sock",
    version: "1.2.3",
    hostname: "workstation",
    osPlatform: "linux/amd64",
    gpuInfo: "NVIDIA GeForce RTX 4090 (24GB)",
    totalMemoryMb: 131072,
    cpuCores: 16,
    containers: [
      { containerId: "c1", modelName: "llama-3.1-70b", status: "running", port: 8081 },
      { containerId: "c2", modelName: "mistral-large-2", status: "starting", port: null },
      { containerId: "c3", modelName: "gemma-2-27b", status: "stopped", port: null },
    ],
  },
  {
    name: "edge-node-1",
    connectionId: "conn-1",
    connectedAt: NOW,
    lastSeen: NOW,
    isConnected: true,
    dockerSocket: "/var/run/docker.sock",
    version: "0.9.1",
    hostname: "edge-node-1",
    osPlatform: "linux/arm64",
    gpuInfo: null,
    totalMemoryMb: 16384,
    cpuCores: 8,
    containers: [],
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
  switchCount: 12,
  lastSwitchMs: 3420,
  avgSwitchMs: 2850,
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
let benchmarks = [...BENCHMARKS];
let registeredRuntimes: RegisteredRuntime[] = [
  {
    id: "rc1",
    displayName: "llama-server",
    image: "unswarm/llama3.1:70b-q4km",
    containerPort: 8080,
    agent: "host",
    canRunAlongWith: [],
    status: "ready",
    runtimeContainerId: "c1",
    mappedPort: 8081,
    errorMessage: null,
    createdAt: NOW,
    lastDiscoveredAt: NOW,
    discoveredModels: [
      { ...MODELS[0], sourceRuntimeId: "rc1" },
      { ...MODELS[4], sourceRuntimeId: "rc1" },
    ],
  },
  {
    id: "rc2",
    displayName: "mistral-server",
    image: "unswarm/mistral-large:123b-q5km",
    containerPort: 8080,
    agent: "host",
    canRunAlongWith: [],
    status: "starting",
    runtimeContainerId: null,
    mappedPort: null,
    errorMessage: null,
    createdAt: NOW,
    lastDiscoveredAt: null,
    discoveredModels: [],
  },
];
let settings = { ...SETTINGS };
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

  // ── Container Registration ──────────────────────────────────
  async registerRuntime(data: RegisterRuntimePayload) {
    await delay(rand(100, 300));
    const agentName = data.agent ?? "host";
    const runtime = (AGENT_CONTAINERS[agentName] ?? []).find(
      (c) => c.modelName === data.image || c.id === data.image,
    );
    const rc: RegisteredRuntime = {
      id: id(),
      displayName: data.displayName,
      image: data.image,
      containerPort: data.containerPort,
      agent: agentName,
      canRunAlongWith: data.canRunAlongWith ?? [],
      status: runtime ? "discovering" : "registered",
      runtimeContainerId: runtime?.id ?? null,
      mappedPort: runtime?.port ?? null,
      errorMessage: null,
      createdAt: new Date().toISOString(),
      lastDiscoveredAt: null,
      discoveredModels: [],
    };
    registeredRuntimes.push(rc);
    return { ...rc, discoveredModels: [] };
  },

  async listRegisteredRuntimes() {
    await delay(rand(80, 200));
    return registeredRuntimes.map((rc) => ({
      ...rc,
      discoveredModels: rc.discoveredModels.map((m) => ({ ...m })),
    }));
  },

  async getRegisteredRuntime(runtimeId: string) {
    await delay(rand(60, 150));
    const rc = registeredRuntimes.find((x) => x.id === runtimeId);
    if (!rc) throw new Error(`Registered runtime ${runtimeId} not found`);
    return {
      ...rc,
      discoveredModels: rc.discoveredModels.map((m) => ({ ...m })),
    };
  },

  async rediscoverRuntime(runtimeId: string) {
    await delay(rand(200, 500));
    const rc = registeredRuntimes.find((x) => x.id === runtimeId);
    if (!rc) throw new Error(`Registered runtime ${runtimeId} not found`);
    rc.status = "ready";
    rc.lastDiscoveredAt = new Date().toISOString();
    return {
      ...rc,
      discoveredModels: rc.discoveredModels.map((m) => ({ ...m })),
    };
  },

  async startRegisteredRuntime(runtimeId: string) {
    await delay(rand(200, 500));
    const rc = registeredRuntimes.find((x) => x.id === runtimeId);
    if (!rc) throw new Error(`Registered runtime ${runtimeId} not found`);
    // Flip the registration into the ready state — the runtime container is now live.
    rc.status = "ready";
    if (rc.runtimeContainerId) {
      const runtime = containers.find((c) => c.id === rc.runtimeContainerId);
      if (runtime) runtime.status = "running";
      // Also reflect the new state in the agent telemetry lookup (host seed).
      const agent = AGENTS.find((a) => a.name === rc.agent);
      const telemetry = agent?.containers.find(
        (t) => t.containerId === rc.runtimeContainerId,
      );
      if (telemetry) telemetry.status = "running";
    }
    return {
      ...rc,
      discoveredModels: rc.discoveredModels.map((m) => ({ ...m })),
    };
  },

  async deleteRuntime(runtimeId: string, deleteModels = false) {
    await delay(rand(60, 150));
    const idx = registeredRuntimes.findIndex((x) => x.id === runtimeId);
    if (idx === -1) throw new Error(`Registered runtime ${runtimeId} not found`);
    if (deleteModels) {
      const modelIds = new Set(registeredRuntimes[idx].discoveredModels.map((m) => m.id));
      models = models.filter((m) => !modelIds.has(m.id));
    }
    registeredRuntimes.splice(idx, 1);
  },

  // Fleet
  async listContainers() {
    await delay(rand(80, 200));
    return containers.map((c) => ({ ...c }));
  },
  async listAgentContainers(agentName: string) {
    await delay(rand(80, 200));
    const list = AGENT_CONTAINERS[agentName] ?? [];
    return list.map((c) => ({ ...c }));
  },
  async runBenchmark(modelId: string, prompt?: string) {
    await delay(rand(200, 500));
    const model = models.find((m) => m.id === modelId);
    if (!model) throw new Error(`Model ${modelId} not found`);
    const b = bench(
      modelId,
      model.name,
      30 + Math.random() * 45,
      90 + Math.random() * 160,
      384,
    );
    if (prompt) b.prompt = prompt;
    benchmarks.unshift(b);
    return b;
  },
  async listBenchmarks() {
    await delay(rand(80, 200));
    return benchmarks.map((b) => ({ ...b }));
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

  // Agents
  async listAgents() {
    await delay(rand(80, 200));
    return AGENTS.map((a) => ({
      ...a,
      containers: a.containers.map((c) => ({ ...c })),
    }));
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

  // ── Prompt Library ────────────────────────────────────────────
  async listPrompts() {
    await delay(rand(60, 150));
    // Return sorted by name ascending (wire contract).
    return [...PROMPTS].sort((a, b) => a.name.localeCompare(b.name));
  },

  async createPrompt(input: { name: string; text: string }) {
    await delay(rand(80, 200));
    if (!input.name.trim() || !input.text.trim()) {
      throw new Error("Name and text are required");
    }
    const prompt: Prompt = {
      id: id(),
      name: input.name.trim(),
      text: input.text.trim(),
      createdAt: new Date().toISOString(),
      updatedAt: new Date().toISOString(),
    };
    PROMPTS.push(prompt);
    return { ...prompt };
  },

  async updatePrompt(promptId: string, input: { name: string; text: string }) {
    await delay(rand(80, 200));
    if (!input.name.trim() || !input.text.trim()) {
      throw new Error("Name and text are required");
    }
    const prompt = PROMPTS.find((p) => p.id === promptId);
    if (!prompt) throw new Error(`Prompt ${promptId} not found`);
    prompt.name = input.name.trim();
    prompt.text = input.text.trim();
    prompt.updatedAt = new Date().toISOString();
    return { ...prompt };
  },

  async deletePrompt(promptId: string) {
    await delay(rand(60, 150));
    const idx = PROMPTS.findIndex((p) => p.id === promptId);
    if (idx === -1) throw new Error(`Prompt ${promptId} not found`);
    PROMPTS.splice(idx, 1);
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

};
