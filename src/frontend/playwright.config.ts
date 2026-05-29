import { defineConfig, devices } from "@playwright/test";

/**
 * Headed-mode Playwright run pointed at the vite dev server on :5173 (proxying /api → :5000).
 * Reuses the externally-managed servers — does NOT spawn them — so tests run against the
 * exact code paths we just curl-validated.
 */
export default defineConfig({
  testDir: "./tests-e2e",
  fullyParallel: false,
  workers: 1,
  retries: 0,
  reporter: [["list"]],
  use: {
    baseURL: "http://localhost:5173",
    headless: false,
    viewport: { width: 1440, height: 900 },
    trace: "retain-on-failure",
    screenshot: "only-on-failure",
    actionTimeout: 15000,
    navigationTimeout: 30000,
  },
  projects: [
    {
      name: "chromium",
      use: { ...devices["Desktop Chrome"], channel: undefined },
    },
  ],
});
