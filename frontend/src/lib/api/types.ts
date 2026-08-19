// ─── Model Registry ───────────────────────────────────────────────

export type ModelStatus = "ready" | "validating" | "invalid" | "deprecated";

export interface BenchmarkResult {
  tokensPerSec: number;
  latencyMs: number;
  timestamp: string;
}

export interface Model {
  id: string;
  name: string;
  family: string;
  parameterSize: string;
  quantization: string;
  status: ModelStatus;
  lastBenchmark: BenchmarkResult | null;
  contextWindow: number;
  containerImage: string;
  createdAt: string;
  updatedAt: string;
}

// ─── Fleet / Containers ───────────────────────────────────────────

export type ContainerStatus =
  | "running"
  | "starting"
  | "stopping"
  | "stopped"
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
