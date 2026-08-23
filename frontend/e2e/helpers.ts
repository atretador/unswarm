/**
 * Shared E2E helpers: hermetic API mocking via Playwright route interception.
 *
 * The app boots by calling GET /api/auth/me (auth-context.tsx) and the queue
 * view polls GET /api/queue/snapshot every 2s plus GET /api/agents every 10s.
 * All three are intercepted here with canned payloads matching the real
 * backend contract (src/lib/api/types.ts). No src code is modified.
 */
import type { Page, Route } from "@playwright/test";
import type { Agent, QueueItem, QueueSnapshot } from "../src/lib/api/types";

const now = () => new Date().toISOString();

export function makeItem(overrides: Partial<QueueItem> & { id: string }): QueueItem {
  return {
    modelRequested: "llama-3.1-70b",
    modelAssigned: null,
    targetId: "host",
    runtimeId: null,
    blockedByRuntimeIds: [],
    status: "waiting",
    priority: 1,
    tokensRequested: 2048,
    tokensGenerated: 0,
    promptTokensPerSec: 0,
    generationTokensPerSec: 0,
    elapsedMs: 0,
    waitMs: 0,
    createdAt: now(),
    ...overrides,
  };
}

/** Minimal agent so the queue view renders an `agent:gpu-node-1` section. */
const AGENTS: Agent[] = [
  {
    name: "gpu-node-1",
    connectionId: "conn-1",
    connectedAt: now(),
    lastSeen: now(),
    isConnected: true,
    dockerSocket: null,
    version: "1.0.0",
    hostname: "gpu-node-1",
    osPlatform: "linux",
    gpuInfo: null,
    totalMemoryMb: 65536,
    cpuCores: 32,
    containers: [],
    scripts: [],
  },
];

/** Multi-lane snapshot: two concurrent lanes + one blocked waiting item + skip budget used. */
export function multiLaneSnapshot(): QueueSnapshot {
  return {
    processing: [
      makeItem({
        id: "q1",
        status: "processing",
        targetId: "host",
        runtimeId: "rt-host-main",
        modelAssigned: "llama-3.1-70b",
        tokensGenerated: 1247,
      }),
      makeItem({
        id: "q5",
        status: "processing",
        targetId: "agent:gpu-node-1",
        runtimeId: "rt-gpu-node-1-a",
        modelRequested: "mistral-large-2",
        modelAssigned: "mistral-large-2",
        tokensGenerated: 640,
      }),
    ],
    currentSlot: null,
    waiting: [
      makeItem({
        id: "q2",
        status: "waiting",
        targetId: "agent:gpu-node-1",
        modelRequested: "mistral-large-2",
        blockedByRuntimeIds: ["rt-gpu-node-1-a"],
        waitMs: 8200,
        priority: 2,
      }),
      makeItem({
        id: "q3",
        status: "waiting",
        targetId: "host",
        modelRequested: "llama-3.1-70b",
        priority: 3,
        waitMs: 5100,
      }),
    ],
    recentCompleted: [],
    activeTransitions: [],
    skipsUsed: 1,
    skipsRemaining: 2,
  };
}

/** Same shape but with the skip feature fully unused/disabled (budget zero). */
export function zeroBudgetSnapshot(): QueueSnapshot {
  const s = multiLaneSnapshot();
  return { ...s, skipsUsed: 0, skipsRemaining: 0 };
}

/** Terminal state: everything drained into recentCompleted. */
export function completedSnapshot(): QueueSnapshot {
  return {
    processing: [],
    currentSlot: null,
    waiting: [],
    recentCompleted: [
      makeItem({
        id: "q1",
        status: "completed",
        modelAssigned: "llama-3.1-70b",
        tokensGenerated: 4096,
        elapsedMs: 12400,
      }),
      makeItem({
        id: "q5",
        status: "completed",
        modelRequested: "mistral-large-2",
        modelAssigned: "mistral-large-2",
        tokensGenerated: 2048,
        elapsedMs: 9800,
      }),
    ],
    activeTransitions: [],
    skipsUsed: 1,
    skipsRemaining: 2,
  };
}

export interface QueueApiMock {
  /** Swap the payload served to subsequent /api/queue/snapshot polls. */
  setSnapshot(next: QueueSnapshot): void;
  /** How many times the snapshot endpoint has been served. */
  snapshotCount(): number;
}

/**
 * Install route handlers on a page. Register order matters: Playwright
 * evaluates handlers LIFO, so the catch-all goes in first and the specific
 * endpoints registered after it take precedence.
 */
export async function installApiMocks(
  page: Page,
  initialSnapshot: QueueSnapshot = multiLaneSnapshot(),
): Promise<QueueApiMock> {
  let snapshot = initialSnapshot;
  let count = 0;

  // Catch-all for any other /api call the app might make — empty list is a
  // safe default and keeps tests hermetic. Anchored so it cannot swallow
  // module URLs that merely contain an "api" path segment (e.g.
  // /src/lib/api/httpClient.ts).
  await page.route(
    /^https?:\/\/[^/]+\/api\//,
    (route: Route) => route.fulfill({ json: [] }),
  );

  await page.route(/^https?:\/\/[^/]+\/api\/auth\/me$/, (route) =>
    route.fulfill({ json: { username: "e2e-admin", isTempPassword: false } }),
  );

  await page.route(/^https?:\/\/[^/]+\/api\/agents$/, (route) =>
    route.fulfill({ json: AGENTS }),
  );

  await page.route(/^https?:\/\/[^/]+\/api\/queue\/snapshot$/, (route) => {
    count += 1;
    return route.fulfill({ json: snapshot });
  });

  return {
    setSnapshot(next) {
      snapshot = next;
    },
    snapshotCount() {
      return count;
    },
  };
}
