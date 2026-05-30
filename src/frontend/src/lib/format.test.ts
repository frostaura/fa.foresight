import { describe, expect, it } from "vitest";
import { fmtRunDate, fmtDateRange } from "./format";

describe("fmtRunDate", () => {
  it("formats a Date as zero-padded DD MMM YYYY", () => {
    // 2026-05-09 (month is 0-indexed in the Date constructor).
    expect(fmtRunDate(new Date(2026, 4, 9))).toBe("09 May 2026");
  });

  it("accepts an epoch-ms number", () => {
    const ms = new Date(2026, 0, 1).getTime();
    expect(fmtRunDate(ms)).toBe("01 Jan 2026");
  });
});

describe("fmtDateRange", () => {
  it("joins start and end with an arrow", () => {
    const start = new Date(2026, 4, 9).getTime();
    const end = new Date(2026, 4, 16).getTime();
    expect(fmtDateRange(start, end)).toBe("09 May 2026 → 16 May 2026");
  });
});
