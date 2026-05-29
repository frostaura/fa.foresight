import { chromium } from "@playwright/test";
import { join } from "path";
const pause = (ms) => new Promise((r) => setTimeout(r, ms));
(async () => {
  const browser = await chromium.launch({ headless: false });
  const page = await (await browser.newContext({ viewport: { width: 1440, height: 900 } })).newPage();
  try {
    await page.goto("http://localhost:5173/models?view=backtesting");
    await pause(2500);
    // pick a heavier interval to make the progress visible for longer
    await page.locator("select").nth(2).selectOption("1m");
    await pause(500);
    await page.getByRole("button", { name: /Run backtest/ }).click();
    await pause(2500);
    await page.screenshot({ path: join(process.cwd(), "regression-shots", "iter-row-progress-1.png") });
    await pause(4000);
    await page.screenshot({ path: join(process.cwd(), "regression-shots", "iter-row-progress-2.png") });
    await pause(4000);
    await page.screenshot({ path: join(process.cwd(), "regression-shots", "iter-row-progress-3.png") });
    console.log("OK");
  } catch (e) { console.error("FAIL:", e.message); }
  finally { await browser.close(); }
})();
