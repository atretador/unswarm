// ─── Model Registry ───────────────────────────────────────────────

export type ModelStatus = "ready" | "validating" | "invalid" | "deprecated";

export type BenchmarkStatus = "completed" | "error";

export interface BenchmarkResult {
  id: string;
  modelId: string;
  modelName: string;
  prompt: string;
  tokensPerSec: number;
  latencyMs: number;
  tokensGenerated: number;
  timestamp: string;
  status: BenchmarkStatus;
  errorMessage?: string | null;
}

/**
 * Summary benchmark attached to a model (backend ModelResponse.LastBenchmark).
 * The wire contract only carries these three fields; tokensGenerated may be
 * present when the frontend has the full run data, but degrades gracefully.
 */
export interface LastBenchmarkResult {
  tokensPerSec: number;
  latencyMs: number;
  timestamp: string;
  /** Tokens generated in the run. Optional — not part of the backend wire contract. */
  tokensGenerated?: number;
}

export interface Model {
  id: string;
  name: string;
  family: string;
  parameterSize: string;
  quantization: string;
  status: ModelStatus;
  lastBenchmark: LastBenchmarkResult | null;
  contextWindow: number;
  containerImage: string;
  sourceContainerId: string | null;
  createdAt: string;
  updatedAt: string;
}

// ─── Container Registration ───────────────────────────────────────

export type ContainerRegistrationStatus =
  | "registered"
  | "starting"
  | "healthy"
  | "discovering"
  | "ready"
  | "error";

export interface RegisterContainerPayload {
  displayName: string;
  image: string;   // container name (pre-provisioned)
  containerPort: number;
  agent?: string;   // "host" or agent name, default "host"
  canRunAlongWith?: string[];   // same-agent container names this may run with
  extraLabels?: Record<string, string>;
}

export interface RegisteredContainer {
  id: string;
  displayName: string;
  image: string;
  containerPort: number;
  agent: string;
  canRunAlongWith: string[];
  status: ContainerRegistrationStatus;
  runtimeContainerId: string | null;
  mappedPort: number | null;
  errorMessage: string | null;
  createdAt: string;
  lastDiscoveredAt: string | null;
  discoveredModels: Model[];
}

// ─── Fleet / Containers ───────────────────────────────────────────

export type ContainerStatus =
  | "running"
  | "starting"
  | "stopping"
  | "stopped"
  | "created"
  | "restarting"
  | "dead"
  | "error";

export interface Container {
  id: string;
  modelId: string;
  modelName: string;
  status: ContainerStatus;
  port: number | null;
  pid: number | null;
  memoryMb: number;
  cpuPercent: number;
  uptime: number; // seconds
  lastHealthCheck: string | null;
  errorMessage: string | null;
  createdAt: string;
}

// ─── Agents ──────────────────────────────────────────────────────

export interface AgentContainerStatus {
  containerId: string;
  modelName: string | null;
  status: string;
  port: number | null;
}

export interface Agent {
  name: string;
  connectionId: string | null;
  connectedAt: string | null;
  lastSeen: string | null;
  isConnected: boolean;
  dockerSocket: string | null;
  version: string | null;
  hostname: string | null;
  osPlatform: string | null;
  gpuInfo: string | null;
  totalMemoryMb: number;
  cpuCores: number;
  containers: AgentContainerStatus[];
}

// ─── Queue ────────────────────────────────────────────────────────

export type QueueItemStatus = "waiting" | "processing" | "completed" | "failed";

export interface QueueItem {
  id: string;
  modelRequested: string;
  modelAssigned: string | null;
  status: QueueItemStatus;
  priority: number;
  tokensRequested: number;
  tokensGenerated: number;
  elapsedMs: number;
  waitMs: number;
  createdAt: string;
}

export interface QueueSnapshot {
  currentSlot: QueueItem | null;
  waiting: QueueItem[];
  recentCompleted: QueueItem[];
  activeTransitions: ModelTransition[];
}

export interface ModelTransition {
  id: string;
  fromModel: string;
  toModel: string;
  status: "draining" | "switching" | "starting" | "complete";
  startedAt: string;
  estimatedCompletion: string | null;
}

// ─── Stats / Dashboard ────────────────────────────────────────────

export interface StatsSummary {
  totalRequests: number;
  activeRequests: number;
  avgLatencyMs: number;
  totalTokensProcessed: number;
  uptimeSeconds: number;
  modelsLoaded: number;
  containersRunning: number;
  queueDepth: number;
  requestsPerMinute: number[];
  errorsLast24h: number;
  tokensPerSecond: number[];
  switchCount: number;
  lastSwitchMs: number;
  avgSwitchMs: number;
}

// ─── Logs ─────────────────────────────────────────────────────────

export type LogLevel = "info" | "warn" | "error" | "debug";

export interface LogEntry {
  id: string;
  timestamp: string;
  level: LogLevel;
  source: string; // container id or "scheduler" / "proxy"
  message: string;
  metadata?: Record<string, unknown>;
}

// ─── Settings ─────────────────────────────────────────────────────

export interface Settings {
  maxConcurrentModels: number;
  defaultModel: string | null;
  requestTimeout: number;
  healthCheckInterval: number;
  autoShutdownIdle: boolean;
  idleTimeout: number;
  logRetention: number;
  enableBenchmarking: boolean;
  priorityMode: "fifo" | "priority";
  batchDrain: boolean;
  lazyStop: boolean;
  maxQueueDepth: number;
}

// ─── Prompt Library ────────────────────────────────────────────────

export interface Prompt {
  id: string;
  name: string;
  text: string;
  createdAt: string;
  updatedAt: string;
}
