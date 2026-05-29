// Headed walk through every iter-4 surface with screenshots after each step. Tests are smoke-only
// — assertions catch hard failures, but the screenshots are the regression artifact you review.
import { chromium } from "@playwright/test";
import { mkdirSync } from "fs";
import { join } from "path";

const SHOTS = join(process.cwd(), "regression-shots");
mkdirSync(SHOTS, { recursive: true });
const pause = (ms) => new Promise((r) => setTimeout(r, ms));
const shot = async (page, name) => { await page.screenshot({ path: join(SHOTS, `${name}.png`), fullPage: false }); console.log(`  ↳ ${name}`); };

(async () => {
  const browser = await chromium.launch({ headless: false, slowMo: 150 });
  const ctx = await browser.newContext({ viewport: { width: 1440, height: 900 } });
  const page = await ctx.newPage();
  const consoleErrors = [];
  page.on("console", (m) => m.type() === "error" && consoleErrors.push(m.text()));

  try {
    console.log("\n[1] Paper Trading — sidebar nav + per-card model picker + zero-crossings chip");
    await page.goto("http://localhost:5173/paper-trading");
    await pause(3000);
    await shot(page, "fw-01-paper-trading");

    console.log("\n[2] Switch the 1m card to RSI+EMA LogReg via the picker dropdown");
    const onem = page.locator("select[title='Model']").first();
    await onem.selectOption({ label: "RSI+EMA LogReg (curl)" });
    await pause(1500);
    await shot(page, "fw-02-card-model-switched");

    console.log("\n[3] Models page — Definitions tab (default LLM + user model)");
    await page.getByRole("link", { name: "Models" }).click();
    await pause(2000);
    await shot(page, "fw-03-models-list");

    console.log("\n[4] Designer (read-only built-in)");
    await page.getByText("Foresight Default LLM", { exact: true }).first().click();
    await pause(2500);
    await shot(page, "fw-04-designer-readonly");

    console.log("\n[5] Designer (editable RSI+EMA LogReg) with palette + AI chat panel");
    await page.locator("[data-fa-designer-close]").click();
    await pause(1000);
    await page.getByText(/RSI\+EMA LogReg/).first().click();
    await pause(2500);
    await shot(page, "fw-05-designer-editable");

    console.log("\n[6] AI assistant — ask for a flow edit, capture diff banner");
    const chat = page.locator("textarea[placeholder*='Bollinger']");
    await chat.fill("Increase the logistic regression L2 to 0.05.");
    await pause(500);
    await shot(page, "fw-06-ai-chat-typing");
    await page.getByRole("button", { name: /Ask/ }).click();
    await page.locator("text=/Diff ready|invalid|Validation/").first().waitFor({ timeout: 60_000 }).catch(() => {});
    await pause(2000);
    await shot(page, "fw-07-ai-chat-diff");

    console.log("\n[7] Click Apply on the diff banner");
    await page.getByRole("button", { name: "Apply" }).click().catch(() => {});
    await pause(1500);
    await shot(page, "fw-08-after-diff-applied");

    console.log("\n[8] Save the edited model");
    await page.getByRole("button", { name: /Save/ }).click().catch(() => {});
    await pause(1500);
    await shot(page, "fw-09-after-save");

    console.log("\n[9] Train the saved model");
    await page.getByRole("button", { name: /Train/ }).first().click().catch(() => {});
    console.log("   (waiting on train — Math.NET fit on last 14d, usually <10s)");
    await pause(20_000);
    await shot(page, "fw-10-after-train");

    console.log("\n[10] Backtesting tab — run, capture report");
    await page.getByRole("button", { name: /Backtesting/ }).click();
    await pause(1500);
    await page.getByRole("button", { name: /Run backtest/ }).click();
    await page.getByText("Hit rate").first().waitFor({ timeout: 90_000 });
    await pause(2000);
    await shot(page, "fw-11-backtest-report");

    console.log("\n[11] Create new model dialog");
    await page.getByRole("button", { name: /Definitions/ }).click();
    await pause(500);
    await page.getByRole("button", { name: /New model/ }).click();
    await pause(1500);
    await shot(page, "fw-12-create-dialog");
    await page.getByRole("button", { name: "Cancel" }).click();
    await pause(500);

    console.log("\n[12] Paper Trading — confirm picker reflects active model");
    await page.getByRole("link", { name: "Paper Trading" }).click();
    await pause(3000);
    await shot(page, "fw-13-paper-final");

    console.log("\n✔ Full walk complete.");
  } catch (e) {
    console.error("✗ Walk failed:", e.message);
    await shot(page, "fw-FAIL");
  } finally {
    if (consoleErrors.length) {
      console.log(`\nConsole errors (${consoleErrors.length}):`);
      for (const c of consoleErrors.slice(0, 10)) console.log("  -", c);
    }
    await browser.close();
  }
})();
