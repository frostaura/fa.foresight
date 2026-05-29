/** @type {import('tailwindcss').Config} */
export default {
  content: ["./index.html", "./src/**/*.{ts,tsx}"],
  theme: {
    extend: {
      // ── FrostAura brand tokens ─────────────────────────────────────────────
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
        "fa-danger": "#F08484",

        // ── shadcn semantic tokens mapped onto FrostAura palette ──────────────
        // These are consumed by the hand-authored shadcn component files below.
        // They read the CSS custom-properties injected in index.css so they
        // always stay in sync with the runtime theme.
        border: "hsl(var(--border))",
        input: "hsl(var(--input))",
        ring: "hsl(var(--ring))",
        background: "hsl(var(--background))",
        foreground: "hsl(var(--foreground))",
        primary: {
          DEFAULT: "hsl(var(--primary))",
          foreground: "hsl(var(--primary-foreground))",
        },
        secondary: {
          DEFAULT: "hsl(var(--secondary))",
          foreground: "hsl(var(--secondary-foreground))",
        },
        destructive: {
          DEFAULT: "hsl(var(--destructive))",
          foreground: "hsl(var(--destructive-foreground))",
        },
        muted: {
          DEFAULT: "hsl(var(--muted))",
          foreground: "hsl(var(--muted-foreground))",
        },
        accent: {
          DEFAULT: "hsl(var(--accent))",
          foreground: "hsl(var(--accent-foreground))",
        },
        popover: {
          DEFAULT: "hsl(var(--popover))",
          foreground: "hsl(var(--popover-foreground))",
        },
        card: {
          DEFAULT: "hsl(var(--card))",
          foreground: "hsl(var(--card-foreground))",
        },
      },
      borderRadius: {
        lg: "var(--radius)",
        md: "calc(var(--radius) - 2px)",
        sm: "calc(var(--radius) - 4px)",
      },
      fontFamily: {
        display: ["ui-sans-serif", "system-ui", "-apple-system", "Inter", "sans-serif"],
        mono: ["ui-monospace", "SF Mono", "Menlo", "monospace"],
      },
      backgroundImage: {
        "fa-gradient": "radial-gradient(1200px 600px at 30% 0%, rgba(164,212,244,0.10), transparent 60%), linear-gradient(180deg, #06121F 0%, #050D17 100%)",
      },
      boxShadow: {
        "fa-glass": "inset 0 1px 0 0 rgba(255,255,255,0.04), 0 1px 2px rgba(0,0,0,0.2)",
      },
      keyframes: {
        "accordion-down": {
          from: { height: "0" },
          to: { height: "var(--radix-accordion-content-height)" },
        },
        "accordion-up": {
          from: { height: "var(--radix-accordion-content-height)" },
          to: { height: "0" },
        },
      },
      animation: {
        "accordion-down": "accordion-down 0.2s ease-out",
        "accordion-up": "accordion-up 0.2s ease-out",
      },
    },
  },
  plugins: [],
};
