import { Bitcoin, CircleHelp } from "lucide-react";

/**
 * Lookup table: symbol → icon component. Adding a new symbol = one entry here and an import.
 * Kept as a record (not a switch) so future code can iterate `Object.keys(SYMBOL_ICONS)` to
 * auto-render every known symbol.
 */
const SYMBOL_ICONS = {
  BTCUSDT: Bitcoin,
} as const;

export type SupportedSymbol = keyof typeof SYMBOL_ICONS;

/**
 * Inline symbol icon. Falls back to a generic placeholder for unknown symbols so adding ETH/SOL
 * later doesn't require touching every call site — drop them into SYMBOL_ICONS and they're done.
 *
 * The icon is wrapped in a span with `title={symbol}` so the human-readable identifier is still
 * available on hover (and to screen readers via the icon's aria-label).
 */
export function SymbolIcon({ symbol, className }: { symbol: string; className?: string }) {
  const Icon = SYMBOL_ICONS[symbol as SupportedSymbol] ?? CircleHelp;
  return (
    <span
      className={`inline-flex items-center justify-center ${className ?? "h-4 w-4"}`}
      title={symbol}
      aria-label={symbol}
    >
      <Icon className="h-full w-full" />
    </span>
  );
}

/**
 * Pill-button symbol picker. Renders one pill per supported symbol with the SymbolIcon + a
 * display label; the active pill is highlighted. Used by the Backtesting form (compact pills)
 * and Paper Trading (with longer human-readable labels via labelFn).
 *
 * Native &lt;select&gt; can't render an icon inside an &lt;option&gt; — that's why this exists. With one
 * supported symbol today it's a single pill, but the component handles N pills cleanly so the
 * second symbol (ETH/SOL) needs no UI changes — just a new SYMBOL_ICONS entry.
 */
export function SymbolPicker({
  symbols,
  value,
  onChange,
  labelFn,
  size = "md",
}: {
  symbols: readonly string[];
  value: string;
  onChange: (next: string) => void;
  /** Display label for a pill. Defaults to the raw symbol (e.g. "BTCUSDT"). */
  labelFn?: (symbol: string) => string;
  /** "sm" = compact pills for inline form fields. "md" = larger pills for the standalone picker. */
  size?: "sm" | "md";
}) {
  const sizes = size === "sm"
    ? "px-2 py-1 text-xs gap-1.5"
    : "px-3 py-1.5 text-sm gap-2";
  const iconSize = size === "sm" ? "h-3.5 w-3.5" : "h-4 w-4";
  return (
    <div className="flex items-center gap-1.5 flex-wrap" role="radiogroup" aria-label="Symbol">
      {symbols.map((s) => {
        const active = s === value;
        return (
          <button
            key={s}
            type="button"
            role="radio"
            aria-checked={active}
            onClick={() => onChange(s)}
            className={`inline-flex items-center rounded-md border transition tabular-nums ${sizes} ${
              active
                ? "bg-fa-glass-strong border-fa-frost/40 text-fa-frost-bright"
                : "bg-fa-glass border-fa-edge text-fa-frost-dim hover:text-fa-frost-bright hover:border-fa-frost/30"
            }`}
          >
            <SymbolIcon symbol={s} className={iconSize} />
            <span>{labelFn ? labelFn(s) : s}</span>
          </button>
        );
      })}
    </div>
  );
}
