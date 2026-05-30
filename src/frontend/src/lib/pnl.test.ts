import { describe, expect, it } from "vitest";
import { pnlClass } from "./pnl";

describe("pnlClass", () => {
  it("treats sub-epsilon magnitudes as neutral", () => {
    expect(pnlClass(0)).toBe("text-fa-frost-dim");
    expect(pnlClass(0.004)).toBe("text-fa-frost-dim");
    expect(pnlClass(-0.004)).toBe("text-fa-frost-dim");
  });

  it("colours positive and negative values distinctly", () => {
    expect(pnlClass(1)).toBe("text-emerald-300");
    expect(pnlClass(-1)).toBe("text-rose-300");
  });
});
