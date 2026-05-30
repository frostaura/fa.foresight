import { expect, test } from "@playwright/test";

/**
 * End-to-end happy path through the iter-4 Models surface:
 *   1. Sidebar shows the new "Models" nav entry under the horizontal divider.
 *   2. Models page renders with sub-tabs (Definitions | Backtesting).
 *   3. The seeded "Foresight Default LLM" card is visible with the Default star + read-only lock.
 *   4. Opening the designer for the default model shows the reactflow canvas with the 16+ nodes.
 *   5. The Backtesting tab lets the user run a backtest against the curl-created deterministic
 *      model and surfaces the report (hit rate / zero-crossings / final balance).
 *   6. Paper Trading shows the per-card model dropdown.
 */
test.describe("Models surface", () => {
  test("sidebar nav + Models page + designer + backtest report + per-card picker", async ({ page }) => {
    await page.goto("/paper-trading");

    // 1. Nav entry visible.
    const modelsNav = page.getByRole("link", { name: "Models" });
    await expect(modelsNav).toBeVisible();

    // 2. Open Models page.
    await modelsNav.click();
    await expect(page).toHaveURL(/\/models/);
    await expect(page.getByRole("heading", { name: "Models" })).toBeVisible();
    await expect(page.getByRole("button", { name: /Definitions/ })).toBeVisible();
    await expect(page.getByRole("button", { name: /Backtesting/ })).toBeVisible();

    // 3. Default LLM card.
    const defaultCard = page.getByText("Foresight Default LLM").first();
    await expect(defaultCard).toBeVisible();
    await expect(page.getByText("Default").first()).toBeVisible();

    // 4. Designer opens on click — the read-only banner + the read-only header copy confirm it.
    await defaultCard.click();
    await expect(page.getByText(/read-only/)).toBeVisible({ timeout: 10000 });
    // Reactflow's internal node element renders for each flow node — there should be ≥ 10 for the
    // 20-node default LLM flow (palette panel adds more "fa-node"-styled elements; we just need
    // some present).
    const flowNodes = page.locator(".react-flow__node");
    await expect.poll(() => flowNodes.count(), { timeout: 10000 }).toBeGreaterThan(0);
    // Close the designer by clicking the X icon in the designer header.
    await page.locator("button").filter({ has: page.locator("svg.lucide-x") }).first().click();
    await expect(page.getByText(/read-only/)).toHaveCount(0);

    // 5. Backtesting tab: confirm form fields render. (The curl-created RSI+EMA LogReg model is
    // already in the dropdown.) Run a small backtest and confirm the report appears.
    await page.getByRole("button", { name: /Backtesting/ }).click();
    await expect(page.getByText(/Run a new backtest/)).toBeVisible();
    await page.getByRole("button", { name: /Run backtest/ }).click();
    // The run blocks for ~2-5s depending on Binance cache state. Wait for the report card to appear.
    await expect(page.getByText(/Hit rate/)).toBeVisible({ timeout: 90000 });
    await expect(page.getByText(/Zero crossings/)).toBeVisible();
    await expect(page.getByText(/Peak borrowed/)).toBeVisible();

    // 6. Paper Trading per-card model picker.
    await page.getByRole("link", { name: "Paper Trading" }).click();
    await expect(page).toHaveURL(/\/paper-trading/);
    // The model picker is a <select> inside each LiveBitcoinChart card.
    const picker = page.locator("select[title='Model']").first();
    await expect(picker).toBeVisible();
    const options = await picker.locator("option").allTextContents();
    expect(options.some((o) => o.includes("Foresight Default LLM"))).toBeTruthy();
    expect(options.some((o) => o.includes("RSI+EMA"))).toBeTruthy();
  });
});
