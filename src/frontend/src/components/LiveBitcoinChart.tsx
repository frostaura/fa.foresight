import { useCallback, useEffect, useMemo, useRef, useState, type ReactNode } from "react";
import { useSort, SortHeader } from "../lib/sort";
import {
  Area,
  Bar,
  CartesianGrid,
  ComposedChart,
  Line,
  ReferenceDot,
  ReferenceLine,
  ResponsiveContainer,
  Tooltip,
  XAxis,
  YAxis
} from "recharts";
import { ArrowDown, ArrowUp, Maximize2, Minimize2, Sparkles, Star } from "lucide-react";
import PaperTradingPanel from "./PaperTradingPanel";
import InfoTip, { TipBody } from "./InfoTip";
import { modelNeedsTraining, useModelTrainGate } from "./ModelTrainGate";
import ShimmerOnChange from "./ShimmerOnChange";
// `Tooltip` from recharts (above) collides with our UI Tooltip — alias to keep both.
import { LivePulse, Spinner, Tooltip as UiTooltip } from "./ui";
import { useLiveKlines, INTERVAL_MS, type BinanceInterval, type Candle } from "../lib/binance";
import { useLiveTimeframeFavorites } from "../lib/liveFavorites";
import { useLivePredictionStream } from "../lib/livePredictionStream";
import { usePaperSession } from "../lib/paperTrading";
import {
  useGetPolymarketReferenceQuery,
  useListActiveModelsQuery,
  useListModelsQuery,
  usePredictLiveMutation,
  useSetActiveModelMutation,
  type LivePrediction,
  type PolymarketReference
} from "../store/api";

type ViewMode = "chart" | "table";

export type ChartKind = "line" | "bar" | "candle";

const UP = "#7CE3B6";
const DOWN = "#F08484";
const NEUTRAL = "#5C8AB4";
const SKIP = "#64748B"; // slate grey — a candle the gate says NOT to bet; not a hit, not a miss.
const LIVE = "#FFA94D"; // orange — in-progress candle marker (TABLE view only; the chart's
                       // active-candle dot is rendered by PendingActiveDot in a lean-driven colour).

// Betting decision gate — mirrors the BACKTESTER's confidence gate (v6 min_confidence = 0.04,
// where confidence = |pUp - 0.5| * 2). Inside the ±2pp band (pUp ∈ (0.48, 0.52)) the model has no
// actionable edge, so the strategy places NO bet — and a bet that's never placed can neither hit
// nor miss. The chart treats these candles as grey "skip" dots and drops them from the visible
// hit-rate, so the headline measures only the bets we'd actually have made. Kept in sync with the
// BetCapsule call + the backtester (BuiltInModels.cs v6 LogisticRegression min_confidence).
const BACKTEST_MIN_CONFIDENCE = 0.04;
function betDecision(pUp: number): "up" | "no" | "down" {
  const conf = Math.abs(pUp - 0.5) * 2;
  return conf < BACKTEST_MIN_CONFIDENCE ? "no" : pUp >= 0.5 ? "up" : "down";
}

// Glowy result dot — sits above its target candle's vertical guide, pinned to the top of the
// chart's y-domain via ReferenceDot. Emerald = hit, rose = miss, amber = pending. The halo
// circle is a same-colour, semi-transparent disc behind the solid dot — gives the glow without
// needing an SVG filter (which doesn't compose cleanly inside recharts' shape callback).
// Native SVG <title> drives the browser-default hover tooltip so the user can hover-verify
// bet side + outcome without flipping to TABLE — necessary because the dot colour is sourced
// from `bet.outcome` (server-recorded) and can disagree with what the display *currently* says
// pUp is, whenever calibration has drifted since the bet was placed.
function ResultDot({ cx, cy, state, tooltip }: { cx?: number; cy?: number; state: "open" | "hit" | "miss" | "skip"; tooltip?: string }) {
  if (cx == null || cy == null) return null;
  const color = state === "open" ? "#E8C26A" : state === "hit" ? UP : state === "miss" ? DOWN : SKIP;
  return (
    <g>
      {tooltip ? <title>{tooltip}</title> : null}
      <circle cx={cx} cy={cy} r={6} fill={color} fillOpacity={0.28} />
      <circle cx={cx} cy={cy} r={3.2} fill={color} />
    </g>
  );
}

/**
 * Score a prediction against the chart's own Binance candles. 2-STEP CANON: the bet direction is
 * `close[T] > close[T-2]` — the target's close vs the ANCHOR (the last closed candle at decision
 * time, two bars before the target). The intervening candle (T-1) was still forming when the bet
 * was decided, so it is excluded as both input and reference. This is the exact question the bet
 * settles on and the backend resolves on, so the dot reflects the real win/loss — NOT the target
 * candle's own red/green body (close vs T-1), which can now legitimately differ.
 *
 * Reading off the chart's candles map keeps the dot using the same Binance numbers the candles are
 * drawn from; close[T-2] equals the prediction's stored `anchorClose`. We fall back to the backend
 * `anchorClose`/`actualClose` only when the candle pair is outside the visible REST window.
 */
function predictionMarkerStateForCandles(
  p: LivePrediction,
  closeByOpenTime: Map<number, number>,
  intervalMs: number
): "open" | "hit" | "miss" {
  const pUp = p.directionUpProbabilityCalibrated ?? p.directionUpProbability;
  const predictedUp = pUp >= 0.5;
  const targetCloseChart = closeByOpenTime.get(p.targetOpenTime);
  // 1-step grading: live predictions are horizon-1 (next candle vs the one before it), so the
  // reference is the IMMEDIATELY-PRIOR candle (prevClose), NOT two bars back. This makes the
  // hit/miss dot always agree with the candle's own colour: a green candle (close > prevClose = up)
  // is a HIT for an UP call and a MISS for a DOWN call. (Was `- 2 * intervalMs`, which graded
  // against a different reference and could mark a DOWN call on a green candle as a HIT.)
  const prevCloseChart = closeByOpenTime.get(p.targetOpenTime - intervalMs);
  // Grade as soon as the target candle is CLOSED — detected by its successor candle existing in the
  // map (the next bar has started). This deliberately does NOT wait for the backend's `resolvedAt`,
  // which lags the candle boundary by a few seconds and otherwise strands a stale amber "pending"
  // dot on the candle that just closed. The successor check also keeps the still-forming candle
  // "open" (no successor yet) so we never grade a live, non-final close. See
  // [[feedback_hitmiss_from_chart_candles]].
  const targetClosed = closeByOpenTime.has(p.targetOpenTime + intervalMs);
  if (targetClosed && targetCloseChart != null && prevCloseChart != null) {
    const actualUp = targetCloseChart > prevCloseChart;
    return predictedUp === actualUp ? "hit" : "miss";
  }
  // Backend fallback for resolved predictions whose candle pair isn't in the chart's REST window.
  if (p.resolvedAt && p.actualClose != null) {
    return predictedUp === (p.actualClose > p.anchorClose) ? "hit" : "miss";
  }
  return "open";
}

// Pulsing dot above the active (still-forming) candle's column. Colour is binary — emerald when
// the in-flight bet would resolve as a hit if the bar closed now, rose when it would be a miss.
// Uses fa-glow-* keyframes that hold the inner dot at opacity ≥ 0.85 so the fill colour stays
// saturated through the whole cycle and never dims into a muddy / orange-looking shade against
// the dark background (the orange impression came from the old fa-live-* keyframes dropping the
// inner dot to opacity 0.15 at the trough, which over `--fa-ink` looked brownish). Caller must
// gate render on a non-null lean.
function PendingActiveDot({ cx, cy, lean }: { cx?: number; cy?: number; lean: "winning" | "losing" | null }) {
  if (cx == null || cy == null) return null;
  // The active (still-forming) candle's orb ALWAYS shows the real lean — emerald if the in-flight
  // bet would currently hit, rose if it would miss. It NEVER greys on the no-bet gate: this is the
  // live "is my position winning" signal and is always meaningful, even for a low-conviction call.
  // Grey/skip applies only to RESOLVED historical no-bet candles, never the candle in progress.
  // Null lean (cold start, no live price yet) falls back to amber "pending".
  const color = lean === "winning" ? UP : lean === "losing" ? DOWN : "#E8C26A";
  return (
    <g style={{ pointerEvents: "none" }}>
      <circle cx={cx} cy={cy} fill={color} className="fa-glow-ring" />
      <circle cx={cx} cy={cy} fill={color} stroke="var(--fa-ink, #06121F)" strokeWidth={1} className="fa-glow-dot" />
    </g>
  );
}

// Candle row used by recharts. `range = [low, high]` lets us draw the wick using a regular Bar
// with the y-scale already applied; the body is drawn explicitly in the custom shape. `prevClose`
// is the close of the immediately-prior candle in the full series — used to colour the body by its
// own move (close-vs-prevClose, not close-vs-open). NOTE under the 2-step canon the hit/miss dots
// score a DIFFERENT question — close(T) vs the anchor close(T-2) — so a dot's verdict can legitimately
// differ from this candle's body colour. When prevClose is missing (first candle in history), the
// shape falls back to standard open-vs-close colouring.
interface Row extends Candle {
  range: [number, number];
  prevClose: number | null;
}

function formatTick(ms: number): string {
  const d = new Date(ms);
  return new Intl.DateTimeFormat(undefined, { hour: "2-digit", minute: "2-digit", hourCycle: "h23" }).format(d);
}

function fmtUsd(v: number): string {
  return `$${v.toLocaleString(undefined, { minimumFractionDigits: 2, maximumFractionDigits: 2 })}`;
}

// Recharts hands a Bar's shape function the bar's scaled rect (x/y/width/height) plus the
// original datum. We reuse the y-scale by computing (high-low) → height, then derive body
// coordinates from the same scale via simple linear interpolation.
type CandleShapeProps = {
  x?: number; y?: number; width?: number; height?: number;
  low?: number; high?: number;
  payload?: Row;
};

function CandleShape(props: CandleShapeProps) {
  const { x = 0, y = 0, width = 0, height = 0, payload } = props;
  if (!payload || width <= 0 || height <= 0) return null;
  const { open, close, high, low, prevClose } = payload;
  // Direction is graded against the prior candle's close — same definition the model uses for
  // `directionUpProbability` and the same one the hit/miss dots score against. This is also what
  // Polymarket-style "BTC Up or Down" markets resolve on. Standard close-vs-open colouring would
  // let a green candle print under a "DOWN was right" hit dot whenever the bar opens above the
  // prior close and then drifts down — visually inconsistent with the rest of the card. Fallback
  // to close-vs-open only when no prior candle is available (very first row in the series).
  const reference = prevClose ?? open;
  // Strict `>` — equality must read as "not up" so this rule matches the hit/miss verdict and the
  // model's directional definition exactly. A previous `>=` here caused a 1-cent doji (close ==
  // prevClose) to print green while the dot scored it as a DOWN-HIT (strict comparison saying the
  // bar didn't go up), producing a "green candle with green HIT dot for a DOWN call" contradiction.
  const isUp = close > reference;
  const color = isUp ? UP : DOWN;
  const range = high - low;
  if (range === 0) return null;

  // y is the pixel for `high`, y + height is the pixel for `low`. Map open/close into that band.
  const yFor = (v: number) => y + ((high - v) / range) * height;
  const yOpen = yFor(open);
  const yClose = yFor(close);
  const bodyTop = Math.min(yOpen, yClose);
  const bodyH = Math.max(1, Math.abs(yClose - yOpen));

  const cx = x + width / 2;
  const bodyW = Math.max(2, width * 0.7);

  return (
    <g>
      {/* Wick */}
      <line x1={cx} x2={cx} y1={y} y2={y + height} stroke={color} strokeWidth={1} />
      {/* Body */}
      <rect
        x={cx - bodyW / 2}
        y={bodyTop}
        width={bodyW}
        height={bodyH}
        fill={isUp ? color : color}
        fillOpacity={isUp ? 0.85 : 0.85}
        stroke={color}
        strokeWidth={1}
      />
    </g>
  );
}

export default function LiveBitcoinChart({
  symbol,
  interval,
  kind,
  visibleCount = 15,
  limit = 500,
  hidePaperPanel = false,
  fill = false,
  bare = false
}: {
  symbol: string;
  interval: BinanceInterval;
  kind: ChartKind;
  visibleCount?: number;
  limit?: number;
  /**
   * Chromeless mode. When true the card drops its own `fa-card` shell (border / background /
   * rounded corners) and renders just padded content, so it can sit seamlessly inside an outer
   * container (e.g. the Live session card's single rounded shell) without nesting rounded boxes.
   */
  bare?: boolean;
  /**
   * Live-trading mode. When true the card skips its internal paper-trading strip (the Live page
   * renders its own real-money numbers strip alongside) AND hides the per-card ModelPicker (the
   * live session pins one model). Default false → behaviour identical to the Paper Trading surface.
   */
  hidePaperPanel?: boolean;
  /**
   * Fill mode. When true the card stretches to its parent's height (`h-full`) and the plot area
   * grows to fill remaining space (`flex-1 min-h-0`) instead of the fixed `h-56`. Used by the
   * resizable chart grid so a card's height tracks its resized container. Independent of fullscreen.
   */
  fill?: boolean;
}) {
  const { candles, loading, error } = useLiveKlines(symbol, interval, limit);
  const { isFav, toggle } = useLiveTimeframeFavorites();
  const favorited = isFav(symbol, interval);
  const [view, setView] = useState<ViewMode>("chart");
  const [fullscreen, setFullscreen] = useState(false);

  // Escape key collapses fullscreen — standard UX for any overlay
  useEffect(() => {
    if (!fullscreen) return;
    const handler = (e: KeyboardEvent) => { if (e.key === "Escape") setFullscreen(false); };
    window.addEventListener("keydown", handler);
    return () => window.removeEventListener("keydown", handler);
  }, [fullscreen]);

  // When on, the chart respects the backtester's confidence gate: candles the model wouldn't bet on
  // (pUp in the ±2pp no-band) render as grey "skip" dots and drop out of the visible hit-rate. When
  // off, every prediction is a virtual bet and counts hit/miss. Persisted globally so the choice
  // sticks across cards and reloads. Off by default — the always-bet view is the baseline.
  const [gateNoBets, setGateNoBets] = useState<boolean>(
    () => typeof localStorage !== "undefined" && localStorage.getItem("fa.chart.gateNoBets") === "1"
  );
  const toggleGate = useCallback(() => {
    setGateNoBets((v) => {
      const next = !v;
      try { localStorage.setItem("fa.chart.gateNoBets", next ? "1" : "0"); } catch { /* ignore */ }
      return next;
    });
  }, []);

  // Server-recorded predictions (overlay + accuracy). SSE push, never polled. Backfilled once via
  // REST on mount; thereafter the backend's LivePredictionEventHub pushes `created` and `resolved`
  // events through a single shared EventSource per page. Cards subscribe through the shared hook
  // and filter their slice by (symbol, interval) — no per-card socket.
  // Resolve the active model for this card (same fallback chain as ModelPicker + the backend
  // ActiveModelResolver). We filter the prediction stream to this model's name so switching the
  // model updates the next-candle headline + chart dots in realtime — the backend already
  // invalidates its resolver cache on switch, so the next predict uses the new model immediately.
  const { data: allModelsForActive } = useListModelsQuery();
  const { data: activeModelRows } = useListActiveModelsQuery();
  const activeModelName = useMemo(() => {
    if (!allModelsForActive || allModelsForActive.length === 0) return undefined;
    const row = activeModelRows?.find((a) => a.symbol === symbol && a.interval === interval);
    const tenantDefault = allModelsForActive.find((m) => m.isDefault && m.tenantId !== null);
    const globalDefault = allModelsForActive.find((m) => m.isDefault && m.tenantId === null);
    const fallback = tenantDefault ?? globalDefault ?? allModelsForActive[0];
    const id = row?.modelId ?? fallback?.id;
    return allModelsForActive.find((m) => m.id === id)?.name;
  }, [allModelsForActive, activeModelRows, symbol, interval]);

  const { predictions } = useLivePredictionStream(symbol, interval, 200, activeModelName);
  // Paper-trading session for this card. We subscribe a second time here (PaperTradingPanel
  // below already calls usePaperSession too) so the chart-dot layer can be driven directly off
  // the bet ledger — "every dot corresponds to a ledger row" by construction. Both subscriptions
  // share the same SSE EventSource under the hood (usePaperSession refcounts), so the only real
  // cost is one extra REST snapshot per card mount.
  const { session: paperSession } = usePaperSession(symbol, interval);
  const [triggerPredict, { isLoading: isPredicting, error: predictError }] = usePredictLiveMutation();

  // Once-per-second nowMs tick — drives the countdown to the next candle close. Independent of
  // the candle-poll cadence so the countdown stays smooth even on slower timeframes.
  const [nowMs, setNowMs] = useState(() => Date.now());
  useEffect(() => {
    const id = window.setInterval(() => setNowMs(Date.now()), 1_000);
    return () => window.clearInterval(id);
  }, []);

  // Auto-predict on mount and whenever the candle boundary advances. horizon=1 covers the next,
  // not-yet-started candle (the next bettable round). horizon=0 covers the CURRENTLY-FORMING candle
  // so its pulsing orb always has a side to render: after a model switch the new model only has the
  // +2 (next) candle from the live path and backfilled CLOSED candles — the forming candle (+1)
  // falls in the gap, which is why its orb used to vanish and the last-closed candle's dot looked
  // like a misplaced orb. Both calls are idempotent on (symbol, interval, target) so they're no-ops
  // once the rows exist.
  const lastCandle = candles[candles.length - 1];
  const nextTargetTime = lastCandle ? lastCandle.openTime + INTERVAL_MS[interval] : 0;
  useEffect(() => {
    if (!lastCandle) return;
    if (!predictions.some((p) => p.targetOpenTime === nextTargetTime)) {
      triggerPredict({ symbol, interval, horizon: 1 });
    }
    if (!predictions.some((p) => p.targetOpenTime === lastCandle.openTime)) {
      triggerPredict({ symbol, interval, horizon: 0 });
    }
    // activeModelName in deps: switching the model re-fires both predicts immediately (the backend
    // resolver cache is invalidated on switch), so the next-candle headline AND the active orb
    // refresh in realtime for the newly-selected model.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [symbol, interval, nextTargetTime, activeModelName]);

  const nextPrediction = predictions.find((p) => p.targetOpenTime === nextTargetTime);

  // Hide any historical horizon=0 predictions (target = currently-active candle). New predictions
  // are horizon=1 only, but the backend may still hold legacy horizon=0 rows; filtering keeps the
  // active column free of "…" markers that imply a mid-candle entry.
  const displayablePredictions = useMemo(
    () => predictions.filter((p) => p.targetOpenTime !== lastCandle?.openTime),
    [predictions, lastCandle?.openTime]
  );

  // ms remaining until the current candle closes (i.e., until the next betting round opens).
  // Clamps to 0 so brief clock-skew or stale poll doesn't render a negative.
  const msToNextCandle = lastCandle ? Math.max(0, lastCandle.openTime + INTERVAL_MS[interval] - nowMs) : 0;

  // No boundary refetch needed — when a candle closes, the auto-predict mutation above fires for
  // the new nextTargetTime, which runs ResolveMaturedAsync on the backend as a side effect; every
  // resolved row is then pushed through the SSE stream as a `resolved` event. Wholly event-driven.

  // Try to find a live Polymarket binary that aligns with the prediction's target candle. When
  // present, its YES price is the canonical fair-market reference; otherwise we fall back to 0.5.
  // Only the next-candle horizon needs this (active candle is mid-window with no clean market).
  const { data: polyRef } = useGetPolymarketReferenceQuery(
    nextTargetTime > 0
      ? { symbol, targetOpenTimeMs: nextTargetTime, intervalMs: INTERVAL_MS[interval] }
      : { symbol, targetOpenTimeMs: 0, intervalMs: 0 },
    { skip: !nextTargetTime }
  );

  const tradeSignal = useMemo(
    () => computeTradeSignal(nextPrediction, polyRef ?? null),
    [nextPrediction, polyRef]
  );

  // Prediction the system made for the currently-active (still-forming) candle — the bar that
  // owns the live orange marker. Created when this bar was the "next candle" one interval ago;
  // surfacing it next to the new next-candle headline lets the user track the *open* bet without
  // dropping into the table. `displayablePredictions` filters this out by design (it's mid-candle
  // and shouldn't render a chart dot), so we read from the raw `predictions` array here.
  const currentPrediction = useMemo(
    () => predictions.find((p) => p.targetOpenTime === lastCandle?.openTime),
    [predictions, lastCandle?.openTime]
  );

  // Live-model indicator state. The prediction stream is already filtered to the active model, so a
  // visible prediction whose `model` matches the selection is PROOF the picked model is the one
  // producing the forecasts. "live" = confirmed driving; "syncing" = a fresh predict is in flight
  // (e.g. just switched models, awaiting its first forecast); "idle" = nothing yet.
  const modelStatus: "live" | "syncing" | "idle" =
    activeModelName && (nextPrediction?.model === activeModelName || currentPrediction?.model === activeModelName)
      ? "live"
      : isPredicting ? "syncing" : "idle";

  // openTime → close map sourced from the chart's own Binance klines. Drives every hit/miss
  // computation in this file so the dots, the headline accuracy chip, and the CURR/PREV strips
  // all agree with the candle bodies. Reading off the same data the candles render from is what
  // guarantees "green candle ↔ HIT for an UP bet" without exception.
  const closeByOpenTime = useMemo(() => {
    const m = new Map<number, number>();
    for (const c of candles) m.set(c.openTime, c.close);
    return m;
  }, [candles]);

  // Real-time lean for the CURR strip: would the bet be a HIT if the bar closed right now?
  // Compares the in-flight live close to the close of the *immediately prior* candle as Binance
  // reports it, so this matches whatever the chart is drawing — never the backend's anchor (which
  // drifts and causes the dot↔candle disagreements). Null only when we can't form a verdict at all
  // (no current prediction, no live candle, or cold start without a prior candle).
  //
  // Exact ties (live close === priorClose) used to return null, which made the dot flash through an
  // amber/neutral colour. We now resolve ties the same way the hit/miss verdict does: actualUp =
  // close > priorClose under strict `>`, so a tie counts as DOWN. Result: UP bet at tie = losing,
  // DOWN bet at tie = winning — symmetric with [[feedback-candle-color-direction]] and the dot stays
  // in {emerald, rose} without a third state.
  const currentLean = useMemo<"winning" | "losing" | null>(() => {
    if (!currentPrediction || !lastCandle || candles.length < 2) return null;
    // 1-step grading: the in-flight (horizon-1) bet is "this candle vs the one before it", so the
    // reference is the IMMEDIATELY-PRIOR candle's close (= the forming candle's prevClose), which is
    // exactly what its body colour is measured against. So the pending orb always matches the live
    // candle colour: a green forming candle is "winning" for UP and "losing" for DOWN.
    const prevClose = candles[candles.length - 2].close;
    const pUp = currentPrediction.directionUpProbabilityCalibrated ?? currentPrediction.directionUpProbability;
    const betUp = pUp >= 0.5;
    const liveUp = lastCandle.close > prevClose;
    return betUp === liveUp ? "winning" : "losing";
  }, [currentPrediction, lastCandle?.close, lastCandle?.openTime, candles]);

  // Most recently resolved prediction — the "did the last call hit or miss" readout sits next to
  // the live next-candle line so the user can sanity-check the system in one glance without
  // dropping into TABLE view. Newest-first scan over displayablePredictions; the SSE stream and
  // REST snapshot are both already DESC by targetOpenTime so the first resolved row IS the latest.
  // Select the most-recent prediction that is RESOLVED ON THE CHART (its target candle has closed —
  // graded by the same close-vs-prevClose rule as the dots + candle body), NOT one that merely has a
  // backend resolvedAt. This keeps the PREV chip in lock-step with the dots/body and avoids the lag
  // where backend resolution trails the candle boundary. See [[feedback_hitmiss_from_chart_candles]].
  const previousPrediction = useMemo(
    () => displayablePredictions.find(
      (p) => predictionMarkerStateForCandles(p, closeByOpenTime, INTERVAL_MS[interval]) !== "open"
    ),
    [displayablePredictions, closeByOpenTime, interval]
  );

  // Slice the tail to honor the page-level zoom. Stats (delta, y-domain, dividers) are derived
  // from the visible slice so the y-axis tightens as the user zooms in — that's what makes
  // small recent moves actually readable. `prevClose` is computed against the FULL series before
  // slicing, so even the leftmost visible bar carries the correct prior close (the candle just
  // off-screen). Without that, the first visible bar would silently fall back to open-vs-close
  // colouring on every zoom and disagree with the rest.
  const rows: Row[] = useMemo(() => {
    const withPrev = candles.map((c, i) => ({
      ...c,
      range: [c.low, c.high] as [number, number],
      prevClose: i > 0 ? candles[i - 1].close : null,
    }));
    return visibleCount > 0 ? withPrev.slice(-visibleCount) : withPrev;
  }, [candles, visibleCount]);

  // Visible-only accuracy of the always-on virtual strategy. Every resolved prediction in view
  // counts as a virtual bet; hit-rate = virtual wins / total. When a paper session is live, the
  // real bet outcomes for covered candles take precedence over the virtual derivation so the
  // headline can't disagree with the ledger. Predictions are the source of truth for uncovered
  // candles — the chart dots and the headline read the same outcome by construction.
  const accuracy = useMemo(() => {
    const visibleOpenTimes = new Set(rows.map((r) => r.openTime));
    let count = 0;
    let hits = 0;
    const covered = new Set<number>();
    if (paperSession) {
      for (const b of paperSession.bets) {
        if (!b.resolved) continue;
        if (!visibleOpenTimes.has(b.targetOpenTime)) continue;
        // Same chart-side rescoring the dots use — see resultDots — so the headline can never
        // disagree with what's drawn above the candles.
        const sideUp = b.side === "UP";
        const targetClose = closeByOpenTime.get(b.targetOpenTime);
        // 1-step grading: vs prevClose (immediately-prior candle) so it matches the candle body.
        const prevClose = closeByOpenTime.get(b.targetOpenTime - INTERVAL_MS[interval]);
        let hit: boolean;
        if (targetClose != null && prevClose != null) {
          hit = sideUp === (targetClose > prevClose);
        } else if (b.outcome != null) {
          hit = b.outcome === "win";
        } else {
          continue;
        }
        count += 1;
        if (hit) hits += 1;
        covered.add(b.targetOpenTime);
      }
    }
    for (const p of displayablePredictions) {
      if (!p.resolvedAt) continue;
      if (!visibleOpenTimes.has(p.targetOpenTime)) continue;
      if (covered.has(p.targetOpenTime)) continue;
      // With the gate on, no-bet candles never enter the hit-rate: the gate said skip, so there's no
      // virtual bet to win or lose. Counting them would dilute the headline with outcomes the
      // strategy disowns. Gate off → every prediction counts (the always-bet baseline).
      const pUp = p.directionUpProbabilityCalibrated ?? p.directionUpProbability;
      if (gateNoBets && betDecision(pUp) === "no") continue;
      const state = predictionMarkerStateForCandles(p, closeByOpenTime, INTERVAL_MS[interval]);
      if (state === "open") continue;
      count += 1;
      if (state === "hit") hits += 1;
    }
    if (count === 0) return null;
    return { count, hitRate: hits / count };
  }, [paperSession, displayablePredictions, rows, closeByOpenTime, interval, gateNoBets]);

  const last = rows[rows.length - 1];
  const first = rows[0];
  const delta = last && first ? last.close - first.open : 0;
  const up = delta >= 0;
  const trendColor = up ? UP : DOWN;

  // Y-domain fits tightly to the visible OHLC range with a small percentage pad on both sides so
  // wicks don't kiss the chart frame. The upper bound is padded more generously (8% vs 4% bottom)
  // so the result-orb ReferenceDots — positioned at yDomain[1] — render fully inside the plot area
  // without being clipped by the SVG's top edge. The chart margin (top: 20) adds additional pixel
  // headroom above the top domain gridline, which is where the orbs sit.
  const yDomain = useMemo<[number, number]>(() => {
    if (rows.length === 0) return [0, 1];
    const lo = Math.min(...rows.map((r) => r.low));
    const hi = Math.max(...rows.map((r) => r.high));
    const range = Math.max(1, hi - lo);
    // Bottom: 4% pad (wicks clear the bottom axis). Top: 8% pad (orbs must sit fully inside).
    return [lo - range * 0.04, hi + range * 0.08];
  }, [rows]);

  // One vertical divider per candle boundary — that's the "every X of the timeframe" behaviour
  // (every 1m for 1m candles, every 15m for 15m, etc.). Drop density on long histories so the
  // grid doesn't visually drown the candles.
  const dividerTimes = useMemo(() => {
    if (rows.length === 0) return [] as number[];
    const everyN = rows.length > 60 ? Math.ceil(rows.length / 30) : 1;
    return rows.filter((_, i) => i % everyN === 0).map((r) => r.openTime);
  }, [rows]);

  const rowOpenTimes = useMemo(() => new Set(rows.map((r) => r.openTime)), [rows]);

  // Per-candle hit/miss markers. The strategy is always running virtually — every prediction is
  // treated as a virtual bet whose side is decided by calibrated pUp (same StakingEngine logic
  // PaperTradingService uses), and a green/red/yellow dot is rendered for every visible candle
  // with a prediction. This is the system's hit-rate readout, independent of whether real paper
  // trading is on. When a real paper session is live, the actual bet outcome takes precedence so
  // the chart and the ledger can't disagree on candles the real session covers.
  const resultDots = useMemo(() => {
    const dots: { id: string; anchor: number; state: "open" | "hit" | "miss" | "skip"; tooltip: string }[] = [];
    const covered = new Set<number>();
    if (paperSession) {
      for (const bet of paperSession.bets) {
        if (!rowOpenTimes.has(bet.targetOpenTime)) continue;
        // Score paper bets against the chart's own candles, same rule the PREV chip and the
        // candle body use. `bet.outcome` is server-recorded off the backend's `anchorClose`,
        // which drifts a few dollars from the actual prior-bar Binance close and can flip the
        // verdict at small ranges — that produced "PREV says HIT but dot says MISS" on the same
        // candle. Fall back to `bet.outcome` only for bets whose candle pair is outside the
        // chart's REST window (very old bets). See [[feedback-hitmiss-from-chart-candles]].
        const sideUp = bet.side === "UP";
        const targetClose = closeByOpenTime.get(bet.targetOpenTime);
        // 1-step grading: vs prevClose (immediately-prior candle) so the dot matches the candle body.
        const prevClose = closeByOpenTime.get(bet.targetOpenTime - INTERVAL_MS[interval]);
        // Grade once the target candle is closed (its successor exists) OR the backend resolved it,
        // scoring from the chart's own closes. This stops the just-closed candle showing a stale
        // amber dot while the backend resolution lags the boundary. See [[feedback_hitmiss_from_chart_candles]].
        const targetClosed = closeByOpenTime.has(bet.targetOpenTime + INTERVAL_MS[interval]);
        let state: "open" | "hit" | "miss";
        if ((targetClosed || bet.resolved) && targetClose != null && prevClose != null) {
          state = sideUp === (targetClose > prevClose) ? "hit" : "miss";
        } else if (bet.resolved) {
          state = bet.outcome === "win" ? "hit" : "miss";
        } else {
          state = "open";
        }
        const placedAt = new Date(bet.placedAt).toLocaleTimeString(undefined, { hour: "2-digit", minute: "2-digit", second: "2-digit", hourCycle: "h23" });
        const sizeStr = `$${bet.size.toFixed(2)}`;
        const tooltip = state === "open"
          ? `${bet.side} · open · placed ${placedAt} · ${sizeStr}`
          : `${bet.side} · ${state} · placed ${placedAt} · ${sizeStr}` +
            (bet.payout != null ? ` · payout $${bet.payout.toFixed(2)}` : "");
        dots.push({ id: `bet:${bet.id}`, anchor: bet.targetOpenTime, state, tooltip });
        covered.add(bet.targetOpenTime);
      }
    }
    for (const p of displayablePredictions) {
      if (!rowOpenTimes.has(p.targetOpenTime)) continue;
      if (covered.has(p.targetOpenTime)) continue;
      const pUp = p.directionUpProbabilityCalibrated ?? p.directionUpProbability;
      const side = pUp >= 0.5 ? "UP" : "DOWN";
      const pct = (pUp * 100).toFixed(0);
      // Gate on → a no-band prediction is a candle we wouldn't have bet, so it shows grey "skip"
      // and is excluded from the hit-rate (see accuracy). Gate off → it scores hit/miss like the rest.
      if (gateNoBets && betDecision(pUp) === "no") {
        dots.push({ id: `pred:${p.id}`, anchor: p.targetOpenTime, state: "skip", tooltip: `${side} (${pct}%) · no bet (gate)` });
        continue;
      }
      const state = predictionMarkerStateForCandles(p, closeByOpenTime, INTERVAL_MS[interval]);
      const tooltip = state === "open"
        ? `${side} (${pct}%) · virtual · open`
        : `${side} (${pct}%) · virtual · ${state}`;
      dots.push({ id: `pred:${p.id}`, anchor: p.targetOpenTime, state, tooltip });
    }
    return dots;
  }, [paperSession, rowOpenTimes, displayablePredictions, closeByOpenTime, interval, gateNoBets]);

  // Bank-balance overlay series. Built from the paper-session ledger: each resolved bet contributes
  // one point (openTime → balanceAfter). Points are snapped to the candle's targetOpenTime so they
  // align with the x-axis ticks. Unresolved bets are skipped — they don't have a final balance yet.
  // The initial balance is prepended as a synthetic point at the session's first bet's targetOpenTime
  // minus one interval so there's a meaningful left anchor even before any bet resolves. When no
  // session exists (or no resolved bets), the series is empty and the overlay is not rendered.
  const balanceSeries = useMemo<{ openTime: number; balance: number }[]>(() => {
    if (!paperSession || paperSession.bets.length === 0) return [];
    const intervalMs = INTERVAL_MS[interval];
    const resolved = paperSession.bets
      .filter((b) => b.resolved && b.balanceAfter != null)
      .sort((a, b) => a.targetOpenTime - b.targetOpenTime);
    if (resolved.length === 0) return [];
    // Initial balance point one candle before the first resolved bet so the line has a visible
    // left origin. Clamped to the visible window so it doesn't force x-scale expansion.
    const points: { openTime: number; balance: number }[] = [
      { openTime: resolved[0].targetOpenTime - intervalMs, balance: paperSession.initialBalance },
      ...resolved.map((b) => ({ openTime: b.targetOpenTime, balance: b.balanceAfter! })),
    ];
    return points;
  }, [paperSession, interval]);

  // Balance Y-domain (hidden secondary YAxis). The balance line reads as a mini-chart sitting on the
  // SAME bottom baseline as the price candles: recharts maps domain[0] → the plot's bottom edge, so
  // we anchor domain[0] just below the lowest balance (trough hugs the bottom border) and stretch
  // the domain top so the peak only reaches ~45% of the height — keeping the whole balance trend in
  // the lower portion, overlaid on the big chart with their bottoms matching.
  const balanceYDomain = useMemo<[number, number]>(() => {
    if (balanceSeries.length === 0) return [0, 1];
    const vals = balanceSeries.map((p) => p.balance);
    const maxBal = Math.max(...vals);
    const minBal = Math.min(...vals);
    // Floor the span for a flat/single-point series so the line doesn't blow up to full height.
    const span = Math.max(maxBal - minBal, Math.max(maxBal * 0.02, 1));
    const lo = minBal - span * 0.05;  // trough ~flush with the bottom border
    const hi = lo + span / 0.45;      // peak reaches ~45% up → bottom-anchored mini-chart
    return [lo, hi];
  }, [balanceSeries]);

  // Merge balance points into the chart row data so recharts can render them on the same x-axis.
  // Each row gets a `balance` field if a ledger point exists for that candle, otherwise undefined
  // (recharts treats undefined as a gap — that's fine, the line only draws where bets resolved).
  const rowsWithBalance = useMemo<(Row & { balance?: number })[]>(() => {
    if (balanceSeries.length === 0) return rows;
    const byTime = new Map(balanceSeries.map((p) => [p.openTime, p.balance]));
    return rows.map((r) => {
      const bal = byTime.get(r.openTime);
      return bal != null ? { ...r, balance: bal } : r;
    });
  }, [rows, balanceSeries]);

  return (
    <div className={fullscreen
      ? "fixed inset-0 z-50 bg-fa-ink border border-fa-edge flex flex-col p-4"
      : `${bare ? "" : "fa-card "}p-4 flex flex-col${fill ? " h-full" : ""}`
    }>
      {/* Header — two compact rows that hold regardless of card width (these cards live in a
          responsive grid, so a viewport-keyed breakpoint can't tell how wide the card actually is).
          Row 1: favorite/interval pill (left) + full-screen (right). Row 2: model selector + gate +
          view toggle. Two clean lines, never three. */}
      <div className="flex items-center gap-2">
        <InfoTip
          content={
            <TipBody title="Favorite timeframe">
              {favorited
                ? "Pinned to your Favorites tab so this timeframe loads first. Click to unpin."
                : "Pin this timeframe to your Favorites tab so it loads first instead of scrolling the full list."}
            </TipBody>
          }
        >
          <button
            onClick={() => toggle(symbol, interval)}
            aria-label={favorited ? "Unfavorite timeframe" : "Favorite timeframe"}
            className={`shrink-0 inline-flex items-center gap-1.5 rounded-md border px-2 py-1 fa-overline transition ${
              favorited
                ? "border-amber-300/40 bg-amber-300/10 text-amber-300 hover:bg-amber-300/15"
                : "border-fa-edge bg-fa-glass text-fa-frost-dim hover:text-fa-frost-bright hover:border-fa-frost/30"
            }`}
          >
            <Star className="h-3 w-3" fill={favorited ? "currentColor" : "none"} />
            {interval}
          </button>
        </InfoTip>

        <button
          onClick={() => setFullscreen((v) => !v)}
          aria-label={fullscreen ? "Exit full screen" : "Full screen"}
          title={fullscreen ? "Exit full screen (Esc)" : "Full screen"}
          className="shrink-0 ml-auto p-1.5 rounded-md border border-fa-edge text-fa-frost-dim hover:text-fa-frost-bright hover:bg-fa-glass transition"
          onKeyDown={(e) => { if (e.key === "Escape") setFullscreen(false); }}
        >
          {fullscreen
            ? <Minimize2 className="h-3.5 w-3.5" />
            : <Maximize2 className="h-3.5 w-3.5" />}
        </button>
      </div>

      {/* Row 2 — model selector (flex-1, shrinks) + gate + view toggle. */}
      <div className="flex items-center gap-2 min-w-0 mt-1.5">
          {!hidePaperPanel && <ModelPicker symbol={symbol} interval={interval} grow status={modelStatus} />}
          <InfoTip
            content={
              <TipBody title={`Confidence gate · ${gateNoBets ? "ON" : "OFF"}`}>
                Only count candles the model is confident enough to bet (pUp outside 48–52%).
                {gateNoBets
                  ? " On: no-bet candles show grey and are dropped from the hit-rate, so accuracy reflects only the bets the strategy would actually place."
                  : " Off: every prediction counts as a virtual bet. Click to skip the no-bet band."}
              </TipBody>
            }
          >
            <button
              onClick={toggleGate}
              aria-pressed={gateNoBets}
              className={`shrink-0 px-2.5 py-1 fa-overline rounded-md border transition ${
                gateNoBets
                  ? "border-fa-frost/40 bg-fa-frost/20 text-fa-frost-bright"
                  : "border-fa-edge text-fa-frost-dim hover:text-fa-frost-bright"
              }`}
            >
              Gate
            </button>
          </InfoTip>
          <div className="shrink-0 flex gap-0.5 rounded-md border border-fa-edge p-0.5">
            {(["chart", "table"] as ViewMode[]).map((m) => (
              <button
                key={m}
                onClick={() => setView(m)}
                className={`px-2.5 py-0.5 fa-overline rounded transition ${
                  view === m
                    ? "bg-fa-frost/20 text-fa-frost-bright"
                    : "text-fa-frost-dim hover:text-fa-frost-bright"
                }`}
              >
                {m}
              </button>
            ))}
          </div>
        </div>

      {/* Row 2 — next-candle band: direction, conf, countdown, trade signal, accuracy. Separated
          from row 1 by a hairline divider inside the component, not a nested tinted box. */}
      <NextCandleAction
        prediction={nextPrediction}
        currentPrediction={currentPrediction}
        currentLean={currentLean}
        previousPrediction={previousPrediction}
        closeByOpenTime={closeByOpenTime}
        intervalMs={INTERVAL_MS[interval]}
        isLoading={isPredicting && !nextPrediction}
        hasError={!!predictError && !nextPrediction}
        accuracy={accuracy}
        msToNextCandle={msToNextCandle}
        tradeSignal={tradeSignal}
        showLive={!loading && !error && rows.length > 0}
      />

      {/* Row 3 — paper trade strip (own band so it doesn't compete with the action card).
          Suppressed in live mode (hidePaperPanel): the Live page owns its real-money strip. */}
      {!hidePaperPanel && (
        <PaperTradingPanel
          symbol={symbol}
          interval={interval}
          closeByOpenTime={closeByOpenTime}
          intervalMs={INTERVAL_MS[interval]}
        />
      )}

      {/* Chart/table container. h-56 in grid card; flex-1 fills the remaining viewport height
          in fullscreen mode for a proper tablet dashboard view. */}
      <div className={`${fullscreen || fill ? "flex-1 min-h-0" : "h-56"} mt-3 relative`}>
        {/* LivePulse now lives inside the NextCandleAction header next to "Next candle". */}
        {loading && rows.length === 0 ? (
          <div className="h-full flex items-center justify-center text-fa-frost-dim text-sm gap-2">
            <Spinner /> Loading {interval}…
          </div>
        ) : error ? (
          <div className="h-full flex items-center justify-center text-rose-300 text-xs px-3 text-center">
            {error}
          </div>
        ) : rows.length === 0 ? (
          <div className="h-full flex items-center justify-center text-fa-frost-dim text-sm">No data.</div>
        ) : view === "table" ? (
          <CandleTable
            rows={rows}
            nextPrediction={nextPrediction}
            nextTargetTime={nextTargetTime}
            predictions={predictions}
          />
        ) : kind === "line" ? (
          <ResponsiveContainer width="100%" height="100%">
            <ComposedChart data={rowsWithBalance} margin={{ top: 20, right: 8, left: 4, bottom: 0 }}>
              <CartesianGrid stroke="rgb(255 255 255 / 0.05)" vertical={false} />
              <XAxis dataKey="openTime" type="number" domain={["dataMin", "dataMax"]} scale="time"
                tickFormatter={(v: number) => formatTick(v)}
                tick={{ fill: "rgb(148 163 184)", fontSize: 10 }}
                stroke="rgb(255 255 255 / 0.1)" minTickGap={48} />
              {/* Primary Y-axis: BTC price scale */}
              <YAxis yAxisId="price" domain={yDomain} tickFormatter={(v: number) => `$${Math.round(v).toLocaleString()}`}
                tick={{ fill: "rgb(148 163 184)", fontSize: 10 }}
                stroke="rgb(255 255 255 / 0.1)" width={60} />
              {/* Secondary Y-axis: bank balance — hidden, scaled independently so the balance line
                  reads as an overlay trend without compressing or stretching the price candles. */}
              {balanceSeries.length > 0 && (
                <YAxis yAxisId="balance" orientation="right" domain={balanceYDomain} hide />
              )}
              <Tooltip
                cursor={{ stroke: "rgb(148 163 184 / 0.4)", strokeDasharray: "2 2" }}
                content={renderTooltip(rows, last?.openTime, nextPrediction)}
              />
              {dividerTimes.map((t) => (
                <ReferenceLine key={t} yAxisId="price" x={t} stroke={NEUTRAL} strokeOpacity={0.12} strokeWidth={1} />
              ))}
              <Area yAxisId="price" type="monotone" dataKey="close" stroke={trendColor} strokeWidth={2}
                fill={trendColor} fillOpacity={0.12} isAnimationActive={false} />
              {/* Bank-balance overlay — glowing FrostAura blue line. Rendered after the area fill
                  so it composites on top of the price series. connectNulls=false lets the line draw
                  only between resolved-bet candles (gaps for candles with no bet outcome). */}
              {balanceSeries.length > 0 && (
                <Line yAxisId="balance" type="monotone" dataKey="balance"
                  stroke="#A4D4F4" strokeWidth={1.5} dot={false} isAnimationActive={false}
                  connectNulls={false} strokeOpacity={0.5} strokeDasharray="2 4" strokeLinecap="round"
                />
              )}
              {resultDots.map((d) => (
                <ReferenceDot
                  key={d.id}
                  yAxisId="price"
                  x={d.anchor}
                  y={yDomain[1]}
                  ifOverflow="visible"
                  shape={(props: unknown) => (
                    <ResultDot {...(props as { cx?: number; cy?: number })} state={d.state} tooltip={d.tooltip} />
                  )}
                />
              ))}
              {last && currentPrediction && (
                <ReferenceDot
                  yAxisId="price"
                  x={last.openTime}
                  y={yDomain[1]}
                  ifOverflow="visible"
                  shape={(p: unknown) => <PendingActiveDot {...(p as { cx?: number; cy?: number })} lean={currentLean} />}
                />
              )}
            </ComposedChart>
          </ResponsiveContainer>
        ) : kind === "bar" ? (
          <ResponsiveContainer width="100%" height="100%">
            <ComposedChart data={rowsWithBalance} margin={{ top: 20, right: 8, left: 4, bottom: 0 }}>
              <CartesianGrid stroke="rgb(255 255 255 / 0.05)" vertical={false} />
              <XAxis dataKey="openTime" type="category"
                tickFormatter={(v: number) => formatTick(v)}
                tick={{ fill: "rgb(148 163 184)", fontSize: 10 }}
                stroke="rgb(255 255 255 / 0.1)" minTickGap={48} />
              <YAxis yAxisId="price" domain={yDomain} tickFormatter={(v: number) => `$${Math.round(v).toLocaleString()}`}
                tick={{ fill: "rgb(148 163 184)", fontSize: 10 }}
                stroke="rgb(255 255 255 / 0.1)" width={60} />
              {balanceSeries.length > 0 && (
                <YAxis yAxisId="balance" orientation="right" domain={balanceYDomain} hide />
              )}
              <Tooltip
                cursor={{ fill: "rgb(148 163 184 / 0.08)" }}
                content={renderTooltip(rows, last?.openTime, nextPrediction)}
              />
              {dividerTimes.map((t) => (
                <ReferenceLine key={t} yAxisId="price" x={t} stroke={NEUTRAL} strokeOpacity={0.12} strokeWidth={1} />
              ))}
              <Bar
                yAxisId="price"
                dataKey="close"
                isAnimationActive={false}
                shape={(props: unknown) => {
                  const { x = 0, y = 0, width = 0, height = 0, payload } =
                    props as { x?: number; y?: number; width?: number; height?: number; payload?: Row };
                  if (!payload) return <g />;
                  const isUp = payload.close >= payload.open;
                  const color = isUp ? UP : DOWN;
                  const w = Math.max(2, width * 0.7);
                  return (
                    <rect
                      x={x + (width - w) / 2}
                      y={y}
                      width={w}
                      height={Math.max(1, height)}
                      fill={color}
                      fillOpacity={0.6}
                    />
                  );
                }}
              />
              {balanceSeries.length > 0 && (
                <Line yAxisId="balance" type="monotone" dataKey="balance"
                  stroke="#A4D4F4" strokeWidth={1.5} dot={false} isAnimationActive={false}
                  connectNulls={false} strokeOpacity={0.5} strokeDasharray="2 4" strokeLinecap="round"
                />
              )}
              {resultDots.map((d) => (
                <ReferenceDot
                  key={d.id}
                  yAxisId="price"
                  x={d.anchor}
                  y={yDomain[1]}
                  ifOverflow="visible"
                  shape={(props: unknown) => (
                    <ResultDot {...(props as { cx?: number; cy?: number })} state={d.state} tooltip={d.tooltip} />
                  )}
                />
              ))}
              {last && currentPrediction && (
                <ReferenceDot
                  yAxisId="price"
                  x={last.openTime}
                  y={yDomain[1]}
                  ifOverflow="visible"
                  shape={(p: unknown) => <PendingActiveDot {...(p as { cx?: number; cy?: number })} lean={currentLean} />}
                />
              )}
            </ComposedChart>
          </ResponsiveContainer>
        ) : (
          <ResponsiveContainer width="100%" height="100%">
            <ComposedChart data={rowsWithBalance} margin={{ top: 20, right: 8, left: 4, bottom: 0 }}>
              <CartesianGrid stroke="rgb(255 255 255 / 0.05)" vertical={false} />
              <XAxis dataKey="openTime" type="category"
                tickFormatter={(v: number) => formatTick(v)}
                tick={{ fill: "rgb(148 163 184)", fontSize: 10 }}
                stroke="rgb(255 255 255 / 0.1)" minTickGap={48} />
              {/* Primary Y-axis: BTC price scale */}
              <YAxis yAxisId="price" domain={yDomain} tickFormatter={(v: number) => `$${Math.round(v).toLocaleString()}`}
                tick={{ fill: "rgb(148 163 184)", fontSize: 10 }}
                stroke="rgb(255 255 255 / 0.1)" width={60} />
              {/* Secondary Y-axis: bank balance — hidden, independently scaled so the balance line
                  reads as an overlay trend without compressing or distorting the price axis. */}
              {balanceSeries.length > 0 && (
                <YAxis yAxisId="balance" orientation="right" domain={balanceYDomain} hide />
              )}
              <Tooltip
                cursor={{ fill: "rgb(148 163 184 / 0.08)" }}
                content={renderTooltip(rows, last?.openTime, nextPrediction)}
              />
              {dividerTimes.map((t) => (
                <ReferenceLine key={t} yAxisId="price" x={t} stroke={NEUTRAL} strokeOpacity={0.12} strokeWidth={1} />
              ))}
              {/* `range` = [low, high] lets recharts compute the wick height; CandleShape draws
                  body + wick using the original OHLC fields off payload. */}
              <Bar yAxisId="price" dataKey="range" isAnimationActive={false} shape={(p: unknown) => <CandleShape {...(p as CandleShapeProps)} />} />
              {/* Bank-balance glowing-blue overlay line. Rendered above the candles so it reads as a
                  portfolio-performance trend compositing over the price view. Uses a secondary hidden
                  Y-axis so the balance scale is independent of the BTC price scale. Only shown when
                  a paper session with at least one resolved bet exists. */}
              {balanceSeries.length > 0 && (
                <Line yAxisId="balance" type="monotone" dataKey="balance"
                  stroke="#A4D4F4" strokeWidth={1.5} dot={false} isAnimationActive={false}
                  connectNulls={false} strokeOpacity={0.5} strokeDasharray="2 4" strokeLinecap="round"
                />
              )}
              {resultDots.map((d) => (
                <ReferenceDot
                  key={d.id}
                  yAxisId="price"
                  x={d.anchor}
                  y={yDomain[1]}
                  ifOverflow="visible"
                  shape={(props: unknown) => (
                    <ResultDot {...(props as { cx?: number; cy?: number })} state={d.state} tooltip={d.tooltip} />
                  )}
                />
              ))}
              {last && currentPrediction && (
                <ReferenceDot
                  yAxisId="price"
                  x={last.openTime}
                  y={yDomain[1]}
                  ifOverflow="visible"
                  shape={(p: unknown) => <PendingActiveDot {...(p as { cx?: number; cy?: number })} lean={currentLean} />}
                />
              )}
            </ComposedChart>
          </ResponsiveContainer>
        )}
      </div>
    </div>
  );
}

interface TradeSignal {
  side: "YES" | "NO"; // YES = bet UP, NO = bet DOWN
  edge: number;       // probability advantage on the chosen side, vs the fair-market reference
}

// Always-trade Trade Signal: take the model's direction every time, no confidence gating, no
// stake sizing (that's paper-trading's job). Polymarket reference is used silently as the fair
// price when available so the edge metric is honest; otherwise we fall back to 0.5 synthetic.
//
// Edge is computed off the *calibrated* pUp — the same value the headline "UP/DOWN n%" reads, the
// same value the bet capsule reads, and the same value the server's StakingEngine.DecideSide
// actually bets on. Using raw here meant the card could show "DOWN 49%" next to "+3.6% edge" while
// the bet capsule sat on `no` — three numbers from the same prediction, two different probability
// universes. Cold start: raw fallback until calibration has data.
function computeTradeSignal(
  prediction: LivePrediction | undefined,
  polyRef: PolymarketReference | null
): TradeSignal | null {
  if (!prediction) return null;
  const p = prediction.directionUpProbabilityCalibrated ?? prediction.directionUpProbability;
  const polyValid = polyRef?.yesPrice != null && polyRef.yesPrice > 0.02 && polyRef.yesPrice < 0.98;
  const marketYes = polyValid ? polyRef!.yesPrice! : 0.5;
  const betYes = p >= marketYes;
  const side: "YES" | "NO" = betYes ? "YES" : "NO";
  const probSide = betYes ? p : 1 - p;
  const marketSide = betYes ? marketYes : 1 - marketYes;
  return { side, edge: probSide - marketSide };
}

function formatCountdown(ms: number): string {
  const total = Math.max(0, Math.floor(ms / 1000));
  const h = Math.floor(total / 3600);
  const m = Math.floor((total % 3600) / 60);
  const s = total % 60;
  if (h > 0) return `${h}h${m.toString().padStart(2, "0")}m`;
  return `${m.toString().padStart(2, "0")}:${s.toString().padStart(2, "0")}`;
}

// Three-state manual-trader call: `up | no | down`. Mirrors the BACKTESTER's confidence gate, not
// the paper trader's. v6's flow definition (BuiltInModels.cs:178) sets `min_confidence: 0.04` on
// the LogisticRegression node, where confidence = |pUp - 0.5| * 2. That maps to a ±2pp band
// around 0.5 where the backtester abstains and the prediction does not count toward hit-rate.
// Reading by hand follows the same gate so what you trade matches what the iteration loop is
// optimizing toward.
//
// The paper trader is downstream: it has no gate (Martingale needs continuous placement) and
// will still bet inside the no-band on its own schedule. The capsule is the backtest-anchored
// lens, not a mirror of the paper trader.
function BetCapsule({ pUp, full = false }: { pUp: number; full?: boolean }) {
  const call = betDecision(pUp);
  const pct = (pUp * 100).toFixed(1);
  const downPct = ((1 - pUp) * 100).toFixed(1);
  const distPp = (Math.abs(pUp - 0.5) * 100).toFixed(1);
  const explain = (id: "up" | "no" | "down"): string => {
    if (id === "up") {
      return id === call
        ? `Active call: bet UP. pUp = ${pct}% (model ${pct}% confident UP), ${distPp} pp above 50/50 — clears the backtester's ±2pp confidence gate (v6 min_confidence = 0.04). This is a candle the backtester counts toward hit-rate, so a hand-placed UP bet matches the loop's measurement.`
        : `Activates at pUp ≥ 52% (backtester confidence gate). Current pUp = ${pct}% — model isn't leaning UP strongly enough for the backtester to count it.`;
    }
    if (id === "down") {
      return id === call
        ? `Active call: bet DOWN. pUp = ${pct}% (model ${downPct}% confident DOWN), ${distPp} pp below 50/50 — clears the backtester's ±2pp confidence gate (v6 min_confidence = 0.04). This is a candle the backtester counts toward hit-rate, so a hand-placed DOWN bet matches the loop's measurement.`
        : `Activates at pUp ≤ 48% (backtester confidence gate). Current pUp = ${pct}% — model isn't leaning DOWN strongly enough for the backtester to count it.`;
    }
    return id === call
      ? `Active call: don't bet. pUp = ${pct}% sits inside the ±2pp noise band the backtester abstains on (v6 min_confidence = 0.04 ⇒ skip in [48%, 52%]). The paper trader still places a bet here on its own (no gate); the backtester does not. Hand-betting noise-band candles is exactly what the gate exists to avoid.`
      : `Activates inside [48%, 52%]. Current pUp = ${pct}% — model has cleared the backtester's confidence gate, follow the highlighted side.`;
  };
  const opts: { id: "up" | "no" | "down"; activeCls: string; inactiveCls: string }[] = [
    { id: "up",   activeCls: "bg-emerald-300/15 text-emerald-300", inactiveCls: "text-emerald-300/25" },
    { id: "no",   activeCls: "bg-amber-300/15 text-amber-300",     inactiveCls: "text-amber-300/25" },
    { id: "down", activeCls: "bg-rose-300/15 text-rose-300",       inactiveCls: "text-rose-300/25" },
  ];
  return (
    <div className={`flex gap-0.5 rounded-md border border-fa-edge p-0.5 ${full ? "w-full" : ""}`}>
      {opts.map((o) => (
        <span
          key={o.id}
          className={`px-2.5 py-0.5 fa-caption tracking-wider rounded transition cursor-help ${full ? "flex-1 text-center" : ""} ${o.id === call ? o.activeCls : o.inactiveCls}`}
          title={explain(o.id)}
        >
          {o.id}
        </span>
      ))}
    </div>
  );
}

function NextCandleAction({
  prediction,
  currentPrediction,
  currentLean,
  previousPrediction,
  closeByOpenTime,
  intervalMs,
  isLoading,
  hasError,
  accuracy,
  msToNextCandle,
  tradeSignal,
  showLive
}: {
  prediction: LivePrediction | undefined;
  currentPrediction?: LivePrediction;
  currentLean?: "winning" | "losing" | null;
  previousPrediction?: LivePrediction;
  closeByOpenTime: Map<number, number>;
  intervalMs: number;
  isLoading: boolean;
  hasError: boolean;
  accuracy?: { count: number; hitRate: number } | null;
  msToNextCandle?: number;
  tradeSignal: TradeSignal | null;
  showLive: boolean;
}) {
  // Calibrated pUp is the value the server's bet side is actually decided on (PaperTradingService
  // → CalibrationRescaler → StakingEngine.DecideSide). Showing raw on the card meant the headline
  // direction and the placed bet could diverge whenever calibration flipped the side. Falling
  // back to raw keeps cold-start (< 20 resolutions per interval) readable before the empirical
  // map exists.
  const probUp = prediction?.directionUpProbabilityCalibrated ?? prediction?.directionUpProbability ?? 0;
  const up = probUp >= 0.5;
  // Show the probability of the CALLED side: P(up) when the call is UP, the implied P(down) = 1−pUp
  // when it's DOWN. A 47% pUp is a 53%-confident DOWN call — showing "47%" next to DOWN read as low
  // conviction when it's actually the opposite. The number now always means "how sure of THIS call".
  const sidePct = (up ? probUp : 1 - probUp) * 100;
  // Countdown turns warm (amber) as it gets close to the next round opening — visual nudge to
  // re-check the call before the candle closes.
  const cdMs = msToNextCandle ?? 0;
  const cdWarm = cdMs > 0 && cdMs <= 15_000;
  const dirColor = up ? "text-emerald-300" : "text-rose-300";

  // Hairline band on the card's own surface — no nested tint, no rounded panel-in-panel.
  return (
    <div className="mt-3 pt-3 border-t border-fa-edge/60">
      <div className="flex items-center gap-2 mb-2">
        <Sparkles className="h-3 w-3 text-amber-300" />
        <span className="fa-overline text-fa-frost-dim">Next candle</span>
        {showLive && <LivePulse />}
        {msToNextCandle != null && (
          <span
            className={`ml-auto tabular-nums fa-caption ${cdWarm ? "text-amber-300" : "text-fa-frost-dim"}`}
            title="Time until the current candle closes (next betting round)"
          >
            opens in {formatCountdown(cdMs)}
          </span>
        )}
      </div>

      {/* The direction line. `leading-none` on every text node + an `inline` SVG nudged with
          translate-y keeps everything sharing the same visible baseline so "UP" and "54%" line up
          cleanly regardless of font size. items-baseline alone would pick the SVG's bottom edge
          on the larger span and shove the smaller percentage down. */}
      {/* 2×2 layout: the big directional call fills the left half across both rows; the right half
          stacks the edge/hit metrics (top) over the up / no-bet / down bar (bottom). */}
      <div className="grid grid-cols-2 grid-rows-2 gap-x-4 gap-y-1.5 items-center">
        {/* Left — big directional call, spanning both rows */}
        <div className="row-span-2 flex items-baseline gap-2 min-w-0">
          {prediction ? (
            <>
              <span className={`text-3xl font-light leading-none ${dirColor}`}>
                {up
                  ? <ArrowUp className="inline h-7 w-7 -translate-y-[3px] mr-0.5" strokeWidth={2.25} />
                  : <ArrowDown className="inline h-7 w-7 -translate-y-[3px] mr-0.5" strokeWidth={2.25} />
                }
                {up ? "UP" : "DOWN"}
              </span>
              <UiTooltip content="Calibrated confidence in the called direction — P(up) for an UP call, the implied P(down) = 1−P(up) for a DOWN call. The bet side is decided on P(up); 50% = no edge, and distance from 50% is conviction either way.">
                <span
                  className={`text-xl leading-none tabular-nums opacity-80 ${dirColor} cursor-help`}
                  title={prediction.reasoning ?? undefined}
                >
                  {sidePct.toFixed(0)}%
                </span>
              </UiTooltip>
            </>
          ) : isLoading ? (
            <span className="text-fa-frost-dim text-sm inline-flex items-center gap-1.5"><Spinner /> Thinking…</span>
          ) : hasError ? (
            <span className="text-rose-300 text-sm">prediction failed</span>
          ) : (
            <span className="text-fa-frost-dim text-sm">—</span>
          )}
        </div>

        {/* Top-right — edge + hit-rate text */}
        <div className="flex items-center gap-x-2 fa-caption sm:text-xs tabular-nums whitespace-nowrap min-w-0">
          {tradeSignal && (
            <span
              className="text-fa-frost-dim"
              title="Probability advantage over the fair-market reference. Side is already implied by the UP/DOWN call."
            >
              <span className={tradeSignal.edge >= 0 ? "text-emerald-300/90" : "text-fa-frost-dim"}>
                {tradeSignal.edge >= 0 ? "+" : ""}{(tradeSignal.edge * 100).toFixed(1)}%
              </span>
              <span className="opacity-70"> edge</span>
            </span>
          )}
          {tradeSignal && accuracy && <span className="text-fa-frost-dim/50" aria-hidden>·</span>}
          {accuracy && (
            <span
              className="text-fa-frost-dim"
              title={`${accuracy.count} resolved predictions visible · hit rate ${(accuracy.hitRate * 100).toFixed(0)}%`}
            >
              <ShimmerOnChange value={accuracy.count}>{accuracy.count}</ShimmerOnChange>
              <span className="opacity-70"> hit </span>
              <ShimmerOnChange
                value={Math.round(accuracy.hitRate * 1000)}
                className={accuracy.hitRate >= 0.5 ? "text-emerald-300" : "text-rose-300"}
              >
                {(accuracy.hitRate * 100).toFixed(0)}%
              </ShimmerOnChange>
            </span>
          )}
          {!tradeSignal && !accuracy && <span className="text-fa-frost-dim/40">—</span>}
        </div>

        {/* Bottom-right — up / no-bet / down bar (full width of the right column) */}
        <div className="flex items-center min-w-0">
          {prediction
            ? <BetCapsule pUp={probUp} full />
            : <span className="text-fa-frost-dim/40 fa-caption">—</span>}
        </div>
      </div>

      {/* Recap strips: CURR (the bet on the currently-active, still-forming candle — outcome
          chip reads "pending" in amber) and PREV (most recently resolved — chip reads HIT/MISS).
          Both sit one tier dimmer than the headline so they read as context, not competing signals,
          and mirror the headline's icon/UP-DOWN/% shape so the eye doesn't have to retrain across
          rows. Hidden individually when their underlying data isn't available (cold start, or no
          resolved prediction yet). */}
      {currentPrediction && (
        <RecapStrip
          label="curr"
          prediction={currentPrediction}
          kind="pending"
          lean={currentLean ?? null}
          closeByOpenTime={closeByOpenTime}
          intervalMs={intervalMs}
        />
      )}
      {previousPrediction && (
        <RecapStrip
          label="prev"
          prediction={previousPrediction}
          kind="resolved"
          lean={null}
          closeByOpenTime={closeByOpenTime}
          intervalMs={intervalMs}
        />
      )}
    </div>
  );
}

/**
 * Single-line "label · UP/DOWN n% · time · outcome-chip" recap used for the CURR and PREV strips
 * under the next-candle headline. Outcome chip colour follows `kind`: amber "pending" while the
 * candle is still forming, emerald "✓ hit" / rose "✗ miss" once the candle has resolved.
 */
function RecapStrip({
  label,
  prediction,
  kind,
  lean,
  closeByOpenTime,
  intervalMs,
}: {
  label: string;
  prediction: LivePrediction;
  kind: "pending" | "resolved";
  lean: "winning" | "losing" | null;
  closeByOpenTime: Map<number, number>;
  intervalMs: number;
}) {
  const pUp = prediction.directionUpProbabilityCalibrated ?? prediction.directionUpProbability;
  const up = pUp >= 0.5;
  const dirColor = up ? "text-emerald-300/70" : "text-rose-300/70";
  const timeLabel = new Intl.DateTimeFormat(undefined, { hour: "2-digit", minute: "2-digit", hourCycle: "h23" }).format(new Date(prediction.targetOpenTime));

  let chip: ReactNode;
  if (kind === "pending") {
    // Subtle real-time lean cue layered onto the still-pending chip — colour and a small arrow tell
    // the user whether the bet WOULD currently resolve as a hit (price has moved the predicted way
    // since anchor) or a miss. Kept amber/dim by default and only tinted on a definite lean — null
    // lean (no live price, or price exactly at anchor) shows the plain amber chip so the cue isn't
    // misleading at the moment of equality.
    if (lean === "winning") {
      chip = (
        <span
          className="fa-pending-shimmer inline-flex items-center gap-1 rounded-sm border border-emerald-300/50 bg-emerald-300/10 px-1.5 py-0.5 text-emerald-300/90 fa-overline"
          title="Bet would resolve as HIT at the current price"
        >
          <ArrowUp className="h-2.5 w-2.5" strokeWidth={2.5} />
          pending
        </span>
      );
    } else if (lean === "losing") {
      chip = (
        <span
          className="fa-pending-shimmer inline-flex items-center gap-1 rounded-sm border border-rose-300/50 bg-rose-300/10 px-1.5 py-0.5 text-rose-300/90 fa-overline"
          title="Bet would resolve as MISS at the current price"
        >
          <ArrowDown className="h-2.5 w-2.5" strokeWidth={2.5} />
          pending
        </span>
      );
    } else {
      chip = (
        <span className="fa-pending-shimmer inline-flex items-center gap-1 rounded-sm border border-amber-300/40 bg-amber-300/10 px-1.5 py-0.5 text-amber-300 fa-overline">
          pending
        </span>
      );
    }
  } else {
    // Grade through the SAME single source of truth as the dots + headline hit-rate
    // (predictionMarkerStateForCandles → close-vs-prevClose), so the PREV verdict can NEVER contradict
    // the candle body. No backend-anchor fallback here: a red candle is always a MISS for an UP call.
    // See [[feedback_hitmiss_from_chart_candles]] + [[feedback_candle_color_direction]].
    const hit = predictionMarkerStateForCandles(prediction, closeByOpenTime, intervalMs) === "hit";
    chip = hit ? (
      <span className="inline-flex items-center gap-1 rounded-sm border border-emerald-300/40 bg-emerald-300/10 px-1.5 py-0.5 text-emerald-300 fa-overline">
        ✓ hit
      </span>
    ) : (
      <span className="inline-flex items-center gap-1 rounded-sm border border-rose-300/40 bg-rose-300/10 px-1.5 py-0.5 text-rose-300 fa-overline">
        ✗ miss
      </span>
    );
  }

  return (
    <div
      className="mt-1.5 flex items-baseline gap-x-2 fa-caption tabular-nums"
      title={prediction.reasoning ?? undefined}
    >
      <span className="fa-overline text-fa-frost-dim/50">{label}</span>
      <span className={`inline-flex items-baseline gap-1 ${dirColor}`}>
        {up
          ? <ArrowUp className="inline h-3 w-3 -translate-y-[1px]" strokeWidth={2.25} />
          : <ArrowDown className="inline h-3 w-3 -translate-y-[1px]" strokeWidth={2.25} />}
        <span>{up ? "UP" : "DOWN"}</span>
        {/* Confidence in the called side: P(up) for UP, implied P(down)=1−pUp for DOWN — mirrors the headline. */}
        <span className="opacity-70">{((up ? pUp : 1 - pUp) * 100).toFixed(0)}%</span>
      </span>
      <span className="text-fa-frost-dim/40" aria-hidden>·</span>
      <span className="text-fa-frost-dim/60">{timeLabel}</span>
      <span className="text-fa-frost-dim/40" aria-hidden>·</span>
      {chip}
    </div>
  );
}

function CandleTable({ rows, nextPrediction, nextTargetTime, predictions }: {
  rows: Row[];
  nextPrediction?: LivePrediction;
  nextTargetTime?: number;
  predictions: LivePrediction[];
}) {
  // Index predictions by the candle openTime they were made FOR. Lets each row look up whether
  // the prediction that was issued ahead of its candle hit or missed once the candle resolved.
  const predictionByOpenTime = useMemo(() => {
    const m = new Map<number, LivePrediction>();
    for (const p of predictions) m.set(p.targetOpenTime, p);
    return m;
  }, [predictions]);

  // Newest first — most useful read order for live data. Re-render flows through whenever the
  // hook refreshes the underlying candles.
  const ordered = useMemo(() => [...rows].reverse(), [rows]);
  const newest = ordered[0];

  // Track which openTimes have ever been seen and the previous close on the topmost candle.
  // - A boundary change (new openTime appears in front) triggers the strong flash on that row.
  // - The latest candle's close keeps updating intra-bar; flash that row gently when it moves.
  const seenRef = useRef<Set<number>>(new Set());
  const lastTopCloseRef = useRef<{ t: number; close: number } | null>(null);

  const [strongFlash, setStrongFlash] = useState<{ t: number; up: boolean } | null>(null);
  const [softFlash, setSoftFlash] = useState<{ t: number; up: boolean } | null>(null);

  useEffect(() => {
    if (!newest) return;
    // Mark prior rows as seen so only genuinely new boundaries flash (avoids flashing every row
    // on first mount).
    const seen = seenRef.current;
    if (seen.size === 0) {
      ordered.forEach((r) => seen.add(r.openTime));
      lastTopCloseRef.current = { t: newest.openTime, close: newest.close };
      return;
    }

    if (!seen.has(newest.openTime)) {
      seen.add(newest.openTime);
      const up = newest.close >= newest.open;
      setStrongFlash({ t: newest.openTime, up });
      const id = window.setTimeout(() => setStrongFlash(null), 1_600);
      lastTopCloseRef.current = { t: newest.openTime, close: newest.close };
      return () => window.clearTimeout(id);
    }

    const prev = lastTopCloseRef.current;
    if (prev && prev.t === newest.openTime && prev.close !== newest.close) {
      const up = newest.close > prev.close;
      setSoftFlash({ t: newest.openTime, up });
      lastTopCloseRef.current = { t: newest.openTime, close: newest.close };
      const id = window.setTimeout(() => setSoftFlash(null), 600);
      return () => window.clearTimeout(id);
    }
    if (prev?.t !== newest.openTime) {
      lastTopCloseRef.current = { t: newest.openTime, close: newest.close };
    }
  }, [newest, ordered]);

  // Sort the historical (non-live) candles only. The synthetic next-prediction row + the live
  // (topmost) candle stay pinned to the top regardless of sort — sorting them with the rest
  // would scatter them through history, which defeats the "active candle at the top" UX.
  type CandleKey = "time" | "price" | "delta" | "result";
  const live = ordered[0];
  const history = useMemo(() => ordered.slice(1), [ordered]);
  const deltaFor = useCallback((openTime: number, close: number) => {
    const idx = ordered.findIndex((c) => c.openTime === openTime);
    const prior = ordered[idx + 1];
    if (!prior || prior.close === 0) return null;
    return (close - prior.close) / prior.close;
  }, [ordered]);
  // Same close-vs-prior-close scoring the chart dots and the headline chip use — table sort then
  // orders by the same hit/miss verdict, never disagreeing with the dot above the bar.
  const resultFor = useCallback((openTime: number) => {
    const pred = predictionByOpenTime.get(openTime);
    if (!pred || !pred.resolvedAt) return null;
    const idx = ordered.findIndex((c) => c.openTime === openTime);
    const prior = ordered[idx + 1];
    if (!prior) return 0; // pending (no prior bar in view to score against)
    const pUp = pred.directionUpProbabilityCalibrated ?? pred.directionUpProbability;
    const predictedUp = pUp >= 0.5;
    const row = ordered[idx];
    const actualUp = row.close > prior.close;
    return predictedUp === actualUp ? 1 : -1;
  }, [predictionByOpenTime, ordered]);
  const { sortedRows: sortedHistory, headerProps } = useSort<Candle, CandleKey>(history, {
    time:   (r) => r.openTime,
    price:  (r) => r.close,
    delta:  (r) => deltaFor(r.openTime, r.close),
    result: (r) => resultFor(r.openTime),
  });
  // Reassemble: keep live candle at index 0, then sorted history.
  const renderRows = useMemo(() => live ? [live, ...sortedHistory] : sortedHistory, [live, sortedHistory]);

  return (
    <div className="h-full overflow-auto fa-scroll">
      <table className="fa-table-bordered w-full fa-caption tabular-nums">
        <thead className="sticky top-0 bg-fa-ink-2/95 backdrop-blur text-fa-frost-dim">
          <tr className="text-left">
            <th className="px-2 py-1 font-normal"><SortHeader<CandleKey> {...headerProps("time")}>Time</SortHeader></th>
            <th className="px-2 py-1 font-normal text-right"><SortHeader<CandleKey> {...headerProps("price")} align="right">Price</SortHeader></th>
            <th className="px-2 py-1 font-normal text-right"><SortHeader<CandleKey> {...headerProps("delta")} align="right">Δ</SortHeader></th>
            <th className="px-2 py-1 font-normal text-right"><SortHeader<CandleKey> {...headerProps("result")} align="right">Result</SortHeader></th>
          </tr>
        </thead>
        <tbody>
          {nextPrediction && nextTargetTime != null && (() => {
            // Synthetic row for the next, not-yet-existing candle. We show the actual numeric
            // prediction (predicted price + delta vs the anchor close) rather than UP/DOWN n%,
            // since the directional probability alongside the word "UP" was reading as "going up
            // by n%". The result cell is just a yellow "next" until the candle resolves.
            const delta = nextPrediction.predictedClose - nextPrediction.anchorClose;
            const up = delta >= 0;
            const priceColor = up ? "text-emerald-300" : "text-rose-300";
            return (
              <tr key={`pred-${nextTargetTime}`} className="border-t border-fa-edge/50 bg-amber-300/[0.07] italic">
                <td className="px-2 py-1 text-amber-300/90">
                  <span className="inline-flex items-center gap-1.5">
                    <Sparkles className="h-3 w-3 shrink-0" />
                    {formatTick(nextTargetTime)}
                  </span>
                </td>
                <td className={`px-2 py-1 text-right ${priceColor}`}>
                  {up ? "↑" : "↓"} {fmtUsd(nextPrediction.predictedClose)}
                </td>
                <td className="px-2 py-1 text-right">
                  <span className="text-fa-frost-dim">{fmtSignedUsd(delta)}</span>
                  <span className={`ml-1.5 ${priceColor}`}>{fmtSignedPct(nextPrediction.predictedChangePct)}</span>
                  <span className={`ml-1.5 ${priceColor}`}>{up ? "▲" : "▼"}</span>
                </td>
                <td className="px-2 py-1 text-right text-amber-300" title={nextPrediction.reasoning ?? undefined}>
                  next
                </td>
              </tr>
            );
          })()}
          {renderRows.map((r, idx) => {
            const isLive = idx === 0 && live != null && r.openTime === live.openTime;
            // Δ vs the chronologically-prior candle. Compute against the *unsorted* `ordered`
            // array so sorting doesn't break the delta — sorting changes display order, not the
            // candle's actual neighbour in time. `ordered` is newest-first, so the prior candle
            // is at index+1 relative to this candle's position there.
            const orderedIdx = ordered.findIndex((c) => c.openTime === r.openTime);
            const prior = ordered[orderedIdx + 1];
            const delta = prior ? r.close - prior.close : null;
            const deltaPct = prior && prior.close !== 0 ? (delta! / prior.close) * 100 : null;
            const up = delta != null ? delta >= 0 : r.close >= r.open;
            const priceColor = up ? "text-emerald-300" : "text-rose-300";
            const priceLabel = isLive ? "Now" : "Close";
            const pred = predictionByOpenTime.get(r.openTime);
            // Result cell mirrors the chart-dot state: open/hit/miss derived from calibrated pUp
            // vs the chart's own close-vs-prior-close, so the table row, the candle body colour,
            // and the dot above the bar always read the same outcome.
            let resultState: "open" | "hit" | "miss" | null = null;
            if (pred) {
              if (!pred.resolvedAt) {
                resultState = "open";
              } else if (!prior) {
                resultState = "open";
              } else {
                const pUp = pred.directionUpProbabilityCalibrated ?? pred.directionUpProbability;
                const predictedUp = pUp >= 0.5;
                const actualUp = r.close > prior.close;
                resultState = predictedUp === actualUp ? "hit" : "miss";
              }
            }
            const isNew = strongFlash?.t === r.openTime;
            const isPulse = !isNew && softFlash?.t === r.openTime;
            const flashClass = isNew
              ? (strongFlash!.up ? "fa-row-flash-up" : "fa-row-flash-down")
              : isPulse
                ? (softFlash!.up ? "fa-row-pulse-up" : "fa-row-pulse-down")
                : "";
            return (
              // Keying on (openTime + flash sentinel) restarts the CSS animation on every flash
              // event — without it, React would reuse the row and the animation would only fire once.
              <tr
                key={`${r.openTime}-${isNew ? `n${strongFlash!.t}` : isPulse ? `p${softFlash!.t}-${r.close}` : "s"}`}
                className={`border-t border-fa-edge/50 ${flashClass}`}
              >
                <td className="px-2 py-1 text-fa-frost-dim">
                  <span className="inline-flex items-center gap-1.5">
                    {isLive ? (
                      <svg width="12" height="12" viewBox="0 0 12 12" aria-label="In-progress candle" className="shrink-0">
                        <circle cx="6" cy="6" fill={LIVE} className="fa-live-ring" />
                        <circle cx="6" cy="6" fill={LIVE} stroke="var(--fa-ink, #06121F)" strokeWidth={0.75} className="fa-live-dot" />
                      </svg>
                    ) : <span className="w-3 shrink-0" />}
                    {formatTick(r.openTime)}
                  </span>
                </td>
                <td className={`px-2 py-1 text-right ${priceColor}`}>
                  <span className="text-fa-frost-dim mr-1.5">{priceLabel}</span>
                  {fmtUsd(r.close)}
                </td>
                <td className="px-2 py-1 text-right">
                  {delta != null ? (
                    <>
                      <span className="text-fa-frost-dim">{fmtSignedUsd(delta)}</span>
                      {deltaPct != null && <span className={`ml-1.5 ${priceColor}`}>{fmtSignedPct(deltaPct)}</span>}
                      <span className={`ml-1.5 ${priceColor}`}>{up ? "▲" : "▼"}</span>
                    </>
                  ) : (
                    <span className="text-fa-frost-dim">—</span>
                  )}
                </td>
                <td className="px-2 py-1 text-right">
                  {isLive ? (
                    <span className="text-fa-frost-dim">live</span>
                  ) : pred == null || resultState == null ? (
                    <span className="text-fa-frost-dim">—</span>
                  ) : resultState === "open" ? (
                    <span className="text-amber-300/80" title={pred.reasoning ?? undefined}>pending</span>
                  ) : (() => {
                    // Show: predicted side · actual move · hit/miss verdict — three pieces of info
                    // in one compact cell so the table reads as a trade journal, not just a score.
                    const pUp = pred.directionUpProbabilityCalibrated ?? pred.directionUpProbability;
                    const predUp = pUp >= 0.5;
                    const actUp = prior ? r.close > prior.close : null;
                    const sideColor = predUp ? "text-emerald-300/60" : "text-rose-300/60";
                    const actColor = actUp == null ? "text-fa-frost-dim/40" : actUp ? "text-emerald-300/60" : "text-rose-300/60";
                    return (
                      <span className="inline-flex items-center gap-1" title={pred.reasoning ?? undefined}>
                        <span className={`fa-caption ${sideColor}`}>{predUp ? "↑" : "↓"}</span>
                        {actUp != null && <span className={`fa-caption ${actColor}`}>{actUp ? "↑" : "↓"}</span>}
                        <span className={resultState === "hit" ? "text-emerald-300" : "text-rose-300"}>
                          {resultState === "hit" ? "hit" : "miss"}
                        </span>
                      </span>
                    );
                  })()}
                </td>
              </tr>
            );
          })}
        </tbody>
      </table>
    </div>
  );
}

function renderTooltip(
  rows: Row[],
  currentOpenTime: number | undefined,
  nextPrediction: LivePrediction | undefined
) {
  return ({ active, payload, label }: { active?: boolean; payload?: Array<{ payload?: Row }>; label?: number | string }) => {
    if (!active || !payload?.length) return null;
    const row = payload[0]?.payload;
    if (!row) return null;
    const labelMs = typeof label === "number" ? label : row.openTime;
    const idx = rows.findIndex((r) => r.openTime === row.openTime);
    const prior = idx > 0 ? rows[idx - 1] : null;
    const isLive = currentOpenTime != null && row.openTime === currentOpenTime;
    // For the live candle, row.close is the in-flight price (the same field the ticker reads).
    // For closed candles it is the final close. Either way we display a single price value.
    const priceLabel = isLive ? "Now" : "Close";
    const priceValue = row.close;
    const delta = prior ? priceValue - prior.close : null;
    const deltaPct = prior && prior.close !== 0 ? (delta! / prior.close) * 100 : null;
    const up = delta != null ? delta >= 0 : row.close >= row.open;
    const priceColor = up ? "text-emerald-300" : "text-rose-300";
    return (
      <div className="rounded-md border border-fa-edge bg-fa-ink/95 px-4 py-3 text-xs shadow-lg backdrop-blur-sm">
        <div className="mb-2">
          <span className="inline-flex items-center rounded-md border border-fa-edge bg-fa-glass px-1.5 py-0.5 fa-overline tabular-nums text-fa-frost-dim">
            {formatTick(labelMs)}
          </span>
        </div>
        <div className="grid grid-cols-[auto_1fr] gap-x-8 gap-y-1.5 tabular-nums leading-tight">
          <span className="text-fa-frost-dim">{priceLabel}</span>
          <span className={`text-right ${priceColor}`}>{fmtUsd(priceValue)}</span>
          {delta != null && (
            <>
              <span className="text-fa-frost-dim">Δ</span>
              <span className="text-right">
                <span className="text-fa-frost-dim">{fmtSignedUsd(delta)}</span>
                {deltaPct != null && (
                  <span className={`ml-2 ${priceColor}`}>{fmtSignedPct(deltaPct)}</span>
                )}
                <span className={`ml-1.5 ${priceColor}`}>{up ? "▲" : "▼"}</span>
              </span>
            </>
          )}
          {isLive && nextPrediction && (() => {
            // Only attach the forward-looking row to the in-flight candle's tooltip — that's the
            // candle the next prediction is anchored on. Delta is vs the prediction's anchorClose
            // (the close at the time the model fired), so the % matches the table's predicted Δ.
            const predDelta = nextPrediction.predictedClose - nextPrediction.anchorClose;
            const predUp = predDelta >= 0;
            const predColor = predUp ? "text-emerald-300" : "text-rose-300";
            return (
              <>
                <span className="col-span-2 border-t border-fa-edge/60 mt-1" />
                <span className="text-amber-300">Next</span>
                <span className="text-right">
                  <span className="text-fa-frost-dim">{fmtSignedUsd(predDelta)}</span>
                  <span className={`ml-2 ${predColor}`}>{fmtSignedPct(nextPrediction.predictedChangePct)}</span>
                  <span className={`ml-1.5 ${predColor}`}>{predUp ? "▲" : "▼"}</span>
                </span>
              </>
            );
          })()}
        </div>
      </div>
    );
  };
}

function fmtSignedUsd(v: number): string {
  const sign = v >= 0 ? "+" : "-";
  return `${sign}$${Math.abs(v).toLocaleString(undefined, { minimumFractionDigits: 2, maximumFractionDigits: 2 })}`;
}

function fmtSignedPct(v: number): string {
  const sign = v >= 0 ? "+" : "";
  return `${sign}${v.toFixed(2)}%`;
}

/**
 * Per-card model picker. The dropdown lists every model visible to the tenant (own + built-ins),
 * and the selection persists in the `active_models` table per (tenant, symbol, interval). Changing
 * it broadcasts an invalidation to the resolver so the next scheduler tick routes through the new
 * model. When no row exists, the resolver falls back to the tenant default → global default LLM.
 */
function ModelPicker({ symbol, interval, grow = false, status = "idle" }:
    { symbol: string; interval: BinanceInterval; grow?: boolean; status?: "live" | "syncing" | "idle" }) {
  const { data: models } = useListModelsQuery();
  const { data: active } = useListActiveModelsQuery();
  const [setActive] = useSetActiveModelMutation();
  const ensureTrained = useModelTrainGate();
  const [open, setOpen] = useState(false);
  const ref = useRef<HTMLDivElement>(null);

  // Close on outside-click / Escape (hooks run unconditionally — before the empty-models guard).
  useEffect(() => {
    if (!open) return;
    const onDoc = (e: MouseEvent) => { if (ref.current && !ref.current.contains(e.target as Node)) setOpen(false); };
    const onKey = (e: KeyboardEvent) => { if (e.key === "Escape") setOpen(false); };
    document.addEventListener("mousedown", onDoc);
    document.addEventListener("keydown", onKey);
    return () => { document.removeEventListener("mousedown", onDoc); document.removeEventListener("keydown", onKey); };
  }, [open]);

  if (!models || models.length === 0) return null;

  const activeRow = active?.find((a) => a.symbol === symbol && a.interval === interval);
  // Fallback chain mirrors the backend ActiveModelResolver: per-card row → tenant-owned default
  // → global built-in default → first available model.
  const tenantDefault = models.find((m) => m.isDefault && m.tenantId !== null);
  const globalDefault = models.find((m) => m.isDefault && m.tenantId === null);
  const fallbackDefault = tenantDefault ?? globalDefault ?? models[0];
  const currentId = activeRow?.modelId ?? fallbackDefault?.id ?? "";
  const currentName = models.find((m) => m.id === currentId)?.name ?? "—";

  const statusTip = status === "live"
    ? `“${currentName}” is live — it is producing the predictions shown on this card right now.`
    : status === "syncing"
      ? `Switching to “${currentName}” — generating its first prediction…`
      : `“${currentName}” selected. Awaiting the next candle's prediction.`;

  // Trigger orb reflects whether the SELECTED model is actively producing predictions.
  const triggerOrb = (
    <span className="relative inline-flex h-2 w-2 shrink-0">
      {status === "live" && <span className="absolute inset-0 rounded-full bg-emerald-400 opacity-75 animate-ping" />}
      <span className={`relative h-2 w-2 rounded-full ${status === "live" ? "bg-emerald-400" : status === "syncing" ? "bg-amber-400 animate-pulse" : "bg-fa-frost-dim/50"}`} />
    </span>
  );

  return (
    <div ref={ref} className={`relative h-6 ${grow ? "flex-1 min-w-0" : "w-44 shrink-0"}`}>
      {/* Custom dropdown — a styled trigger + panel (replaces the native <select> so we can render
          a status orb per option). The active model's orb is emerald (live-pulsing when it's
          actually forecasting); every other option gets a dim slate orb so the active one stands out. */}
      <InfoTip
        content={
          <TipBody title="Model">
            Picks which model drives this card — its predictions, the chart dots, and the hit-rate.
            Switching re-runs the new model across the whole visible window instantly.
            <span className="mt-1 block text-fa-frost/80">{statusTip}</span>
          </TipBody>
        }
      >
        <button
          type="button"
          onClick={() => setOpen((o) => !o)}
          aria-label="Model"
          aria-expanded={open}
          className={`h-6 w-full flex items-center gap-1.5 px-2 rounded-md border bg-fa-glass text-fa-frost-bright fa-caption transition ${
            status === "live" ? "border-emerald-400/40 ring-1 ring-emerald-400/20" : "border-fa-edge hover:border-fa-frost/30"
          }`}
        >
          {triggerOrb}
          <span className="flex-1 truncate text-left">{currentName}</span>
          <svg className={`h-3 w-3 shrink-0 text-fa-frost-dim transition-transform ${open ? "rotate-180" : ""}`} viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth="1.5">
            <path d="M4 6l4 4 4-4" strokeLinecap="round" strokeLinejoin="round" />
          </svg>
        </button>
      </InfoTip>

      {open && (
        <div className="absolute left-0 top-full mt-1 z-50 min-w-full w-max max-w-[280px] max-h-72 overflow-auto rounded-md border border-fa-edge bg-fa-ink-2/95 backdrop-blur-md shadow-2xl shadow-fa-ink/80 py-1">
          {models.map((m) => {
            const selected = m.id === currentId;
            const needsTraining = modelNeedsTraining(m);
            return (
              <button
                key={m.id}
                type="button"
                onClick={async () => {
                  setOpen(false);
                  // An untrained model produces no forecasts — gate selection behind training.
                  if (needsTraining && !(await ensureTrained(m))) return;
                  setActive({ symbol, interval, modelId: m.id });
                }}
                className={`w-full flex items-center gap-2 px-2.5 py-1.5 fa-caption text-left transition ${
                  selected ? "bg-emerald-400/10 text-fa-frost-bright" : "text-fa-frost-dim hover:bg-fa-frost/10 hover:text-fa-frost-bright"
                }`}
              >
                {/* Selected = emerald (live-pulsing when forecasting); others = dim slate orb. */}
                <span className="relative inline-flex h-2 w-2 shrink-0">
                  {selected && status === "live" && <span className="absolute inset-0 rounded-full bg-emerald-400 opacity-75 animate-ping" />}
                  <span className={`relative h-2 w-2 rounded-full ${selected ? "bg-emerald-400" : needsTraining ? "bg-amber-400/70" : "bg-slate-500/60"}`} />
                </span>
                <span className="flex-1 truncate">{m.name}</span>
                {needsTraining ? (
                  <span className="shrink-0 fa-caption font-medium text-amber-200 bg-amber-400/10 border border-amber-400/30 rounded px-1 py-0.5">Train</span>
                ) : selected ? (
                  <svg className="h-3 w-3 shrink-0 text-emerald-400" viewBox="0 0 16 16" fill="none" stroke="currentColor" strokeWidth="2">
                    <path d="M3.5 8.5l3 3 6-7" strokeLinecap="round" strokeLinejoin="round" />
                  </svg>
                ) : null}
              </button>
            );
          })}
        </div>
      )}
    </div>
  );
}

