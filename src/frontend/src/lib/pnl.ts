// Sign-based color class for P&L surfaces. ε guard keeps floating-point dust from tinting a
// neutral value. One-line swappable if the palette ever changes — keep all P&L color decisions
// pointed here.
export function pnlClass(v: number): string {
  if (Math.abs(v) < 0.005) return "text-fa-frost-dim";
  return v > 0 ? "text-emerald-300" : "text-rose-300";
}
