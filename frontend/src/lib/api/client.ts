import type {
  Container,
  LogEntry,
  Model,
  QueueSnapshot,
  Settings,
  StatsSummary,
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

  // Fleet
  listContainers(): Promise<Container[]>;
  startContainer(modelId: string): Promise<Container>;
  stopContainer(containerId: string): Promise<void>;
  restartContainer(containerId: string): Promise<Container>;

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

  // Settings
  getSettings(): Promise<Settings>;
  updateSettings(data: Partial<Settings>): Promise<Settings>;
}
