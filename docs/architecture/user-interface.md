# User Interface

> Stack: React + TypeScript + Vite + Redux Toolkit + Tailwind + shadcn/ui on a semantic-token design system (FrostAura brand: frosty glass, light-blue on dark-blue, clean, minimal, elegant). No raw color/spacing literals; shadcn primitives over bespoke controls.

## Navigation
Left sidebar (collapsible, persists; auto-collapses on narrow/tablet):
- **Trading** (default) → three sub-views:
  - **Status** — overview of everything: all live + paper sessions, overlaid balance curves + hit/miss markers, per-side (live vs paper) totals.
  - **Live** — live sessions; create/configure/arm; per-session chart + ledger.
  - **Paper** — paper sessions; same surface, no real money.
- **Models** — model catalogue + the dual-view authoring designer.
- **Strategies** — strategy catalogue + the same dual-view designer.
- **Backtesting** — backtest + chaos/bust runner and results (matrix, bust-rate, distributions).
- **Settings** — venue selection (Polymarket default), wallet/go-live, account balance.

## Cross-cutting UI requirements
- **Charts**: open well-framed (sane default zoom, not zoomed-out); **full-screenable** for a tablet dashboard; preserve the intricacies — hit/miss/skip/pending **result orbs**, the pulsing **active-candle lean dot**, **zero-crossing wildness** markers, the per-bet ledger.
- **Dual-view designer**: a real draggable canvas (nodes actually drag, snap, connect) with a synced **code view**; a **step-through** control to play nodes one at a time and inspect outputs.
- **Session config**: venue → symbol → timeframe options driven by the active venue capability matrix; starting balance + initial bet + strategy + model + gate; config-hash duplicate guard surfaced inline.
- **No functional regression**: the redesign re-skins and fixes UX; every existing signal stays present and working.
