import { Children, cloneElement, isValidElement, useCallback, useEffect, useState, type FocusEvent, type MouseEvent, type ReactElement, type ReactNode } from "react";
import { createPortal } from "react-dom";

interface TipPos {
  left: number;
  top?: number;
  bottom?: number;
  placement: "top" | "bottom";
}

const GAP = 8;       // px gap between trigger and tooltip
const MARGIN = 8;    // min px from any viewport edge
const FLIP_MIN = 130; // if less than this much room below, prefer flipping above

// Module-level singleton: only one tooltip is ever open. Moving the pointer directly from one
// trigger to another doesn't always emit a mouseleave on the one being left (so its own close()
// can be skipped), which would strand a tooltip on screen. Whenever any tooltip opens it closes the
// previously-open one, guaranteeing at most one is visible regardless of missed leave events.
let dismissOpenTip: (() => void) | null = null;

/**
 * Hover/focus tooltip that explains a control. Edge-aware: it prefers to sit BELOW the trigger but
 * flips ABOVE when there isn't enough room below (and there's more above), and clamps horizontally
 * so it never spills off either side. Rendered through a portal with `position: fixed` so it can't
 * be clipped by the card's overflow, and `pointer-events: none` so it never eats hover/clicks.
 *
 * Layout-transparent: it clones its single child and attaches handlers + measures via
 * `event.currentTarget`, so it adds no wrapper DOM node and can't disturb flex sizing of the
 * controls it decorates. The child must be a single DOM element (button/div), not a bare component.
 */
export default function InfoTip({ content, children, width = 264 }: { content: ReactNode; children: ReactNode; width?: number }) {
  const [pos, setPos] = useState<TipPos | null>(null);
  // Hold the live trigger element so scroll/resize can re-place the tooltip while it's open.
  const [el, setEl] = useState<HTMLElement | null>(null);

  const place = useCallback((node: HTMLElement) => {
    const r = node.getBoundingClientRect();
    const vw = window.innerWidth;
    const vh = window.innerHeight;
    // Center over the trigger, then clamp into the viewport.
    let left = r.left + r.width / 2 - width / 2;
    left = Math.max(MARGIN, Math.min(left, vw - width - MARGIN));
    const spaceBelow = vh - r.bottom;
    const spaceAbove = r.top;
    // Flip above only when below is cramped AND above genuinely has more room.
    const placement: "top" | "bottom" = spaceBelow < FLIP_MIN && spaceAbove > spaceBelow ? "top" : "bottom";
    setPos(
      placement === "bottom"
        ? { left, top: r.bottom + GAP, placement }
        : { left, bottom: vh - r.top + GAP, placement }
    );
  }, [width]);

  const close = useCallback(() => {
    if (dismissOpenTip === close) dismissOpenTip = null;
    setEl(null);
    setPos(null);
  }, []);
  const open = useCallback((node: HTMLElement) => {
    if (dismissOpenTip && dismissOpenTip !== close) dismissOpenTip();
    dismissOpenTip = close;
    setEl(node);
    place(node);
  }, [place, close]);

  // On unmount, surrender the singleton if we still hold it so a stale closure can't be re-invoked.
  useEffect(() => () => { if (dismissOpenTip === close) dismissOpenTip = null; }, [close]);

  // Keep the tooltip glued to the trigger if the page scrolls or resizes while it's open.
  useEffect(() => {
    if (!el) return;
    const reflow = () => place(el);
    window.addEventListener("scroll", reflow, true);
    window.addEventListener("resize", reflow);
    return () => {
      window.removeEventListener("scroll", reflow, true);
      window.removeEventListener("resize", reflow);
    };
  }, [el, place]);

  const child = Children.only(children);
  if (!isValidElement(child)) return <>{children}</>;
  const c = child as ReactElement<{
    onMouseEnter?: (e: MouseEvent<HTMLElement>) => void;
    onMouseLeave?: (e: MouseEvent<HTMLElement>) => void;
    onFocus?: (e: FocusEvent<HTMLElement>) => void;
    onBlur?: (e: FocusEvent<HTMLElement>) => void;
  }>;

  const merged = cloneElement(c, {
    onMouseEnter: (e: MouseEvent<HTMLElement>) => { c.props.onMouseEnter?.(e); open(e.currentTarget); },
    onMouseLeave: (e: MouseEvent<HTMLElement>) => { c.props.onMouseLeave?.(e); close(); },
    onFocus: (e: FocusEvent<HTMLElement>) => { c.props.onFocus?.(e); open(e.currentTarget); },
    onBlur: (e: FocusEvent<HTMLElement>) => { c.props.onBlur?.(e); close(); },
  });

  return (
    <>
      {merged}
      {pos &&
        createPortal(
          <div
            role="tooltip"
            style={{ position: "fixed", left: pos.left, top: pos.top, bottom: pos.bottom, width }}
            className="z-[100] pointer-events-none rounded-lg border border-fa-edge bg-fa-ink-2/95 px-3 py-2 text-[11px] leading-relaxed text-fa-frost shadow-2xl shadow-fa-ink/80 backdrop-blur-md"
          >
            {content}
          </div>,
          document.body
        )}
    </>
  );
}

/** Small helper to keep tooltip bodies consistent: a frost-bright title line + a dim description. */
export function TipBody({ title, children }: { title: string; children: ReactNode }) {
  return (
    <div>
      <div className="mb-0.5 font-medium text-fa-frost-bright">{title}</div>
      <div className="text-fa-frost-dim">{children}</div>
    </div>
  );
}
