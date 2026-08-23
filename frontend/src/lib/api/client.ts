import type {
  Agent,
  AgentAvailableScript,
  ApiKeyCreateResponse,
  ApiKeyItem,
  AgentScriptStatus,
  BenchmarkResult,
  CloudProvider,
  CloudProviderInput,
  CloudProviderRead,
  CloudProviderUpdateInput,
  Container,
  FetchModelsResult,
  LogEntry,
  MetricsTimeBucket,
  Model,
  ModelUsageSummary,
  Prompt,
  PromptInput,
  PromptVersion,
  ProviderUsageSummary,
  QueueSnapshot,
  RegisterRuntimePayload,
  RegisteredRuntime,
  Settings,
  StatsSummary,
  ToggleConcurrencyPayload,
  ToggleConcurrencyResponse,
  UpdateRuntimeConcurrencyPayload,
  UpdateRuntimePayload,
  UsageRecordResponse,
  UsageTotalsResponse,
  User,
} from "./types";

/**
 * Typed client interface for the unswarm control-plane API.
 * Panels consume this; swap the adapter for real backend.
 */
export interface UnswarmClient {
  // Models
  listModels(): Promise<Model[]>;
  getModel(id: string): Promise<Model>;
  createModel(data: Omit<Model, "id" | "createdAt" | "updatedAt">): Promise<Model>;
  updateModel(id: string, data: Partial<Model>): Promise<Model>;
  deleteModel(id: string): Promise<void>;

  // Container Registration
  registerRuntime(data: RegisterRuntimePayload): Promise<RegisteredRuntime>;
  listRegisteredRuntimes(): Promise<RegisteredRuntime[]>;
  getRegisteredRuntime(id: string): Promise<RegisteredRuntime>;
  rediscoverRuntime(id: string): Promise<RegisteredRuntime>;
  /** Start the runtime container backing a registered runtime. */
  startRegisteredRuntime(id: string): Promise<RegisteredRuntime>;
  /** Stop a registered runtime (script or container) by registration id. */
  stopRegisteredRuntime(id: string): Promise<RegisteredRuntime>;
  deleteRuntime(id: string, deleteModels?: boolean): Promise<void>;

  /** Update a registered runtime's display name. */
  updateRuntime(id: string, payload: UpdateRuntimePayload): Promise<RegisteredRuntime>;

  /** Update the concurrency list for a registered runtime (full replacement). */
  updateRuntimeConcurrency(id: string, payload: UpdateRuntimeConcurrencyPayload): Promise<RegisteredRuntime>;

  /** Atomically toggle concurrency between two runtimes in a single DB transaction. */
  toggleRuntimeConcurrency(payload: ToggleConcurrencyPayload): Promise<ToggleConcurrencyResponse>;

  // Fleet
  listContainers(): Promise<Container[]>;
  startContainer(modelId: string): Promise<Container>;
  stopContainer(containerId: string): Promise<void>;
  restartContainer(containerId: string): Promise<Container>;

  /** Containers running on a specific agent (used by the manage-containers picker). */
  listAgentContainers(agentName: string): Promise<Container[]>;

  /** Available launcher scripts on an agent (from agent's scripts_dir). */
  listAgentScripts(agentName: string): Promise<AgentScriptStatus[]>;

  /** Launcher scripts available on a remote agent (queried live over WebSocket). */
  listAvailableScripts(agentName: string): Promise<AgentAvailableScript[]>;

  /** Run a benchmark against a model. Optional promptId resolves server-side. */
  runBenchmark(modelId: string, opts?: { promptId?: string }): Promise<BenchmarkResult>;

  /** Benchmark history, newest first (max 50). Optional modelId filters. */
  listBenchmarks(modelId?: string): Promise<BenchmarkResult[]>;

  // Agents
  listAgents(): Promise<Agent[]>;

  // Queue
  getQueueSnapshot(): Promise<QueueSnapshot>;
  cancelQueueItem(itemId: string): Promise<void>;
  /** Immediately clear conversation holds on a target so held items proceed. */
  releaseTargetHold(targetId: string): Promise<void>;

  // Stats
  getStats(): Promise<StatsSummary>;

  // Logs
  getLogs(opts?: {
    source?: string;
    level?: string;
    limit?: number;
    since?: string;
  }): Promise<LogEntry[]>;

  /**
   * Subscribe to a live stream of log entries. Returns an unsubscribe function.
   * Optional `onError` fires when the stream connection fails or drops.
   */
  subscribeLogs(
    callback: (entry: LogEntry) => void,
    onError?: () => void,
  ): () => void;

  // Prompt Library
  listPrompts(): Promise<Prompt[]>;
  createPrompt(input: PromptInput): Promise<Prompt>;
  updatePrompt(id: string, input: PromptInput): Promise<Prompt>;
  deletePrompt(id: string): Promise<void>;
  setDefaultPrompt(id: string): Promise<Prompt>;
  listPromptVersions(promptId: string): Promise<PromptVersion[]>;
  getPromptVersion(promptId: string, version: number): Promise<PromptVersion>;
  rollbackPrompt(promptId: string, version: number): Promise<Prompt>;

  // Settings
  getSettings(): Promise<Settings>;
  updateSettings(data: Partial<Settings>): Promise<Settings>;

  // Auth
  login(username: string, password: string): Promise<{ username: string; isTempPassword: boolean }>;
  logout(): Promise<void>;
  getMe(): Promise<{ username: string; isTempPassword: boolean }>;
  changePassword(currentPassword: string, newPassword: string): Promise<void>;

  // User Management
  listUsers(): Promise<User[]>;
  createUser(username: string, password: string): Promise<User>;
  deleteUser(id: string): Promise<void>;
  resetPassword(id: string, newPassword: string): Promise<void>;

  // API Keys — manage inference keys that authenticate to the /v1 proxy.
  // These are NOT login credentials.
  createApiKey(name: string): Promise<ApiKeyCreateResponse>;
  /** Create an agent-scoped API key (authenticates to the agent channel). */
  createAgentApiKey(name: string): Promise<ApiKeyCreateResponse>;
  listApiKeys(): Promise<ApiKeyItem[]>;
  getApiKey(id: string): Promise<ApiKeyItem>;
  revokeApiKey(id: string): Promise<void>;
  rotateApiKey(id: string): Promise<ApiKeyCreateResponse>;

  // Cloud Providers
  listCloudProviders(): Promise<CloudProvider[]>;
  getCloudProvider(id: string): Promise<CloudProviderRead>;
  createCloudProvider(data: CloudProviderInput): Promise<CloudProviderRead>;
  updateCloudProvider(id: string, data: CloudProviderUpdateInput): Promise<CloudProviderRead>;
  deleteCloudProvider(id: string): Promise<void>;
  fetchCloudProviderModels(id: string): Promise<FetchModelsResult>;
  testAndFetchModels(baseUrl: string, apiKey: string): Promise<FetchModelsResult>;

  // Metrics
  getMetricsUsage(opts?: {
    from?: string;
    to?: string;
    provider?: string;
    model?: string;
    page?: number;
    pageSize?: number;
  }): Promise<{ items: UsageRecordResponse[]; total: number; page: number; pageSize: number }>;
  getMetricsSummary(opts?: {
    from?: string;
    to?: string;
    granularity?: "hour" | "day" | "week" | "month";
    provider?: string;
    model?: string;
  }): Promise<MetricsTimeBucket[]>;
  getMetricsModels(opts?: {
    from?: string;
    to?: string;
    provider?: string;
    model?: string;
  }): Promise<ModelUsageSummary[]>;
  getMetricsProviders(opts?: {
    from?: string;
    to?: string;
  }): Promise<ProviderUsageSummary[]>;
  getMetricsTotals(opts?: {
    from?: string;
    to?: string;
    provider?: string;
    model?: string;
  }): Promise<UsageTotalsResponse>;
}
