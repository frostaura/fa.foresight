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
  const [animKey, setAnimKey] = useState(0);

  useEffect(() => {
    if (prevRef.current === value) return;
    prevRef.current = value;
    setAnimKey((k) => k + 1);
  }, [value]);

  // animKey=0 means no shimmer yet (initial mount). After the first real change, animKey>=1
  // and we mount the inner span keyed on animKey so the CSS animation restarts each time.
  if (animKey === 0) {
    return <span className={className}>{children}</span>;
  }
  return (
    <span
      key={animKey}
      className={className ? `fa-shimmer ${className}` : "fa-shimmer"}
      style={{ animationDuration: `${durationMs}ms` }}
    >
      {children}
    </span>
  );
}
