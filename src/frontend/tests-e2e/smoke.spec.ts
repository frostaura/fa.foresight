import { expect, test } from "@playwright/test";

/**
 * Headless shell smoke — the CI E2E gate.
 *
 * Verifies the SPA boots and serves its app shell. It deliberately does NOT depend on the
 * backend API, so it can run in CI against a frontend-only dev server. The full-stack journeys
 * (see models.spec.ts) require a locally-running backend + seeded data and are run on demand via
 * `npm run test:e2e:full`.
 */
test("app shell boots and mounts", async ({ page }) => {
  await page.goto("/");

  // Title comes from index.html — present immediately, no backend needed.
  await expect(page).toHaveTitle(/Foresight/i);

  // The SPA mounts into #root; once React renders, the node has children.
  const root = page.locator("#root");
  await expect(root).toBeAttached();
  await expect.poll(() => root.evaluate((el) => el.childElementCount), { timeout: 15000 }).toBeGreaterThan(0);
});
