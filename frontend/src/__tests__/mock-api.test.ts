import { describe, it, expect, beforeEach, vi } from "vitest";
import { mockClient, setMockLatency } from "../lib/api/mock";

beforeEach(() => {
  setMockLatency(0); // instant tests
});

describe("mockClient", () => {
  describe("listModels", () => {
    it("returns array of models with expected fields", async () => {
      const models = await mockClient.listModels();
      expect(Array.isArray(models)).toBe(true);
      expect(models.length).toBeGreaterThan(0);

      for (const m of models) {
        expect(m).toHaveProperty("id");
        expect(m).toHaveProperty("name");
        expect(m).toHaveProperty("family");
        expect(m).toHaveProperty("parameterSize");
        expect(m).toHaveProperty("quantization");
        expect(m).toHaveProperty("status");
        expect(m).toHaveProperty("lastBenchmark");
        expect(m).toHaveProperty("contextWindow");
        expect(m).toHaveProperty("containerImage");
        expect(m).toHaveProperty("createdAt");
        expect(m).toHaveProperty("updatedAt");
      }
    });

    it("returns a copy (not the same reference each time)", async () => {
      const a = await mockClient.listModels();
      const b = await mockClient.listModels();
      expect(a).not.toBe(b);
      expect(a).toEqual(b);
    });
  });

  describe("getQueueSnapshot", () => {
    it("returns correct shape", async () => {
      const snap = await mockClient.getQueueSnapshot();
      expect(snap).toHaveProperty("currentSlot");
      expect(snap).toHaveProperty("waiting");
      expect(snap).toHaveProperty("recentCompleted");
      expect(snap).toHaveProperty("activeTransitions");
      expect(Array.isArray(snap.waiting)).toBe(true);
      expect(Array.isArray(snap.recentCompleted)).toBe(true);
    });
  });

  describe("container lifecycle transitions", () => {
    it("startContainer creates a new container with 'starting' status", async () => {
      const c = await mockClient.startContainer("1");
      expect(c.status).toBe("starting");
      expect(c.modelId).toBe("1");
      expect(c).toHaveProperty("id");
    });

    it("stopContainer transitions status to 'stopped'", async () => {
      const containers = await mockClient.listContainers();
      const running = containers.find((c) => c.status === "running");
      expect(running).toBeDefined();

      await mockClient.stopContainer(running!.id);
      const updated = await mockClient.listContainers();
      const stopped = updated.find((c) => c.id === running!.id);
      expect(stopped?.status).toBe("stopped");
    });

    it("restartContainer transitions status to 'running'", async () => {
      const containers = await mockClient.listContainers();
      const stopped = containers.find((c) => c.status === "stopped");
      expect(stopped).toBeDefined();

      await mockClient.restartContainer(stopped!.id);
      const updated = await mockClient.listContainers();
      const restarted = updated.find((c) => c.id === stopped!.id);
      expect(restarted?.status).toBe("running");
    });
  });

  describe("latency injection", () => {
    it("runs instantly when latency is 0", async () => {
      setMockLatency(0);
      const start = performance.now();
      await mockClient.listModels();
      const elapsed = performance.now() - start;
      expect(elapsed).toBeLessThan(50); // well under any real delay
    });
  });

  describe("getModel", () => {
    it("returns a model by id", async () => {
      const m = await mockClient.getModel("1");
      expect(m.id).toBe("1");
      expect(m.name).toBe("llama-3.1-70b");
    });

    it("throws for unknown id", async () => {
      await expect(mockClient.getModel("nonexistent")).rejects.toThrow("not found");
    });
  });

  describe("settings round-trip", () => {
    it("getSettings returns settings, updateSettings patches", async () => {
      const s = await mockClient.getSettings();
      expect(s).toHaveProperty("maxConcurrentModels");
      expect(s).toHaveProperty("defaultModel");
      expect(s).toHaveProperty("priorityMode");
      expect(s).toHaveProperty("batchDrain");
      expect(s).toHaveProperty("lazyStop");
      expect(s).toHaveProperty("maxQueueDepth");

      const updated = await mockClient.updateSettings({ requestTimeout: 999 });
      expect(updated.requestTimeout).toBe(999);
    });
  });

  describe("getLogs filtering", () => {
    it("returns all logs with no filter", async () => {
      const logs = await mockClient.getLogs();
      expect(logs.length).toBe(6);
    });

    it("filters by source", async () => {
      const logs = await mockClient.getLogs({ source: "scheduler" });
      expect(logs.every((l) => l.source === "scheduler")).toBe(true);
      expect(logs.length).toBeGreaterThanOrEqual(1);
    });

    it("filters by level", async () => {
      const logs = await mockClient.getLogs({ level: "error" });
      expect(logs.every((l) => l.level === "error")).toBe(true);
      expect(logs.length).toBe(1);
    });

    it("filters by limit", async () => {
      const logs = await mockClient.getLogs({ limit: 2 });
      expect(logs.length).toBe(2);
    });
  });

  describe("subscribeLogs", () => {
    it("delivers log entries via callback", async () => {
      vi.useFakeTimers();
      const entries: unknown[] = [];
      const unsub = mockClient.subscribeLogs((e) => entries.push(e));

      // The mock uses setInterval at 3000ms
      vi.advanceTimersByTime(3100);
      expect(entries.length).toBeGreaterThanOrEqual(1);
      expect(entries[0]).toHaveProperty("id");
      expect(entries[0]).toHaveProperty("level");
      expect(entries[0]).toHaveProperty("message");

      unsub();
      vi.useRealTimers();
    });

    it("unsubscribe stops delivery", async () => {
      vi.useFakeTimers();
      const entries: unknown[] = [];
      const unsub = mockClient.subscribeLogs((e) => entries.push(e));

      unsub();
      vi.advanceTimersByTime(6100);
      expect(entries.length).toBe(0);
      vi.useRealTimers();
    });
  });
});
