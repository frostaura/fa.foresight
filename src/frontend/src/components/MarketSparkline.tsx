import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { useGetMarketHistoryQuery } from "../store/api";

const WIDTH = 160;
const HEIGHT = 36;
const PAD_Y = 3;
const TARGET_YES = 0.5; // toss-up reference; standard target for binary prediction markets.
const MIN_RANGE = 0.04; // minimum span in probability units — keeps flat series from collapsing to a hairline.
const RANGE_PAD = 0.15; // headroom above/below the data range so the line never hugs the edge.

// Design-system tokens — line/dot are trend-colored; fills convey direction on the auto-zoomed axis.
const TREND_UP = "var(--fa-success, #7CE3B6)";
const TREND_DOWN = "var(--fa-danger, #F08484)";
const GUIDE = "var(--fa-edge, rgba(164, 212, 244, 0.18))";
const MIDLINE = "var(--fa-frost-dim, #5C8AB4)";

function fmtTime(iso: string): string {
  const d = new Date(iso);
  const now = Date.now();
  const ageH = (now - d.getTime()) / 36e5;
  if (ageH < 24) return d.toLocaleTimeString(undefined, { hour: "2-digit", minute: "2-digit" });
  if (ageH < 24 * 7) return d.toLocaleDateString(undefined, { weekday: "short", hour: "2-digit", minute: "2-digit" });
  return d.toLocaleDateString(undefined, { month: "short", day: "numeric" });
}

export default function MarketSparkline({
  providerId,
  externalId,
  interval = "1d"
}: {
  providerId: string;
  externalId: string;
  interval?: string;
}) {
  const wrapRef = useRef<HTMLDivElement | null>(null);
  const svgRef = useRef<SVGSVGElement | null>(null);
  const [visible, setVisible] = useState(false);
  const [hoverIdx, setHoverIdx] = useState<number | null>(null);

  useEffect(() => {
    if (visible) return;
    const el = wrapRef.current;
    if (!el) return;
    const io = new IntersectionObserver(
      (entries) => {
        if (entries.some((e) => e.isIntersecting)) {
          setVisible(true);
          io.disconnect();
        }
      },
      { rootMargin: "200px 0px" }
    );
    io.observe(el);
    return () => io.disconnect();
  }, [visible]);

  const { data = [], isFetching } = useGetMarketHistoryQuery(
    { providerId, externalId, interval },
    { skip: !visible }
  );

  const chart = useMemo(() => {
    if (data.length < 2) return null;
    const ys = data.map((p) => p.yes);
    const xSpan = Math.max(1e-9, data.length - 1);
    const usableH = HEIGHT - PAD_Y * 2;
    // Auto-zoom Y to the data range so sub-1% moves are visible. Clamp to [0,1] and enforce a
    // minimum span so a flat series renders as a band, not a hairline.
    const rawMin = Math.min(...ys);
    const rawMax = Math.max(...ys);
    const span = Math.max(MIN_RANGE, rawMax - rawMin);
    const pad = span * RANGE_PAD;
    const yMin = Math.max(0, Math.min(rawMin - pad, 1 - span - pad * 2));
    const yMax = Math.min(1, yMin + span + pad * 2);
    const range = Math.max(1e-9, yMax - yMin);
    const valueToY = (v: number) => HEIGHT - PAD_Y - ((v - yMin) / range) * usableH;
    const points = data.map((p, i) => {
      const x = (i / xSpan) * WIDTH;
      const y = valueToY(p.yes);
      return [x, y] as const;
    });
    const line = points.map(([x, y], i) => `${i === 0 ? "M" : "L"}${x.toFixed(2)},${y.toFixed(2)}`).join(" ");
    const fillArea = `${line} L${WIDTH.toFixed(2)},${(HEIGHT - PAD_Y).toFixed(2)} L0,${(HEIGHT - PAD_Y).toFixed(2)} Z`;
    const last = points[points.length - 1];
    const lastValue = data[data.length - 1].yes;
    const positive = ys[ys.length - 1] >= ys[0];
    const midInRange = TARGET_YES >= yMin && TARGET_YES <= yMax;
    return {
      line,
      fillArea,
      positive,
      points,
      last,
      lastValue,
      yMin,
      yMax,
      yMid: midInRange ? valueToY(TARGET_YES) : null
    };
  }, [data]);

  const onPointer = useCallback((e: React.PointerEvent<SVGSVGElement>) => {
    if (!chart || !svgRef.current || data.length < 2) return;
    const rect = svgRef.current.getBoundingClientRect();
    const rel = (e.clientX - rect.left) / Math.max(1, rect.width);
    const idx = Math.round(rel * (data.length - 1));
    setHoverIdx(Math.max(0, Math.min(data.length - 1, idx)));
  }, [chart, data.length]);
  const onLeave = useCallback(() => setHoverIdx(null), []);

  const hover = hoverIdx != null && chart ? {
    pt: chart.points[hoverIdx],
    yes: data[hoverIdx].yes,
    t: data[hoverIdx].t
  } : null;

  const ready = visible && !isFetching && chart != null;
  const tooltipLeft = hover ? Math.max(0, Math.min(100, (hover.pt[0] / WIDTH) * 100)) : 0;
  const stroke = chart?.positive ? TREND_UP : TREND_DOWN;

  return (
    <div
      ref={wrapRef}
      className="relative h-9 w-full"
      onClick={(e) => { if (hoverIdx != null) e.preventDefault(); }}
    >
      {!visible || (isFetching && data.length === 0) ? (
        <div className="h-full w-full rounded bg-fa-glass" />
      ) : chart == null ? (
        <div className="h-full w-full rounded bg-fa-glass flex items-center justify-center text-[10px] text-fa-frost-dim/60">
          no history
        </div>
      ) : (
        <div className="h-full w-full flex items-stretch" style={{ opacity: ready ? 1 : 0, transition: "opacity 500ms ease-out" }}>
          <div className="w-7 shrink-0 flex flex-col justify-between text-[8px] tabular-nums leading-none text-fa-frost-dim/70 py-px pointer-events-none select-none">
            <span>{(chart.yMax * 100).toFixed(0)}%</span>
            <span>{(chart.lastValue * 100).toFixed(0)}%</span>
            <span>{(chart.yMin * 100).toFixed(0)}%</span>
          </div>
          <div className="flex-1 relative">
            <svg
              ref={svgRef}
              viewBox={`0 0 ${WIDTH} ${HEIGHT}`}
              preserveAspectRatio="none"
              onPointerMove={onPointer}
              onPointerLeave={onLeave}
              className="h-full w-full block cursor-crosshair"
            >
              {/* Range bounds — subtle horizontal guides at the auto-zoomed yMin/yMax. */}
              <line x1={0} y1={PAD_Y} x2={WIDTH} y2={PAD_Y} stroke={GUIDE} strokeWidth={0.5} vectorEffect="non-scaling-stroke" />
              <line x1={0} y1={HEIGHT - PAD_Y} x2={WIDTH} y2={HEIGHT - PAD_Y} stroke={GUIDE} strokeWidth={0.5} vectorEffect="non-scaling-stroke" />

              {/* Trend-colored fill below the price line — direction at a glance. */}
              <path d={chart.fillArea} fill={stroke} fillOpacity={0.16} />

              {/* 50% toss-up reference — only drawn when in range. */}
              {chart.yMid != null && (
                <line
                  x1={0} y1={chart.yMid} x2={WIDTH} y2={chart.yMid}
                  stroke={MIDLINE} strokeOpacity={0.45} strokeDasharray="3 2" strokeWidth={0.6}
                  vectorEffect="non-scaling-stroke"
                />
              )}

              {/* Current price guide — dashed, trend-colored. */}
              <line
                x1={0} y1={chart.last[1]} x2={WIDTH} y2={chart.last[1]}
                stroke={stroke} strokeOpacity={0.4} strokeDasharray="3 2" strokeWidth={0.6}
                vectorEffect="non-scaling-stroke"
                style={{ transition: "y1 600ms ease-out, y2 600ms ease-out" }}
              />

              <path d={chart.line} fill="none" stroke={stroke} strokeWidth={1.5} vectorEffect="non-scaling-stroke" />
              <circle
                cx={chart.last[0]} cy={chart.last[1]} r={2.5}
                fill={stroke} stroke="var(--fa-ink, #06121F)" strokeWidth={1}
                vectorEffect="non-scaling-stroke"
                style={{ transition: "cy 600ms ease-out, cx 600ms ease-out" }}
              />
              {hover && (
                <>
                  <line
                    x1={hover.pt[0]} y1={0} x2={hover.pt[0]} y2={HEIGHT}
                    stroke="rgb(148 163 184 / 0.6)" strokeWidth={1}
                    vectorEffect="non-scaling-stroke" strokeDasharray="2 2"
                  />
                  <circle
                    cx={hover.pt[0]} cy={hover.pt[1]} r={2.5}
                    fill={stroke} stroke="var(--fa-ink, #06121F)" strokeWidth={1}
                    vectorEffect="non-scaling-stroke"
                  />
                </>
              )}
            </svg>

            {hover && (
              <div
                className="pointer-events-none absolute -top-10 z-10 -translate-x-1/2 whitespace-nowrap rounded border border-fa-edge bg-fa-ink/95 px-2 py-1 text-[10px] leading-tight text-fa-frost shadow-lg"
                style={{ left: `${tooltipLeft}%` }}
              >
                <div className="tabular-nums text-fa-frost-bright">{(hover.yes * 100).toFixed(1)}% YES</div>
                <div className="text-fa-frost-dim">{fmtTime(hover.t)}</div>
              </div>
            )}
          </div>
        </div>
      )}
    </div>
  );
}
