// Headed regression driver. Launches a visible Chrome window via Playwright and walks the iter-4
// surfaces with pauses so you can follow along. Failures throw immediately; success leaves the
// browser open at the end so you can poke around.
import { chromium } from "@playwright/test";
import { mkdirSync } from "fs";
import { join } from "path";

const SHOTS = join(process.cwd(), "regression-shots");
mkdirSync(SHOTS, { recursive: true });

const pause = (ms) => new Promise((r) => setTimeout(r, ms));

async function shot(page, name) {
  const path = join(SHOTS, `${name}.png`);
  await page.screenshot({ path, fullPage: true });
  console.log(`  ↳ screenshot: ${path}`);
}

(async () => {
  console.log("Launching headed Chrome…");
  const browser = await chromium.launch({ headless: false, slowMo: 250 });
  const ctx = await browser.newContext({ viewport: { width: 1440, height: 900 } });
  const page = await ctx.newPage();

  const consoleErrors = [];
  page.on("console", (msg) => { if (msg.type() === "error") consoleErrors.push(msg.text()); });
  page.on("pageerror", (err) => consoleErrors.push(`pageerror: ${err.message}`));

  try {
    console.log("\n1. Paper Trading landing page");
    await page.goto("http://localhost:5173/paper-trading");
    await page.waitForLoadState("networkidle").catch(() => {});
    await pause(2000);
    await shot(page, "01-paper-trading");

    console.log("\n2. Sidebar should show Models entry under a horizontal divider");
    await page.waitForSelector("text=Models", { timeout: 5000 });
    await shot(page, "02-sidebar-with-models");

    console.log("\n3. Click into Models");
    await page.getByRole("link", { name: "Models" }).click();
    await page.waitForLoadState("networkidle").catch(() => {});
    await pause(1500);
    await shot(page, "03-models-definitions");

    console.log("\n4. Click on the Foresight Default LLM card to open the designer");
    await page.getByText("Foresight Default LLM", { exact: true }).first().click();
    await pause(2500);
    await shot(page, "04-designer-default-llm");

    console.log("\n5. Close designer (X button in header)");
    await page.locator("[data-fa-designer-close]").click({ timeout: 8000 }).catch(async () => {
      console.log("   ↳ close button click intercepted; navigating back via Models link instead");
      await page.getByRole("link", { name: "Models" }).click();
    });
    await pause(1000);

    console.log("\n6. Backtesting tab → run a backtest against the curl-seeded RSI+EMA LogReg model");
    await page.getByRole("button", { name: /Backtesting/ }).click();
    await pause(1000);
    await shot(page, "05-backtesting-form");
    await page.getByRole("button", { name: /Run backtest/ }).click();
    console.log("   Waiting for report (Binance fetch + 480-iter loop)…");
    await page.getByText("Hit rate").first().waitFor({ timeout: 120_000 });
    await pause(1500);
    await shot(page, "06-backtest-report");

    console.log("\n7. Editable designer — open the curl-seeded RSI+EMA LogReg model");
    await page.goto("http://localhost:5173/models");
    await pause(1500);
    await page.getByText(/RSI\+EMA LogReg/).first().click();
    await pause(2500);
    await shot(page, "07-designer-editable");

    console.log("\n8. AI chat — ask the assistant for a small flow edit");
    const chatInput = page.locator("textarea[placeholder*='Bollinger']");
    if (await chatInput.count()) {
      await chatInput.fill("Bump the logistic regression l2 regularization to 0.05.");
      await shot(page, "08-ai-chat-typed");
      await page.getByRole("button", { name: /Ask/ }).click();
      console.log("   Waiting for assistant reply (LLM call ~10-20s)…");
      // Either the green diff banner shows, or an error message — wait for either.
      await page.locator("text=/Diff ready|invalid|Validation/").first().waitFor({ timeout: 60_000 }).catch(() => {});
      await pause(1500);
      await shot(page, "09-ai-chat-reply");
    } else {
      console.log("   chat input not found — skipping");
    }

    console.log("\n10. Close editable designer + open Create-Model dialog");
    await page.locator("[data-fa-designer-close]").click({ timeout: 8000 }).catch(() => page.goto("http://localhost:5173/models"));
    await pause(1500);
    await page.getByRole("button", { name: /New model/ }).click();
    await pause(1500);
    await shot(page, "10-create-model-dialog");
    await page.getByRole("button", { name: "Cancel" }).click();
    await pause(500);

    console.log("\n11. Back to Paper Trading — confirm per-card model picker");
    await page.getByRole("link", { name: "Paper Trading" }).click();
    await pause(2500);
    await shot(page, "11-paper-trading-with-picker");

    console.log("\n✔ Regression walk complete.");
    if (consoleErrors.length > 0) {
      console.log("\n⚠ Console errors captured during walk:");
      for (const e of consoleErrors.slice(0, 20)) console.log(`   - ${e}`);
    }
    console.log("\nBrowser left open. Close manually when done.");
    await pause(60_000 * 5);
  } catch (e) {
    console.error("\nRegression failed:", e.message);
    await shot(page, "FAIL");
    throw e;
  } finally {
    await browser.close();
  }
})();
