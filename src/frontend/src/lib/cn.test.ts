import { describe, expect, it } from "vitest";
import { cn } from "./cn";

describe("cn", () => {
  it("merges class names and drops falsy values", () => {
    expect(cn("a", false, null, undefined, "c")).toBe("a c");
  });

  it("lets later Tailwind utilities win over earlier conflicting ones", () => {
    expect(cn("px-2", "px-4")).toBe("px-4");
  });
});
