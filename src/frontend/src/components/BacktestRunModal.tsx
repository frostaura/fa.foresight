import { useEffect, useMemo, useState } from "react";
import { X } from "lucide-react";
import {
  ComposedChart, Line, Scatter, XAxis, YAxis, CartesianGrid, Tooltip, ResponsiveContainer, ReferenceLine,
} from "recharts";
import SideDrawer from "./SideDrawer";
import { useGetBacktestBetsQuery, useGetHistoricalCandlesQuery, type Backtest } from "../store/api";

const UP = "#34d399";    // emerald — hit
const DOWN = "#fb7185";  // rose — miss
const NEUTRAL = "#64748b"; // slate — no bet
const PRICE = "#7dd3fc"; // sky — price line
const BAL = "#a78bfa";   // violet — balance overlay

type DotState = "hit" | "miss" | "nobet";
interface Row { t: number; price: number; balance: number; dotY: number; state: DotState; size: number | null; }

function fmtTime(ms: number) {
  const d = new Date(ms);
  return d.toLocaleString(undefined, { month: "short", day: "numeric", hour: "2-digit", minute: "2-digit", hourCycle: "h23" });
}

/**
 * Full report for a single backtest run: a price line over the run window with green/red/grey
 * dots (hit / miss / no-bet) above it, the bank-balance curve overlaid on a second axis, and the
 * per-bet ledger below (latest first) with the cumulative % delta from the initial bankroll.
 *
 * The price series comes from /api/historical-candles (close-only), and the dots + balance from
 * the per-run /bets ledger — so every dot corresponds to a ledger row by construction.
 */
export default function BacktestRunModal({ run, modelName, onClose }: { run: Backtest; modelName: string; onClose: () => void }) {
  const { data: bets, isFetching: betsLoading } = useGetBacktestBetsQuery({ id: run.id, take: 10000 });
  const { data: candles, isFetching: candlesLoading } = useGetHistoricalCandlesQuery({
    symbol: run.symbol, interval: run.interval, start: run.startTime, end: run.endTime,
  });

  const rows: Row[] = useMemo(() => {
    if (!candles || candles.length === 0) return [];
    // Index bets by the candle openTime they settled against.
    const betByT = new Map<number, { won: boolean; size: number; balanceAfter: number }>();
    for (const b of bets ?? []) betByT.set(b.targetOpenTime, { won: b.won, size: b.size, balanceAfter: b.balanceAfter });
    let balance = run.initialBalance;
    return candles.map((c) => {
      const bet = betByT.get(c.t);
      if (bet) balance = bet.balanceAfter;
      const state: DotState = bet ? (bet.won ? "hit" : "miss") : "nobet";
      return { t: c.t, price: c.c, balance, dotY: c.c, state, size: bet?.size ?? null };
    });
  }, [candles, bets, run.initialBalance]);

  const ledger = useMemo(() => {
    // Latest first. cumPct = (balanceAfter - initial) / initial.
    return [...(bets ?? [])]
      .sort((a, b) => b.targetOpenTime - a.targetOpenTime)
      .map((b) => ({
        t: b.targetOpenTime,
        won: b.won,
        side: b.side,
        size: b.size,
        cumPct: (b.balanceAfter - run.initialBalance) / run.initialBalance * 100,
      }));
  }, [bets, run.initialBalance]);

  const loading = betsLoading || candlesLoading;
  const deltaPct = run.finalBalance == null ? null : (run.finalBalance - run.initialBalance) / run.initialBalance * 100;
  const dotFill = (s: DotState) => (s === "hit" ? UP : s === "miss" ? DOWN : NEUTRAL);

  // Keep the loader up while recharts does its (heavy, flickery) first draw OFF-SCREEN at opacity 0,
  // then fade the finished chart in — no more post-render flicker. We mount the chart as soon as the
  // data is ready but only flip `chartReady` after a short beat so the draw lands behind the loader.
  const [chartReady, setChartReady] = useState(false);
  useEffect(() => {
    setChartReady(false);
    if (loading || rows.length === 0) return;
    const t = setTimeout(() => setChartReady(true), 280);
    return () => clearTimeout(t);
  }, [loading, rows]);

  return (
    <SideDrawer open onClose={onClose} widthClass="w-full md:w-[820px] lg:w-[960px]">
      <div className="flex flex-col h-full">
        <div className="px-5 py-4 border-b border-fa-edge flex items-center justify-between gap-3">
          <div className="min-w-0">
            <div className="text-fa-frost-bright text-sm font-medium truncate">{modelName}</div>
            <div className="text-fa-frost-dim text-xs truncate">
              {run.symbol} · {run.interval} · {fmtTime(run.startTime)} → {fmtTime(run.endTime)} · {run.strategyId}
            </div>
          </div>
          <div className="flex items-center gap-3 shrink-0">
            <div className="text-right">
              <div className="text-fa-frost-bright text-sm tabular-nums">
                {run.finalBalance == null ? "—" : `$${run.finalBalance.toFixed(2)}`}
              </div>
              {deltaPct != null && (
                <div className={`text-xs tabular-nums ${deltaPct > 0 ? "text-emerald-300" : deltaPct < 0 ? "text-rose-300" : "text-fa-frost-dim"}`}>
                  {deltaPct >= 0 ? "+" : ""}{deltaPct.toFixed(1)}% · {run.hitRate == null ? "—" : `${(run.hitRate * 100).toFixed(1)}% hit`}
                </div>
              )}
            </div>
            <button onClick={onClose} aria-label="Close" title="Close (Esc)"
              className="h-8 w-8 inline-flex items-center justify-center rounded-md border border-fa-edge bg-fa-glass text-fa-frost-dim hover:text-fa-frost-bright hover:border-fa-frost/30 transition">
              <X className="h-4 w-4" />
            </button>
          </div>
        </div>

        <div className="flex-1 overflow-y-auto px-5 py-4 space-y-5">
          {run.status === "no-bets" && (
            <div className="text-amber-300 text-xs bg-amber-300/10 border border-amber-300/30 rounded-md px-3 py-2">
              {run.error ?? "No bets were placed in this run."}
            </div>
          )}

          {/* Price + balance + dots */}
          <div className="fa-card p-3">
            <div className="text-fa-frost-dim text-[11px] mb-2 flex items-center gap-4">
              <span><span className="inline-block w-2 h-2 rounded-full mr-1" style={{ background: PRICE }} />Price</span>
              <span><span className="inline-block w-2 h-2 rounded-full mr-1" style={{ background: BAL }} />Bank balance</span>
              <span><span className="inline-block w-2 h-2 rounded-full mr-1" style={{ background: UP }} />Hit</span>
              <span><span className="inline-block w-2 h-2 rounded-full mr-1" style={{ background: DOWN }} />Miss</span>
              <span><span className="inline-block w-2 h-2 rounded-full mr-1" style={{ background: NEUTRAL }} />No bet</span>
            </div>
            <div className="relative h-[300px]">
              {/* Chart mounts as soon as data is ready but stays invisible (opacity-0) during its
                  initial draw; once `chartReady` flips we fade the finished render in. */}
              {!loading && rows.length > 0 && (
                <div className={`absolute inset-0 transition-opacity duration-500 ${chartReady ? "opacity-100" : "opacity-0"}`}>
              <ResponsiveContainer width="100%" height={300}>
                <ComposedChart data={rows} margin={{ top: 8, right: 8, left: 0, bottom: 0 }}>
                  <CartesianGrid stroke="#1e293b" strokeDasharray="3 3" />
                  <XAxis dataKey="t" tickFormatter={fmtTime} tick={{ fill: "#64748b", fontSize: 10 }} minTickGap={60} />
                  <YAxis yAxisId="price" orientation="left" domain={["auto", "auto"]} tick={{ fill: "#64748b", fontSize: 10 }} width={56} tickFormatter={(v) => `$${Math.round(v / 1000)}k`} />
                  <YAxis yAxisId="bal" orientation="right" domain={["auto", "auto"]} tick={{ fill: BAL, fontSize: 10 }} width={50} tickFormatter={(v) => `$${v.toFixed(0)}`} />
                  <Tooltip
                    contentStyle={{ background: "#0b1220", border: "1px solid #1e293b", borderRadius: 8, fontSize: 12 }}
                    labelFormatter={(t) => fmtTime(t as number)}
                    formatter={(val: number, name) => [name === "balance" ? `$${val.toFixed(2)}` : `$${val.toFixed(0)}`, name === "balance" ? "Balance" : "Price"]}
                  />
                  <ReferenceLine yAxisId="bal" y={run.initialBalance} stroke={BAL} strokeDasharray="2 4" strokeOpacity={0.4} />
                  <Line yAxisId="price" type="monotone" dataKey="price" stroke={PRICE} strokeWidth={1.5} dot={false} isAnimationActive={false} />
                  <Line yAxisId="bal" type="monotone" dataKey="balance" stroke={BAL} strokeWidth={1.5} dot={false} isAnimationActive={false} />
                  {/* Hit/miss/no-bet dots sit on the price line, coloured per outcome. */}
                  <Scatter yAxisId="price" dataKey="dotY" isAnimationActive={false}
                    shape={(props: { cx?: number; cy?: number; payload?: Row }) => {
                      const { cx, cy, payload } = props;
                      if (cx == null || cy == null || !payload) return <g />;
                      return <circle cx={cx} cy={cy - 6} r={payload.state === "nobet" ? 1.5 : 2.5} fill={dotFill(payload.state)} fillOpacity={payload.state === "nobet" ? 0.5 : 0.95} />;
                    }} />
                </ComposedChart>
              </ResponsiveContainer>
                </div>
              )}
              {/* Loader overlay — shown while fetching AND while the chart draws behind it. */}
              {(loading || (rows.length > 0 && !chartReady)) && (
                <div className="absolute inset-0 flex items-center justify-center gap-2 text-fa-frost-dim text-sm">
                  <span className="h-4 w-4 rounded-full border-2 border-fa-frost-dim/30 border-t-fa-frost-bright animate-spin" />
                  Drawing chart…
                </div>
              )}
              {!loading && rows.length === 0 && (
                <div className="absolute inset-0 flex items-center justify-center text-fa-frost-dim text-sm">No candle data for this window.</div>
              )}
            </div>
          </div>

          {/* Ledger — latest first */}
          <div className="fa-card p-0 overflow-hidden">
            <div className="px-3 py-2 text-fa-frost-bright text-xs font-medium border-b border-fa-edge">
              Ledger · {ledger.length.toLocaleString()} bet{ledger.length === 1 ? "" : "s"}
            </div>
            <div className="max-h-[420px] overflow-y-auto">
              <table className="fa-table-bordered w-full text-xs">
                <thead className="text-fa-frost-dim sticky top-0 bg-fa-ink/95 backdrop-blur">
                  <tr className="text-left">
                    <th className="font-normal py-1 px-3 text-center">Result</th>
                    <th className="font-normal py-1 px-3">Timeframe</th>
                    <th className="font-normal py-1 px-3 text-right">Bet size</th>
                    <th className="font-normal py-1 px-3 text-right">Δ % from start</th>
                  </tr>
                </thead>
                <tbody>
                  {ledger.map((l, i) => (
                    <tr key={`${l.t}-${i}`} className="border-t border-fa-edge/40">
                      <td className="py-1 px-3 text-center">
                        <span className={`inline-block h-2 w-2 rounded-full ${l.won ? "bg-emerald-400 shadow-[0_0_6px_rgba(52,211,153,0.6)]" : "bg-rose-400 shadow-[0_0_6px_rgba(251,113,133,0.6)]"}`}
                          title={`${l.won ? "HIT" : "MISS"} · ${l.side}`} />
                      </td>
                      <td className="py-1 px-3 text-fa-frost-dim tabular-nums">{fmtTime(l.t)}</td>
                      <td className="py-1 px-3 text-right tabular-nums text-fa-frost-bright">${l.size.toFixed(2)}</td>
                      <td className={`py-1 px-3 text-right tabular-nums ${l.cumPct > 0 ? "text-emerald-300" : l.cumPct < 0 ? "text-rose-300" : "text-fa-frost-dim"}`}>
                        {l.cumPct >= 0 ? "+" : ""}{l.cumPct.toFixed(1)}%
                      </td>
                    </tr>
                  ))}
                  {ledger.length === 0 && !loading && (
                    <tr><td colSpan={4} className="py-6 text-center text-fa-frost-dim">No bets in this run.</td></tr>
                  )}
                </tbody>
              </table>
            </div>
          </div>
        </div>
      </div>
    </SideDrawer>
  );
}
