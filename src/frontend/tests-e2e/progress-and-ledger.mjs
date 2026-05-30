import { chromium } from "@playwright/test";
import { join } from "path";
const pause = (ms) => new Promise((r) => setTimeout(r, ms));
(async () => {
  const browser = await chromium.launch({ headless: false, slowMo: 200 });
  const page = await (await browser.newContext({ viewport: { width: 1440, height: 900 } })).newPage();
  try {
    // Models page → backtesting
    await page.goto("http://localhost:5173/models?view=backtesting");
    await pause(2500);
    await page.screenshot({ path: join(process.cwd(), "regression-shots", "iter-bt-form.png") });
    // Click run, capture mid-run progress
    await page.getByRole("button", { name: /Run backtest/ }).click();
    await pause(1500);
    await page.screenshot({ path: join(process.cwd(), "regression-shots", "iter-bt-running.png") });
    await pause(2500);
    await page.screenshot({ path: join(process.cwd(), "regression-shots", "iter-bt-running2.png") });
    // Wait for completion
    await page.locator("text=/Run backtest/").first().waitFor({ timeout: 120_000 });
    await pause(1500);
    await page.screenshot({ path: join(process.cwd(), "regression-shots", "iter-bt-done.png") });
    // Hover the first row's ledger icon to open the popover (Visual tab default)
    const ledgerCells = page.locator("table tr td:last-child button");
    await ledgerCells.first().hover();
    await pause(2500);
    await page.screenshot({ path: join(process.cwd(), "regression-shots", "iter-bt-popover-visual.png") });
    // Click Ledger tab in the popover
    const ledgerTab = page.getByRole("button", { name: /Ledger/, exact: false }).last();
    await ledgerTab.click().catch(() => {});
    await pause(1500);
    await page.screenshot({ path: join(process.cwd(), "regression-shots", "iter-bt-popover-ledger.png") });
    console.log("OK");
  } catch (e) {
    console.error("FAIL:", e.message);
    await page.screenshot({ path: join(process.cwd(), "regression-shots", "iter-bt-FAIL.png") });
  } finally {
    await browser.close();
  }
})();
