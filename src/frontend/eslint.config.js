import js from "@eslint/js";
import tseslint from "typescript-eslint";
import reactHooks from "eslint-plugin-react-hooks";
import reactRefresh from "eslint-plugin-react-refresh";

export default tseslint.config(
  { ignores: ["dist", "node_modules"] },
  {
    extends: [js.configs.recommended, ...tseslint.configs.recommended],
    files: ["**/*.{ts,tsx}"],
    plugins: {
      "react-hooks": reactHooks,
      "react-refresh": reactRefresh,
    },
    rules: {
      ...reactHooks.configs.recommended.rules,
      "react-refresh/only-export-components": ["warn", { allowConstantExport: true }],
      // Relax some strict rules that are fine for this codebase
      "@typescript-eslint/no-unused-vars": ["warn", { argsIgnorePattern: "^_", varsIgnorePattern: "^_" }],
      "@typescript-eslint/no-explicit-any": "warn",
      // eslint-plugin-react-hooks v7 promotes the React Compiler advisory rules to errors.
      // These flag patterns that are not compiler-optimal (sync setState in effects, ref/var
      // access ordering, inline component creation) rather than correctness bugs. Keep them
      // visible as warnings and adopt incrementally instead of failing CI on working code.
      "react-hooks/set-state-in-effect": "warn",
      "react-hooks/refs": "warn",
      "react-hooks/immutability": "warn",
      "react-hooks/static-components": "warn",
      // Design-system guard: ban arbitrary font sizes so typography stays centralised.
      // Use a role (fa-overline / fa-caption / fa-body / fa-section-title / fa-metric /
      // fa-metric-sm — see index.css) or a standard scale step (text-xs/sm/base/lg/xl/2xl).
      "no-restricted-syntax": [
        "error",
        {
          selector: "Literal[value=/text-\\[\\d+px\\]/]",
          message:
            "Arbitrary font size (text-[..px]) is banned. Use a typography role (fa-overline/fa-caption/fa-body/fa-section-title/fa-metric/fa-metric-sm) or a standard step (text-xs/sm/base/lg/xl/2xl). See index.css.",
        },
        {
          selector: "TemplateElement[value.raw=/text-\\[\\d+px\\]/]",
          message:
            "Arbitrary font size (text-[..px]) is banned. Use a typography role or a standard step (text-xs/sm/base/lg/xl/2xl). See index.css.",
        },
      ],
    },
  }
);
