/**
 * Compact, human-friendly formatters for the Models / Backtesting surfaces. Sign-based colouring
 * stays in lib/pnl.ts; this module owns dates only.
 */

/** Same calendar day (locale) → "22:38" (24h); otherwise → "09 May 2026". */
export function fmtRunTime(iso: string | Date): string {
  const d = typeof iso === "string" ? new Date(iso) : iso;
  const now = new Date();
  if (d.toDateString() === now.toDateString())
    return d.toLocaleTimeString([], { hour: "2-digit", minute: "2-digit", hourCycle: "h23" });
  return fmtRunDate(d);
}

/** "09 May 2026" — DD MMM YYYY with zero-padded day. */
export function fmtRunDate(d: Date | string | number): string {
  const date = d instanceof Date ? d : new Date(d);
  const day = String(date.getDate()).padStart(2, "0");
  const month = date.toLocaleString("en", { month: "short" });
  return `${day} ${month} ${date.getFullYear()}`;
}

/** "09 May 2026 → 16 May 2026" — backtest window from epoch-ms start/end. */
export function fmtDateRange(startMs: number, endMs: number): string {
  return `${fmtRunDate(startMs)} → ${fmtRunDate(endMs)}`;
}
