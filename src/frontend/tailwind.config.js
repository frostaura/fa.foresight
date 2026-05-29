/** @type {import('tailwindcss').Config} */
export default {
  content: ["./index.html", "./src/**/*.{ts,tsx}"],
  theme: {
    extend: {
      colors: {
        "fa-ink": "#06121F",
        "fa-ink-2": "#0B1B30",
        "fa-ink-3": "#102744",
        "fa-frost": "#A4D4F4",
        "fa-frost-bright": "#D4ECFF",
        "fa-frost-dim": "#5C8AB4",
        "fa-glass": "rgba(164, 212, 244, 0.06)",
        "fa-glass-strong": "rgba(164, 212, 244, 0.12)",
        "fa-edge": "rgba(164, 212, 244, 0.18)",
        "fa-success": "#7CE3B6",
        "fa-warning": "#F6C667",
        "fa-danger": "#F08484"
      },
      fontFamily: {
        display: ["ui-sans-serif", "system-ui", "-apple-system", "Inter", "sans-serif"],
        mono: ["ui-monospace", "SF Mono", "Menlo", "monospace"]
      },
      backgroundImage: {
        "fa-gradient": "radial-gradient(1200px 600px at 30% 0%, rgba(164,212,244,0.10), transparent 60%), linear-gradient(180deg, #06121F 0%, #050D17 100%)"
      },
      boxShadow: {
        "fa-glass": "inset 0 1px 0 0 rgba(255,255,255,0.04), 0 1px 2px rgba(0,0,0,0.2)"
      }
    }
  },
  plugins: []
};
