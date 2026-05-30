import { chromium } from "@playwright/test";

(async () => {
  const browser = await chromium.launch({ headless: true });
  const page = await browser.newPage();
  await page.goto("http://localhost:5173/models");
  await page.waitForTimeout(1500);
  await page.getByText("Foresight Default LLM", { exact: true }).first().click();
  await page.waitForTimeout(2500);
  const data = await page.evaluate(() => {
    const out = [];
    for (const n of document.querySelectorAll(".react-flow__node")) {
      const labels = [...n.querySelectorAll("span")].map((s) => s.textContent?.trim()).filter(Boolean);
      const handles = [...n.querySelectorAll(".react-flow__handle")].map((h) => ({
        id: h.getAttribute("data-handleid") || h.getAttribute("data-id"),
        type: h.classList.contains("react-flow__handle-left") ? "input" :
              h.classList.contains("react-flow__handle-right") ? "output" : "?"
      }));
      const typeIdLabel = labels.find((l) => l?.includes("."));
      out.push({ typeIdLabel, labels, handles });
    }
    return out;
  });
  console.log(JSON.stringify(data, null, 2));
  await browser.close();
})();
