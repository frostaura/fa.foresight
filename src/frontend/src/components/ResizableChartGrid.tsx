import {
  createContext,
  useCallback,
  useContext,
  useLayoutEffect,
  useRef,
  useState,
  type ReactNode,
} from "react";
import { useLocalStorageState } from "../lib/persistedState";
import { cn } from "../lib/cn";

/**
 * Dependency-free, corner-drag-resizable chart grid.
 *
 * The grid derives its column count from its OWN measured width (ResizeObserver), so it adapts to
 * sidebar collapse and viewport changes without horizontal scroll. Each card persists a raw
 * `colSpan` (1..maxCols) and a freeform `height`; the span is clamped to the live column count only
 * for rendering, so a 3-wide card keeps its preference even on a temporarily 1-col layout.
 */

const GAP_PX = 16; // matches gap-4

function clamp(n: number, lo: number, hi: number) {
  return Math.min(Math.max(n, lo), hi);
}

type GridContextValue = {
  /** Live column count derived from the container width. */
  columns: number;
  /** Pixel width of a single grid track (one column). */
  trackPx: number;
  /** Gap between tracks, in px. */
  gapPx: number;
};

const GridContext = createContext<GridContextValue>({
  columns: 1,
  trackPx: 0,
  gapPx: GAP_PX,
});

export function ResizableChartGrid({
  children,
  minColPx = 360,
  maxCols = 3,
  className,
}: {
  children: ReactNode;
  minColPx?: number;
  maxCols?: number;
  className?: string;
}) {
  const ref = useRef<HTMLDivElement | null>(null);
  const [width, setWidth] = useState(0);

  useLayoutEffect(() => {
    const el = ref.current;
    if (!el) return;
    const measure = () => setWidth(el.clientWidth);
    measure();
    const ro = new ResizeObserver(measure);
    ro.observe(el);
    return () => ro.disconnect();
  }, []);

  const columns = clamp(
    Math.floor((width + GAP_PX) / (minColPx + GAP_PX)),
    1,
    maxCols,
  );
  const trackPx = columns > 0 ? (width - (columns - 1) * GAP_PX) / columns : width;

  return (
    <GridContext.Provider value={{ columns, trackPx, gapPx: GAP_PX }}>
      <div
        ref={ref}
        className={cn("grid gap-4", className)}
        style={{ gridTemplateColumns: `repeat(${columns}, minmax(0, 1fr))` }}
      >
        {children}
      </div>
    </GridContext.Provider>
  );
}

type StoredSize = { colSpan: number; height: number };

export function ResizableChartCard({
  id,
  children,
  defaultHeight = 460,
  minHeight = 260,
  maxHeight = 1000,
}: {
  id: string;
  children: ReactNode;
  defaultHeight?: number;
  minHeight?: number;
  maxHeight?: number;
}) {
  const { columns, trackPx, gapPx } = useContext(GridContext);
  const [stored, setStored] = useLocalStorageState<StoredSize>(
    "fa.chartsize." + id,
    { colSpan: 1, height: defaultHeight },
  );

  // Live values during a drag (so the card animates without thrashing localStorage). Null = idle.
  const [live, setLive] = useState<StoredSize | null>(null);
  const drag = useRef<{
    startX: number;
    startY: number;
    startWidth: number;
    startHeight: number;
  } | null>(null);

  const colSpan = live ? live.colSpan : stored.colSpan;
  const height = live ? live.height : stored.height;
  const renderSpan = Math.min(Math.max(colSpan, 1), Math.max(columns, 1));

  const onPointerDown = useCallback(
    (e: React.PointerEvent<HTMLDivElement>) => {
      e.preventDefault();
      e.stopPropagation();
      const span = Math.min(Math.max(stored.colSpan, 1), Math.max(columns, 1));
      const startWidth = span * trackPx + (span - 1) * gapPx;
      drag.current = {
        startX: e.clientX,
        startY: e.clientY,
        startWidth,
        startHeight: stored.height,
      };
      setLive({ colSpan: span, height: stored.height });
      e.currentTarget.setPointerCapture(e.pointerId);
    },
    [columns, trackPx, gapPx, stored.colSpan, stored.height],
  );

  const onPointerMove = useCallback(
    (e: React.PointerEvent<HTMLDivElement>) => {
      const d = drag.current;
      if (!d) return;
      const liveWidth = d.startWidth + (e.clientX - d.startX);
      const targetSpan = clamp(
        Math.round((liveWidth + gapPx) / (trackPx + gapPx)),
        1,
        Math.max(columns, 1),
      );
      const liveHeight = clamp(
        d.startHeight + (e.clientY - d.startY),
        minHeight,
        maxHeight,
      );
      setLive({ colSpan: targetSpan, height: liveHeight });
    },
    [columns, trackPx, gapPx, minHeight, maxHeight],
  );

  const endDrag = useCallback(
    (e: React.PointerEvent<HTMLDivElement>) => {
      if (!drag.current) return;
      drag.current = null;
      setLive((cur) => {
        if (cur) setStored({ colSpan: cur.colSpan, height: cur.height });
        return null;
      });
      try {
        e.currentTarget.releasePointerCapture(e.pointerId);
      } catch {
        // capture may already be released — non-fatal.
      }
    },
    [setStored],
  );

  return (
    <div
      style={{
        gridColumn: `span ${renderSpan} / span ${renderSpan}`,
        height,
      }}
      className={cn("relative min-w-0", live && "select-none")}
    >
      <div className="h-full w-full">{children}</div>

      {/* Bottom-right corner resize grip. Pointer events cover mouse + touch. */}
      <div
        role="separator"
        aria-label="Resize chart"
        className={cn(
          "absolute bottom-1 right-1 h-4 w-4 cursor-se-resize touch-none",
          "text-fa-frost-dim/50 hover:text-fa-frost transition-colors",
        )}
        onPointerDown={onPointerDown}
        onPointerMove={onPointerMove}
        onPointerUp={endDrag}
        onPointerCancel={endDrag}
      >
        <svg viewBox="0 0 16 16" className="h-full w-full" aria-hidden="true">
          <path
            d="M14 6 L6 14 M14 10 L10 14 M14 14 L14 14"
            stroke="currentColor"
            strokeWidth="1.5"
            strokeLinecap="round"
            fill="none"
          />
        </svg>
      </div>
    </div>
  );
}
