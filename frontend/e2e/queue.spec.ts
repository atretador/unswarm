import { expect, test } from "@playwright/test";
import {
  completedSnapshot,
  installApiMocks,
  multiLaneSnapshot,
  zeroBudgetSnapshot,
} from "./helpers";

test.beforeEach(async ({ page }) => {
  await installApiMocks(page);
});

test("queue view loads and shows per-target sections from a multi-lane snapshot", async ({
  page,
}) => {
  await page.goto("/queue");

  await expect(page.getByRole("heading", { name: "Queue" })).toBeVisible();

  // Per-target sections: host + the agent reported by /api/agents
  await expect(page.getByText("Host (local)")).toBeVisible();
  await expect(page.getByText("Agent: gpu-node-1")).toBeVisible();
});

test("multiple concurrent processing rows render with their runtime chips", async ({
  page,
}) => {
  await page.goto("/queue");

  // One processing row per runtime lane, each with its mono runtime chip
  const hostRow = page.getByText("llama-3.1-70b").first();
  await expect(hostRow).toBeVisible();
  await expect(page.getByText("rt-host-main", { exact: true })).toBeVisible();
  await expect(page.getByText("rt-gpu-node-1-a", { exact: true })).toBeVisible();

  // sr-only live region reports both lanes as processing
  await expect(page.getByText(/2 processing/)).toHaveCount(1);
});

test("a waiting item shows its blocked-by pill", async ({ page }) => {
  await page.goto("/queue");

  // q2 on agent:gpu-node-1 is blocked by rt-gpu-node-1-a (single blocker → id shown)
  await expect(page.getByText("blocked by rt-gpu-node-1-a")).toBeVisible();

  // The unblocked head-of-line item on host shows "next up" instead
  await expect(page.getByText("next up")).toBeVisible();
});

test("skip-budget indicator appears when budget state is non-zero", async ({
  page,
}) => {
  await page.goto("/queue");

  // skipsRemaining=2, skipsUsed=1 in the default snapshot
  await expect(page.getByText("Skip budget: 2 left (1 used)")).toBeVisible();
});

test("skip-budget indicator hides when budget state is zero", async ({
  page,
}) => {
  await installApiMocks(page, zeroBudgetSnapshot());
  await page.goto("/queue");

  await expect(page.getByRole("heading", { name: "Queue" })).toBeVisible();
  await expect(page.getByText(/Skip budget:/)).toHaveCount(0);
});

test("snapshot transition: items move to recentCompleted without a reload", async ({
  page,
}) => {
  const api = await installApiMocks(page);
  await page.goto("/queue");

  // Initial state: two lanes processing, no completed card yet
  await expect(page.getByText("rt-host-main", { exact: true })).toBeVisible();
  await expect(page.getByText("Recent completed")).toHaveCount(0);

  // Mark this page instance; if the app reloads, the flag disappears.
  await page.evaluate(() => {
    (window as unknown as Record<string, unknown>).__e2eNoReload = true;
  });

  // Wait for at least one background poll to complete before flipping the
  // snapshot, so we know the update arrives via refetch, not initial load.
  await expect.poll(() => api.snapshotCount()).toBeGreaterThan(1);
  api.setSnapshot(completedSnapshot());

  // UI updates via polling — no navigation/reload.
  await expect(page.getByText("Recent completed (2)")).toBeVisible({
    timeout: 10_000,
  });
  await expect(page.getByText("completed", { exact: true })).toHaveCount(2);

  // Processing rows are gone once drained.
  await expect(page.getByText("rt-host-main", { exact: true })).toHaveCount(0);
  await expect(page.getByText("rt-gpu-node-1-a", { exact: true })).toHaveCount(0);

  // Live region flips to idle.
  await expect(page.getByText(/Queue: 0 waiting, idle/)).toBeVisible();

  // Same document throughout — proves no reload happened.
  expect(
    await page.evaluate(
      () => (window as unknown as Record<string, unknown>).__e2eNoReload,
    ),
  ).toBe(true);
});
