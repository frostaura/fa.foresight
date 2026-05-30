import { defineConfig } from "vitest/config";
import path from "node:path";

// Standalone Vitest config so the app's Vite/PWA plugin chain does not load during tests.
export default defineConfig({
  resolve: {
    alias: { "@": path.resolve(__dirname, "src") },
  },
  test: {
    environment: "node",
    include: ["src/**/*.{test,spec}.{ts,tsx}"],
    coverage: {
      provider: "v8",
      // Emit coverage-summary.json for the CI threshold gate, plus human-readable output.
      reporter: ["text", "json", "json-summary"],
      reportsDirectory: "coverage",
      // Count the whole source tree in the denominator so the number reflects real coverage.
      all: true,
      include: ["src/**/*.{ts,tsx}"],
      exclude: [
        "src/**/*.{test,spec}.{ts,tsx}",
        "src/**/*.d.ts",
        "src/main.tsx",
        "src/vite-env.d.ts",
      ],
    },
  },
});
