import { chromium } from "@playwright/test";
import { join } from "path";
const pause = (ms) => new Promise((r) => setTimeout(r, ms));
(async () => {
  const browser = await chromium.launch({ headless: false });
  const page = await (await browser.newContext({ viewport: { width: 1440, height: 900 } })).newPage();
  try {
    await page.goto("http://localhost:5173/models?view=backtesting");
    await pause(2500);
    // hover + small pause to let popover open, then screenshot. Use mouse move directly to keep
    // the cursor over the bridge area between trigger + popover.
    const btn = page.locator("table tr td:last-child button").first();
    const box = await btn.boundingBox();
    if (!box) throw new Error("button not found");
    await page.mouse.move(box.x + box.width/2, box.y + box.height/2);
    await pause(1500);
    // popover should now be open below the button — move the cursor INTO the popover area
    await page.mouse.move(box.x + box.width/2, box.y + box.height + 100);
    await pause(2000);
    await page.screenshot({ path: join(process.cwd(), "regression-shots", "iter-pop-visual.png") });
    // click the Ledger tab inside the popover — it has lowercase text "ledger"
    await page.locator("button").filter({ hasText: /^ledger$/ }).click({ timeout: 5000 }).catch(async () => {
      // fallback: keyboard nav
      console.log("Could not find ledger tab button directly");
    });
    await pause(2000);
    await page.screenshot({ path: join(process.cwd(), "regression-shots", "iter-pop-ledger.png") });
    console.log("OK");
  } catch (e) { console.error("FAIL:", e.message); await page.screenshot({ path: join(process.cwd(), "regression-shots", "iter-pop-FAIL.png") }); }
  finally { await browser.close(); }
})();
