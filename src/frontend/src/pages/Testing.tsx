import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { useSearchParams } from "react-router-dom";
import {
  AlertTriangle,
  FlaskConical,
  Info,
  Loader2,
  Play,
  Trash2,
  X,
} from "lucide-react";
import { useConfirm } from "../components/ConfirmDialog";
import InfoTip, { TipBody } from "../components/InfoTip";
import PageHeader from "../components/PageHeader";
import { SymbolIcon, SymbolPicker } from "../components/SymbolIcon";
import BacktestRunModal from "../components/BacktestRunModal";
import SideDrawer from "../components/SideDrawer";
import RichMultiSelect, { type RichMultiSelectOption } from "../components/RichMultiSelect";
import { ProgressInline } from "../components/ProgressInline";
import { Tabs, TabsList, TabsTrigger, TabsContent } from "../components/ui/tabs";
import { useChaosProgress } from "../lib/chaosStream";
import { cn } from "../lib/cn";
import { fmtRunDate, fmtRunTime } from "../lib/format";
import { useSort, SortHeader } from "../lib/sort";
import { useLocalStorageState } from "../lib/persistedState";
import { computeModelScore, parseIntervalWfStats } from "./Models";
import { modelNeedsTraining, useModelTrainGate } from "../components/ModelTrainGate";
import {
  useClearBacktestsMutation,
  useGetBacktestBatchQuery,
  useGetChaosSamplesQuery,
  useGetStakingStrategiesQuery,
  useGetSymbolsQuery,
  useListBacktestsQuery,
  useListChaosRunsQuery,
  useListModelsQuery,
  useRunBacktestMutation,
  useRunChaosMutation,
  type Backtest,
  type ChaosRequest,
  type ChaosRunNormalized,
  type Model,
} from "../store/api";

// ── Tab param persistence ────────────────────────────────────────────────────────────────────

type TestingTab = "backtest" | "chaos";

export default function Testing() {
  const [searchParams, setSearchParams] = useSearchParams();
  const tab = (searchParams.get("tab") as TestingTab | null) ?? "backtest";
  const setTab = (t: TestingTab) => {
    const next = new URLSearchParams(searchParams);
    next.set("tab", t);
    setSearchParams(next, { replace: true });
  };

  const { data: allModels } = useListModelsQuery(void 0);
  const models = useMemo(() => (allModels ?? []).filter((m) => !m.isArchived), [allModels]);
  const eligible = useMemo(() => models.filter((m) => m.supportsBacktesting), [models]);

  return (
    <div className="h-full flex flex-col min-h-0">
      <div className="shrink-0 z-30 bg-fa-ink/95 backdrop-blur">
        <PageHeader
          title="Testing"
          subtitle="Validate models and strategies before they trade."
        />
      </div>

      <div className="px-4 sm:px-8 py-4 sm:py-6 flex-1 min-h-0 overflow-y-auto flex flex-col">
        <Tabs value={tab} onValueChange={(v) => setTab(v as TestingTab)} className="flex flex-col flex-1 min-h-0">
          <TabsList className="shrink-0 mb-6">
            <TabsTrigger value="backtest">Backtest</TabsTrigger>
            <TabsTrigger value="chaos">
              <FlaskConical className="h-3.5 w-3.5 mr-1.5" />
              Chaos Test
            </TabsTrigger>
          </TabsList>

          <TabsContent value="backtest" className="flex-1 min-h-0 flex flex-col">
            <BacktestTab models={models} eligible={eligible} />
          </TabsContent>

          <TabsContent value="chaos" className="flex-1 min-h-0 flex flex-col">
            <ChaosTab models={models} eligible={eligible} />
          </TabsContent>
        </Tabs>
      </div>
    </div>
  );
}

// ── Model selection helpers ──────────────────────────────────────────────────────────────────

/**
 * Maps eligible models to RichMultiSelect options. Deterministic models that haven't been trained
 * yet are rendered `locked` with a "Train" pill — clicking them opens the train gate instead of
 * selecting. Trained deterministic models always carry their walk-forward score; LLM models (which
 * never train) show an "LLM" tag.
 */
function buildModelOptions(eligible: Model[]): RichMultiSelectOption[] {
  return eligible.map((m) => {
    const needs = modelNeedsTraining(m);
    const score = computeModelScore(m);
    return {
      value: m.id,
      label: m.name,
      sublabel: m.kind,
      stat: needs
        ? undefined
        : score != null
          ? `${score.toFixed(1)}%`
          : m.kind === "llm"
            ? "LLM"
            : undefined,
      locked: needs,
      actionLabel: m.trainingStatus === "training" ? "Training…" : "Train",
    };
  });
}

/** First eligible model that's ready to use (trained, or an LLM that needs no training). */
function firstUsableModel(eligible: Model[]): Model | undefined {
  return eligible.find((m) => !modelNeedsTraining(m));
}

/**
 * Keeps a model selection valid: drops ids that are gone or untrained (e.g. a stale localStorage
 * pick from before the train gate existed), falling back to the first usable model when nothing
 * usable remains. Returns the same array reference when no change is needed (loop-safe in effects).
 */
function sanitizeModelSelection(prev: string[], eligible: Model[]): string[] {
  const cleaned = prev.filter((id) => {
    const m = eligible.find((x) => x.id === id);
    return m != null && !modelNeedsTraining(m);
  });
  const next = cleaned.length > 0
    ? cleaned
    : (() => { const f = firstUsableModel(eligible); return f ? [f.id] : []; })();
  return next.length === prev.length && next.every((id, i) => id === prev[i]) ? prev : next;
}

// ── Risk math helpers ─────────────────────────────────────────────────────────────────────────

interface RiskMetrics {
  p: number;
  q: number;
  lives: number;
  edge: number;
  kellyFraction: number;
  kellyBet: number;
  halfKellyBet: number;
  pRuin: number;
  safeBetForTarget: (targetPct: number) => number | null;
  verdict: { label: string; tone: "safe" | "warn" | "danger" | "neutral" };
}

function computeRiskMetrics(hitRatePct: number, bankroll: number, bet: number): RiskMetrics {
  const p = Math.min(1, Math.max(0, hitRatePct / 100));
  const q = 1 - p;
  const lives = bet > 0 ? Math.floor(bankroll / bet) : 0;
  const edge = 2 * p - 1;
  const kellyFraction = Math.max(0, edge);
  const kellyBet = bankroll * kellyFraction;
  const halfKellyBet = kellyBet / 2;

  let pRuin: number;
  if (p <= 0.5 + 1e-9) {
    pRuin = 1;
  } else if (lives <= 0) {
    pRuin = 1;
  } else {
    pRuin = Math.pow(q / p, lives);
  }

  const safeBetForTarget = (targetPct: number): number | null => {
    if (p <= 0.5 + 1e-9) return null;
    const ratio = q / p;
    const target = Math.max(1e-9, targetPct / 100);
    const N = Math.log(target) / Math.log(ratio);
    if (!isFinite(N) || N <= 0) return null;
    return bankroll / Math.ceil(N);
  };

  let verdict: RiskMetrics["verdict"];
  if (p <= 0.5 + 1e-9) verdict = { label: "No edge — eventual ruin", tone: "danger" };
  else if (pRuin < 0.01) verdict = { label: "Safe", tone: "safe" };
  else if (pRuin < 0.05) verdict = { label: "Aggressive", tone: "warn" };
  else if (pRuin < 0.20) verdict = { label: "Risky", tone: "warn" };
  else verdict = { label: "Gambling", tone: "danger" };

  return { p, q, lives, edge, kellyFraction, kellyBet, halfKellyBet, pRuin, safeBetForTarget, verdict };
}

// ── Candle helpers ────────────────────────────────────────────────────────────────────────────

function intervalToMs(interval: string): number {
  return interval === "1m" ? 60_000 : interval === "15m" ? 900_000 : 300_000;
}

function optimalLookbackDays(interval: string): number {
  switch (interval) {
    case "1m": return 90;
    case "5m": return 180;
    case "15m": return 720;
    default: return 180;
  }
}

/**
 * Converts a sampled-window length (in candles) to whole days for the given interval.
 * days = windowLength × intervalMs ÷ 1 day. Rounds to the nearest day, with a 1-day floor so a
 * sub-day window never renders as "0d".
 */
function windowCandlesToDays(windowLength: number, interval: string): number {
  const days = (windowLength * intervalToMs(interval)) / 86_400_000;
  return Math.max(1, Math.round(days));
}

// ── Shared form components ────────────────────────────────────────────────────────────────────

function Field({ label, info, children }: { label: string; info?: { title: string; body: React.ReactNode }; children: React.ReactNode }) {
  return (
    <label className="block">
      <div className="fa-overline text-fa-frost-dim mb-1 flex items-center gap-1">
        <span>{label}</span>
        {info && (
          <InfoTip width={280} content={<TipBody title={info.title}>{info.body}</TipBody>}>
            <button type="button" aria-label={`About ${label}`} className="text-fa-frost-dim/70 hover:text-fa-frost-bright transition leading-none">
              <Info className="h-3 w-3" />
            </button>
          </InfoTip>
        )}
      </div>
      {children}
    </label>
  );
}

/**
 * One label-over-value group on the borderless Risk-preview rail. No box — groups are separated
 * by a thin left divider (applied by the parent via `border-l`). The first item carries no divider.
 */
function RailMetric({
  label, value, sub, tooltip, tone, accent,
}: {
  label: string;
  value: string;
  sub?: string;
  tooltip?: string;
  tone?: "safe" | "warn" | "danger" | "neutral";
  accent?: boolean;
}) {
  const toneColor = tone === "safe" ? "text-emerald-300"
    : tone === "warn" ? "text-amber-300"
    : tone === "danger" ? "text-rose-300"
    : accent ? "text-fa-frost-bright"
    : "text-fa-frost-bright";
  return (
    <div className="min-w-0" title={tooltip}>
      <div className="fa-overline text-fa-frost-dim">{label}</div>
      <div className={cn("fa-metric-sm mt-0.5", toneColor)}>{value}</div>
      {sub && <div className="fa-caption text-fa-frost-dim/70 mt-0.5 tabular-nums">{sub}</div>}
    </div>
  );
}

// ── RiskPreview ───────────────────────────────────────────────────────────────────────────────

function RiskPreview({
  models, modelIds, interval, bankroll, bet, strategyIds, availableStrategyNames,
}: {
  models: Model[];
  modelIds: string[];
  interval: string;
  bankroll: number;
  bet: number;
  strategyIds: string[];
  availableStrategyNames: Record<string, string>;
}) {
  const [expanded, setExpanded] = useLocalStorageState<boolean>("fa.backtesting.riskPreview.expanded", false);

  const { hitRate, worstRate, source, modelName } = useMemo(() => {
    const selected = models.filter((m) => modelIds.includes(m.id));
    if (selected.length === 0) return { hitRate: 54, worstRate: 52, source: "default" as const, modelName: null };
    type Candidate = { rate: number; worst: number; name: string };
    const candidates: Candidate[] = [];
    for (const m of selected) {
      const s = parseIntervalWfStats(m.trainedState)[interval];
      if (s != null && !Number.isNaN(s.mean)) candidates.push({ rate: s.mean * 100, worst: s.worst * 100, name: m.name });
    }
    if (candidates.length === 0) return { hitRate: 54, worstRate: 52, source: "default" as const, modelName: null };
    const best = candidates.reduce((a, b) => (a.rate >= b.rate ? a : b));
    return { hitRate: best.rate, worstRate: best.worst, source: "training" as const, modelName: best.name };
  }, [models, modelIds, interval]);

  const metrics = useMemo(() => computeRiskMetrics(worstRate, bankroll, bet), [worstRate, bankroll, bet]);

  const safeBet1pct = metrics.safeBetForTarget(1);
  const safeBet5pct = metrics.safeBetForTarget(5);
  const hasMartingale = strategyIds.includes("martingale");
  const onlyMartingale = strategyIds.length === 1 && hasMartingale;
  const martingaleMaxLosses = bet > 0 ? Math.floor(Math.log2(bankroll / bet + 1)) : 0;

  const toneClass = {
    safe: "text-emerald-300 bg-emerald-300/10 border-emerald-300/30",
    warn: "text-amber-300 bg-amber-300/10 border-amber-300/30",
    danger: "text-rose-300 bg-rose-300/10 border-rose-300/30",
    neutral: "text-fa-frost-dim bg-fa-glass border-fa-edge",
  }[source === "default" ? "neutral" : metrics.verdict.tone];

  const sourceCaveat =
    source === "training" ? "from training walk-forward"
    : "no training data yet — assuming a generic 54% (train the model for real numbers)";

  const optimalBet = metrics.kellyBet > 0 ? metrics.halfKellyBet : null;
  const betVsOptimal = optimalBet != null && optimalBet > 0 ? bet / optimalBet : null;

  return (
    <div className="fa-card px-5 py-4">
      <button
        type="button"
        onClick={() => setExpanded(!expanded)}
        className="w-full flex items-start justify-between gap-3 flex-wrap text-left group"
        aria-expanded={expanded}
      >
        <div className="space-y-0.5 min-w-0">
          <div className="text-fa-frost-bright text-sm font-medium inline-flex items-center gap-2">
            <span className={cn("transition-transform inline-block text-fa-frost-dim group-hover:text-fa-frost-bright", expanded ? "rotate-90" : "")}>▸</span>
            Risk preview
          </div>
          <div className="text-fa-frost-dim fa-caption">
            {modelName ? <><span className="text-fa-frost-bright">{modelName}</span> · </> : null}
            <span className="font-mono">{interval}</span> · {hitRate.toFixed(1)}% hit rate
            <span className="text-fa-frost-dim/60"> ({sourceCaveat})</span>
            {source === "training" && (
              <span className="text-fa-frost-dim/60"> · risk sized on {worstRate.toFixed(1)}% worst-fold edge</span>
            )}
            {strategyIds.length > 0 && (
              <> · {strategyIds.map((id) => availableStrategyNames[id] ?? id).join(" + ")}</>
            )}
          </div>
        </div>
        <span className={cn(
          "inline-flex items-center gap-1.5 px-2.5 py-1 rounded-full text-xs border tabular-nums shrink-0",
          toneClass,
        )}>
          <span className="fa-caption">●</span>
          {metrics.verdict.label}
          {source !== "default" && metrics.p > 0.5 && (
            <span className="fa-caption opacity-70">· {(metrics.pRuin * 100).toFixed(metrics.pRuin < 0.001 ? 4 : metrics.pRuin < 0.01 ? 3 : 2)}% bust</span>
          )}
        </span>
      </button>

      {expanded && (
        <div className="mt-4 space-y-4">
          {/* Borderless metric rail — label-over-value groups separated by thin vertical dividers,
              no boxes around each metric. Wraps gracefully on narrow viewports. */}
          <div className="flex flex-wrap gap-x-6 gap-y-4">
            <RailMetric
              label="Optimal bet"
              value={optimalBet != null ? `$${optimalBet.toFixed(2)}` : "—"}
              sub="half-Kelly · best risk/reward"
              tooltip="The half-Kelly bet size — half the growth-maximising fraction. Canonical 'fastest safe' bet."
              accent
            />
            <div className="pl-6 border-l border-fa-edge/40">
              <RailMetric
                label="Your bet"
                value={`$${bet.toLocaleString()}`}
                sub={`${metrics.lives} lives · ${
                  metrics.p <= 0.5 ? "no edge"
                  : metrics.pRuin >= 0.001 ? `${(metrics.pRuin * 100).toFixed(metrics.pRuin < 0.01 ? 3 : 2)}% bust`
                  : `${(metrics.pRuin * 100).toFixed(4)}% bust`
                }`}
                tone={metrics.verdict.tone}
                tooltip="Your current bet size, plus how many losses your bankroll can absorb and the resulting gambler's-ruin probability."
              />
            </div>
            <div className="pl-6 border-l border-fa-edge/40">
              <RailMetric
                label="Risk:reward"
                value={
                  betVsOptimal == null ? "—"
                  : betVsOptimal >= 1.01 ? `${betVsOptimal.toFixed(1)}× over`
                  : betVsOptimal <= 0.99 ? `${(1 / betVsOptimal).toFixed(1)}× under`
                  : "Optimal"
                }
                sub={
                  betVsOptimal == null ? "no edge available"
                  : betVsOptimal >= 1.01 ? `reduce → $${optimalBet!.toFixed(2)}`
                  : betVsOptimal <= 0.99 ? "safer than half-Kelly"
                  : "matched to your edge"
                }
                tone={
                  betVsOptimal == null ? "danger"
                  : betVsOptimal >= 2 ? "danger"
                  : betVsOptimal >= 1.25 ? "warn"
                  : "safe"
                }
                tooltip="Ratio of your bet to the half-Kelly optimum."
              />
            </div>
            <div className="pl-6 border-l border-fa-edge/40">
              <RailMetric label="Lives" value={metrics.lives.toString()} sub={`$${bankroll.toLocaleString()} ÷ $${bet.toLocaleString()}`} tooltip="Number of losses absorbable from starting bankroll." />
            </div>
            <div className="pl-6 border-l border-fa-edge/40">
              <RailMetric
                label="P(bust)"
                value={metrics.p <= 0.5 ? "100%" : (metrics.pRuin >= 0.001 ? `${(metrics.pRuin * 100).toFixed(metrics.pRuin < 0.01 ? 3 : 2)}%` : `${(metrics.pRuin * 100).toFixed(4)}%`)}
                sub={metrics.p <= 0.5 ? "no edge" : "(q÷p)^lives"}
                tone={metrics.verdict.tone}
                tooltip="Gambler's ruin probability. Below ~1% is the practical 'safe' threshold."
              />
            </div>
            <div className="pl-6 border-l border-fa-edge/40">
              <RailMetric label="Full Kelly" value={metrics.kellyBet > 0 ? `$${metrics.kellyBet.toFixed(2)}` : "—"} sub={`2p − 1 = ${(metrics.kellyFraction * 100).toFixed(2)}%`} tooltip="The growth-maximising bet fraction. Has severe drawdowns in practice." />
            </div>
          </div>

          <div className="space-y-1.5 fa-caption">
            {metrics.p > 0.5 && safeBet1pct != null && (
              <div className="text-fa-frost-dim">
                <span className="text-fa-frost-bright">For &lt; 1% bust risk</span> at {hitRate.toFixed(1)}% hit rate with ${bankroll.toLocaleString()} bankroll, bet ≤ <span className="text-emerald-300 tabular-nums">${safeBet1pct.toFixed(2)}</span>.
              </div>
            )}
            {metrics.p > 0.5 && safeBet5pct != null && safeBet5pct !== safeBet1pct && (
              <div className="text-fa-frost-dim">
                <span className="text-fa-frost-bright">For &lt; 5% bust risk</span> — accept some real ruin chance for faster compounding — bet ≤ <span className="text-amber-300 tabular-nums">${safeBet5pct.toFixed(2)}</span>.
              </div>
            )}
            {hasMartingale && (
              <div className="text-amber-300/80 mt-2 leading-relaxed">
                ⚠ {onlyMartingale ? "Martingale selected." : "Martingale is one of the selected strategies."} It doubles bet size on each loss, so the math above (which assumes flat staking) is an <em>under-estimate</em> of bust risk. With ${bet} initial bet on ${bankroll.toLocaleString()} bankroll, Martingale can absorb at most <span className="text-fa-frost-bright">{martingaleMaxLosses}</span> consecutive losses before busting. Use martingale for stress-testing, not real sizing.
              </div>
            )}
          </div>

          <div className="fa-caption text-fa-frost-dim space-y-2 max-w-3xl border-l-2 border-fa-edge pl-3">
            <p>
              <span className="text-fa-frost-bright">Lives</span> = bankroll ÷ bet. It's how many consecutive losses you can take from a cold start before the bankroll can't fund the next bet. With ${bankroll.toLocaleString()} and a ${bet} flat bet that's{" "}
              <span className="text-fa-frost-bright">{metrics.lives}</span> lives.
            </p>
            <p>
              <span className="text-fa-frost-bright">Gambler's ruin formula:</span>{" "}
              <span className="font-mono">P(bust) = (q/p)^lives</span>. This is the probability that a random walk with positive drift ever touches zero. Below 1% is the practical threshold.
            </p>
            <p>
              <span className="text-fa-frost-bright">Kelly criterion:</span> the bet fraction that maximises long-run growth is <span className="font-mono">2p − 1</span> of your bankroll. At {hitRate.toFixed(1)}% that's{" "}
              <span className="tabular-nums">{(metrics.kellyFraction * 100).toFixed(2)}%</span> of bankroll per bet. Full Kelly is volatile — half-Kelly trades ~25% of growth for far less variance.
            </p>
          </div>
        </div>
      )}
    </div>
  );
}

// ── BacktestReport ────────────────────────────────────────────────────────────────────────────

function Stat({ label, value, valueClass, hint }: { label: string; value: string; valueClass?: string; hint?: string }) {
  return (
    <div title={hint}>
      <div className="fa-overline text-fa-frost-dim">{label}</div>
      <div className={cn("fa-metric-sm text-fa-frost-bright", valueClass)}>{value}</div>
    </div>
  );
}

function BacktestReport({ bt, models }: { bt: Backtest; models: Model[] }) {
  const model = models.find((m) => m.id === bt.modelId);
  const pnl = (bt.finalBalance ?? 0) - bt.initialBalance;
  const pnlCls = pnl > 0 ? "text-emerald-300" : pnl < 0 ? "text-rose-300" : "text-fa-frost-dim";
  return (
    <div className="fa-card px-6 py-5">
      <div className="flex items-center justify-between gap-3 mb-4">
        <div className="min-w-0">
          <div className="text-fa-frost-bright text-sm font-medium truncate">
            {model?.name ?? "Model"} · {bt.symbol} · {bt.interval}
          </div>
          <div className="text-fa-frost-dim text-xs">
            {new Date(bt.startTime).toLocaleDateString()} → {new Date(bt.endTime).toLocaleDateString()} ·{" "}
            <span className={cn("fa-overline",
              bt.status === "complete" ? "text-emerald-300" :
              bt.status === "running" ? "text-amber-300" : "text-rose-300")}>
              {bt.status}
            </span>
          </div>
        </div>
      </div>
      <div className="grid grid-cols-2 sm:grid-cols-3 md:grid-cols-6 gap-4">
        <Stat label="Hit rate" value={bt.hitRate == null ? "—" : `${(bt.hitRate * 100).toFixed(1)}%`} />
        <Stat label="Bets" value={`${bt.betsWon}/${bt.betsPlaced}`} />
        <Stat label="Final balance" value={bt.finalBalance == null ? "—" : `$${bt.finalBalance.toFixed(2)}`} valueClass={pnlCls} />
        <Stat label="Max drawdown" value={bt.maxDrawdown == null ? "—" : `$${bt.maxDrawdown.toFixed(2)}`} />
        <Stat label="Peak borrowed" value={bt.peakBorrowed == null ? "$0.00" : `$${bt.peakBorrowed.toFixed(2)}`} hint="Maximum |negative balance| reached. Only relevant when Allow borrow is on." />
        <Stat label="Zero crossings" value={bt.zeroCrossingsCount.toString()} hint="Effective bankruptcy events — steps where the next bet exceeded available bankroll." />
      </div>
      {bt.error && <div className="mt-3 text-rose-300 text-xs">Error: {bt.error}</div>}
    </div>
  );
}

// ── BacktestHistory ───────────────────────────────────────────────────────────────────────────

function BustTestBatchModal({ batchId, modelName, onClose, onOpenRun }:
    { batchId: string; modelName: string; onClose: () => void; onOpenRun: (r: Backtest) => void }) {
  const { data: rungs, isFetching } = useGetBacktestBatchQuery(batchId);
  const ordered = useMemo(() => [...(rungs ?? [])].sort((a, b) => (a.lookbackDay ?? 0) - (b.lookbackDay ?? 0)), [rungs]);
  const firstBust = ordered.find((r) => (r.zeroCrossingsCount ?? 0) > 0)?.lookbackDay ?? null;
  const initial = ordered[0]?.initialBalance ?? 0;
  return (
    <SideDrawer open onClose={onClose} widthClass="w-full md:w-[760px]">
      <div className="flex flex-col h-full">
        <div className="px-5 py-4 border-b border-fa-edge flex items-start justify-between gap-3">
          <div className="min-w-0">
            <div className="text-fa-frost-bright text-sm font-medium truncate">Bust test · {modelName}</div>
            <div className="text-fa-frost-dim text-xs">
              {ordered.length} rung{ordered.length === 1 ? "" : "s"} · lookback 1…{ordered[ordered.length - 1]?.lookbackDay ?? 0} days ·{" "}
              {firstBust == null
                ? <span className="text-emerald-300">survived every window</span>
                : <span className="text-rose-300">first bust at day {firstBust}</span>}
            </div>
          </div>
          <button onClick={onClose} aria-label="Close" title="Close (Esc)"
            className="h-8 w-8 shrink-0 inline-flex items-center justify-center rounded-md border border-fa-edge bg-fa-glass text-fa-frost-dim hover:text-fa-frost-bright hover:border-fa-frost/30 transition">
            <X className="h-4 w-4" />
          </button>
        </div>
        <div className="flex-1 overflow-y-auto px-5 py-4">
          {isFetching ? (
            <div className="text-fa-frost-dim text-sm py-8 text-center">Loading…</div>
          ) : (
            <table className="fa-table-bordered w-full text-xs">
              <thead className="text-fa-frost-dim">
                <tr className="text-left">
                  <th className="font-normal py-1 px-2 text-center">Status</th>
                  <th className="font-normal py-1 px-2 text-right">Lookback</th>
                  <th className="font-normal py-1 px-2 text-center">Hit rate</th>
                  <th className="font-normal py-1 px-2 text-right">Balance</th>
                  <th className="font-normal py-1 px-2 text-right">Δ %</th>
                </tr>
              </thead>
              <tbody>
                {ordered.map((r) => {
                  const running = r.status === "running";
                  const bust = (r.zeroCrossingsCount ?? 0) > 0;
                  const pct = r.finalBalance == null ? null : (r.finalBalance - initial) / initial * 100;
                  return (
                    <tr key={r.id} onClick={() => onOpenRun(r)} className="border-t border-fa-edge/40 cursor-pointer hover:bg-fa-frost/[0.04]" title="Open this rung's run report">
                      <td className="py-1 px-2 text-center">
                        <span className={cn("fa-status-orb", running ? "fa-status-running" : bust ? "fa-status-bust" : "fa-status-clean")} />
                      </td>
                      <td className="py-1 px-2 text-right tabular-nums text-fa-frost-bright">{r.lookbackDay} d</td>
                      <td className={cn("py-1 px-2 text-center tabular-nums", r.hitRate == null ? "text-fa-frost-dim" : r.hitRate >= 0.5 ? "text-emerald-300" : "text-rose-300")}>
                        {r.hitRate == null ? "—" : `${(r.hitRate * 100).toFixed(1)}%`}
                      </td>
                      <td className="py-1 px-2 text-right tabular-nums text-fa-frost-bright">{r.finalBalance == null ? "—" : `$${r.finalBalance.toFixed(2)}`}</td>
                      <td className={cn("py-1 px-2 text-right tabular-nums", pct == null ? "text-fa-frost-dim" : pct > 0 ? "text-emerald-300" : pct < 0 ? "text-rose-300" : "text-fa-frost-dim")}>
                        {pct == null ? "—" : `${pct >= 0 ? "+" : ""}${pct.toFixed(1)}%`}
                      </td>
                    </tr>
                  );
                })}
              </tbody>
            </table>
          )}
        </div>
      </div>
    </SideDrawer>
  );
}

function BacktestHistory({ rows, models, runningProgress }:
    { rows: Backtest[]; models: Model[]; runningProgress: Record<string, { placed: number; total: number; kind: string }> }) {
  const batchIds = Array.from(new Set(rows.map((r) => r.batchId).filter((b): b is string => !!b)));
  const batchClassFor = (id?: string | null) => id ? `fa-batch fa-batch-${(batchIds.indexOf(id) % 5) + 1}` : "";
  type RunKey = "started" | "model" | "strategy" | "symbol" | "interval" | "duration" | "hitRate" | "finalBalance" | "finalPct" | "maxMartingale";
  const modelNameById = useMemo(() => {
    const m = new Map<string, string>();
    for (const x of models) m.set(x.id, x.name);
    return m;
  }, [models]);
  const collapsedRows = useMemo(() => {
    const out: Backtest[] = [];
    const byBatch = new Map<string, Backtest[]>();
    for (const r of rows) {
      if (r.batchKind === "bust-test" && r.batchId) {
        const arr = byBatch.get(r.batchId); if (arr) arr.push(r); else byBatch.set(r.batchId, [r]);
      } else {
        out.push(r);
      }
    }
    for (const [, arr] of byBatch) {
      const busted = arr.filter((r) => (r.zeroCrossingsCount ?? 0) > 0)
        .sort((a, b) => (a.lookbackDay ?? 0) - (b.lookbackDay ?? 0));
      const rep = busted.length > 0
        ? busted[0]
        : [...arr].sort((a, b) => (b.lookbackDay ?? 0) - (a.lookbackDay ?? 0))[0];
      out.push(rep);
    }
    return out;
  }, [rows]);
  const bustSummary = useMemo(() => {
    const m = new Map<string, { rungs: number; done: number; pending: boolean; anyBust: boolean; firstBustDay: number | null; maxDay: number }>();
    for (const r of rows) {
      if (r.batchKind !== "bust-test" || !r.batchId) continue;
      const cur = m.get(r.batchId) ?? { rungs: 0, done: 0, pending: false, anyBust: false, firstBustDay: null as number | null, maxDay: 0 };
      cur.rungs += 1;
      cur.maxDay = Math.max(cur.maxDay, r.lookbackDay ?? 0);
      if (r.status === "running" || r.status === "queued") cur.pending = true;
      else cur.done += 1;
      if ((r.zeroCrossingsCount ?? 0) > 0) {
        cur.anyBust = true;
        const d = r.lookbackDay ?? 0;
        cur.firstBustDay = cur.firstBustDay == null ? d : Math.min(cur.firstBustDay, d);
      }
      m.set(r.batchId, cur);
    }
    return m;
  }, [rows]);
  const [openRun, setOpenRun] = useState<Backtest | null>(null);
  const [openBatchId, setOpenBatchId] = useState<string | null>(null);
  const { sortedRows, headerProps } = useSort<Backtest, RunKey>(collapsedRows, {
    started: (r) => new Date(r.startedAt),
    model: (r) => modelNameById.get(r.modelId) ?? "",
    strategy: (r) => r.strategyId ?? "",
    symbol: (r) => r.symbol,
    interval: (r) => r.interval,
    duration: (r) => r.endTime - r.startTime,
    hitRate: (r) => r.hitRate ?? null,
    finalBalance: (r) => r.finalBalance ?? null,
    finalPct: (r) => r.finalBalance == null ? null : (r.finalBalance - r.initialBalance) / r.initialBalance,
    maxMartingale: (r) => r.maxMartingaleStep ?? 0,
  }, { defaultKey: "started", defaultDir: "desc" });
  const [clearBacktests, { isLoading: isClearing }] = useClearBacktestsMutation();
  const confirm = useConfirm();
  const onClear = async () => {
    await confirm({
      title: `Clear ${rows.length} backtest run${rows.length === 1 ? "" : "s"}?`,
      description: "Every row in the recent-runs table will be removed permanently. This cannot be undone.",
      confirmLabel: `Delete ${rows.length} run${rows.length === 1 ? "" : "s"}`,
      destructive: true,
      onConfirm: async () => { await clearBacktests({}).unwrap(); },
    });
  };
  if (rows.length === 0) return null;
  return (
    <div className="fa-card px-5 py-4 flex-1 min-h-0 flex flex-col">
      <div className="flex items-center justify-between mb-3 shrink-0">
        <div className="fa-section-title">Recent runs</div>
        <button onClick={onClear} disabled={isClearing}
          className="inline-flex items-center gap-1.5 text-xs text-fa-frost-dim hover:text-rose-300 transition disabled:opacity-50"
          title="Delete every backtest run shown in the table">
          {isClearing ? <Loader2 className="h-3 w-3 animate-spin" /> : <Trash2 className="h-3 w-3" />}
          {isClearing ? "Clearing…" : "Clear all"}
        </button>
      </div>
      <div className="flex-1 min-h-0 overflow-auto">
        <table className="fa-table-bordered min-w-full text-xs [&_th]:whitespace-nowrap [&_td]:whitespace-nowrap">
          <thead className="text-fa-frost-dim sticky top-0 z-10 bg-fa-ink/95 backdrop-blur">
            <tr>
              <th className="font-normal pb-2 px-2 w-8 text-center" aria-label="Run status"></th>
              <th className="font-normal pb-2 pr-4 text-center"><SortHeader<RunKey> {...headerProps("started")}>Started</SortHeader></th>
              <th className="font-normal pb-2 pr-4 text-center"><SortHeader<RunKey> {...headerProps("symbol")}>Symbol</SortHeader></th>
              <th className="font-normal pb-2 pr-4 text-center">Range</th>
              <th className="font-normal pb-2 pr-4 text-center"><SortHeader<RunKey> {...headerProps("duration")}>Duration</SortHeader></th>
              <th className="font-normal pb-2 pr-4 text-center"><SortHeader<RunKey> {...headerProps("model")}>Model</SortHeader></th>
              <th className="font-normal pb-2 pr-4 text-center"><SortHeader<RunKey> {...headerProps("strategy")}>Strategy</SortHeader></th>
              <th className="font-normal pb-2 pr-4 text-center"><SortHeader<RunKey> {...headerProps("interval")}>Interval</SortHeader></th>
              <th className="font-normal pb-2 pr-4 text-center"><SortHeader<RunKey> {...headerProps("hitRate")}>Hit rate</SortHeader></th>
              <th className="font-normal pb-2 pr-4 text-right"><SortHeader<RunKey> {...headerProps("finalBalance")} align="right">Balance</SortHeader></th>
              <th className="font-normal pb-2 pr-4 text-right"><SortHeader<RunKey> {...headerProps("finalPct")} align="right">Δ %</SortHeader></th>
              <th className="font-normal pb-2 pr-4 text-center" aria-label="Peak bet multiplier vs initial"><SortHeader<RunKey> {...headerProps("maxMartingale")}><span className="fa-caption opacity-60">×</span></SortHeader></th>
            </tr>
          </thead>
          <tbody>
            {sortedRows.map((r, idx) => {
              const model = models.find((m) => m.id === r.modelId);
              const finalBalance = r.finalBalance ?? null;
              const finalDelta = finalBalance == null ? null : finalBalance - r.initialBalance;
              const hitClass = r.hitRate == null ? "text-fa-frost-dim" : r.hitRate >= 0.5 ? "text-emerald-300" : "text-rose-300";
              const isNoBets = r.status === "no-bets";
              const sweep = (r.batchKind === "bust-test" && r.batchId) ? bustSummary.get(r.batchId) : undefined;
              const isBust = sweep ? sweep.anyBust : (r.zeroCrossingsCount ?? 0) > 0;
              const isRunningRow = sweep ? sweep.pending : (r.status === "running" || r.status === "queued");
              const rowProgress = isRunningRow && runningProgress[r.id] ? runningProgress[r.id] : null;
              const rowPct = rowProgress && rowProgress.total > 0 ? Math.min(100, (rowProgress.placed / rowProgress.total) * 100) : null;
              const batchClass = batchClassFor(r.batchId);
              const orbClass = isRunningRow ? "fa-status-running" : isNoBets ? "fa-status-running" : isBust ? "fa-status-bust" : "fa-status-clean";
              const orbTitle = isRunningRow
                ? rowProgress && rowPct != null ? `Running — ${rowPct.toFixed(0)}% complete (${rowProgress.placed.toLocaleString()} candles processed)` : "Running…"
                : isNoBets ? (r.error ?? "No bets were placed in this run.")
                : isBust ? `Bankrupt — strategy could not afford the doubled-up bet on ${r.zeroCrossingsCount} step${r.zeroCrossingsCount === 1 ? "" : "s"}.`
                : "Solvent throughout.";
              const winLossTint = isRunningRow ? "" : isBust ? "bg-rose-400/[0.06]" : finalDelta != null && finalDelta > 0 ? "bg-emerald-400/[0.05]" : finalDelta != null && finalDelta < 0 ? "bg-rose-400/[0.06]" : "";
              const stripeTint = winLossTint ? "" : (idx % 2 === 1 ? "bg-fa-frost/[0.018]" : "");
              const peakMultiplier = Math.pow(2, r.maxMartingaleStep ?? 0);
              const isBustBatch = r.batchKind === "bust-test" && !!r.batchId;
              const onRowClick = () => {
                if (isBustBatch && r.batchId) setOpenBatchId(r.batchId);
                else if (!isRunningRow) setOpenRun(r);
              };
              return (
                <tr key={r.id} onClick={onRowClick} className={cn("border-t border-fa-edge/40 relative cursor-pointer hover:bg-fa-frost/[0.04] transition-colors", isRunningRow && "fa-backtest-shimmer", winLossTint, stripeTint, batchClass)}
                  title={isBustBatch ? "Open bust-test report" : "Open run report"}>
                  <td className="py-1.5 px-2 text-center"><span className={cn("fa-status-orb", orbClass)} title={orbTitle} /></td>
                  <td className="py-1.5 pr-4 text-fa-frost-dim" title={new Date(r.startedAt).toLocaleString()}>{fmtRunTime(r.startedAt)}</td>
                  <td className="py-1.5 pr-4 text-fa-frost-bright text-center"><SymbolIcon symbol={r.symbol} className="h-5 w-5" /></td>
                  <td className="py-1.5 pr-4 tabular-nums leading-tight">
                    <div className="text-fa-frost-bright text-xs">from {fmtRunDate(r.startTime)}</div>
                    <div className="text-fa-frost-dim fa-caption">to {fmtRunDate(r.endTime)}</div>
                  </td>
                  <td className="py-1.5 pr-4 text-center text-fa-frost-bright tabular-nums">
                    {sweep ? `1–${sweep.maxDay} d` : `${Math.round((r.endTime - r.startTime) / 86_400_000)} d`}
                  </td>
                  <td className="py-1.5 pr-4 text-fa-frost-bright">
                    <div className="flex items-center justify-between gap-3">
                      <span>{model?.name ?? "?"}</span>
                      {isBustBatch && sweep ? (() => {
                        const n = sweep.rungs;
                        if (sweep.pending) {
                          return <span className="ml-1.5 fa-overline rounded-full px-1.5 py-0.5 border text-cyan-300 bg-cyan-300/10 border-cyan-300/30" title={`Bust test in progress — running ${sweep.done} of ${n} windows.`}>bust test · running {sweep.done}/{n}</span>;
                        }
                        const survived = !sweep.anyBust;
                        const verdict = survived ? `no bust ≤ ${n}d` : `bust @ day ${sweep.firstBustDay}`;
                        const cls = survived ? "text-emerald-300 bg-emerald-300/10 border-emerald-300/30" : "text-rose-300 bg-rose-300/10 border-rose-300/30";
                        return <span className={cn("ml-1.5 fa-overline rounded-full px-1.5 py-0.5 border", cls)} title={`Bust test: ${n} backtest${n === 1 ? "" : "s"} over the last 1…${n} days. Click to open the full per-window report.`}>bust test · {verdict}</span>;
                      })() : !isRunningRow ? (() => {
                        const cls = isNoBets ? "text-amber-300 bg-amber-300/10 border-amber-300/30" : isBust ? "text-rose-300 bg-rose-300/10 border-rose-300/30" : "text-emerald-300 bg-emerald-300/10 border-emerald-300/30";
                        const label = isNoBets ? "no bets" : isBust ? "went bust" : "survived";
                        return <span className={cn("fa-overline rounded-full px-1.5 py-0.5 border whitespace-nowrap", cls)}>{label}</span>;
                      })() : null}
                    </div>
                  </td>
                  <td className="py-1.5 pr-4 text-fa-frost-dim capitalize">{r.strategyId ?? "flat"}</td>
                  <td className="py-1.5 pr-4 text-fa-frost-bright">{r.interval}</td>
                  <td className={cn("py-1.5 pr-4 text-right tabular-nums", hitClass)}>{r.hitRate == null ? "—" : `${(r.hitRate * 100).toFixed(1)}%`}</td>
                  <td className="py-1.5 pr-4 text-right tabular-nums text-fa-frost-bright">{finalBalance == null ? "—" : `$${finalBalance.toFixed(2)}`}</td>
                  {(() => {
                    const pct = finalBalance == null ? null : (finalBalance - r.initialBalance) / r.initialBalance * 100;
                    const pctClass = pct == null ? "text-fa-frost-dim" : pct > 0 ? "text-emerald-300" : pct < 0 ? "text-rose-300" : "text-fa-frost-dim";
                    return <td className={cn("py-1.5 pr-4 text-right tabular-nums", pctClass)}>{pct == null ? "—" : `${pct >= 0 ? "+" : ""}${pct.toFixed(1)}%`}</td>;
                  })()}
                  <td className={cn("py-1.5 pr-4 text-right tabular-nums", r.maxMartingaleStep >= 8 ? "text-rose-300" : r.maxMartingaleStep >= 5 ? "text-amber-300" : "text-fa-frost-dim")} title={`Peak bet was ${peakMultiplier}× the initial bet (chain depth ${r.maxMartingaleStep ?? 0}).`}>
                    {peakMultiplier.toLocaleString()}×
                  </td>
                </tr>
              );
            })}
          </tbody>
        </table>
      </div>
      {openRun && (
        <BacktestRunModal run={openRun} modelName={modelNameById.get(openRun.modelId) ?? "Model"} onClose={() => setOpenRun(null)} />
      )}
      {openBatchId && (
        <BustTestBatchModal
          batchId={openBatchId}
          modelName={modelNameById.get(rows.find((r) => r.batchId === openBatchId)?.modelId ?? "") ?? "Model"}
          onClose={() => setOpenBatchId(null)}
          onOpenRun={(run) => { setOpenBatchId(null); setOpenRun(run); }}
        />
      )}
    </div>
  );
}

// ── BacktestTab ───────────────────────────────────────────────────────────────────────────────

function BacktestTab({ models, eligible }: { models: Model[]; eligible: Model[] }) {
  const { data: symbolsResp } = useGetSymbolsQuery();
  const { data: strategiesResp } = useGetStakingStrategiesQuery();
  const ensureTrained = useModelTrainGate();
  const supportedSymbols = symbolsResp?.symbols ?? ["BTCUSDT"];
  const supportedIntervals = ["5m"];
  const availableStrategies = strategiesResp?.strategies ?? [{ id: "flat", name: "Flat", description: "" }];

  const [modelIds, setModelIds] = useLocalStorageState<string[]>("fa.backtesting.modelIds", () => { const m = firstUsableModel(eligible); return m ? [m.id] : []; });
  const [strategyIds, setStrategyIds] = useLocalStorageState<string[]>("fa.backtesting.strategyIds", ["flat"]);
  const [symbol, setSymbol] = useLocalStorageState<string>("fa.backtesting.symbol", supportedSymbols[0] ?? "BTCUSDT");
  const [interval, setInterval] = useLocalStorageState<string>("fa.backtesting.interval", "5m");
  useEffect(() => { if (interval !== "5m") setInterval("5m"); }, [interval, setInterval]);
  const [days, setDays] = useLocalStorageState<number>("fa.backtesting.days", () => optimalLookbackDays("5m"));
  const isFirstIntervalEffect = useRef(true);
  useEffect(() => {
    if (isFirstIntervalEffect.current) { isFirstIntervalEffect.current = false; return; }
    setDays(optimalLookbackDays(interval));
  }, [interval, setDays]);
  const [initialBalance, setInitialBalance] = useLocalStorageState<number>("fa.backtesting.initialBalance", 1000);
  const [initialBetSize, setInitialBetSize] = useLocalStorageState<number>("fa.backtesting.initialBetSize", 10);
  const [allowBorrow, setAllowBorrow] = useLocalStorageState<boolean>("fa.backtesting.allowBorrow", false);
  const [applyGate, setApplyGate] = useLocalStorageState<boolean>("fa.backtesting.applyGate", false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    setModelIds((prev) => sanitizeModelSelection(prev, eligible));
  }, [eligible]);

  // Selecting an untrained model opens the train gate; we only add it once trained.
  const handleModelAction = async (id: string) => {
    const m = eligible.find((x) => x.id === id);
    if (!m) return;
    if (await ensureTrained(m)) setModelIds((prev) => (prev.includes(id) ? prev : [...prev, id]));
  };

  useEffect(() => {
    if (initialBetSize > initialBalance) setInitialBetSize(initialBalance);
  }, [initialBalance, initialBetSize, setInitialBetSize]);

  useEffect(() => {
    if (!strategiesResp) return;
    const validIds = new Set(strategiesResp.strategies.map((s) => s.id));
    setStrategyIds((prev) => {
      const filtered = prev.filter((id) => validIds.has(id));
      return filtered.length > 0 ? filtered : [strategiesResp.default];
    });
  }, [strategiesResp]);

  const [run, { data: latestRun }] = useRunBacktestMutation();
  const { data: history, refetch } = useListBacktestsQuery({});
  const [progress, setProgress] = useState<Record<string, { placed: number; total: number; kind: string }>>({});
  const subscriptions = useRef<Map<string, EventSource>>(new Map());

  const subscribeToRun = useCallback((id: string) => {
    if (subscriptions.current.has(id)) return;
    const es = new EventSource(`/api/backtests/${id}/stream`);
    subscriptions.current.set(id, es);
    const cleanup = () => {
      es.close();
      subscriptions.current.delete(id);
      setProgress((p) => { const next = { ...p }; delete next[id]; return next; });
      refetch();
    };
    es.onmessage = (msg) => {
      try {
        const evt = JSON.parse(msg.data);
        setProgress((p) => ({ ...p, [id]: { placed: evt.candlesProcessed ?? 0, total: evt.totalCandles ?? 0, kind: evt.kind } }));
        if (evt.kind === "completed" || evt.kind === "failed") {
          if (evt.kind === "failed" && evt.error) setError(evt.error);
          cleanup();
        }
      } catch { /* malformed frame */ }
    };
    es.onerror = cleanup;
  }, [refetch]);

  useEffect(() => {
    if (!history) return;
    for (const row of history) {
      if (row.status === "running" && !subscriptions.current.has(row.id)) {
        setProgress((p) => p[row.id] ? p : ({ ...p, [row.id]: { placed: 0, total: 0, kind: "started" } }));
        subscribeToRun(row.id);
      }
    }
  }, [history, subscribeToRun]);

  useEffect(() => {
    const subs = subscriptions.current;
    return () => { for (const es of subs.values()) es.close(); subs.clear(); };
  }, []);

  // No polling: each running backtest opens a per-run SSE (subscribeToRun) whose terminal event
  // refetches the history, flipping the row running → complete. The list stays live via those
  // push events, not an interval.

  const fanout = useMemo(() =>
    modelIds.flatMap((m) => strategyIds.map((s) => ({ modelId: m, strategyId: s }))),
    [modelIds, strategyIds]);
  const isABMode = fanout.length > 1;

  const isRunning = Object.keys(progress).length > 0;
  const progressVals = Object.values(progress).filter((p) => p.total > 0);
  const progressPct = progressVals.length === 0 ? 0 :
    Math.min(100, progressVals.reduce((sum, p) => sum + (p.placed / p.total) * 100, 0) / progressVals.length);
  const aggPlaced = Object.values(progress).reduce((sum, p) => sum + p.placed, 0);
  const aggTotal = Object.values(progress).reduce((sum, p) => sum + p.total, 0);

  const onRun = async () => {
    setError(null);
    if (modelIds.length === 0) { setError("Pick at least one deterministic model."); return; }
    if (strategyIds.length === 0) { setError("Pick at least one staking strategy."); return; }
    const endTime = Date.now();
    const startTime = endTime - days * 24 * 60 * 60 * 1000;
    const batchId = isABMode ? crypto.randomUUID() : undefined;
    const estTotal = Math.floor((days * 86_400_000) / intervalToMs(interval));
    const launches = fanout.map(async ({ modelId: mId, strategyId: sId }) => {
      try {
        const row = await run({ modelId: mId, symbol, interval, startTime, endTime, initialBalance, initialBetSize, allowBorrow, batchId, strategyId: sId, applyGate }).unwrap();
        setProgress((p) => ({ ...p, [row.id]: { placed: 0, total: estTotal, kind: "started" } }));
        refetch();
        subscribeToRun(row.id);
      } catch (e: unknown) {
        const err = e as { data?: { error?: string }; status?: number };
        setError(err.data?.error ?? "Backtest failed");
      }
    });
    await Promise.allSettled(launches);
  };

  if (eligible.length === 0) {
    return (
      <div className="fa-card px-6 py-12 text-center space-y-3">
        <AlertTriangle className="h-6 w-6 text-amber-300 mx-auto" />
        <p className="text-fa-frost-bright">No backtestable models</p>
        <p className="text-fa-frost-dim text-sm max-w-md mx-auto">
          LLM-based models can't be backtested (non-deterministic). Build a deterministic model in Models — a linear or logistic regression on indicator features — to enable backtesting.
        </p>
      </div>
    );
  }

  return (
    <div className="flex-1 min-h-0 flex flex-col gap-4">
      <div className="fa-card px-5 py-4 shrink-0">
        <div className="flex items-center justify-between mb-4">
          <div className="fa-section-title inline-flex items-center gap-1.5">
            Run a new backtest
            <InfoTip width={320} content={<TipBody title="Backtest">Backtest replays the selected staking strategy against historical candles. With Allow borrow on, balance can dip negative; with strict-bust on, the run halts the moment the next sized bet would exceed the bankroll. A/B mode posts one run per model × strategy in parallel, grouped by batch id.</TipBody>}>
              <button type="button" aria-label="About backtests" className="text-fa-frost-dim/70 hover:text-fa-frost-bright transition leading-none">
                <Info className="h-3.5 w-3.5" />
              </button>
            </InfoTip>
          </div>
          {isABMode && (
            <span className="fa-overline text-amber-300 bg-amber-300/10 border border-amber-300/30 rounded-full px-2 py-0.5">
              A/B · {fanout.length} runs · {modelIds.length}m × {strategyIds.length}s
            </span>
          )}
        </div>
        <div className="grid grid-cols-1 md:grid-cols-2 gap-4 mb-4">
          <Field label={`Models${modelIds.length > 1 ? ` (${modelIds.length} selected)` : ""}`}
            info={{ title: "Models", body: "Pick one model to backtest, or several to fan out runs in parallel. Selecting multiple here AND multiple strategies produces an A/B grid." }}>
            <RichMultiSelect
              options={buildModelOptions(eligible)}
              value={modelIds}
              onChange={setModelIds}
              onOptionAction={handleModelAction}
              placeholder="Select models…"
            />
          </Field>
          <Field label={`Staking strategies${strategyIds.length > 1 ? ` (${strategyIds.length} selected)` : ""}`}
            info={{ title: "Staking strategy", body: "How each bet is sized. Flat = constant stake. Martingale = double after a loss. Selecting several fans the chosen models across all selected strategies." }}>
            <RichMultiSelect
              options={availableStrategies.map((s): RichMultiSelectOption => ({
                value: s.id,
                label: s.name,
                sublabel: s.description || undefined,
              }))}
              value={strategyIds}
              onChange={setStrategyIds}
              placeholder="Select strategies…"
              minSelected={1}
            />
          </Field>
        </div>
        <div className="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-5 gap-4">
          <Field label="Symbol">
            <SymbolPicker symbols={supportedSymbols} value={symbol} onChange={setSymbol} size="sm" />
          </Field>
          <Field label="Interval">
            <select value={interval} onChange={(e) => setInterval(e.target.value)}
              className="fa-select w-full bg-fa-glass border border-fa-edge rounded-md px-2 py-1.5 text-fa-frost-bright text-sm">
              {supportedIntervals.map((iv) => <option key={iv} value={iv} className="bg-fa-ink-2">{iv}</option>)}
            </select>
          </Field>
          <Field label="Lookback (days)"
            info={{ title: "Lookback window", body: "How many days back to backtest, ending now. 5m default (180d) gives roughly 50k candles — enough for a meaningful hit-rate." }}>
            <input type="number" min={1} max={730} value={days}
              onChange={(e) => setDays(Math.min(730, Math.max(1, Number(e.target.value))))}
              className="fa-input w-full bg-fa-glass border border-fa-edge rounded-md px-2 py-1.5 text-fa-frost-bright text-sm" />
          </Field>
          <Field label="Bankroll ($)"
            info={{ title: "Starting bankroll", body: "Notional starting balance. The 'initial bet' is automatically capped at the bankroll." }}>
            <input type="number" min={10} value={initialBalance}
              onChange={(e) => {
                const next = Math.max(10, Number(e.target.value));
                setInitialBalance(next);
                if (initialBetSize > next) setInitialBetSize(next);
              }}
              className="fa-input w-full bg-fa-glass border border-fa-edge rounded-md px-2 py-1.5 text-fa-frost-bright text-sm" />
          </Field>
          <Field label="Initial bet ($)"
            info={{ title: "Initial bet size", body: "The stake on the first bet. Clamped to [$1, bankroll]." }}>
            <input type="number" min={1} max={initialBalance} value={initialBetSize}
              onChange={(e) => setInitialBetSize(Math.min(initialBalance, Math.max(1, Number(e.target.value))))}
              className="fa-input w-full bg-fa-glass border border-fa-edge rounded-md px-2 py-1.5 text-fa-frost-bright text-sm" />
          </Field>
        </div>
        <div className="mt-4 flex items-center gap-3 flex-wrap justify-between">
          <div className="flex items-center gap-3 flex-wrap text-xs text-fa-frost-dim">
            {isRunning && aggTotal > 0 && (
              <span className="tabular-nums">
                Processing candle {aggPlaced.toLocaleString()} of {aggTotal.toLocaleString()} · {progressPct.toFixed(1)}%
              </span>
            )}
            {error && <span className="text-rose-300">{error}</span>}
          </div>
          <div className="flex items-center gap-3 flex-wrap ml-auto">
            <label className="inline-flex items-center gap-2 cursor-pointer select-none"
              title={allowBorrow ? "Run continues even when a staking step exceeds the bankroll." : "Run halts the moment the next sized bet would exceed the bankroll — matches live paper-trading bankruptcy."}>
              <span className="relative inline-block w-9 h-5">
                <input type="checkbox" checked={allowBorrow} onChange={(e) => setAllowBorrow(e.target.checked)} className="peer sr-only" />
                <span className={cn("absolute inset-0 rounded-full transition-colors", allowBorrow ? "bg-fa-frost-bright/40" : "bg-fa-edge")} />
                <span className={cn("absolute top-0.5 left-0.5 h-4 w-4 rounded-full bg-fa-frost-bright transition-transform", allowBorrow ? "translate-x-4" : "translate-x-0")} />
              </span>
              <span className="text-fa-frost-dim text-xs">
                Allow borrow {allowBorrow ? <span className="text-emerald-300">(on)</span> : <span className="text-amber-300">(strict bust)</span>}
              </span>
            </label>
            <label className="inline-flex items-center gap-2 cursor-pointer select-none"
              title={applyGate ? "Confidence gate ON: candles with pUp in the ±2pp no-bet band are SKIPPED." : "Confidence gate OFF: bet every candle (the always-bet baseline)."}>
              <span className="relative inline-block w-9 h-5">
                <input type="checkbox" checked={applyGate} onChange={(e) => setApplyGate(e.target.checked)} className="peer sr-only" />
                <span className={cn("absolute inset-0 rounded-full transition-colors", applyGate ? "bg-fa-frost-bright/40" : "bg-fa-edge")} />
                <span className={cn("absolute top-0.5 left-0.5 h-4 w-4 rounded-full bg-fa-frost-bright transition-transform", applyGate ? "translate-x-4" : "translate-x-0")} />
              </span>
              <span className="text-fa-frost-dim text-xs">
                Confidence gate {applyGate ? <span className="text-emerald-300">(skip no-bets)</span> : <span className="text-amber-300">(always bet)</span>}
              </span>
            </label>
            <button onClick={onRun}
              disabled={isRunning || modelIds.length === 0 || strategyIds.length === 0}
              className="inline-flex items-center gap-2 px-4 py-2 rounded-md bg-fa-frost-bright/20 hover:bg-fa-frost-bright/30 text-fa-frost-bright text-sm border border-fa-frost-bright/30 disabled:opacity-50 disabled:cursor-not-allowed transition relative overflow-hidden">
              {isRunning && (
                <span aria-hidden className="absolute inset-y-0 left-0 bg-emerald-300/30 transition-[width] duration-300" style={{ width: `${progressPct}%` }} />
              )}
              <span className="relative inline-flex items-center gap-2">
                {isRunning ? <Loader2 className="h-4 w-4 animate-spin" /> : <Play className="h-4 w-4" />}
                {isRunning ? `Running ${Object.keys(progress).length}…` : isABMode ? `Run ${fanout.length}-run A/B` : "Run backtest"}
              </span>
            </button>
          </div>
        </div>
        {isABMode && (
          <p className="text-fa-frost-dim/70 fa-caption mt-3">
            A/B mode posts {fanout.length} runs in parallel ({modelIds.length} model{modelIds.length > 1 ? "s" : ""} × {strategyIds.length} strateg{strategyIds.length > 1 ? "ies" : "y"}), grouped by batch id.
          </p>
        )}
      </div>

      <RiskPreview
        models={models}
        modelIds={modelIds}
        interval={interval}
        bankroll={initialBalance}
        bet={initialBetSize}
        strategyIds={strategyIds}
        availableStrategyNames={Object.fromEntries(availableStrategies.map((s) => [s.id, s.name]))}
      />

      {(() => {
        if (!latestRun || isABMode) return null;
        const live = history?.find((r) => r.id === latestRun.id);
        if (!live || live.status === "running") return null;
        return <BacktestReport bt={live} models={models} />;
      })()}

      <BacktestHistory rows={history ?? []} models={models} runningProgress={progress} />
    </div>
  );
}

// ── ChaosSamplesDrawer ────────────────────────────────────────────────────────────────────────

/**
 * Per-window drill-in for a completed chaos run. Fetches GET /api/chaos/{id}/samples and shows
 * each random window's outcome (start time, survived flag, final balance, drawdown). Reuses the
 * SideDrawer chrome from BustTestBatchModal for visual consistency.
 */
function ChaosSamplesDrawer({ run, modelName, onClose }:
    { run: ChaosRunNormalized; modelName: string; onClose: () => void }) {
  const { data: samples, isFetching } = useGetChaosSamplesQuery(run.id);
  const ordered = useMemo(
    () => [...(samples ?? [])].sort((a, b) => a.finalBalance - b.finalBalance),
    [samples],
  );
  const bustedCount = ordered.filter((s) => !s.survived).length;
  const windowDays = windowCandlesToDays(run.windowLength, run.interval);
  return (
    <SideDrawer open onClose={onClose} widthClass="w-full md:w-[760px]">
      <div className="flex flex-col h-full">
        <div className="px-5 py-4 border-b border-fa-edge flex items-start justify-between gap-3">
          <div className="min-w-0">
            <div className="text-fa-frost-bright text-sm font-medium truncate">Chaos windows · {modelName}</div>
            <div className="text-fa-frost-dim text-xs">
              {ordered.length} window{ordered.length === 1 ? "" : "s"} · {windowDays}d each ·{" "}
              {bustedCount === 0
                ? <span className="text-emerald-300">survived every window</span>
                : <span className="text-rose-300">{bustedCount} busted</span>}
              {" · "}<span className="capitalize">{run.strategyId}</span>
            </div>
          </div>
          <button onClick={onClose} aria-label="Close" title="Close (Esc)"
            className="h-8 w-8 shrink-0 inline-flex items-center justify-center rounded-md border border-fa-edge bg-fa-glass text-fa-frost-dim hover:text-fa-frost-bright hover:border-fa-frost/30 transition">
            <X className="h-4 w-4" />
          </button>
        </div>
        <div className="flex-1 overflow-y-auto px-5 py-4">
          {isFetching && ordered.length === 0 ? (
            <div className="text-fa-frost-dim text-sm py-8 text-center">Loading…</div>
          ) : ordered.length === 0 ? (
            <div className="text-fa-frost-dim text-sm py-8 text-center">No per-window samples recorded for this run.</div>
          ) : (
            <table className="fa-table-bordered w-full text-xs">
              <thead className="text-fa-frost-dim">
                <tr className="text-left">
                  <th className="font-normal py-1 px-2 text-center">Status</th>
                  <th className="font-normal py-1 px-2 text-left">Window start</th>
                  <th className="font-normal py-1 px-2 text-right">Final balance</th>
                  <th className="font-normal py-1 px-2 text-right">Max DD</th>
                  <th className="font-normal py-1 px-2 text-right" title="Times the balance crossed zero">0-cross</th>
                </tr>
              </thead>
              <tbody>
                {ordered.map((s) => (
                  <tr key={s.id} className="border-t border-fa-edge/40">
                    <td className="py-1 px-2 text-center">
                      <span className={cn("fa-status-orb", s.survived ? "fa-status-clean" : "fa-status-bust")} title={s.survived ? "Survived the window." : "Busted in this window."} />
                    </td>
                    <td className="py-1 px-2 text-fa-frost-dim tabular-nums" title={new Date(s.startMs).toLocaleString()}>
                      {fmtRunDate(s.startMs)} {fmtRunTime(new Date(s.startMs).toISOString())}
                    </td>
                    <td className={cn("py-1 px-2 text-right tabular-nums", s.survived ? "text-fa-frost-bright" : "text-rose-300")}>
                      ${s.finalBalance.toFixed(2)}
                    </td>
                    <td className={cn("py-1 px-2 text-right tabular-nums", s.maxDrawdown > 0 ? "text-rose-300" : "text-fa-frost-dim")}>
                      {s.maxDrawdown === 0 ? "$0.00" : `-$${s.maxDrawdown.toFixed(2)}`}
                    </td>
                    <td className={cn("py-1 px-2 text-right tabular-nums", s.zeroCrossings > 0 ? "text-amber-300" : "text-fa-frost-dim")}>
                      {s.zeroCrossings}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          )}
        </div>
      </div>
    </SideDrawer>
  );
}

// ── ChaosTab ──────────────────────────────────────────────────────────────────────────────────

function ChaosTab({ models, eligible }: { models: Model[]; eligible: Model[] }) {
  const { data: symbolsResp } = useGetSymbolsQuery();
  const { data: strategiesResp } = useGetStakingStrategiesQuery();
  const ensureTrained = useModelTrainGate();
  const supportedSymbols = symbolsResp?.symbols ?? ["BTCUSDT"];
  const availableStrategies = strategiesResp?.strategies ?? [{ id: "flat", name: "Flat", description: "" }];

  const [modelIds, setModelIds] = useLocalStorageState<string[]>("fa.chaos.modelIds", () => { const m = firstUsableModel(eligible); return m ? [m.id] : []; });
  const [strategyId, setStrategyId] = useLocalStorageState<string>("fa.chaos.strategyId", "flat");
  const [symbol, setSymbol] = useLocalStorageState<string>("fa.chaos.symbol", supportedSymbols[0] ?? "BTCUSDT");
  const interval = "5m";
  const [initialBalance, setInitialBalance] = useLocalStorageState<number>("fa.chaos.initialBalance", 1000);
  const [initialBetSize, setInitialBetSize] = useLocalStorageState<number>("fa.chaos.initialBetSize", 10);
  const [windowDays, setWindowDays] = useLocalStorageState<number>("fa.chaos.windowDays", 14);
  const [sampleCount, setSampleCount] = useLocalStorageState<number>("fa.chaos.sampleCount", 200);
  const [allowBorrow, setAllowBorrow] = useLocalStorageState<boolean>("fa.chaos.allowBorrow", false);
  const [error, setError] = useState<string | null>(null);
  const [openSamplesRun, setOpenSamplesRun] = useState<ChaosRunNormalized | null>(null);

  const [runChaos, { isLoading: chaosLoading }] = useRunChaosMutation();
  const { data: chaosRuns, refetch: refetchChaos } = useListChaosRunsQuery({});
  const chaosSubs = useRef<Map<string, EventSource>>(new Map());

  // Push-based chaos progress: one SSE per in-flight batch (GET /api/chaos/stream?batchId). A combo
  // finishing emits a progress frame whose sampleIndex reaches totalSamples — we refetch the list so
  // that row flips running → complete; the terminal completed/failed frame closes the stream. This
  // replaces the old 2s interval poll.
  const subscribeToBatch = useCallback((batchId: string) => {
    if (chaosSubs.current.has(batchId)) return;
    const es = new EventSource(`/api/chaos/stream?batchId=${batchId}`);
    chaosSubs.current.set(batchId, es);
    const cleanup = () => {
      es.close();
      chaosSubs.current.delete(batchId);
      refetchChaos();
    };
    es.onmessage = (msg) => {
      try {
        const evt = JSON.parse(msg.data);
        if (evt.kind === "completed" || evt.kind === "failed") { cleanup(); return; }
        // A single combo just finished (or errored) when its progress reaches the sample total.
        if (evt.kind === "progress" && (evt.sampleIndex ?? 0) >= (evt.totalSamples ?? 0)) refetchChaos();
      } catch { /* malformed frame */ }
    };
    es.onerror = cleanup;
  }, [refetchChaos]);

  // Subscribe to any batch with a still-running row (covers runs started in another tab/session).
  useEffect(() => {
    for (const cr of chaosRuns ?? []) {
      if (cr.status === "running" && cr.batchId && !chaosSubs.current.has(cr.batchId)) {
        subscribeToBatch(cr.batchId);
      }
    }
  }, [chaosRuns, subscribeToBatch]);

  // Close every open chaos stream on unmount.
  useEffect(() => {
    const subs = chaosSubs.current;
    return () => { for (const es of subs.values()) es.close(); subs.clear(); };
  }, []);

  useEffect(() => {
    setModelIds((prev) => sanitizeModelSelection(prev, eligible));
  }, [eligible]);

  // Selecting an untrained model opens the train gate; we only add it once trained.
  const handleModelAction = async (id: string) => {
    const m = eligible.find((x) => x.id === id);
    if (!m) return;
    if (await ensureTrained(m)) setModelIds((prev) => (prev.includes(id) ? prev : [...prev, id]));
  };

  useEffect(() => {
    if (initialBetSize > initialBalance) setInitialBetSize(initialBalance);
  }, [initialBalance, initialBetSize, setInitialBetSize]);

  useEffect(() => {
    if (!strategiesResp) return;
    const validIds = new Set(strategiesResp.strategies.map((s) => s.id));
    if (!validIds.has(strategyId)) setStrategyId(strategiesResp.default ?? "flat");
  }, [strategiesResp, strategyId, setStrategyId]);

  // days → candles for the chaos window length. candlesPerDay = 1 day ÷ intervalMs.
  const candlesPerDay = 86_400_000 / intervalToMs(interval);
  const windowLengthCandles = Math.round(windowDays * candlesPerDay);

  const isRunning = chaosLoading || (chaosRuns ?? []).some((cr) => cr.status === "running");

  // The batch currently executing — its rows share one batchId. We stream that batch's combo/sample
  // progress so the run shows real movement instead of a 2s-polled orb. Idle (null) when nothing runs.
  const runningBatchId = useMemo(
    () => (chaosRuns ?? []).find((cr) => cr.status === "running")?.batchId ?? null,
    [chaosRuns],
  );
  const chaosProgress = useChaosProgress(runningBatchId);

  const onRun = async () => {
    setError(null);
    if (modelIds.length === 0) { setError("Pick at least one trained model."); return; }
    const req: ChaosRequest = {
      modelIds,
      strategyIds: [strategyId],
      symbol,
      interval,
      windowLengthCandles,
      lengthSweep: null,
      sampleCount,
      initialBalance,
      initialBetSize,
      allowBorrow,
      seed: null,
    };
    try {
      // Chaos fans out over all selected models internally — one call per launch.
      const { batchId } = await runChaos(req).unwrap();
      refetchChaos();
      if (batchId) subscribeToBatch(batchId);
    } catch (e: unknown) {
      const err = e as { data?: { error?: string } };
      setError(err.data?.error ?? "Chaos test failed");
    }
  };

  const modelNameById = useMemo(() => {
    const m = new Map<string, string>();
    for (const x of models) m.set(x.id, x.name);
    return m;
  }, [models]);
  const sortedChaos = useMemo(
    () => [...(chaosRuns ?? [])].sort((a, b) => new Date(b.startedAt).getTime() - new Date(a.startedAt).getTime()),
    [chaosRuns],
  );

  if (eligible.length === 0) {
    return (
      <div className="fa-card px-6 py-12 text-center space-y-3">
        <AlertTriangle className="h-6 w-6 text-amber-300 mx-auto" />
        <p className="text-fa-frost-bright">No backtestable models</p>
        <p className="text-fa-frost-dim text-sm max-w-md mx-auto">
          Build a deterministic model in Models to enable chaos testing.
        </p>
      </div>
    );
  }

  return (
    <div className="flex-1 min-h-0 flex flex-col gap-4">
      {/* Launch form */}
      <div className="fa-card px-5 py-4 shrink-0">
        <div className="fa-section-title mb-4 inline-flex items-center gap-1.5">
          Run a chaos test
          <InfoTip width={360} content={<TipBody title="Chaos test">A chaos test draws many random windows from history and replays each one for the selected model + strategy. It reports how often the strategy busts (bust rate), the spread of profit outcomes (P5/P50/P95), the worst drawdown seen, and a Pass verdict (bust rate 0 AND median profit &gt; 0). It answers "does this survive random starting points?" rather than a single fixed window.</TipBody>}>
            <button type="button" aria-label="About chaos tests" className="text-fa-frost-dim/70 hover:text-fa-frost-bright transition leading-none">
              <Info className="h-3.5 w-3.5" />
            </button>
          </InfoTip>
        </div>
        <div className="grid grid-cols-1 md:grid-cols-2 gap-4 mb-4">
          <Field label={`Models${modelIds.length > 1 ? ` (${modelIds.length})` : ""}`}
            info={{ title: "Models", body: "Pick one or more trained deterministic models. The chaos engine fans out one result row per model × strategy × window length." }}>
            <RichMultiSelect
              options={buildModelOptions(eligible)}
              value={modelIds}
              onChange={setModelIds}
              onOptionAction={handleModelAction}
              placeholder="Select models…"
            />
          </Field>
          <Field label="Staking strategy"
            info={{ title: "Staking strategy", body: "The staking method applied across every sampled window. A single strategy keeps the survival comparison clean." }}>
            <RichMultiSelect
              options={availableStrategies.map((s): RichMultiSelectOption => ({
                value: s.id,
                label: s.name,
                sublabel: s.description || undefined,
              }))}
              value={[strategyId]}
              onChange={(next) => { if (next.length > 0) setStrategyId(next[next.length - 1]); }}
              placeholder="Select strategy…"
              minSelected={1}
            />
          </Field>
        </div>
        <div className="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-5 gap-4">
          <Field label="Symbol">
            <SymbolPicker symbols={supportedSymbols} value={symbol} onChange={setSymbol} size="sm" />
          </Field>
          <Field label="Window length (days)"
            info={{ title: "Window length", body: "How long each tested window is. Each of the N samples replays a random window of this many days. Converted to candles internally for the 5m interval." }}>
            <input type="number" min={1} max={365} value={windowDays}
              onChange={(e) => setWindowDays(Math.min(365, Math.max(1, Number(e.target.value))))}
              className="fa-input w-full bg-fa-glass border border-fa-edge rounded-md px-2 py-1.5 text-fa-frost-bright text-sm" />
          </Field>
          <Field label="Samples"
            info={{ title: "Samples", body: "How many random windows to draw and replay. More samples = a more reliable bust-rate estimate but a longer run. 200 is a sensible default." }}>
            <input type="number" min={10} max={1000} value={sampleCount}
              onChange={(e) => setSampleCount(Math.min(1000, Math.max(10, Number(e.target.value))))}
              className="fa-input w-full bg-fa-glass border border-fa-edge rounded-md px-2 py-1.5 text-fa-frost-bright text-sm" />
          </Field>
          <Field label="Bankroll ($)">
            <input type="number" min={10} value={initialBalance}
              onChange={(e) => {
                const next = Math.max(10, Number(e.target.value));
                setInitialBalance(next);
                if (initialBetSize > next) setInitialBetSize(next);
              }}
              className="fa-input w-full bg-fa-glass border border-fa-edge rounded-md px-2 py-1.5 text-fa-frost-bright text-sm" />
          </Field>
          <Field label="Initial bet ($)">
            <input type="number" min={1} max={initialBalance} value={initialBetSize}
              onChange={(e) => setInitialBetSize(Math.min(initialBalance, Math.max(1, Number(e.target.value))))}
              className="fa-input w-full bg-fa-glass border border-fa-edge rounded-md px-2 py-1.5 text-fa-frost-bright text-sm" />
          </Field>
        </div>
        <div className="mt-4 flex items-center gap-3 flex-wrap justify-between">
          <div className="flex items-center gap-3 flex-wrap text-xs text-fa-frost-dim">
            {error && <span className="text-rose-300">{error}</span>}
          </div>
          <div className="flex items-center gap-3 flex-wrap ml-auto">
            <label className="inline-flex items-center gap-2 cursor-pointer select-none"
              title={allowBorrow ? "Window continues even when a staking step exceeds the bankroll." : "Window halts the moment the next bet would exceed bankroll — strict bust."}>
              <span className="relative inline-block w-9 h-5">
                <input type="checkbox" checked={allowBorrow} onChange={(e) => setAllowBorrow(e.target.checked)} className="peer sr-only" />
                <span className={cn("absolute inset-0 rounded-full transition-colors", allowBorrow ? "bg-fa-frost-bright/40" : "bg-fa-edge")} />
                <span className={cn("absolute top-0.5 left-0.5 h-4 w-4 rounded-full bg-fa-frost-bright transition-transform", allowBorrow ? "translate-x-4" : "translate-x-0")} />
              </span>
              <span className="text-fa-frost-dim text-xs">
                Allow borrow {allowBorrow ? <span className="text-emerald-300">(on)</span> : <span className="text-amber-300">(strict bust)</span>}
              </span>
            </label>
            <button onClick={onRun} disabled={isRunning || modelIds.length === 0}
              className="inline-flex items-center gap-2 px-4 py-2 rounded-md bg-fa-frost-bright/20 hover:bg-fa-frost-bright/30 text-fa-frost-bright text-sm border border-fa-frost-bright/30 disabled:opacity-50 disabled:cursor-not-allowed transition">
              {isRunning ? <Loader2 className="h-4 w-4 animate-spin" /> : <FlaskConical className="h-4 w-4" />}
              {isRunning
                ? "Chaos test running…"
                : `Run chaos test · ${sampleCount} windows × ${windowDays}d`}
            </button>
          </div>
        </div>
        {isRunning && (
          <div className="mt-4">
            <ProgressInline
              pct={chaosProgress.pct}
              label={chaosProgress.label ?? "Sampling random windows…"}
              tone="amber"
            />
          </div>
        )}
      </div>

      {/* Chaos run summary from /api/chaos — the single source of truth for this tab, produced by
          the Run chaos test button above. Each row drills into its per-window samples. */}
      {sortedChaos.length > 0 ? (
        <div className="fa-card px-5 py-4 flex-1 min-h-0 flex flex-col">
          <div className="fa-section-title mb-3 shrink-0">Chaos run history</div>
          <div className="flex-1 min-h-0 overflow-auto">
            <table className="fa-table-bordered min-w-full text-xs [&_th]:whitespace-nowrap [&_td]:whitespace-nowrap">
              <thead className="text-fa-frost-dim sticky top-0 z-10 bg-fa-ink/95 backdrop-blur">
                <tr>
                  <th className="font-normal px-2 w-8 text-center" aria-label="Status"></th>
                  <th className="font-normal pr-4 text-left">Model</th>
                  <th className="font-normal pr-4 text-left">Strategy</th>
                  <th className="font-normal pr-4 text-center">Symbol</th>
                  <th className="font-normal pr-4 text-center">Interval</th>
                  <th className="font-normal pr-4 text-right">Window</th>
                  <th className="font-normal pr-4 text-right">Samples</th>
                  <th className="font-normal pr-4 text-right">Bust rate</th>
                  <th className="font-normal pr-4 text-right">P50 profit</th>
                  <th className="font-normal pr-4 text-right">Worst DD</th>
                  <th className="font-normal pr-4 text-center">Pass</th>
                  <th className="font-normal pr-4 text-right">Started</th>
                </tr>
              </thead>
              <tbody>
                {sortedChaos.map((cr: ChaosRunNormalized, idx) => {
                  const model = models.find((m) => m.id === cr.modelId);
                  const isRunningCr = cr.status === "running";
                  const orbClass = isRunningCr ? "fa-status-running" : cr.pass ? "fa-status-clean" : "fa-status-bust";
                  const bustPct = cr.bustRate == null ? "—" : `${(cr.bustRate * 100).toFixed(1)}%`;
                  const bustClass = cr.bustRate == null ? "text-fa-frost-dim" : cr.bustRate > 0.1 ? "text-rose-300" : cr.bustRate > 0.02 ? "text-amber-300" : "text-emerald-300";
                  const wDays = windowCandlesToDays(cr.windowLength, cr.interval);
                  const stripe = idx % 2 === 1 ? "bg-fa-frost/[0.018]" : "";
                  const clickable = cr.status === "complete";
                  return (
                    <tr key={cr.id} onClick={clickable ? () => setOpenSamplesRun(cr) : undefined}
                      className={cn("border-t border-fa-edge/40 transition-colors", isRunningCr && "fa-backtest-shimmer", stripe,
                        clickable ? "cursor-pointer hover:bg-fa-frost/[0.04]" : "")}
                      title={clickable ? "Open the per-window samples for this run" : isRunningCr ? "Running — samples available once complete" : undefined}>
                      <td className="px-2 text-center"><span className={cn("fa-status-orb", orbClass)} /></td>
                      <td className="pr-4 text-fa-frost-bright">{model?.name ?? cr.modelId.slice(0, 8)}</td>
                      <td className="pr-4 text-fa-frost-dim capitalize">{cr.strategyId}</td>
                      <td className="pr-4 text-center"><SymbolIcon symbol={cr.symbol} className="h-5 w-5" /></td>
                      <td className="pr-4 text-center text-fa-frost-bright">{cr.interval}</td>
                      <td className="pr-4 text-right tabular-nums text-fa-frost-bright" title={`${cr.windowLength.toLocaleString()} candles per sampled window`}>{wDays}d</td>
                      <td className="pr-4 text-right tabular-nums text-fa-frost-dim" title="Random windows tested">{cr.sampleCount.toLocaleString()}</td>
                      <td className={cn("pr-4 text-right tabular-nums", bustClass)}>{bustPct}</td>
                      <td className={cn("pr-4 text-right tabular-nums", cr.profitP50 == null ? "text-fa-frost-dim" : cr.profitP50 > 0 ? "text-emerald-300" : "text-rose-300")}>
                        {cr.profitP50 == null ? "—" : `$${cr.profitP50.toFixed(2)}`}
                      </td>
                      <td className={cn("pr-4 text-right tabular-nums", cr.worstDrawdown == null || cr.worstDrawdown === 0 ? "text-fa-frost-dim" : "text-rose-300")}
                        title="Worst peak-to-trough balance drop across all sampled windows">
                        {cr.worstDrawdown == null ? "—" : cr.worstDrawdown === 0 ? "$0.00" : `-$${cr.worstDrawdown.toFixed(2)}`}
                      </td>
                      <td className="pr-4 text-center">
                        <span className={cn("fa-overline rounded-full px-1.5 py-0.5 border",
                          isRunningCr ? "text-cyan-300 bg-cyan-300/10 border-cyan-300/30" :
                          cr.pass ? "text-emerald-300 bg-emerald-300/10 border-emerald-300/30" : "text-rose-300 bg-rose-300/10 border-rose-300/30")}>
                          {isRunningCr ? "running" : cr.pass ? "pass" : "fail"}
                        </span>
                      </td>
                      <td className="pr-4 text-right text-fa-frost-dim" title={new Date(cr.startedAt).toLocaleString()}>{fmtRunTime(cr.startedAt)}</td>
                    </tr>
                  );
                })}
              </tbody>
            </table>
          </div>
        </div>
      ) : (
        <div className="fa-card px-6 py-12 text-center">
          <p className="text-fa-frost-dim text-sm">No chaos runs yet. Run a chaos test above to see results here.</p>
        </div>
      )}

      {openSamplesRun && (
        <ChaosSamplesDrawer
          run={openSamplesRun}
          modelName={modelNameById.get(openSamplesRun.modelId) ?? openSamplesRun.modelId.slice(0, 8)}
          onClose={() => setOpenSamplesRun(null)}
        />
      )}
    </div>
  );
}

