import { defineConfig, devices } from "@playwright/test";

const isCI = !!process.env.CI;

/**
 * Playwright config pointed at the vite dev server on :5173 (proxying /api → :5000).
 *
 * In CI we run headless and let Playwright start the frontend dev server itself (`webServer`
 * below) so the smoke gate has no external dependency. Locally we default to headed and reuse
 * an already-running dev server, preserving the manual full-stack debugging workflow.
 *
 * The CI gate (`npm run test:e2e`) runs only the frontend-only shell smoke (smoke.spec.ts).
 * Full-stack journeys (models.spec.ts) need a running backend + seeded data and are run via
 * `npm run test:e2e:full` against a locally-managed stack.
 */
export default defineConfig({
  testDir: "./tests-e2e",
  fullyParallel: false,
  workers: 1,
  retries: 0,
  reporter: [["list"]],
  webServer: {
    command: "npm run dev -- --port 5173 --strictPort",
    url: "http://localhost:5173",
    reuseExistingServer: !isCI,
    timeout: 120_000,
  },
  use: {
    baseURL: "http://localhost:5173",
    headless: isCI,
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
