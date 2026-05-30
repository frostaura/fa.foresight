import { chromium } from "@playwright/test";
import { join } from "path";
(async () => {
  const browser = await chromium.launch({ headless: false });
  const page = await (await browser.newContext({ viewport: { width: 1440, height: 900 } })).newPage();
  await page.goto("http://localhost:5173/models?view=backtesting");
  await page.waitForTimeout(2000);
  await page.getByRole("button", { name: /Run backtest/ }).click();
  await page.getByText("Hit rate").first().waitFor({ timeout: 240_000 });
  await page.waitForTimeout(2000);
  await page.screenshot({ path: join(process.cwd(), "regression-shots", "iter-bt-fresh.png"), fullPage: false });
  console.log("OK");
  await browser.close();
})();
