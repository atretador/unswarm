import type {
  Agent,
  ApiKeyCreateResponse,
  ApiKeyItem,
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
  UpdateRuntimeConcurrencyPayload,
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
  deleteRuntime(id: string, deleteModels?: boolean): Promise<void>;

  /** Update the concurrency list for a registered runtime (full replacement). */
  updateRuntimeConcurrency(id: string, payload: UpdateRuntimeConcurrencyPayload): Promise<RegisteredRuntime>;

  // Fleet
  listContainers(): Promise<Container[]>;
  startContainer(modelId: string): Promise<Container>;
  stopContainer(containerId: string): Promise<void>;
  restartContainer(containerId: string): Promise<Container>;

  /** Containers running on a specific agent (used by the manage-containers picker). */
  listAgentContainers(agentName: string): Promise<Container[]>;

  /** Available launcher scripts on an agent (from agent's scripts_dir). */
  listAgentScripts(agentName: string): Promise<AgentScriptStatus[]>;

  /** Run a benchmark against a model. Optional promptId resolves server-side. */
  runBenchmark(modelId: string, opts?: { promptId?: string }): Promise<BenchmarkResult>;

  /** Benchmark history, newest first (max 50). Optional modelId filters. */
  listBenchmarks(modelId?: string): Promise<BenchmarkResult[]>;

  // Agents
  listAgents(): Promise<Agent[]>;

  // Queue
  getQueueSnapshot(): Promise<QueueSnapshot>;

  // Stats
  getStats(): Promise<StatsSummary>;

  // Logs
  getLogs(opts?: {
    source?: string;
    level?: string;
    limit?: number;
    since?: string;
  }): Promise<LogEntry[]>;

  /** Subscribe to a live stream of log entries. Returns an unsubscribe function. */
  subscribeLogs(callback: (entry: LogEntry) => void): () => void;

  // Prompt Library
  listPrompts(): Promise<Prompt[]>;
  createPrompt(input: { name: string; text: string }): Promise<Prompt>;
  updatePrompt(id: string, input: { name: string; text: string }): Promise<Prompt>;
  deletePrompt(id: string): Promise<void>;
  setDefaultPrompt(id: string): Promise<Prompt>;

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
  listApiKeys(): Promise<ApiKeyItem[]>;
  getApiKey(id: string): Promise<ApiKeyItem>;
  revokeApiKey(id: string): Promise<void>;
  rotateApiKey(id: string): Promise<ApiKeyCreateResponse>;
}
