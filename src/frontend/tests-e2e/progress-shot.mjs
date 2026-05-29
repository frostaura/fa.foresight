import { chromium } from "@playwright/test";
import { join } from "path";
const pause = (ms) => new Promise((r) => setTimeout(r, ms));
(async () => {
  const browser = await chromium.launch({ headless: false });
  const page = await (await browser.newContext({ viewport: { width: 1440, height: 900 } })).newPage();
  try {
    await page.goto("http://localhost:5173/models?view=backtesting");
    await pause(2500);
    // Pick 1m interval (heaviest) to make the progress bar tangible
    await page.selectOption("select >> nth=2", "1m"); // interval is the 3rd select after Model + Symbol
    await pause(800);
    await page.getByRole("button", { name: /Run backtest/ }).click();
    // Snap a series so we catch the progress bar at various fills
    await pause(2000);
    await page.screenshot({ path: join(process.cwd(), "regression-shots", "iter-progress-1.png") });
    await pause(3000);
    await page.screenshot({ path: join(process.cwd(), "regression-shots", "iter-progress-2.png") });
    await pause(3000);
    await page.screenshot({ path: join(process.cwd(), "regression-shots", "iter-progress-3.png") });
    // wait for done
    await page.locator("text=/Run backtest/").first().waitFor({ timeout: 120_000 });
    await pause(1500);
    await page.screenshot({ path: join(process.cwd(), "regression-shots", "iter-progress-done.png") });
    console.log("OK");
  } catch (e) { console.error("FAIL:", e.message); await page.screenshot({ path: join(process.cwd(), "regression-shots", "iter-progress-FAIL.png") }); }
  finally { await browser.close(); }
})();
