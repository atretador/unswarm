// ─── Model Registry ───────────────────────────────────────────────

export type ModelStatus = "ready" | "validating" | "invalid" | "deprecated";

export type BenchmarkStatus = "completed" | "error";

export interface BenchmarkResult {
  id: string;
  modelId: string;
  modelName: string;
  prompt: string;
  promptId?: string | null;
  promptName?: string | null;
  promptVersion?: number | null;
  tokensPerSec: number;
  latencyMs: number;
  tokensGenerated: number;
  timestamp: string;
  status: BenchmarkStatus;
  errorMessage?: string | null;
  /** Raw LLM output text. Null/absent when the run errored or text wasn't captured. */
  response?: string | null;
  /** Model reasoning/thinking text. Null/absent when the model emitted none or it wasn't captured. */
  reasoning?: string | null;
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
  promptName?: string | null;
  promptVersion?: number | null;
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
  sourceRuntimeId: string | null;
  sourceRuntimeName: string | null;
  sourceRuntimeAgent: string | null;
  createdAt: string;
  updatedAt: string;
  /** Origin of the model: "swarm" for self-hosted, "cloud" for third-party API. */
  origin?: string;
  /** Provider name for cloud models (e.g. "openai"). Null for swarm models. */
  providerName?: string | null;
}

// ─── Container Registration ───────────────────────────────────────

export type ContainerRegistrationStatus =
  | "registered"
  | "starting"
  | "healthy"
  | "discovering"
  | "ready"
  | "error";

export interface RegisterRuntimePayload {
  displayName: string;
  image: string;   // container name (pre-provisioned)
  containerPort: number;
  mappedPort?: number; // host-mapped port (auto-resolved via Docker inspect when omitted)
  agent?: string;   // "host" or agent name, default "host"
  canRunAlongWith?: string[];   // same-agent container names this may run with
  extraLabels?: Record<string, string>;
  runtimeKind?: 'container' | 'script';
  launcherPath?: string;
}

/** Full-replacement payload for updating a runtime's concurrency list. */
export interface UpdateRuntimeConcurrencyPayload {
  canRunAlongWith: string[];
  maxConcurrentInferences?: number;
}

/** Payload for updating a registered runtime's display name, port, and concurrency. */
export interface UpdateRuntimePayload {
  displayName?: string;
  containerPort?: number;
  mappedPort?: number;
  maxConcurrentInferences?: number;
}

/** Atomically toggle concurrency between two runtimes. */
export interface ToggleConcurrencyPayload {
  runtimeAId: string;
  runtimeBId: string;
  canRunAlongWith: boolean;
}

/** Response from the atomic toggle endpoint. */
export interface ToggleConcurrencyResponse {
  a: RegisteredRuntime;
  b: RegisteredRuntime;
}

export interface RegisteredRuntime {
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
  runtimeKind?: 'container' | 'script';
  launcherPath?: string | null;
  runtimeProcessId?: number | null;
  maxConcurrentInferences: number;
}

// ─── Swarm / Containers ───────────────────────────────────────────

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

export interface AgentScriptStatus {
  path: string;
  pid: number;
  status: string; // "running" | "stopped"
  port: number;
  startTime: number; // unix ms
}

export interface AgentAvailableScript {
  path: string;
  name: string;
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
  scripts: AgentScriptStatus[];
}

// ─── Queue ────────────────────────────────────────────────────────

export type QueueItemStatus = "waiting" | "processing" | "completed" | "failed";

export interface QueueItem {
  id: string;
  modelRequested: string;
  modelAssigned: string | null;
  targetId: string | null;
  /** Registered runtime id of the lane serving this item (null until routed). */
  runtimeId: string | null;
  /** In-flight runtime ids currently blocking this waiting item (empty = could start next). */
  blockedByRuntimeIds: string[];
  /**
   * Present when this item is held back because an active tool-call
   * conversation occupies its runtime (not by active inference work).
   * `blockedByRuntimeIds` may still be non-empty alongside this.
   */
  heldByConversation?: {
    model: string;
    runtimeId: string;
    requestCount: number;
    /** ISO DateTimeOffset when the conversation hold lapses. */
    holdExpiresAt: string;
  } | null;
  status: QueueItemStatus;
  priority: number;
  tokensRequested: number;
  tokensGenerated: number;
  promptTokensPerSec: number;
  generationTokensPerSec: number;
  elapsedMs: number;
  waitMs: number;
  createdAt: string;
  errorMessage?: string | null;
}

export interface QueueSnapshot {
  /**
   * All in-flight items across every runtime lane.
   * Legacy backends may omit this — fall back to [currentSlot] when absent.
   */
  processing?: QueueItem[];
  /** Legacy compat alias: oldest processing item. May be absent on newer payloads. */
  currentSlot?: QueueItem | null;
  waiting: QueueItem[];
  recentCompleted: QueueItem[];
  activeTransitions: ModelTransition[];
  /** Total skip budget consumed across all lanes. */
  skipsUsed: number;
  /** Remaining skip budget (0 when the skip feature is disabled). */
  skipsRemaining: number;
}

export interface ModelTransition {
  id: string;
  /** Model being replaced, or null when nothing is stopped/replaced. */
  fromModel: string | null;
  toModel: string;
  /** All runtimes this transition stops, each with its resident model ("going down"). */
  stopping: Array<{ runtimeId: string; model: string }>;
  /** Registered runtime id whose lane performed this switch. */
  runtimeId?: string | null;
  status: "draining" | "switching" | "starting" | "complete";
  startedAt: string;
  estimatedCompletion: string | null;
}

export interface ModelTransition {
  id: string;
  fromModel: string | null;
  toModel: string;
  /** All runtimes this transition stops, each with its resident model ("going down"). */
  stopping: Array<{ runtimeId: string; model: string }>;
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
  /** Prompt tokens served from KV cache (best-effort; 0 when engine doesn't report it). */
  totalPromptTokensCached: number;
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
  parallelSlotSkipLimit: number;
  enableParallelSlotSkip: boolean;
  queueStepsTillReset: number;
  /**
   * When true, a recently-active tool-call conversation holds its runtime
   * against eviction by incompatible models (conversation affinity).
   */
  enableConversationAffinity: boolean;
  /** How long a conversation keeps its hold after its last request (seconds). */
  conversationDwellSeconds: number;
  /** When true, strip "cloud/" or "managed/" prefixes from model display names. */
  hideOriginPrefix: boolean;
  /** Map of agent names to custom display names (e.g. { "host": "My Workstation" }). */
  agentDisplayNames: Record<string, string>;
  /**
   * Max wait time for a runtime to become healthy during startup (seconds, 10–600).
   */
  healthCheckTimeoutSeconds: number;
  /**
   * How long raw usage records are kept before purging (days, ≥0).
   * Optional — older backends may not expose it yet.
   */
  usageRetentionDays?: number;
  /**
   * Server-side per-provider monthly budgets as a JSON string:
   * `{"provider":{"tokenBudget":number,"costBudget":number}}`.
   * Optional — older backends may not expose it yet.
   */
  providerBudgetsJson?: string;

  /** Number of retry attempts for server errors (5xx) before falling back to the next router model. */
  routerRetryAttempts: number;
  /** Delay between retry attempts in milliseconds. */
  routerRetryDelayMs: number;
}

// ─── Prompt Library ────────────────────────────────────────────────

export interface Prompt {
  id: string;
  name: string;
  text: string;
  /** Generation token cap applied when running benchmarks with this prompt (16–32768). */
  maxTokens: number;
  isDefault?: boolean;
  currentVersion?: number;
  createdAt: string;
  updatedAt: string;
}

/**
 * Payload for creating/updating a prompt.
 * `maxTokens` is optional — the backend applies its default (256) when omitted.
 */
export interface PromptInput {
  name: string;
  text: string;
  maxTokens?: number;
}

export interface PromptVersion {
  id: string;
  promptId: string;
  version: number;
  text: string;
  createdAt: string;
}

// ─── Users ────────────────────────────────────────────────────────
export interface User {
  id: string;
  username: string;
  isTempPassword: boolean;
}

// ─── API Keys ──────────────────────────────────────────────────────
// Managed keys authenticate to the inference proxy (/v1) and the agent
// channel (/api/agents + /ws/agent). They are NOT login credentials — the
// two auth surfaces are strictly separate. Login cookies never carry a scope.
export type ApiKeyScope = "inference" | "agent";

export interface ApiKeyItem {
  id: string;
  name: string;
  /** Short human-readable marker of the key. Never the full secret. */
  keyPrefix: string;
  scope: ApiKeyScope;
  isActive: boolean;
  createdAt: string;
  lastUsedAt: string | null;
}

/** Returned exactly once at create/rotate. Carries the raw `secret`. */
export interface ApiKeyCreateResponse extends ApiKeyItem {
  secret: string;
}

// ─── API-key access control (provider/model allow-lists) ───────────

/** One entry of GET /api/provider-model-catalog. */
export interface ProviderModelCatalogEntry {
  name: string;
  kind: "cloud" | "local" | "router";
  models: string[];
}

/**
 * Per-key access grants, saved via PUT /api/apikeys/{id}/access.
 * Empty `providers` + empty `models` = unrestricted (full access).
 */
export interface ApiKeyAccess {
  providers: string[];
  models: string[];
}

/** Aggregate usage for a single key over a time window. */
export interface ApiKeyUsageResponse {
  totals: {
    requestCount: number;
    promptTokens: number;
    completionTokens: number;
    cachedTokens: number;
  };
  /** Per-model breakdown; optional so partial backends degrade gracefully. */
  models?: Array<{
    model: string;
    provider?: string;
    requestCount: number;
    promptTokens: number;
    completionTokens: number;
    cachedTokens: number;
  }>;
}

// ─── Cloud Providers ──────────────────────────────────────────────

/** Auth type for cloud providers: 0 = API Key, 1 = ChatGPT Subscription (OAuth). */
export type CloudProviderAuthType = 0 | 1;

export interface CloudProvider {
  id: string;
  name: string;
  baseUrl: string;
  apiKeyHint: string;
  authType: CloudProviderAuthType;
  modelCount: number;
  createdAt: string;
  updatedAt: string;
}

export interface CloudProviderRead extends CloudProvider {
  baseUrlFull: string;
  chatgptAccountId: string | null;
  tokenExpiresAt: string | null;
}

export interface CloudProviderInput {
  name: string;
  baseUrl: string;
  apiKey: string;
  authType: CloudProviderAuthType;
}

export interface CloudProviderUpdateInput {
  baseUrl: string;
  apiKey?: string | null;
  apiKeyHint?: string | null;
}

export interface FetchModelsResult {
  modelIds: string[];
}

export interface OAuthStartResult {
  deviceAuthId: string;
  userCode: string;
  verificationUrl: string;
  interval: number;
}

export interface OAuthPollResult {
  status?: string;
  chatgptAccountId?: string;
  error?: string;
}

export interface OAuthRefreshResult {
  success: boolean;
  tokenExpiresAt: string;
}

// ─── Metrics ──────────────────────────────────────────────────────

/** Time-window bounds shared by every analytics endpoint. */
export interface MetricsWindow {
  from?: string;
  to?: string;
}

/**
 * Shared provider/model multi-select filters accepted by the analytics
 * endpoints. Values within a dimension are ANY-of (exact match); the two
 * dimensions combine with AND. Empty/undefined = unfiltered.
 */
export interface MetricsFilters {
  providers?: string[];
  models?: string[];
}

export type MetricsAnalyticsParams = MetricsWindow & MetricsFilters;

export interface MetricsUsageParams extends MetricsAnalyticsParams {
  page?: number;
  pageSize?: number;
}

export interface MetricsSummaryParams extends MetricsAnalyticsParams {
  granularity?: "hour" | "day" | "week" | "month";
  /** Split each bucket per provider or model (comparison series). */
  groupBy?: "provider" | "model";
}

export interface UsagePageResponse {
  items: UsageRecordResponse[];
  total: number;
  page: number;
  pageSize: number;
}

export interface MetricsTimeBucket {
  bucketStart: string; // ISO DateTimeOffset
  bucketEnd: string;
  /** Group identity when the request used groupBy=provider|model; null otherwise. */
  group?: string | null;
  requestCount: number;
  streamingRequests?: number;
  promptTokens: number;
  completionTokens: number;
  cachedTokens: number;
  avgLatencyMs: number;
}

export interface ModelUsageSummary {
  provider: string;
  model: string;
  requestCount: number;
  streamingRequests?: number;
  promptTokens: number;
  completionTokens: number;
  cachedTokens: number;
  avgLatencyMs: number;
  p50LatencyMs?: number;
  p95LatencyMs?: number;
  p99LatencyMs?: number;
  maxLatencyMs?: number;
}

export interface ProviderUsageSummary {
  provider: string;
  requestCount: number;
  streamingRequests?: number;
  promptTokens: number;
  completionTokens: number;
  cachedTokens: number;
}

export interface UsageTotalsResponse {
  from: string;
  to: string;
  totalRequests: number;
  totalStreamingRequests?: number;
  totalPromptTokens: number;
  totalCompletionTokens: number;
  totalCachedTokens: number;
  avgLatencyMs: number;
  p50LatencyMs?: number;
  p95LatencyMs?: number;
  p99LatencyMs?: number;
  maxLatencyMs?: number;
}

/** One bucket of the latency-distribution histogram. */
export interface MetricsLatencyBand {
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
  kind: "cloud" | "local" | "router";
}

export interface UsageRecordResponse {
  id: string;
  timestamp: string;
  provider: string;
  model: string;
  promptTokens: number;
  completionTokens: number;
  cachedTokens: number;
  isStreaming: boolean;
  elapsedMs: number;
  /** Name of the API key that authenticated the request, when known. */
  apiKeyName?: string | null;
}

// ─── Router Profiles ─────────────────────────────────────────────

export interface RouterProfileEntry {
  modelId: string;
  priority: number;
  isEnabled: boolean;
}

export interface RouterProfile {
  id: string;
  name: string;
  mode: "Auto" | "Manual";
  entries: RouterProfileEntry[];
  activeModelId: string | null;
  createdAt: string;
  updatedAt: string;
}

export interface RouterProfileEntryInput {
  modelId: string;
  priority: number;
  isEnabled: boolean;
}

export interface RouterProfileInput {
  name: string;
  mode: "Auto" | "Manual";
  entries: RouterProfileEntryInput[];
}

// ─── Test Chat (direct model testing through the proxy) ───────────

export type ChatRole = "system" | "user" | "assistant";

export interface ChatMessage {
  role: ChatRole;
  content: string;
}

/** Options for one sendTestChat turn. */
export interface SendTestChatOptions {
  /** Optional system prompt, prepended server-side as the first message. */
  system?: string;
  maxTokens?: number;
  temperature?: number;
  /** Disable streaming and wait for the full completion (default: stream). */
  stream?: boolean;
  /** Abort signal wired to the drawer's Stop button. */
  signal?: AbortSignal;
  /**
   * Streamed deltas: `content` is answer text, `reasoning` is thinking text.
   * Called zero or more times before the promise resolves; never after.
   */
  onDelta?: (delta: { content?: string; reasoning?: string }) => void;
}

/** Result of one test-chat turn (assistant reply + observed stats). */
export interface TestChatTurnResult {
  content: string;
  /** Thinking/reasoning text when the model emitted any. */
  reasoning: string | null;
  latencyMs: number;
  promptTokens: number | null;
  completionTokens: number | null;
}
