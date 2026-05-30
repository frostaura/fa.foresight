import { useEffect, useRef, useState, type ReactNode } from "react";

/**
 * Briefly shimmers its children every time `value` changes. The first mount does not shimmer —
 * we initialise from the first observed value so static reads (e.g. balance loaded from storage
 * on refresh) don't flash spuriously. Subsequent changes apply the fa-shimmer CSS class for one
 * animation cycle.
 */
export default function ShimmerOnChange({
  value,
  children,
  durationMs = 1100,
  className,
}: {
  value: unknown;
  children: ReactNode;
  durationMs?: number;
  /**
   * Extra classes applied to the wrapper span on *both* the static and animating renders. This is
   * how callers colour the text — `.fa-shimmer` paints with a gradient on `currentColor`, so any
   * `text-emerald-300` etc. must sit on the same element as `.fa-shimmer` rather than on a child
   * (the child's colour gets swallowed by `-webkit-text-fill-color: transparent` on the wrapper).
   */
  className?: string;
}) {
  const prevRef = useRef(value);
  // `key` restarts the CSS animation on each change; `on` is true only WHILE the shimmer plays.
  const [state, setState] = useState({ key: 0, on: false });

  useEffect(() => {
    if (prevRef.current === value) return;
    prevRef.current = value;
    setState((s) => ({ key: s.key + 1, on: true }));
    const id = window.setTimeout(() => setState((s) => ({ ...s, on: false })), durationMs);
    return () => window.clearTimeout(id);
  }, [value, durationMs]);

  // CRITICAL: `.fa-shimmer` paints the text with a transparent fill + currentColor gradient, which
  // SWALLOWS any colour set on a child. So we only apply it WHILE shimmering, then revert to a plain
  // `className` span — that restores the caller's solid colour (e.g. green/red P&L) after the sweep
  // instead of leaving the text permanently colourless. Callers that want the shimmer ITSELF tinted
  // should pass the colour via `className` (it sits on the same element as `.fa-shimmer`).
  if (!state.on) {
    return <span className={className}>{children}</span>;
  }
  return (
    <span
      key={state.key}
      className={className ? `fa-shimmer ${className}` : "fa-shimmer"}
      style={{ animationDuration: `${durationMs}ms` }}
    >
      {children}
    </span>
  );
}
