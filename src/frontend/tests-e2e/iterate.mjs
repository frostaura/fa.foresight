// Quick one-shot visual capture. Usage: `node iterate.mjs <surface> [model-name]`
//   surfaces: paper, models, designer-default, designer-editable, create-dialog,
//             backtesting, ai-chat
// Single screenshot, then exit (no slowMo, no long pauses).
import { chromium } from "@playwright/test";
import { mkdirSync } from "fs";
import { join } from "path";

const args = process.argv.slice(2);
const surface = args[0] ?? "paper";
const modelName = args[1] ?? "RSI+EMA LogReg";

const SHOTS = join(process.cwd(), "regression-shots");
mkdirSync(SHOTS, { recursive: true });

const pause = (ms) => new Promise((r) => setTimeout(r, ms));

(async () => {
  const browser = await chromium.launch({ headless: false });
  const ctx = await browser.newContext({ viewport: { width: 1440, height: 900 } });
  const page = await ctx.newPage();
  try {
    if (surface === "paper") {
      await page.goto("http://localhost:5173/paper-trading");
      await pause(3000);
    } else if (surface === "models") {
      await page.goto("http://localhost:5173/models");
      await pause(2000);
    } else if (surface === "designer-default") {
      await page.goto("http://localhost:5173/models");
      await pause(1500);
      await page.getByText("Foresight Default LLM", { exact: true }).first().click();
      await pause(2500);
    } else if (surface === "designer-editable") {
      await page.goto("http://localhost:5173/models");
      await pause(1500);
      await page.getByText(new RegExp(modelName.replace(/[.*+?^${}()|[\]\\]/g, "\\$&"))).first().click();
      await pause(2500);
    } else if (surface === "create-dialog") {
      await page.goto("http://localhost:5173/models");
      await pause(1500);
      await page.getByRole("button", { name: /New model/ }).click();
      await pause(1000);
    } else if (surface === "backtesting") {
      await page.goto("http://localhost:5173/models?view=backtesting");
      await pause(1500);
    } else if (surface === "ai-chat") {
      await page.goto("http://localhost:5173/models");
      await pause(1500);
      await page.getByText(new RegExp(modelName.replace(/[.*+?^${}()|[\]\\]/g, "\\$&"))).first().click();
      await pause(2500);
      const chat = page.locator("textarea[placeholder*='Bollinger']");
      await chat.fill("Lower l2 to 0.005");
      await page.getByRole("button", { name: /Ask/ }).click();
      await page.locator("text=/Diff ready|invalid|Validation|failed/").first().waitFor({ timeout: 60_000 }).catch(() => {});
      await pause(1500);
    }
    const path = join(SHOTS, `iter-${surface}.png`);
    await page.screenshot({ path, fullPage: false });
    console.log(`OK ${path}`);
  } catch (e) {
    console.error("FAIL", e.message);
    await page.screenshot({ path: join(SHOTS, `iter-${surface}-FAIL.png`) });
  } finally {
    await browser.close();
  }
})();
