import { chromium } from "@playwright/test";
import { join } from "path";
const pause = (ms) => new Promise((r) => setTimeout(r, ms));
(async () => {
  const browser = await chromium.launch({ headless: false });
  const page = await (await browser.newContext({ viewport: { width: 1440, height: 900 } })).newPage();
  try {
    await page.goto("http://localhost:5173/models?view=backtesting");
    await pause(2500);
    // click the first ledger icon to lock open
    const btn = page.locator("table tr td:last-child button").first();
    await btn.click();
    await pause(3000);
    await page.screenshot({ path: join(process.cwd(), "regression-shots", "iter-pop-visual.png") });
    // tab Ledger
    await page.locator("button:has-text('LEDGER')").last().click();
    await pause(2500);
    await page.screenshot({ path: join(process.cwd(), "regression-shots", "iter-pop-ledger.png") });
    console.log("OK");
  } catch (e) { console.error("FAIL:", e.message); await page.screenshot({ path: join(process.cwd(), "regression-shots", "iter-pop-FAIL.png") }); }
  finally { await browser.close(); }
})();
