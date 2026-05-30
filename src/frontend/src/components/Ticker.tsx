/**
 * Ticker — a single-line, continuously auto-scrolling Wall-St-style tape.
 *
 * Items are rendered twice inside a flex track (A A). The CSS keyframe `fa-ticker` translates
 * the track by -50%, which brings the first copy perfectly to where the second started —
 * creating a seamless infinite loop with no JS timers.
 *
 * prefers-reduced-motion: the animation is absent, the row becomes a simple horizontally-
 * scrollable strip the user can drag with pointer/keyboard.
 */
import { type ReactNode } from "react";

export interface TickerItem {
  key: string;
  content: ReactNode;
}

interface TickerProps {
  items: TickerItem[];
  /** Tailwind height class. Defaults to h-8 (~one text-row with padding). */
  heightClass?: string;
  /** Divider rendered between items. Defaults to a mid-opacity bullet. */
  divider?: ReactNode;
}

const DEFAULT_DIVIDER = (
  <span className="mx-3 text-fa-frost-dim/40 select-none" aria-hidden>•</span>
);

export default function Ticker({ items, heightClass = "h-8", divider = DEFAULT_DIVIDER }: TickerProps) {
  if (items.length === 0) return null;

  // Build the inner row once; it is rendered twice inside the track so the duplicate copy
  // is always adjacent to the original — that's what makes the seamless wrap work.
  const row = (suffix: string) =>
    items.map((item, i) => (
      <span key={`${item.key}-${suffix}`} className="inline-flex items-center whitespace-nowrap">
        {item.content}
        {i < items.length - 1 && divider}
        {i === items.length - 1 && divider}
      </span>
    ));

  return (
    <div
      className={`w-full overflow-hidden ${heightClass} flex items-center`}
      style={{
        maskImage: "linear-gradient(to right, transparent 0%, black 4%, black 96%, transparent 100%)",
        WebkitMaskImage: "linear-gradient(to right, transparent 0%, black 4%, black 96%, transparent 100%)",
      }}
      aria-label="Top performers ticker"
    >
      {/* prefers-reduced-motion: no animation class applied → this becomes a normal scrollable row */}
      <div className="fa-ticker-track">
        {row("a")}
        {row("b")}
      </div>
    </div>
  );
}
