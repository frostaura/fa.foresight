import { useEffect, useMemo, useState } from "react";
import { Area, AreaChart, CartesianGrid, Customized, ReferenceLine, ResponsiveContainer, Tooltip, XAxis, YAxis } from "recharts";
import { LivePulse, Spinner } from "./ui";
import { useGetMarketHistoryQuery } from "../store/api";

// Live-poll cadence per timeframe. Short windows refresh near-realtime so the chart visibly
// moves like Polymarket; the "All" view changes slowly so a longer cadence avoids wasted fetches.
const POLL_MS: Record<string, number> = {
  "1h": 5_000,
  "6h": 5_000,
  "1d": 15_000,
  max: 60_000
};

const YES_FILL = "var(--fa-success, #7CE3B6)";
const NO_FILL = "var(--fa-danger, #F08484)";
const MIDLINE = "var(--fa-frost-dim, #5C8AB4)";

// Renders the live-edge dot with a pulsing ring. The `key` prop in the caller forces a remount
// per tick so the ring's CSS animation restarts on every new datapoint — explicit "tick happened"
// signal even when the YES value is unchanged.
function LiveTickDot({ cx, cy, color }: { cx?: number; cy?: number; color: string }) {
  if (cx == null || cy == null) return null;
  return (
    <g style={{ pointerEvents: "none" }}>
      <circle
        cx={cx} cy={cy} r={4}
        fill={color}
        fillOpacity={0.6}
        style={{ transformBox: "fill-box", transformOrigin: "center", animation: "fa-tick-ring 1.8s ease-out forwards" }}
      />
      <circle
        cx={cx} cy={cy} r={3.5}
        fill={color}
        stroke="var(--fa-ink, #06121F)"
        strokeWidth={1}
        style={{ transformBox: "fill-box", transformOrigin: "center", animation: "fa-tick-core 1.2s ease-out" }}
      />
    </g>
  );
}

function YesNoDonut({ yes, size = 52 }: { yes: number; size?: number }) {
  const stroke = 8;
  const r = size / 2 - stroke / 2;
  const c = 2 * Math.PI * r;
  const yesPct = Math.max(0, Math.min(100, yes));
  const yesLen = (yesPct / 100) * c;
  const center = size / 2;
  return (
    <svg width={size} height={size} viewBox={`0 0 ${size} ${size}`} aria-hidden role="img" style={{ transform: "rotate(-90deg)" }}>
      <circle cx={center} cy={center} r={r} fill="none" stroke={NO_FILL} strokeWidth={stroke} strokeOpacity={0.75} />
      <circle
        cx={center} cy={center} r={r} fill="none"
        stroke={YES_FILL} strokeWidth={stroke} strokeOpacity={0.95}
        strokeDasharray={`${yesLen} ${c}`}
        style={{ transition: "stroke-dasharray 600ms ease-out" }}
      />
    </svg>
  );
}

const INTERVALS: { label: string; value: string }[] = [
  { label: "1H", value: "1h" },
  { label: "6H", value: "6h" },
  { label: "1D", value: "1d" },
  { label: "All", value: "max" }
];

function formatTick(iso: string, interval: string): string {
  const d = new Date(iso);
  if (interval === "1h" || interval === "6h") {
    return d.toLocaleTimeString(undefined, { hour: "2-digit", minute: "2-digit" });
  }
  if (interval === "1d") {
    return d.toLocaleTimeString(undefined, { hour: "2-digit", minute: "2-digit" });
  }
  return d.toLocaleDateString(undefined, { month: "short", day: "numeric" });
}

export default function PriceChart({
  providerId,
  externalId,
  livePrice
}: {
  providerId: string;
  externalId: string;
  livePrice?: number;
}) {
  const [interval, setInterval] = useState<string>("1d");
  const { data = [], isFetching, isSuccess } = useGetMarketHistoryQuery(
    { providerId, externalId, interval },
    { pollingInterval: POLL_MS[interval] ?? 15_000 }
  );

  // Tick every 5s so the live edge point keeps advancing on the X-axis even when YES is flat —
  // gives the Polymarket-style "alive" feel without requiring the price to actually change.
  const [nowTick, setNowTick] = useState(() => Date.now());
  useEffect(() => {
    const id = window.setInterval(() => setNowTick(Date.now()), 5_000);
    return () => window.clearInterval(id);
  }, []);

  const series = useMemo(
    () => {
      const base = data.map((p) => {
        const yesPct = Math.round(p.yes * 1000) / 10;
        return { t: p.t, yesPct, noPct: Math.round((100 - yesPct) * 10) / 10 };
      });
      // Always append a synthetic live edge point with a fresh timestamp. Polymarket's history
      // endpoint buckets server-side, so without this the right edge stays frozen between buckets.
      if (livePrice != null && livePrice >= 0 && livePrice <= 1) {
        const livePct = Math.round(livePrice * 1000) / 10;
        base.push({
          t: new Date(nowTick).toISOString(),
          yesPct: livePct,
          noPct: Math.round((100 - livePct) * 10) / 10
        });
      }
      return base;
    },
    [data, livePrice, nowTick]
  );
  const latest = series.length > 0 ? series[series.length - 1].yesPct : null;
  const first = series.length > 0 ? series[0].yesPct : null;
  const delta = latest != null && first != null ? latest - first : null;
  const deltaPositive = delta != null && delta >= 0;
  const stroke = deltaPositive ? YES_FILL : NO_FILL;

  // Auto-zoom the Y axis to the data range with padding so sub-1% moves are visible. Clamp
  // to [0,100] and enforce a minimum span so a flat market still renders as a clear band.
  const yDomain = useMemo<[number, number]>(() => {
    if (series.length === 0) return [0, 100];
    const vals = series.map((p) => p.yesPct);
    const rawMin = Math.min(...vals);
    const rawMax = Math.max(...vals);
    const MIN_SPAN = 4;
    const span = Math.max(MIN_SPAN, rawMax - rawMin);
    const pad = Math.max(0.5, span * 0.15);
    const lo = Math.max(0, Math.min(rawMin - pad, 100 - span - pad * 2));
    const hi = Math.min(100, lo + span + pad * 2);
    return [Math.floor(lo), Math.ceil(hi)];
  }, [series]);
  const [yLo, yHi] = yDomain;
  const yTicks = useMemo(() => {
    const lo = yLo;
    const hi = yHi;
    const ticks = [lo, Math.round((lo + hi) / 2), hi];
    if (50 > lo && 50 < hi && !ticks.includes(50)) ticks.push(50);
    return Array.from(new Set(ticks)).sort((a, b) => a - b);
  }, [yLo, yHi]);
  const showMidline = 50 > yLo && 50 < yHi;

  return (
    <div className="fa-card p-4">
      <div className="flex items-center justify-between mb-3 gap-4 flex-wrap">
        <div className="flex items-center gap-3">
          <YesNoDonut yes={latest ?? 0} />
          <div>
            <div className="text-fa-frost-dim text-[10px] uppercase tracking-wider">Probability</div>
            <div className="flex items-baseline gap-4 mt-0.5">
              <div className="flex items-baseline gap-1.5">
                <span className="text-2xl font-light tabular-nums" style={{ color: YES_FILL }}>
                  {latest != null ? `${latest.toFixed(1)}%` : "—"}
                </span>
                <span className="text-[10px] uppercase tracking-wider text-fa-frost-dim">Yes</span>
              </div>
              <div className="flex items-baseline gap-1.5">
                <span className="text-lg font-light tabular-nums" style={{ color: NO_FILL }}>
                  {latest != null ? `${(100 - latest).toFixed(1)}%` : "—"}
                </span>
                <span className="text-[10px] uppercase tracking-wider text-fa-frost-dim">No</span>
              </div>
              {delta != null && (
                <div className={`text-xs tabular-nums ${deltaPositive ? "text-emerald-300" : "text-rose-300"}`}>
                  {deltaPositive ? "+" : ""}{delta.toFixed(1)} pts
                </div>
              )}
            </div>
          </div>
        </div>
        <div className="flex items-center gap-2">
          {isSuccess && series.length > 0 ? (
            <LivePulse title={`Live — refreshing every ${(POLL_MS[interval] ?? 15_000) / 1000}s`} />
          ) : null}
          <div className="flex gap-1 rounded-md border border-fa-edge p-0.5">
          {INTERVALS.map((i) => (
            <button
              key={i.value}
              onClick={() => setInterval(i.value)}
              className={`px-3 py-1 text-xs rounded transition ${
                interval === i.value
                  ? "bg-fa-frost/20 text-fa-frost-bright"
                  : "text-fa-frost-dim hover:text-fa-frost-bright"
              }`}
            >
              {i.label}
            </button>
          ))}
          </div>
        </div>
      </div>

      <div className="h-72">
        {isFetching && series.length === 0 ? (
          <div className="h-full flex items-center justify-center text-fa-frost-dim text-sm gap-2">
            <Spinner /> Loading price history…
          </div>
        ) : series.length === 0 ? (
          <div className="h-full flex items-center justify-center text-fa-frost-dim text-sm">
            No price history available for this market.
          </div>
        ) : (
          <ResponsiveContainer width="100%" height="100%">
            <AreaChart data={series} margin={{ top: 8, right: 8, left: -8, bottom: 0 }}>
              <CartesianGrid stroke="rgb(255 255 255 / 0.05)" vertical={false} />
              <XAxis
                dataKey="t"
                tickFormatter={(v: string) => formatTick(v, interval)}
                tick={{ fill: "rgb(148 163 184)", fontSize: 11 }}
                stroke="rgb(255 255 255 / 0.1)"
                minTickGap={48}
              />
              <YAxis
                domain={yDomain}
                ticks={yTicks}
                tickFormatter={(v: number) => `${v}%`}
                tick={{ fill: "rgb(148 163 184)", fontSize: 11 }}
                stroke="rgb(255 255 255 / 0.1)"
                width={44}
                allowDataOverflow={false}
              />
              <Tooltip
                cursor={{ stroke: "rgb(148 163 184 / 0.4)", strokeDasharray: "2 2" }}
                content={({ active, payload, label }) => {
                  if (!active || !payload?.length) return null;
                  const yesP = payload.find((p) => p.dataKey === "yesPct");
                  const yesV = yesP && typeof yesP.value === "number" ? yesP.value : Number(yesP?.value ?? 0);
                  const noV = Math.round((100 - yesV) * 10) / 10;
                  return (
                    <div className="rounded-md border border-fa-edge bg-fa-ink/95 px-3 py-2 text-xs shadow-lg backdrop-blur-sm">
                      <div className="text-fa-frost-dim mb-1">
                        {label ? new Date(label as string).toLocaleString() : ""}
                      </div>
                      <div className="tabular-nums flex items-center justify-between gap-4 leading-tight" style={{ color: YES_FILL }}>
                        <span>YES</span>
                        <span>{yesV.toFixed(1)}%</span>
                      </div>
                      <div className="tabular-nums flex items-center justify-between gap-4 leading-tight" style={{ color: NO_FILL }}>
                        <span>NO</span>
                        <span>{noV.toFixed(1)}%</span>
                      </div>
                    </div>
                  );
                }}
              />
              {/* 50% toss-up reference — only drawn when it falls inside the zoomed range. */}
              {showMidline && (
                <ReferenceLine y={50} stroke={MIDLINE} strokeOpacity={0.45} strokeDasharray="3 2" strokeWidth={1} />
              )}
              {/* Trend-colored area + line — auto-zoomed so sub-1% moves are visible. */}
              <Area
                type="monotone"
                dataKey="yesPct"
                stroke={stroke}
                strokeWidth={2}
                fill={stroke}
                fillOpacity={0.16}
                activeDot={{ r: 4, fill: stroke, stroke: "var(--fa-ink, #06121F)", strokeWidth: 1 }}
                isAnimationActive={false}
              />
              {/* Live-tick pulse — Customized layer reads the rendered Area's last point so the
                  dot positions correctly on every timeframe (category XAxis is fussy otherwise).
                  Keyed on nowTick so the CSS ripple restarts on every new datapoint. */}
              <Customized
                key={nowTick}
                component={(props: unknown) => {
                  const items = (props as { formattedGraphicalItems?: Array<{ props?: { points?: Array<{ x: number; y: number }> } }> }).formattedGraphicalItems;
                  if (!items?.length) return null;
                  const pts = items[0]?.props?.points;
                  if (!pts?.length) return null;
                  const last = pts[pts.length - 1];
                  if (last?.x == null || last?.y == null) return null;
                  return <LiveTickDot cx={last.x} cy={last.y} color={stroke} />;
                }}
              />
            </AreaChart>
          </ResponsiveContainer>
        )}
      </div>
    </div>
  );
}
