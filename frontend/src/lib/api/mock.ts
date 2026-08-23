import type {
  Agent,
  AgentAvailableScript,
  ApiKeyCreateResponse,
  ApiKeyItem,
  BenchmarkResult,
  CloudProvider,
  CloudProviderRead,
  CloudProviderUpdateInput,
  Container,
  FetchModelsResult,
  LastBenchmarkResult,
  LogEntry,
  Model,
  Prompt,
  PromptVersion,
  QueueSnapshot,
  RegisterRuntimePayload,
  RegisteredRuntime,
  Settings,
  StatsSummary,
  ToggleConcurrencyPayload,
  User,
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

/**
 * Deterministic-enough fake secret for the mock client. Mirrors the backend's
 * base64url, no-padding shape without depending on a real CSPRNG global.
 */
function randomSecret(): string {
  const alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789_-";
  let out = "";
  for (let i = 0; i < 43; i++) {
    out += alphabet[Math.floor(Math.random() * alphabet.length)];
  }
  return out;
}

const NOW = new Date().toISOString();

// ─── Helpers for benchmark seeds ─────────────────────────────────

function bench(modelId: string, modelName: string, tokensPerSec: number, latencyMs: number, tokensGenerated = 512): BenchmarkResult {
  return {
    id: `b-${modelId}`,
    modelId,
    modelName,
    prompt: "Default benchmark prompt — explain the proxy architecture in three sentences.",
    promptId: null,
    promptName: null,
    promptVersion: null,
    tokensPerSec,
    latencyMs,
    tokensGenerated,
    timestamp: NOW,
    status: "completed",
    errorMessage: null,
  };
}

/** Model-level summary (Model.lastBenchmark). tokensGenerated is optional — the backend wire contract omits it. */
function lastBench(tokensPerSec: number, latencyMs: number, tokensGenerated?: number, promptName?: string, promptVersion?: number): LastBenchmarkResult {
  return {
    tokensPerSec,
    latencyMs,
    timestamp: NOW,
    ...(tokensGenerated !== undefined ? { tokensGenerated } : {}),
    ...(promptName !== undefined ? { promptName } : {}),
    ...(promptVersion !== undefined ? { promptVersion } : {}),
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
    promptId: "p4",
    promptName: "Long-form writing",
    promptVersion: 2,
    tokensPerSec: 44.7,
    latencyMs: 108,
    tokensGenerated: 368,
    timestamp: new Date(Date.now() - 2 * 60_000).toISOString(),
    status: "completed",
    errorMessage: null,
    reasoning:
      "The user wants a long-form piece, so I should prioritize structure over brevity: intro framing, three sections with concrete examples, and a short conclusion. KV sizing math first (context length × bytes per token), then batch trade-offs. Keep each section under ~150 words so the total lands near 400.",
  },
  {
    id: "b7",
    modelId: "2",
    modelName: "mistral-large-2",
    prompt: "Default benchmark prompt — explain the proxy architecture in three sentences.",
    promptId: "p1",
    promptName: "Concise summary",
    promptVersion: 3,
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
    promptId: "p2",
    promptName: "Code review",
    promptVersion: 1,
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
    promptId: "p1",
    promptName: "Concise summary",
    promptVersion: 3,
    tokensPerSec: 56.3,
    latencyMs: 91,
    tokensGenerated: 512,
    timestamp: new Date(Date.now() - 6 * 3600_000).toISOString(),
    status: "completed",
    errorMessage: null,
    reasoning:
      "Two sentences max. The key technical differences are vocabulary size (128k vs 256k) and how each handles byte fallback — lead with that, skip training-data details.",
  },
  {
    id: "b4",
    modelId: "4",
    modelName: "phi-3.5-mini",
    prompt: "Default benchmark prompt — explain the proxy architecture in three sentences.",
    promptId: "p1",
    promptName: "Concise summary",
    promptVersion: 3,
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
    promptId: "p1",
    promptName: "Concise summary",
    promptVersion: 3,
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
    promptId: "p1",
    promptName: "Concise summary",
    promptVersion: 3,
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
    promptId: "p1",
    promptName: "Concise summary",
    promptVersion: 3,
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
    maxTokens: 256,
    isDefault: true,
    currentVersion: 3,
    createdAt: NOW,
    updatedAt: NOW,
  },
  {
    id: "p2",
    name: "Code review",
    text: "Review this code for bugs, performance issues, and readability. Be specific about line numbers and suggest concrete fixes. Keep suggestions actionable — no vague advice.",
    maxTokens: 256,
    currentVersion: 1,
    createdAt: NOW,
    updatedAt: NOW,
  },
  {
    id: "p3",
    name: "Creative rewrite",
    text: "Rewrite the following text in a more engaging, conversational tone while preserving all technical accuracy and the original structure. Use short sentences and active voice. Add one concrete example where it clarifies the point.",
    maxTokens: 256,
    currentVersion: 1,
    createdAt: NOW,
    updatedAt: NOW,
  },
  {
    id: "p4",
    name: "Long-form writing",
    text: "You are an experienced technical writer. Expand the following notes into a well-structured blog post with an introduction, three main sections, and a conclusion. Include practical tips, real-world examples, and potential pitfalls. Aim for approximately 800 words, maintain a professional but approachable tone, and ensure the content flows naturally from one section to the next.",
    maxTokens: 2048,
    currentVersion: 2,
    createdAt: NOW,
    updatedAt: NOW,
  },
];

// ─── Prompt Version History Seed ─────────────────────────────────

const PROMPT_VERSIONS: PromptVersion[] = [
  // p1 (Concise summary) — 3 versions
  {
    id: "pv-p1-1",
    promptId: "p1",
    version: 1,
    text: "Summarize the input in two sentences maximum.",
    createdAt: new Date(Date.now() - 14 * 86400_000).toISOString(),
  },
  {
    id: "pv-p1-2",
    promptId: "p1",
    version: 2,
    text: "Summarize the input in two sentences maximum, focusing on the key technical points.",
    createdAt: new Date(Date.now() - 7 * 86400_000).toISOString(),
  },
  {
    id: "pv-p1-3",
    promptId: "p1",
    version: 3,
    text: "Summarize the input in two sentences maximum, focusing on the key technical points. Use plain language without jargon.",
    createdAt: NOW,
  },
  // p4 (Long-form writing) — 2 versions
  {
    id: "pv-p4-1",
    promptId: "p4",
    version: 1,
    text: "Expand the following notes into a well-structured blog post. Aim for approximately 600 words.",
    createdAt: new Date(Date.now() - 10 * 86400_000).toISOString(),
  },
  {
    id: "pv-p4-2",
    promptId: "p4",
    version: 2,
    text: "You are an experienced technical writer. Expand the following notes into a well-structured blog post with an introduction, three main sections, and a conclusion. Include practical tips, real-world examples, and potential pitfalls. Aim for approximately 800 words, maintain a professional but approachable tone, and ensure the content flows naturally from one section to the next.",
    createdAt: NOW,
  },
  // p2 and p3 — only version 1 (currentVersion: 1)
  {
    id: "pv-p2-1",
    promptId: "p2",
    version: 1,
    text: "Review this code for bugs, performance issues, and readability. Be specific about line numbers and suggest concrete fixes. Keep suggestions actionable — no vague advice.",
    createdAt: NOW,
  },
  {
    id: "pv-p3-1",
    promptId: "p3",
    version: 1,
    text: "Rewrite the following text in a more engaging, conversational tone while preserving all technical accuracy and the original structure. Use short sentences and active voice. Add one concrete example where it clarifies the point.",
    createdAt: NOW,
  },
];

// ─── API Keys Seed ────────────────────────────────────────────────
// Inference keys authenticate to the /v1 proxy; agent keys authenticate to the
// agent channel (/api/agents + /ws/agent). They are NOT login credentials.
// The Go agent's key is provisioned via config and seeded into the managed
// store at startup — here we mirror that as a pre-existing managed row.
const API_KEYS: ApiKeyItem[] = [
  {
    id: "ak-seed-0001",
    name: "Go agent",
    keyPrefix: "ak_7f3a9c",
    scope: "agent",
    isActive: true,
    createdAt: new Date(Date.now() - 30 * 86400_000).toISOString(),
    lastUsedAt: new Date(Date.now() - 12 * 3600_000).toISOString(),
  },
  {
    id: "ak-seed-0002",
    name: "Local dashboard test",
    keyPrefix: "usk_2bd41e",
    scope: "inference",
    isActive: true,
    createdAt: new Date(Date.now() - 7 * 86400_000).toISOString(),
    lastUsedAt: null,
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
    lastBenchmark: lastBench(42.3, 120, 512, "Concise summary", 3),
    contextWindow: 128000,
    containerImage: "unswarm/llama3.1:70b-q4km",
    sourceRuntimeId: "rc1",
    sourceRuntimeName: "llama3.1-70b",
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
    lastBenchmark: lastBench(28.7, 185, undefined, "Concise summary", 3),
    contextWindow: 128000,
    containerImage: "unswarm/mistral-large:123b-q5km",
    sourceRuntimeId: null,
    sourceRuntimeName: null,
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
    sourceRuntimeName: null,
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
    lastBenchmark: lastBench(98.1, 32, 640, "Concise summary", 3),
    contextWindow: 128000,
    containerImage: "unswarm/phi3.5-mini:fp16",
    sourceRuntimeId: null,
    sourceRuntimeName: null,
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
    lastBenchmark: lastBench(55.0, 95, 448, "Concise summary", 3),
    contextWindow: 8192,
    containerImage: "unswarm/gemma2:27b-q4ks",
    sourceRuntimeId: "rc1",
    sourceRuntimeName: "gemma2-27b",
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
    scripts: [],
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
    scripts: [
      { path: "/opt/scripts/run_vllm.sh", pid: 0, status: "stopped", port: 0, startTime: 0 },
    ],
  },
];

/** Available .sh files on each agent's scripts_dir (returned by listAvailableScripts). */
const AGENT_AVAILABLE_SCRIPTS: Record<string, AgentAvailableScript[]> = {
  "edge-node-1": [
    { path: "/home/user/scripts/run_llama.sh", name: "run_llama.sh" },
    { path: "/home/user/scripts/run_vllm.sh", name: "run_vllm.sh" },
    { path: "/home/user/scripts/start_api.sh", name: "start_api.sh" },
  ],
  // "gpu-node-1" returns [] to exercise empty state
  "gpu-node-1": [],
};

const QUEUE_SNAPSHOT: QueueSnapshot = {
  processing: [
    {
      id: "q1",
      modelRequested: "llama-3.1-70b",
      modelAssigned: "llama-3.1-70b",
      targetId: "host",
      runtimeId: "rt-host-main",
      blockedByRuntimeIds: [],
      status: "processing",
      priority: 1,
      tokensRequested: 4096,
      tokensGenerated: 1247,
      promptTokensPerSec: 0,
      generationTokensPerSec: 0,
      elapsedMs: 3400,
      waitMs: 120,
      createdAt: NOW,
    },
    {
      id: "q5",
      modelRequested: "mistral-large-2",
      modelAssigned: "mistral-large-2",
      targetId: "agent:gpu-node-1",
      runtimeId: "rt-gpu-node-1-a",
      blockedByRuntimeIds: [],
      status: "processing",
      priority: 2,
      tokensRequested: 2048,
      tokensGenerated: 640,
      promptTokensPerSec: 0,
      generationTokensPerSec: 0,
      elapsedMs: 1800,
      waitMs: 300,
      createdAt: NOW,
    },
  ],
  currentSlot: null,
  skipsUsed: 1,
  skipsRemaining: 2,
  waiting: [
    {
      id: "q2",
      modelRequested: "mistral-large-2",
      modelAssigned: null,
      targetId: "agent:gpu-node-1",
      runtimeId: "rt-gpu-node-1-b",
      blockedByRuntimeIds: ["rt-gpu-node-1-a"],
      // Conversation affinity demo: held by an active tool-call conversation
      // on the same runtime that is also listed as blocking.
      heldByConversation: {
        model: "mistral-large-2",
        runtimeId: "rt-gpu-node-1-a",
        requestCount: 7,
        // Countdown demo: hold lapses ~45s from module load
        holdExpiresAt: new Date(Date.now() + 45_000).toISOString(),
      },
      status: "waiting",
      priority: 2,
      tokensRequested: 2048,
      tokensGenerated: 0,
      promptTokensPerSec: 0,
      generationTokensPerSec: 0,
      elapsedMs: 0,
      waitMs: 8200,
      createdAt: NOW,
    },
    {
      id: "q3",
      modelRequested: "llama-3.1-70b",
      modelAssigned: null,
      targetId: "host",
      runtimeId: null,
      blockedByRuntimeIds: [],
      status: "waiting",
      priority: 3,
      tokensRequested: 1024,
      tokensGenerated: 0,
      promptTokensPerSec: 0,
      generationTokensPerSec: 0,
      elapsedMs: 0,
      waitMs: 5100,
      createdAt: NOW,
    },
    {
      id: "q6",
      modelRequested: "gemma-2-27b",
      modelAssigned: null,
      targetId: "host",
      runtimeId: null,
      blockedByRuntimeIds: ["rt-host-main"],
      status: "waiting",
      priority: 4,
      tokensRequested: 512,
      tokensGenerated: 0,
      promptTokensPerSec: 0,
      generationTokensPerSec: 0,
      elapsedMs: 0,
      waitMs: 2400,
      createdAt: NOW,
    },
  ],
  recentCompleted: [
    {
      id: "q4",
      modelRequested: "llama-3.1-70b",
      modelAssigned: "llama-3.1-70b",
      targetId: "host",
      runtimeId: "rt-host-main",
      blockedByRuntimeIds: [],
      status: "completed",
      priority: 1,
      tokensRequested: 2048,
      tokensGenerated: 2048,
      promptTokensPerSec: 0,
      generationTokensPerSec: 0,
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
  totalPromptTokensCached: 9_120_000,
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
  requestTimeout: 120,
  healthCheckInterval: 10,
  autoShutdownIdle: false,
  idleTimeout: 300,
  logRetention: 168,
  enableBenchmarking: true,
  priorityMode: "priority",
  batchDrain: false,
  lazyStop: true,
  maxQueueDepth: 32,
  parallelSlotSkipLimit: 3,
  enableParallelSlotSkip: false,
  queueStepsTillReset: 3,
  enableConversationAffinity: true,
  conversationDwellSeconds: 45,
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

// ─── Cloud Providers Seed ───────────────────────────────────────

const CLOUD_PROVIDERS: (CloudProvider & { apiKey?: string })[] = [
  {
    id: "cp-seed-001",
    name: "openai",
    baseUrl: "https://api.openai.com",
    apiKeyHint: "sk-proj…x9aZ",
    apiKey: "sk-proj-real-key-placeholder",
    modelCount: 2,
    createdAt: NOW,
    updatedAt: NOW,
  },
  {
    id: "cp-seed-002",
    name: "anthropic",
    baseUrl: "https://api.anthropic.com",
    apiKeyHint: "sk-ant…3f9a",
    apiKey: "sk-ant-real-key-placeholder",
    modelCount: 3,
    createdAt: NOW,
    updatedAt: NOW,
  },
];

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
    maxConcurrentInferences: 2,
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
    maxConcurrentInferences: 1,
  },
];
let settings = { ...SETTINGS };

// ─── Users Seed ─────────────────────────────────────────────────

const MOCK_USERS: User[] = [
  { id: "u1", username: "admin", isTempPassword: false },
  { id: "u2", username: "alice", isTempPassword: true },
];
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
      ...(data.runtimeKind ? { runtimeKind: data.runtimeKind } : {}),
      ...(data.launcherPath ? { launcherPath: data.launcherPath } : {}),
      maxConcurrentInferences: 1,
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

  async stopRegisteredRuntime(runtimeId: string) {
    await delay(rand(200, 500));
    const rc = registeredRuntimes.find((x) => x.id === runtimeId);
    if (!rc) throw new Error(`Registered runtime ${runtimeId} not found`);
    // For scripts, flip back to "registered" so the card shows Start again.
    rc.status = "registered";
    if (rc.runtimeContainerId) {
      const runtime = containers.find((c) => c.id === rc.runtimeContainerId);
      if (runtime) runtime.status = "stopped";
      const agent = AGENTS.find((a) => a.name === rc.agent);
      const telemetry = agent?.containers.find(
        (t) => t.containerId === rc.runtimeContainerId,
      );
      if (telemetry) telemetry.status = "stopped";
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

  async updateRuntimeConcurrency(runtimeId: string, payload: { canRunAlongWith: string[]; maxConcurrentInferences?: number }) {
    await delay(rand(80, 200));
    const rc = registeredRuntimes.find((x) => x.id === runtimeId);
    if (!rc) throw new Error(`Registered runtime ${runtimeId} not found`);
    rc.canRunAlongWith = [...payload.canRunAlongWith];
    if (payload.maxConcurrentInferences !== undefined) {
      rc.maxConcurrentInferences = payload.maxConcurrentInferences;
    }
    return {
      ...rc,
      discoveredModels: rc.discoveredModels.map((m) => ({ ...m })),
    };
  },

  async toggleRuntimeConcurrency(payload: ToggleConcurrencyPayload) {
    await delay(rand(80, 200));
    const rcA = registeredRuntimes.find((x) => x.id === payload.runtimeAId);
    const rcB = registeredRuntimes.find((x) => x.id === payload.runtimeBId);
    if (!rcA) throw new Error(`Registered runtime ${payload.runtimeAId} not found`);
    if (!rcB) throw new Error(`Registered runtime ${payload.runtimeBId} not found`);

    if (payload.canRunAlongWith) {
      // Toggle ON: add peer displayName if not present
      if (!rcA.canRunAlongWith.some((n) => n.toLowerCase() === rcB.displayName.toLowerCase())) {
        rcA.canRunAlongWith = [...rcA.canRunAlongWith, rcB.displayName];
      }
      if (!rcB.canRunAlongWith.some((n) => n.toLowerCase() === rcA.displayName.toLowerCase())) {
        rcB.canRunAlongWith = [...rcB.canRunAlongWith, rcA.displayName];
      }
    } else {
      // Toggle OFF: remove peer displayName/image
      rcA.canRunAlongWith = rcA.canRunAlongWith.filter(
        (n) => n.toLowerCase() !== rcB.displayName.toLowerCase() && n.toLowerCase() !== rcB.image.toLowerCase(),
      );
      rcB.canRunAlongWith = rcB.canRunAlongWith.filter(
        (n) => n.toLowerCase() !== rcA.displayName.toLowerCase() && n.toLowerCase() !== rcA.image.toLowerCase(),
      );
    }

    return {
      a: { ...rcA, discoveredModels: rcA.discoveredModels.map((m) => ({ ...m })) },
      b: { ...rcB, discoveredModels: rcB.discoveredModels.map((m) => ({ ...m })) },
    };
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
  async listAgentScripts(agentName: string) {
    await delay(rand(80, 200));
    const agent = AGENTS.find((a) => a.name === agentName);
    return (agent?.scripts ?? []).map((s) => ({ ...s }));
  },
  async listAvailableScripts(agentName: string) {
    await delay(rand(80, 200));
    return (AGENT_AVAILABLE_SCRIPTS[agentName] ?? []).map((s) => ({ ...s }));
  },
  async runBenchmark(modelId: string, opts?: { promptId?: string }) {
    await delay(rand(200, 500));
    const model = models.find((m) => m.id === modelId);
    if (!model) throw new Error(`Model ${modelId} not found`);
    const prompt = opts?.promptId
      ? PROMPTS.find((p) => p.id === opts.promptId)
      : PROMPTS.find((p) => p.isDefault);
    const b = bench(
      modelId,
      model.name,
      30 + Math.random() * 45,
      90 + Math.random() * 160,
      384,
    );
    if (prompt) {
      b.prompt = prompt.text;
      b.promptId = prompt.id;
      b.promptName = prompt.name;
      b.promptVersion = prompt.currentVersion ?? 1;
    }
    benchmarks.unshift(b);
    return b;
  },
  async listBenchmarks(modelId?: string) {
    await delay(rand(80, 200));
    let result = benchmarks.map((b) => ({ ...b }));
    if (modelId) {
      result = result.filter((b) => b.modelId === modelId);
    }
    return result;
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
      processing: QUEUE_SNAPSHOT.processing?.map((p) => ({ ...p })),
      waiting: QUEUE_SNAPSHOT.waiting.map((w) => ({ ...w })),
      recentCompleted: QUEUE_SNAPSHOT.recentCompleted.map((r) => ({ ...r })),
      activeTransitions: QUEUE_SNAPSHOT.activeTransitions.map((t) => ({ ...t })),
    };
  },
  async cancelQueueItem(_itemId: string) {
    await delay(rand(20, 60));
    // Mock: just return void (success)
  },
  async releaseTargetHold(_targetId: string) {
    await delay(rand(20, 60));
    // Mock: clear any conversation holds on this target
    for (const w of QUEUE_SNAPSHOT.waiting) {
      if (w.heldByConversation && (w.targetId ?? "host") === _targetId) {
        w.heldByConversation = null;
      }
    }
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

  async createPrompt(input: { name: string; text: string; maxTokens?: number }) {
    await delay(rand(80, 200));
    if (!input.name.trim() || !input.text.trim()) {
      throw new Error("Name and text are required");
    }
    const prompt: Prompt = {
      id: id(),
      name: input.name.trim(),
      text: input.text.trim(),
      maxTokens: input.maxTokens ?? 256,
      createdAt: new Date().toISOString(),
      updatedAt: new Date().toISOString(),
    };
    PROMPTS.push(prompt);
    return { ...prompt };
  },

  async updatePrompt(promptId: string, input: { name: string; text: string; maxTokens?: number }) {
    await delay(rand(80, 200));
    if (!input.name.trim() || !input.text.trim()) {
      throw new Error("Name and text are required");
    }
    const prompt = PROMPTS.find((p) => p.id === promptId);
    if (!prompt) throw new Error(`Prompt ${promptId} not found`);
    const textChanged = prompt.text !== input.text.trim();
    prompt.name = input.name.trim();
    prompt.text = input.text.trim();
    if (input.maxTokens !== undefined) {
      prompt.maxTokens = input.maxTokens;
    }
    if (textChanged) {
      prompt.currentVersion = (prompt.currentVersion ?? 1) + 1;
    }
    prompt.updatedAt = new Date().toISOString();
    return { ...prompt };
  },

  async deletePrompt(promptId: string) {
    await delay(rand(60, 150));
    const idx = PROMPTS.findIndex((p) => p.id === promptId);
    if (idx === -1) throw new Error(`Prompt ${promptId} not found`);
    PROMPTS.splice(idx, 1);
  },

  async setDefaultPrompt(promptId: string) {
    await delay(rand(60, 150));
    const prompt = PROMPTS.find((p) => p.id === promptId);
    if (!prompt) throw new Error(`Prompt ${promptId} not found`);
    for (const p of PROMPTS) {
      p.isDefault = false;
    }
    prompt.isDefault = true;
    prompt.updatedAt = new Date().toISOString();
    return { ...prompt };
  },

  async listPromptVersions(promptId: string) {
    await delay(rand(60, 150));
    const prompt = PROMPTS.find((p) => p.id === promptId);
    if (!prompt) throw new Error(`Prompt ${promptId} not found`);
    return PROMPT_VERSIONS
      .filter((v) => v.promptId === promptId)
      .sort((a, b) => b.version - a.version)
      .map((v) => ({ ...v }));
  },

  async getPromptVersion(promptId: string, version: number) {
    await delay(rand(60, 150));
    const prompt = PROMPTS.find((p) => p.id === promptId);
    if (!prompt) throw new Error(`Prompt ${promptId} not found`);
    const v = PROMPT_VERSIONS.find(
      (pv) => pv.promptId === promptId && pv.version === version,
    );
    if (!v) throw new Error(`Version ${version} not found for prompt ${promptId}`);
    return { ...v };
  },

  async rollbackPrompt(promptId: string, version: number) {
    await delay(rand(80, 200));
    const prompt = PROMPTS.find((p) => p.id === promptId);
    if (!prompt) throw new Error(`Prompt ${promptId} not found`);
    const v = PROMPT_VERSIONS.find(
      (pv) => pv.promptId === promptId && pv.version === version,
    );
    if (!v) throw new Error(`Version ${version} not found for prompt ${promptId}`);

    // Restore text from the target version
    prompt.text = v.text;
    prompt.currentVersion = (prompt.currentVersion ?? 1) + 1;
    prompt.updatedAt = new Date().toISOString();

    // Create a new version record for the rollback (audit trail)
    const newVersion: PromptVersion = {
      id: `pv-${promptId}-${prompt.currentVersion}`,
      promptId,
      version: prompt.currentVersion,
      text: v.text,
      createdAt: new Date().toISOString(),
    };
    PROMPT_VERSIONS.push(newVersion);

    return { ...prompt };
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

  // ── Auth ─────────────────────────────────────────────────────
  async login(username: string, _password: string) {
    await delay(rand(80, 200));
    if (!username) throw new Error("Invalid username or password");
    return { username, isTempPassword: false };
  },

  async logout() {
    await delay(rand(30, 80));
  },

  async getMe() {
    await delay(rand(30, 80));
    // Default: no user logged in (will throw 401 from backend, caught by auth context)
    throw new Error("Not authenticated");
  },

  async changePassword(_currentPassword: string, _newPassword: string) {
    await delay(rand(80, 200));
  },

  // ── User Management ──────────────────────────────────────────
  async listUsers() {
    await delay(rand(80, 200));
    return [...MOCK_USERS];
  },

  async createUser(username: string, password: string) {
    await delay(rand(100, 200));
    if (!username.trim()) throw new Error("Username is required.");
    if (password.length < 6) throw new Error("Password must be at least 6 characters.");
    if (MOCK_USERS.some((u) => u.username === username.trim())) {
      throw new Error("Username already exists.");
    }
    const user: User = {
      id: id(),
      username: username.trim(),
      isTempPassword: true,
    };
    MOCK_USERS.push(user);
    return { ...user };
  },

  async deleteUser(userId: string) {
    await delay(rand(60, 150));
    const idx = MOCK_USERS.findIndex((u) => u.id === userId);
    if (idx === -1) throw new Error(`User ${userId} not found`);
    MOCK_USERS.splice(idx, 1);
  },

  async resetPassword(userId: string, _newPassword: string) {
    await delay(rand(80, 200));
    const user = MOCK_USERS.find((u) => u.id === userId);
    if (!user) throw new Error(`User ${userId} not found`);
    user.isTempPassword = true;
  },

  // ── API Keys ─────────────────────────────────────────────────
  async createApiKey(name: string) {
    await delay(rand(80, 180));
    if (!name.trim()) {
      throw new Error("Name is required.");
    }
    const scope: ApiKeyItem["scope"] = "inference";
    const secret = `sk_${randomSecret()}`;
    const created: ApiKeyCreateResponse = {
      id: id(),
      name: name.trim(),
      keyPrefix: secret.slice(0, 11),
      scope,
      isActive: true,
      createdAt: new Date().toISOString(),
      lastUsedAt: null,
      secret,
    };
    API_KEYS.push(created);
    return { ...created };
  },

  async createAgentApiKey(name: string) {
    await delay(rand(80, 180));
    if (!name.trim()) {
      throw new Error("Name is required.");
    }
    const scope: ApiKeyItem["scope"] = "agent";
    const secret = `ak_${randomSecret()}`;
    const created: ApiKeyCreateResponse = {
      id: id(),
      name: name.trim(),
      keyPrefix: secret.slice(0, 11),
      scope,
      isActive: true,
      createdAt: new Date().toISOString(),
      lastUsedAt: null,
      secret,
    };
    API_KEYS.push(created);
    return { ...created };
  },

  async listApiKeys() {
    await delay(rand(60, 150));
    return API_KEYS.map((k) => ({ ...k }));
  },

  async getApiKey(id: string) {
    await delay(rand(60, 150));
    const key = API_KEYS.find((k) => k.id === id);
    if (!key) throw new Error(`API key ${id} not found.`);
    return { ...key };
  },

  async revokeApiKey(id: string) {
    await delay(rand(60, 150));
    const key = API_KEYS.find((k) => k.id === id);
    if (!key) throw new Error(`API key ${id} not found.`);
    key.isActive = false;
  },

  async rotateApiKey(id: string) {
    await delay(rand(80, 180));
    const key = API_KEYS.find((k) => k.id === id);
    if (!key) throw new Error(`API key ${id} not found.`);
    const secret = `sk_${randomSecret()}`;
    Object.assign(key, {
      keyPrefix: secret.slice(0, 11),
      isActive: true,
      lastUsedAt: null,
    });
    return { ...key, secret };
  },

  // ── Cloud Providers ──────────────────────────────────────────
  async listCloudProviders() {
    await delay(rand(80, 200));
    return CLOUD_PROVIDERS.map(({ apiKey: _, ...p }) => ({ ...p }));
  },

  async getCloudProvider(providerId: string) {
    await delay(rand(60, 150));
    const p = CLOUD_PROVIDERS.find((x) => x.id === providerId);
    if (!p) throw new Error(`Cloud provider ${providerId} not found`);
    const { apiKey: _, ...rest } = p;
    return { ...rest, baseUrlFull: p.baseUrl };
  },

  async createCloudProvider(data: { name: string; baseUrl: string; apiKey: string }) {
    await delay(rand(100, 300));
    if (!data.name.trim()) throw new Error("Name is required.");
    if (CLOUD_PROVIDERS.some((p) => p.name === data.name.trim())) {
      throw new Error("Provider name already exists.");
    }
    const hint = data.apiKey.length > 16
      ? data.apiKey.slice(0, 8) + "…" + data.apiKey.slice(-4)
      : data.apiKey.slice(0, 4) + "…" + data.apiKey.slice(-4);
    const provider: CloudProvider & { apiKey?: string } = {
      id: `cp-${++nextId}`,
      name: data.name.trim(),
      baseUrl: data.baseUrl.trim().replace(/\/+$/, ""),
      apiKeyHint: hint,
      apiKey: data.apiKey,
      modelCount: 0,
      createdAt: new Date().toISOString(),
      updatedAt: new Date().toISOString(),
    };
    CLOUD_PROVIDERS.push(provider);
    const { apiKey: _, ...rest } = provider;
    return { ...rest, baseUrlFull: provider.baseUrl };
  },

  async updateCloudProvider(providerId: string, data: { baseUrl: string; apiKey?: string | null; apiKeyHint?: string | null }) {
    await delay(rand(80, 200));
    const p = CLOUD_PROVIDERS.find((x) => x.id === providerId);
    if (!p) throw new Error(`Cloud provider ${providerId} not found`);
    p.baseUrl = data.baseUrl.trim().replace(/\/+$/, "");
    if (data.apiKey) {
      p.apiKey = data.apiKey;
      p.apiKeyHint = data.apiKey.length > 16
        ? data.apiKey.slice(0, 8) + "…" + data.apiKey.slice(-4)
        : data.apiKey.slice(0, 4) + "…" + data.apiKey.slice(-4);
    }
    p.updatedAt = new Date().toISOString();
    const { apiKey: _, ...rest } = p;
    return { ...rest, baseUrlFull: p.baseUrl };
  },

  async deleteCloudProvider(providerId: string) {
    await delay(rand(60, 150));
    const idx = CLOUD_PROVIDERS.findIndex((x) => x.id === providerId);
    if (idx === -1) throw new Error(`Cloud provider ${providerId} not found`);
    CLOUD_PROVIDERS.splice(idx, 1);
  },

  async fetchCloudProviderModels(providerId: string) {
    await delay(rand(300, 800));
    const p = CLOUD_PROVIDERS.find((x) => x.id === providerId);
    if (!p) throw new Error(`Cloud provider ${providerId} not found`);
    // Mock: return fake model list based on provider name
    const modelMap: Record<string, string[]> = {
      openai: ["gpt-4o", "gpt-4o-mini", "o1-preview", "o1-mini"],
      anthropic: ["claude-sonnet-4-20250514", "claude-3-5-haiku-20241022", "claude-3-opus-20240229"],
    };
    const modelIds = modelMap[p.name] ?? ["model-1", "model-2"];
    p.modelCount = modelIds.length;
    p.updatedAt = new Date().toISOString();
    return { modelIds };
  },

};
