import { defineConfig } from "@playwright/test";

/**
 * Browser-level E2E tests for the unswarm frontend.
 *
 * Serving strategy: hermetic network interception. The Vite dev server is
 * started by Playwright's webServer, and each test installs `page.route`
 * handlers that serve canned `/api/*` responses matching the real backend
 * contract (see e2e/helpers.ts). No backend process is required and no src
 * code is modified.
 */
export default defineConfig({
  testDir: "./e2e",
  // Sequential execution keeps the shared dev server and route mocks predictable.
  fullyParallel: false,
  workers: 1,
  retries: 1,
  timeout: 30_000,
  expect: {
    timeout: 10_000,
  },
  use: {
    baseURL: "http://localhost:5173",
    trace: "retain-on-failure",
  },
  webServer: {
    command: "npm run dev -- --port 5173 --strictPort",
    url: "http://localhost:5173",
    reuseExistingServer: !process.env.CI,
    timeout: 60_000,
  },
});
