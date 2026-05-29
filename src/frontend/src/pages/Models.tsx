import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { useSearchParams } from "react-router-dom";
import { Star, Lock, FlaskConical, Brain, Plus, Loader2, Play, AlertTriangle, Cpu, Trash2, X, Info } from "lucide-react";
import { useConfirm } from "../components/ConfirmDialog";
import CreateModelDialog from "../components/CreateModelDialog";
import FlowDesigner from "../components/FlowDesigner";
import InfoTip, { TipBody } from "../components/InfoTip";
import PageHeader from "../components/PageHeader";
import { cn } from "../lib/cn";
import { fmtRunDate, fmtRunTime } from "../lib/format";
import { pnlClass } from "../lib/pnl";
import { useSort, SortHeader } from "../lib/sort";
import { SymbolIcon, SymbolPicker } from "../components/SymbolIcon";
import BacktestRunModal from "../components/BacktestRunModal";
import SideDrawer from "../components/SideDrawer";
import { useLocalStorageState } from "../lib/persistedState";
import {
  useClearBacktestsMutation,
  useDeleteModelMutation,
  useGetSymbolsQuery,
  useGetStakingStrategiesQuery,
  useListModelsQuery,
  useListBacktestsQuery,
  useRunBacktestMutation,
  useRunBustTestMutation,
  useGetBacktestBatchQuery,
  useSetDefaultModelMutation,
  useTrainModelMutation,
  type Backtest,
  type Model
} from "../store/api";

type SubTab = "definitions" | "backtesting";

/**
 * Models page — CRUD surface for prediction models. Mirrors the Paper Trading sub-tab pattern: a
 * sticky page header followed by a sticky sub-tab band, with the active sub-tab persisted in the
 * `?view=` URL query param.
 *
 * iter-4 ships this page as a list-only view with the Definitions / Backtesting tab structure in
 * place. The full drag-drop flow designer canvas + AI assistant chat panel + backtest UI surface
 * land in follow-up tasks #70 and #71 — until then those panels render an empty-state inviting the
 * user to use the API. Critically: the navigation entry + sub-tab pattern + model card layout +
 * RTK Query wiring are all in place so the designer can be slotted in without further routing or
 * data-fetch plumbing.
 */
export default function Models() {
  const [search, setSearch] = useSearchParams();
  const subTab = (search.get("view") as SubTab | null) ?? "definitions";
  const setSubTab = (tab: SubTab) => {
    const next = new URLSearchParams(search);
    next.set("view", tab);
    setSearch(next, { replace: true });
  };

  // Poll only while a training run is in flight so the persistent server-side job auto-syncs onto
  // this screen — including when the user opens the page fresh after closing the browser mid-train.
  // The effect flips polling on when any model reports trainingStatus="training" and off again on
  // the first poll that sees it clear, so an idle page makes no extra requests.
  const [pollTraining, setPollTraining] = useState(false);
  const { data: models, isLoading } = useListModelsQuery(undefined, {
    pollingInterval: pollTraining ? 3000 : 0,
    skipPollingIfUnfocused: true,
  });
  useEffect(() => {
    setPollTraining((models ?? []).some((m) => m.trainingStatus === "training"));
  }, [models]);
  const ordered = useMemo(() => {
    if (!models) return [];
    // Built-ins last, current tenant's models first, default flagged on top.
    return [...models].sort((a, b) => {
      if (a.isDefault !== b.isDefault) return a.isDefault ? -1 : 1;
      if (a.isBuiltIn !== b.isBuiltIn) return a.isBuiltIn ? 1 : -1;
      return a.name.localeCompare(b.name);
    });
  }, [models]);

  return (
    <div className="h-full flex flex-col min-h-0">
      <div className="shrink-0 z-30 bg-fa-ink/95 backdrop-blur">
        <PageHeader
          title="Models"
          subtitle="Define and switch between prediction engines for live forecasts and paper trading."
        />
        <div className="bg-fa-ink/95 backdrop-blur border-b border-fa-edge px-8 pt-3">
          <div className="flex items-center gap-1">
            {(
              [
                { id: "definitions", label: "Definitions" },
                { id: "backtesting", label: "Backtesting" }
              ] as { id: SubTab; label: string }[]
            ).map((t) => (
              <button
                key={t.id}
                onClick={() => setSubTab(t.id)}
                className={cn(
                  "px-4 py-2 text-sm border-b-2 -mb-px transition",
                  subTab === t.id
                    ? "text-fa-frost-bright border-fa-frost-bright"
                    : "text-fa-frost-dim border-transparent hover:text-fa-frost-bright"
                )}
              >
                {t.label}
              </button>
            ))}
          </div>
        </div>
      </div>

      <div className="px-8 py-6 flex-1 min-h-0 overflow-y-auto flex flex-col">
        {subTab === "definitions" ? (
          <DefinitionsTab models={ordered} isLoading={isLoading} />
        ) : (
          <BacktestingTab models={ordered} />
        )}
      </div>
    </div>
  );
}

function DefinitionsTab({ models, isLoading }: { models: Model[]; isLoading: boolean }) {
  const [showCreate, setShowCreate] = useState(false);
  // The "effective default" is the SINGLE model the resolver would pick if no per-card override
  // exists: tenant-owned default beats the global built-in. Computing it here means exactly one
  // card paints a DEFAULT badge even when both rows happen to have isDefault=true.
  const tenantDefault = models.find((m) => m.isDefault && m.tenantId !== null);
  const globalDefault = models.find((m) => m.isDefault && m.tenantId === null);
  const effectiveDefaultId = tenantDefault?.id ?? globalDefault?.id ?? null;

  // Card sort. Two keys (score / name) × two directions (asc / desc). Default is score desc so
  // the strongest model is first to read. Persisted under fa.models.sort.* so the user's pick
  // sticks across reloads — same pattern the backtesting form uses.
  type CardSortKey = "score" | "name";
  const [sortKey, setSortKey] = useLocalStorageState<CardSortKey>("fa.models.sort.key", "score");
  const [sortDir, setSortDir] = useLocalStorageState<"asc" | "desc">("fa.models.sort.dir", "desc");
  const sortedModels = useMemo(() => {
    const dir = sortDir === "asc" ? 1 : -1;
    const arr = [...models];
    arr.sort((a, b) => {
      if (sortKey === "name") return a.name.localeCompare(b.name) * dir;
      const sa = computeModelScore(a);
      const sb = computeModelScore(b);
      // Null scores sink to the bottom regardless of direction — sorting by score ascending
      // shouldn't bury the never-trained rows at the top.
      if (sa == null && sb == null) return 0;
      if (sa == null) return 1;
      if (sb == null) return -1;
      return (sa - sb) * dir;
    });
    return arr;
  }, [models, sortKey, sortDir]);

  const toggleSort = (key: CardSortKey) => {
    if (sortKey !== key) {
      // Switching keys → re-anchor to that key's most-useful direction (score desc shows the
      // best first; name asc shows A→Z first).
      setSortKey(key);
      setSortDir(key === "score" ? "desc" : "asc");
    } else {
      setSortDir((prev) => (prev === "asc" ? "desc" : "asc"));
    }
  };
  return (
    <div className="space-y-4">
      {/* Leaderboard gets its own row, centred — it's a distinct visual element with weight, not
          a third item in a toolbar. The toolbar underneath stays a simple two-item flex (count
          left, action button right). Stacking removes the 3-column grid imbalance where the
          leaderboard dwarfed the surrounding text. */}
      <div className="flex justify-center">
        <BestPerIntervalTable models={models} />
      </div>
      <div className="flex items-center justify-between gap-3">
        <div className="text-fa-frost-dim text-sm">
          {isLoading ? "Loading…" : `${models.length} model${models.length === 1 ? "" : "s"}`}
        </div>
        <div className="flex items-center gap-3">
          {/* Sort picker — two-key toggle. Clicking the active key flips its direction; clicking
              the inactive key activates it with that key's natural starting direction (Score
              starts at desc so "best first" lands; Name starts at asc so A→Z lands). */}
          <div className="inline-flex items-center gap-1 text-[11px] text-fa-frost-dim">
            <span className="uppercase tracking-wider">Sort</span>
            {(["score", "name"] as const).map((k) => {
              const active = sortKey === k;
              const arrow = !active ? "" : sortDir === "asc" ? "↑" : "↓";
              return (
                <button
                  key={k}
                  type="button"
                  onClick={() => toggleSort(k)}
                  className={cn(
                    "inline-flex items-center gap-1 px-2 py-1 rounded-md border transition",
                    active
                      ? "bg-fa-glass-strong border-fa-frost/30 text-fa-frost-bright"
                      : "bg-fa-glass border-fa-edge text-fa-frost-dim hover:text-fa-frost-bright hover:border-fa-frost/20",
                  )}
                >
                  <span className="capitalize">{k}</span>
                  {arrow && <span className="text-[10px] leading-none">{arrow}</span>}
                </button>
              );
            })}
          </div>
          <button
            type="button"
            onClick={() => setShowCreate(true)}
            className="inline-flex items-center gap-2 px-3 py-2 rounded-md border border-fa-edge bg-fa-glass hover:bg-fa-glass-strong text-fa-frost-bright text-sm transition"
          >
            <Plus className="h-4 w-4" />
            New model
          </button>
          {showCreate && <CreateModelDialog onClose={() => setShowCreate(false)} />}
        </div>
      </div>

      {!isLoading && models.length === 0 && (
        <div className="fa-card px-6 py-12 text-center">
          <p className="text-fa-frost-dim">
            No models yet. The seeded "Foresight Default LLM" should appear here on first boot.
          </p>
        </div>
      )}

      <div className="grid grid-cols-1 md:grid-cols-2 xl:grid-cols-3 gap-4">
        {sortedModels.map((m) => (
          <ModelCard key={m.id} model={m} isEffectiveDefault={m.id === effectiveDefaultId} />
        ))}
      </div>
    </div>
  );
}

/**
 * Parse a model's TrainedState JSON and return `{interval → best WF accuracy}`. The trainer
 * stores per-interval walk-forward results under `variants[interval].walkForward.{lrMean,logrMean}`;
 * we surface the better of the two (matches the trainer's own "best fit found" output). Returns
 * an empty record for untrained models or legacy single-interval state. Exported-ish (top-level)
 * so both ModelCard and the leaderboard can read from a single canonical implementation.
 */
/**
 * Single canonical score for a model — the arithmetic mean of the per-interval walk-forward
 * accuracies displayed in the 1M / 5M / 15M cells on the card. Kept identical to what the
 * row shows so SCORE = mean(visible values), which is what the eye expects when a row of
 * numbers ends in a summary cell. We deliberately do NOT prefer the backend's `averageScore`
 * (mean of backtest hit-rates) here — that lives on the leaderboard, which has its own
 * scoresByInterval column and is internally consistent. Mixing the two surfaces produced
 * rows where SCORE didn't equal the average of the three numbers next to it.
 */
function computeModelScore(model: Model): number | null {
  const vals = Object.values(parseIntervalWfAccs(model.trainedState));
  if (vals.length === 0) return null;
  return (vals.reduce((a, b) => a + b, 0) / vals.length) * 100;
}

function parseIntervalWfAccs(trainedState: string | null | undefined): Record<string, number> {
  const out: Record<string, number> = {};
  const stats = parseIntervalWfStats(trainedState);
  for (const [iv, s] of Object.entries(stats)) out[iv] = s.mean;
  return out;
}

/**
 * Per-interval walk-forward stats: the point-estimate accuracy (`mean`, the best engine's WF mean)
 * AND a conservative `worst` = the lowest single-fold accuracy (the weakest regime we actually
 * observed). Gambler's-ruin is hyper-sensitive near 50% — plugging the optimistic mean in badly
 * understates bust risk — so the Risk preview sizes/scores against `worst`, the honest downside.
 */
function parseIntervalWfStats(trainedState: string | null | undefined): Record<string, { mean: number; worst: number }> {
  const out: Record<string, { mean: number; worst: number }> = {};
  if (!trainedState) return out;
  try {
    type Wf = { lrMean?: number; logrMean?: number; gbtMean?: number; lrFoldAccs?: number[]; logrFoldAccs?: number[]; gbtFoldAccs?: number[] };
    const parsed = JSON.parse(trainedState) as { variants?: Record<string, { walkForward?: Wf }> };
    const variants = parsed.variants;
    if (!variants) return out;
    for (const [interval, v] of Object.entries(variants)) {
      const wf = v.walkForward;
      if (!wf) continue;
      // Pick the engine with the highest WF mean (gbt for v2, logreg for v1/v6, etc.).
      const engines: { mean: number; folds: number[] }[] = [
        { mean: wf.gbtMean ?? 0, folds: wf.gbtFoldAccs ?? [] },
        { mean: wf.logrMean ?? 0, folds: wf.logrFoldAccs ?? [] },
        { mean: wf.lrMean ?? 0, folds: wf.lrFoldAccs ?? [] },
      ].filter((e) => e.mean > 0);
      if (engines.length === 0) continue;
      const best = engines.reduce((a, b) => (a.mean >= b.mean ? a : b));
      const folds = best.folds.filter((x) => typeof x === "number");
      // Worst observed fold = honest downside regime. With no fold data, take a 2pp haircut so the
      // risk isn't computed on a bare point estimate.
      const worst = folds.length >= 1 ? Math.min(...folds) : Math.max(0, best.mean - 0.02);
      out[interval] = { mean: best.mean, worst };
    }
  } catch { /* legacy / corrupt JSON — leave map empty */ }
  return out;
}

function BestPerIntervalTable({ models }: { models: Model[] }) {
  // For each supported interval, pick the best-performing model. Score source preference:
  //   1. Most-recent completed backtest hit-rate (out-of-sample, more honest) — from API's
  //      scoresByInterval, expressed as 0-100 percent.
  //   2. Training walk-forward accuracy (in-sample) — parsed from model.trainedState, returns
  //      0-1 fraction. Multiplied by 100 to align with the backtest scale.
  // The fallback means a freshly-trained model surfaces on the leaderboard immediately, and gets
  // upgraded to the (more honest) backtest score once the user runs one. Ties broken by name.
  const intervals = ["1m", "5m", "15m"] as const;
  const leaders = useMemo(() => intervals.map((iv) => {
    const ranked = models
      .map((m) => {
        const backtestScore = m.scoresByInterval?.[iv];
        const wfScore = backtestScore == null ? parseIntervalWfAccs(m.trainedState)[iv] * 100 : undefined;
        const score = backtestScore ?? wfScore;
        const source: "backtest" | "wf" | null = backtestScore != null ? "backtest" : (wfScore != null && !Number.isNaN(wfScore) ? "wf" : null);
        return { m, score, source };
      })
      .filter((x): x is { m: Model; score: number; source: "backtest" | "wf" } => x.score != null && !Number.isNaN(x.score) && x.source != null)
      .sort((a, b) => b.score - a.score || a.m.name.localeCompare(b.m.name));
    return { interval: iv, leader: ranked[0] ?? null };
  }), [models]);
  const anyData = leaders.some((l) => l.leader != null);
  type LeaderKey = "interval" | "name" | "hitRate";
  const { sortedRows, headerProps } = useSort<typeof leaders[number], LeaderKey>(leaders, {
    interval: (r) => r.interval,
    name: (r) => r.leader?.m.name ?? null,
    hitRate: (r) => r.leader?.score ?? null,
  });
  return (
    <div
      className={cn(
        "w-fit rounded-lg border border-fa-edge/40 bg-fa-ink-2/60 px-5 py-3",
        !anyData && "opacity-60",
      )}
      title="Best-performing model per interval. Hit-rate source: most-recent completed backtest on BTCUSDT, with fallback to training walk-forward accuracy when no backtest exists."
    >
      <div className="text-[10px] uppercase tracking-[0.12em] text-fa-frost-dim mb-2 text-center">
        Top performers
      </div>
      <table className="text-[11px] border-separate border-spacing-0">
        <thead>
          <tr className="text-fa-frost-dim">
            <th className="pr-8 pb-2 text-left font-normal uppercase tracking-wider border-b border-fa-edge/30">
              <SortHeader<LeaderKey> {...headerProps("interval")}>Interval</SortHeader>
            </th>
            <th className="pr-8 pb-2 text-left font-normal uppercase tracking-wider border-b border-fa-edge/30">
              <SortHeader<LeaderKey> {...headerProps("name")}>Leader</SortHeader>
            </th>
            <th className="pb-2 text-right font-normal uppercase tracking-wider border-b border-fa-edge/30">
              <SortHeader<LeaderKey> {...headerProps("hitRate")} align="right">Hit rate</SortHeader>
            </th>
          </tr>
        </thead>
        <tbody>
          {sortedRows.map(({ interval, leader }) => (
            <tr key={interval}>
              <td className="pr-8 py-1.5 font-mono text-fa-frost">{interval}</td>
              <td className="pr-8 py-1.5 text-fa-frost-bright truncate max-w-[200px]">
                {leader ? leader.m.name : <span className="text-fa-frost-dim">—</span>}
              </td>
              {/* Hit-rate cell: % digits right-aligned, a fixed-width slot reserved AFTER for
                  the optional "~" fallback marker. Without the slot, rows with ~ pushed the
                  % digits leftward and they no longer vertically aligned across rows. */}
              <td className={cn("py-1.5 text-right tabular-nums", leader ? pnlClass(leader.score - 50) : "text-fa-frost-dim")}>
                {leader ? (
                  <span className="inline-flex items-baseline justify-end" title={leader.source === "backtest" ? "From the most-recent completed backtest." : "From training walk-forward validation. Run a backtest to see an out-of-sample number here."}>
                    <span>{`${leader.score.toFixed(1)}%`}</span>
                    <span className="inline-block w-3 text-left text-fa-frost-dim/60">{leader.source === "wf" ? "~" : ""}</span>
                  </span>
                ) : "—"}
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

function ModelCard({ model, isEffectiveDefault }: { model: Model; isEffectiveDefault: boolean }) {
  const KindIcon = model.kind === "llm" ? Brain : FlaskConical;
  const [train, { isLoading: isTraining }] = useTrainModelMutation();
  const [setDefault] = useSetDefaultModelMutation();
  const [deleteModel] = useDeleteModelMutation();
  const [error, setError] = useState<string | null>(null);
  const [showDesigner, setShowDesigner] = useState(false);
  const confirm = useConfirm();

  // Per-interval WF accuracy. Shared parser used by the leaderboard too — same data source so
  // the card's 1m/5m/15m percentages and the leaderboard's hit-rate fall back to identical
  // numbers when no backtest exists for the model.
  const intervalAccs = useMemo(() => parseIntervalWfAccs(model.trainedState), [model.trainedState]);
  const hasTrainingRange = model.trainStartMs != null && model.trainEndMs != null;
  // "training" is now server-authoritative: the fit runs on a background task, so the spinner
  // reflects the persisted job state (which survives reloads / closing the browser), not just this
  // tab's in-flight mutation. The local `isTraining` covers the instant between the click and the
  // first refetch that flips trainingStatus to "training".
  const isTrainingNow = isTraining || model.trainingStatus === "training";

  const onTrain = async () => {
    setError(null);
    // Server trains all supported intervals (1m/5m/15m) in parallel, each with its own lookback
    // window. The client just picks a symbol — no per-interval interval / window plumbing here.
    try {
      await train({ id: model.id, symbol: "BTCUSDT" }).unwrap();
    } catch (e: unknown) {
      const err = e as { data?: { error?: string } };
      setError(err.data?.error ?? "Train failed");
    }
  };

  const onDelete = async () => {
    const ok = await confirm({
      title: "Delete model",
      description: <>The model <span className="text-fa-frost-bright">"{model.name}"</span> will be removed permanently along with any trained state. Existing backtest history rows referencing it remain, but new runs against this model will be impossible.</>,
      confirmLabel: "Delete model",
      destructive: true,
    });
    if (!ok) return;
    setError(null);
    try { await deleteModel(model.id).unwrap(); }
    catch (e: unknown) {
      const err = e as { data?: { error?: string } };
      setError(err.data?.error ?? "Delete failed");
    }
  };

  return (
    <div className="fa-card px-5 py-4 flex flex-col gap-3 cursor-pointer hover:border-fa-frost/30 transition"
         onClick={() => setShowDesigner(true)}
         title="Open designer">
      {showDesigner && <FlowDesigner model={model} onClose={() => setShowDesigner(false)} />}
      <div className="flex items-start justify-between gap-3">
        <div className="flex items-center gap-2 min-w-0">
          <KindIcon className="h-4 w-4 text-fa-frost-bright shrink-0" />
          <div className="text-fa-frost-bright text-sm font-medium truncate" title={model.name}>
            {model.name}
          </div>
          {model.isBuiltIn && (
            <Lock className="h-3.5 w-3.5 text-fa-frost-dim shrink-0" aria-label="Built-in (read-only)" />
          )}
        </div>
        <button
          onClick={(e) => { e.stopPropagation(); if (!isEffectiveDefault) setDefault(model.id); }}
          disabled={isEffectiveDefault}
          className="text-[10px] uppercase tracking-wider flex items-center gap-1 shrink-0 transition disabled:cursor-default"
          title={isEffectiveDefault ? "This is the current default" : "Set as the tenant default"}
        >
          {isEffectiveDefault ? (
            <span className="text-amber-300 flex items-center gap-1"><Star className="h-3 w-3 fill-amber-300" /> Default</span>
          ) : (
            <span className="text-fa-frost-dim hover:text-amber-300"><Star className="h-3 w-3" /></span>
          )}
        </button>
      </div>

      {model.description && (
        <p className="text-fa-frost-dim text-xs leading-relaxed line-clamp-2">{model.description}</p>
      )}

      <div className="flex items-center gap-4 text-[11px] text-fa-frost-dim">
        <div>
          <div className="uppercase tracking-wider text-[10px]">Kind</div>
          <div className="text-fa-frost-bright">{model.kind === "llm" ? "LLM" : "Deterministic"}</div>
        </div>
        {["1m", "5m", "15m"].map((iv) => (
          <div
            key={iv}
            title={`Walk-forward validation accuracy of the ${iv} variant (mean across 5 expanding-window folds). Each interval has its own coefficients calibrated for its data distribution; the variant matching the run's interval is used at inference time.`}>
            <div className="uppercase tracking-wider text-[10px]">{iv.toUpperCase()}</div>
            <div className="text-fa-frost-bright tabular-nums">
              {intervalAccs[iv] == null ? "—" : `${(intervalAccs[iv] * 100).toFixed(1)}%`}
            </div>
          </div>
        ))}
        {(() => {
          const score = computeModelScore(model);
          const tooltip = score == null
            ? "No trained state — train the model to see a score."
            : "Arithmetic mean of the per-interval walk-forward accuracies shown above.";
          return (
            <div title={tooltip}>
              <div className="uppercase tracking-wider text-[10px]">Score</div>
              <div className={cn("tabular-nums", score == null ? "text-fa-frost-dim" : pnlClass(score - 50))}>
                {score == null ? "—" : `${score.toFixed(1)}%`}
              </div>
            </div>
          );
        })()}
      </div>

      {model.kind === "deterministic" && !model.isBuiltIn && (
        <div className="flex items-center gap-2 pt-1">
          <button onClick={(e) => { e.stopPropagation(); onTrain(); }} disabled={isTrainingNow}
            className="inline-flex items-center gap-1.5 px-2.5 py-1 rounded-md border border-fa-edge bg-fa-glass hover:bg-fa-glass-strong text-fa-frost-bright text-[11px] transition disabled:opacity-50">
            {isTrainingNow ? <Loader2 className="h-3 w-3 animate-spin" /> : <Cpu className="h-3 w-3" />}
            {isTrainingNow ? "Training…" : "Train"}
          </button>
          <button onClick={(e) => { e.stopPropagation(); onDelete(); }}
            className="inline-flex items-center gap-1.5 px-2.5 py-1 rounded-md border border-fa-edge bg-fa-glass hover:bg-rose-300/10 hover:border-rose-300/30 text-fa-frost-dim hover:text-rose-300 text-[11px] transition ml-auto">
            <Trash2 className="h-3 w-3" />
            Delete
          </button>
        </div>
      )}

      {hasTrainingRange && (
        <div className="text-[10px] text-fa-frost-dim/70 -mt-1" title="The candle range the model was trained on. Honest-backtest runs the model on a window strictly outside this.">
          Trained {model.trainSymbol}/{model.trainInterval} · {fmtRunDate(new Date(model.trainStartMs as number))} → {fmtRunDate(new Date(model.trainEndMs as number))}
        </div>
      )}

      {isTrainingNow && (
        <div className="text-[10px] text-fa-frost-dim/70 -mt-1" title="Training runs on the server and keeps going if you close this page — come back any time and the result syncs in.">
          Training in progress (server-side){model.trainingStartedAt ? ` · started ${fmtRunDate(new Date(model.trainingStartedAt))}` : ""} — safe to leave this page.
        </div>
      )}
      {!isTrainingNow && model.trainingStatus === "failed" && (
        <div className="text-rose-300 text-[11px]" title={model.trainingError ?? undefined}>
          Last training failed: {model.trainingError ?? "unknown error"}
        </div>
      )}
      {error && <div className="text-rose-300 text-[11px]">{error}</div>}
    </div>
  );
}

/**
 * Pure math for the Risk preview card. Inputs are the form's bankroll + bet + the model's
 * empirical hit-rate at the current interval; outputs are gambler's-ruin / Kelly metrics that
 * the UI presents in real time so the user can see when their sizing is unsafe BEFORE they
 * click Run.
 *
 * Model assumed: independent even-money bets at probability p of winning, flat staking. With
 * martingale or other progressive staking these numbers are a lower bound on bust risk — the
 * Risk preview surfaces a separate caveat in that case.
 *
 * Gambler's-ruin formula: with N "lives" (bankroll / bet, integer units) and edge ratio
 * r = q/p < 1, P(bust before edge dominates) = r^N. That's the classical result for a
 * positive-drift random walk on a half-line.
 *
 * Kelly: optimal-growth bet fraction is `2p - 1` for even-money bets — the difference between
 * win and loss probability. Half-Kelly halves the variance (and the growth rate) and is the
 * conventional "fastest safe" choice in practitioner literature.
 */
interface RiskMetrics {
  p: number;                          // win probability (0-1)
  q: number;                          // loss probability (0-1)
  lives: number;                      // floor(bankroll / bet) — losses absorbable from start
  edge: number;                       // 2p - 1, EV per unit staked
  kellyFraction: number;              // = edge (clamped >= 0)
  kellyBet: number;                   // bankroll * kellyFraction
  halfKellyBet: number;               // kellyBet / 2
  pRuin: number;                      // 0-1; 1 when no edge or bet > bankroll
  safeBetForTarget: (targetPct: number) => number | null;  // bet size to land at target ruin%
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
    // No edge (or negative edge) → with flat staking on an infinite horizon, ruin is certain.
    pRuin = 1;
  } else if (lives <= 0) {
    // Bet exceeds bankroll — guaranteed bust on first loss.
    pRuin = 1;
  } else {
    pRuin = Math.pow(q / p, lives);
  }

  // Inverse: what bet size lands you at `targetPct`% ruin? Solves (q/p)^N = target/100 for N,
  // then bet = bankroll / ceil(N). Returns null when there's no edge (the equation has no
  // finite solution).
  const safeBetForTarget = (targetPct: number): number | null => {
    if (p <= 0.5 + 1e-9) return null;
    const ratio = q / p;
    const target = Math.max(1e-9, targetPct / 100);
    const N = Math.log(target) / Math.log(ratio);   // both logs < 0, so N > 0
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

/**
 * Real-time risk preview shown in the Backtesting tab between the form and the runs table.
 * Source-of-truth for "is this sizing sane" — recomputes on every form change.
 *
 * Hit-rate source: ONLY the model's training walk-forward accuracy for the selected interval.
 * Backtest hit-rate is deliberately ignored even when present — a single backtest's score depends
 * on the chosen lookback window, so wiring it in here made the Risk Preview flip-flop between
 * runs and cleared-runs states (the same model + form would show different bust-risk after a
 * backtest was added or wiped). Training WF is a property of the model itself, computed once at
 * train time, so the preview stays stable across the runs table's lifecycle.
 *
 * With multiple models selected (A/B), we pick the maximum hit-rate among them — the optimistic
 * scenario. The math here is per-model conceptually, but presenting N stacked panels would
 * dominate the page; one optimistic readout + a multi-model note is the chosen tradeoff.
 */
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
  // Collapsed by default — the most-important numbers (optimal bet, current bust risk) are visible
  // collapsed; the educational explainer + extra tiles live behind the expand. Once a user opens it
  // their preference persists across reloads via localStorage.
  const [expanded, setExpanded] = useLocalStorageState<boolean>("fa.backtesting.riskPreview.expanded", false);

  // Pick the optimistic hit-rate across selected models + remember its provenance for the caveat.
  // Training walk-forward only — see the function docstring for why backtest scores are excluded.
  const { hitRate, worstRate, source, modelName } = useMemo(() => {
    const selected = models.filter((m) => modelIds.includes(m.id));
    // Default (untrained): assume a generic 54% point edge, 52% conservative (a 2pp haircut).
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

  // All risk/sizing numbers are computed on the CONSERVATIVE (worst-fold) edge, not the optimistic
  // point estimate — ruin probability explodes near 50%, and a 52.9% mean whose worst regime was
  // 51% is genuinely ~15-20% bust at these lives, which is what we observe empirically. Sizing the
  // optimal bet on the same conservative edge keeps the whole card internally consistent.
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

  // Optimal bet = half-Kelly (the canonical best risk-adjusted bet size in practitioner literature
  // — full Kelly maximises growth but has crushing drawdowns; half-Kelly trades ~25% of growth for
  // far less variance). When there's no edge, no optimal bet exists and we fall back to a "—".
  const optimalBet = metrics.kellyBet > 0 ? metrics.halfKellyBet : null;
  // Ratio of current bet to optimal — > 1 means over-betting (which is what triggers ruin risk);
  // < 1 means under-betting (giving up growth but extra-safe). The phrasing in the collapsed view
  // adapts to which side of optimal the user is on.
  const betVsOptimal = optimalBet != null && optimalBet > 0 ? bet / optimalBet : null;

  return (
    <div className="fa-card px-6 py-5">
      {/* Header row — always visible whether the card is collapsed or expanded. The entire header
          is the click target so the user has a wide hit area and doesn't have to aim at the small
          chevron. Verdict badge stays inside the header so the safety read is always present. */}
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
          <div className="text-fa-frost-dim text-[11px]">
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
          <span className="text-[9px]">●</span>
          {metrics.verdict.label}
          {source !== "default" && metrics.p > 0.5 && (
            <span className="text-[10px] opacity-70">· {(metrics.pRuin * 100).toFixed(metrics.pRuin < 0.001 ? 4 : metrics.pRuin < 0.01 ? 3 : 2)}% bust</span>
          )}
        </span>
      </button>

      {/* Collapsed summary — three-stat strip that surfaces the most important question on the
          card: "what should I bet, and how does my current bet compare?". Optimal bet = half-Kelly
          (the conventional best risk-adjusted choice). Designed to answer everything at-a-glance
          so the user only expands when they want the full breakdown / explainer. */}
      {!expanded && (
        <div className="mt-4 grid grid-cols-1 sm:grid-cols-3 gap-3">
          <Metric
            label="Optimal bet"
            value={optimalBet != null ? `$${optimalBet.toFixed(2)}` : "—"}
            sub="half-Kelly · best risk/reward"
            tooltip="The half-Kelly bet size — half the growth-maximising fraction. Canonical 'fastest safe' bet: gives up ~25% of full-Kelly growth for materially less variance and bust risk. Bet at or below this and you're sizing rationally for your edge."
            accent
          />
          <Metric
            label="Your bet"
            value={`$${bet.toLocaleString()}`}
            sub={`${metrics.lives} lives · ${
              metrics.p <= 0.5 ? "no edge"
              : metrics.pRuin >= 0.001 ? `${(metrics.pRuin * 100).toFixed(metrics.pRuin < 0.01 ? 3 : 2)}% bust`
              : `${(metrics.pRuin * 100).toFixed(4)}% bust`
            }`}
            tone={metrics.verdict.tone}
            tooltip="Your current bet size, plus how many losses your bankroll can absorb (lives = bankroll / bet) and the resulting gambler's-ruin probability."
          />
          <Metric
            label="Risk:reward"
            value={
              betVsOptimal == null ? "—"
              : betVsOptimal >= 1.01 ? `${betVsOptimal.toFixed(1)}× over`
              : betVsOptimal <= 0.99 ? `${(1 / betVsOptimal).toFixed(1)}× under`
              : "Optimal"
            }
            sub={
              betVsOptimal == null ? "no edge available"
              : betVsOptimal >= 1.01 ? `reduce bet → $${optimalBet!.toFixed(2)} for max growth/risk`
              : betVsOptimal <= 0.99 ? "safer than half-Kelly · giving up growth"
              : "matched to your edge"
            }
            tone={
              betVsOptimal == null ? "danger"
              : betVsOptimal >= 2 ? "danger"
              : betVsOptimal >= 1.25 ? "warn"
              : "safe"
            }
            tooltip="Ratio of your bet to the half-Kelly optimum. >1× means you're betting more than your edge supports (faster compounding, higher ruin risk); <1× means you're under-betting (safer than necessary but growing slower). The Kelly criterion is the formal answer to risk/reward optimisation."
          />
        </div>
      )}

      {/* Expanded view — the educational breakdown + full metric grid + ruin-target recommendations
          + martingale caveat + math explainer. Shown only when the card is open so the page stays
          quiet by default. */}
      {expanded && (
        <div className="mt-4 space-y-4">
          <div className="grid grid-cols-2 sm:grid-cols-4 gap-3">
            <Metric
              label="Lives"
              value={metrics.lives.toString()}
              sub={`$${bankroll.toLocaleString()} ÷ $${bet.toLocaleString()}`}
              tooltip="Number of $-bet-sized losses your bankroll can absorb before the next bet would bust strict-bust mode. Every extra life multiplies survival by p/q."
            />
            <Metric
              label="P(bust)"
              value={metrics.p <= 0.5 ? "100%" : (
                metrics.pRuin >= 0.001 ? `${(metrics.pRuin * 100).toFixed(metrics.pRuin < 0.01 ? 3 : 2)}%`
                : `${(metrics.pRuin * 100).toFixed(4)}%`
              )}
              sub={metrics.p <= 0.5 ? "no edge" : "(q÷p)^lives"}
              tone={metrics.verdict.tone}
              tooltip="Gambler's ruin: probability the bankroll hits zero at some point before the edge wins out. Computed as (q/p) raised to the number of lives. Below ~1% is the practical 'safe' threshold."
            />
            <Metric
              label="Half-Kelly bet"
              value={metrics.halfKellyBet > 0 ? `$${metrics.halfKellyBet.toFixed(2)}` : "—"}
              sub={`(2p − 1) ÷ 2 = ${(metrics.kellyFraction * 50).toFixed(2)}%`}
              tooltip="Half the Kelly fraction of your bankroll. The conventional 'fastest safe' bet size — gives up roughly 25% of the growth rate of full Kelly for materially less variance."
              accent
            />
            <Metric
              label="Full Kelly bet"
              value={metrics.kellyBet > 0 ? `$${metrics.kellyBet.toFixed(2)}` : "—"}
              sub={`2p − 1 = ${(metrics.kellyFraction * 100).toFixed(2)}%`}
              tooltip="The mathematically growth-maximising bet fraction. Has terrifying drawdowns in practice — almost nobody bets full Kelly. Shown here as the upper ceiling."
            />
          </div>

          <div className="space-y-1.5 text-[11px]">
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
                ⚠ {onlyMartingale ? "Martingale selected." : "Martingale is one of the selected strategies."} It doubles bet size on each loss, so the math above (which assumes flat staking) is an <em>under-estimate</em> of bust risk. With ${bet} initial bet on ${bankroll.toLocaleString()} bankroll, Martingale can absorb at most <span className="text-fa-frost-bright">{martingaleMaxLosses}</span> consecutive losses before busting — and the chance of seeing that streak at least once over a long run is essentially 1 unless your edge is huge. Use martingale for stress-testing, not real sizing.
              </div>
            )}
          </div>

          <div className="text-[11px] text-fa-frost-dim leading-relaxed space-y-2 max-w-3xl border-l-2 border-fa-edge pl-3">
            <p>
              <span className="text-fa-frost-bright">Lives</span> = bankroll ÷ bet. It's how many
              consecutive losses you can take from a cold start before the bankroll can't fund the
              next bet. With ${bankroll.toLocaleString()} and a ${bet} flat bet that's{" "}
              <span className="text-fa-frost-bright">{metrics.lives}</span> lives — the only sizing
              number that really matters for survival.
            </p>
            <p>
              <span className="text-fa-frost-bright">Each extra life</span> multiplies your odds of{" "}
              ever going broke by <span className="font-mono">q/p</span> ={" "}
              <span className="text-fa-frost-bright tabular-nums">{(metrics.q / Math.max(metrics.p, 1e-9)).toFixed(3)}</span>.
              At {hitRate.toFixed(1)}% hit rate each life cuts bust risk by ~
              {((1 - metrics.q / Math.max(metrics.p, 1e-9)) * 100).toFixed(1)}%. So bust risk decays{" "}
              <em>exponentially</em> in lives — adding 10 lives roughly squares your chance of survival.
            </p>
            <p>
              <span className="text-fa-frost-bright">Gambler's ruin formula:</span>{" "}
              <span className="font-mono">P(bust) = (q/p)^lives</span>. This is the probability that
              a random walk with positive drift (your edge) ever touches zero. It's the cleanest
              answer to "is this sizing safe" — below 1% is the practical threshold.
            </p>
            <p>
              <span className="text-fa-frost-bright">Kelly criterion:</span> the bet fraction that
              maximises long-run growth is <span className="font-mono">2p − 1</span> of your bankroll
              — the edge per unit staked. At {hitRate.toFixed(1)}% that's{" "}
              <span className="tabular-nums">{(metrics.kellyFraction * 100).toFixed(2)}%</span> of
              bankroll per bet. Full Kelly is volatile — half-Kelly trades ~25% of growth for far
              less variance and is the conventional pick. Beyond full Kelly you're paying for growth
              you can't get and accepting extra bust risk for nothing.
            </p>
          </div>
        </div>
      )}
    </div>
  );
}

function Metric({
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
    <div className="rounded-md border border-fa-edge/40 bg-fa-ink-2/40 px-3 py-2" title={tooltip}>
      <div className="text-[10px] uppercase tracking-wider text-fa-frost-dim">{label}</div>
      <div className={cn("text-lg tabular-nums leading-tight mt-0.5", toneColor)}>{value}</div>
      {sub && <div className="text-[10px] text-fa-frost-dim/70 mt-0.5 tabular-nums">{sub}</div>}
    </div>
  );
}

function BacktestingTab({ models }: { models: Model[] }) {
  const eligible = useMemo(() => models.filter((m) => m.supportsBacktesting), [models]);
  const { data: symbolsResp } = useGetSymbolsQuery();
  const { data: strategiesResp } = useGetStakingStrategiesQuery();
  const supportedSymbols = symbolsResp?.symbols ?? ["BTCUSDT"];
  // Backtests are 5m-only (per the project canon): the 2-step prediction horizon, microstructure
  // dump cadence, and all the iteration history are 5m. Locking the interval keeps backtests the
  // honest source of truth and prevents the v1+ofx-at-15m-720d no-data trap.
  const supportedIntervals = ["5m"];
  const availableStrategies = strategiesResp?.strategies ?? [{ id: "flat", name: "Flat", description: "" }];
  // Every form field below is mirrored to localStorage so a page reload (or a navigate-away and
  // back) restores exactly the configuration the user last had selected. Keys are namespaced
  // `fa.backtesting.*` so we never clobber an unrelated feature's storage entry. The defaults
  // here are what a brand-new user sees on first visit — once they touch a control, that choice
  // sticks.
  const [modelIds, setModelIds] = useLocalStorageState<string[]>("fa.backtesting.modelIds", () => eligible[0] ? [eligible[0].id] : []);
  // Strategy fallback is "flat" — matches the backend default and the docs/user expectation that
  // the no-Martingale honest baseline is the safer first pick. Persisted choice overrides.
  const [strategyIds, setStrategyIds] = useLocalStorageState<string[]>("fa.backtesting.strategyIds", ["flat"]);
  const [symbol, setSymbol] = useLocalStorageState<string>("fa.backtesting.symbol", supportedSymbols[0] ?? "BTCUSDT");
  // 5m-only (see supportedIntervals above). Force-correct any stale persisted non-5m value.
  const [interval, setInterval] = useLocalStorageState<string>("fa.backtesting.interval", "5m");
  useEffect(() => { if (interval !== "5m") setInterval("5m"); }, [interval, setInterval]);
  // Lookback default is interval-aware so a fresh backtest hits ~1-10k candles — enough for
  // stable hit-rate / drawdown stats without burning compute on six-figure iteration counts.
  // 365-day cap matches the proactive cache warm so any pick is guaranteed to be cached.
  const [days, setDays] = useLocalStorageState<number>("fa.backtesting.days", () => optimalLookbackDays("5m"));
  // Re-default the lookback whenever the user picks a different interval. We only fire when the
  // user actively changed the interval (tracked via a ref) — not on initial restore from
  // localStorage, where the persisted days+interval pair should round-trip verbatim.
  const isFirstIntervalEffect = useRef(true);
  useEffect(() => {
    if (isFirstIntervalEffect.current) { isFirstIntervalEffect.current = false; return; }
    setDays(optimalLookbackDays(interval));
  }, [interval, setDays]);
  const [initialBalance, setInitialBalance] = useLocalStorageState<number>("fa.backtesting.initialBalance", 1000);
  const [initialBetSize, setInitialBetSize] = useLocalStorageState<number>("fa.backtesting.initialBetSize", 10);
  // Default OFF — matches the live-paper-trading bust contract so backtest survival reads true.
  // Borrow-through is useful for strategy R&D (lets the curve dip negative without halting), but
  // for default UX the honest live-equivalent behaviour wins.
  const [allowBorrow, setAllowBorrow] = useLocalStorageState<boolean>("fa.backtesting.allowBorrow", false);
  // Confidence gate: when on, the run SKIPS low-conviction candles (±2pp no-bet band) instead of
  // betting every candle — the same equation the live paper gate + chart GATE use. Off by default
  // = always-bet baseline. The whole point is to A/B whether sitting out coin-flips improves P&L.
  const [applyGate, setApplyGate] = useLocalStorageState<boolean>("fa.backtesting.applyGate", false);
  // Bust-test mode: a single model + strategy run across increasing lookback windows (1..max days)
  // — answers "would this have survived the last k days?" for every k up to max.
  const [mode, setMode] = useLocalStorageState<"standard" | "bust-test">("fa.backtesting.mode", "standard");
  const [maxLookbackDays, setMaxLookbackDays] = useLocalStorageState<number>("fa.backtesting.maxLookbackDays", 14);
  const [runBustTest, { isLoading: bustLoading }] = useRunBustTestMutation();
  const [error, setError] = useState<string | null>(null);

  // Default first model once eligible loads.
  useEffect(() => {
    if (modelIds.length === 0 && eligible[0]) setModelIds([eligible[0].id]);
  }, [eligible, modelIds.length]);

  // Invariant: bet ≤ bankroll. The bankroll onChange handler snaps bet down when the user
  // shrinks bankroll, but that doesn't cover the case where stale localStorage restored an
  // out-of-bounds pair (e.g. bet=159 from before the clamp landed, bankroll=60). This effect
  // re-establishes the invariant on mount and whenever bankroll changes for any reason.
  useEffect(() => {
    if (initialBetSize > initialBalance) setInitialBetSize(initialBalance);
  }, [initialBalance, initialBetSize, setInitialBetSize]);

  // Keep strategy selection in sync with the catalogue once it arrives — clear anything the
  // server doesn't know about and fall back to the declared default if the user emptied the list.
  useEffect(() => {
    if (!strategiesResp) return;
    const validIds = new Set(strategiesResp.strategies.map((s) => s.id));
    setStrategyIds((prev) => {
      const filtered = prev.filter((id) => validIds.has(id));
      return filtered.length > 0 ? filtered : [strategiesResp.default];
    });
  }, [strategiesResp]);

  const [run, { data: latestRun }] = useRunBacktestMutation();
  // History pulls every run for the tenant so multi-model A/B siblings are all visible side-by-
  // side in the same table — the Model + Strategy columns disambiguate. Filtering by primary
  // model used to hide batch siblings and made the comparison invisible.
  const { data: history, refetch } = useListBacktestsQuery({});
  // Streaming progress for the in-flight run(s). Keyed by backtest id so an A/B batch can show
  // multiple progress bars without clobbering each other. Cleared per-id on Completed/Failed.
  const [progress, setProgress] = useState<Record<string, { placed: number; total: number; kind: string }>>({});

  // Track active SSE subscriptions per backtest id so we don't re-subscribe on every re-render.
  // Keyed by backtest id; entry removed on stream completion or component unmount.
  const subscriptions = useRef<Map<string, EventSource>>(new Map());

  // Shared SSE subscribe helper — used by both fresh launches (onRun below) AND the reload-sync
  // effect that hooks into already-running backtests when the page mounts. Idempotent on id:
  // calling twice for the same id is a no-op (the second call returns immediately).
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
        // Progress is candles-processed / total-candles (not bets placed) — a gated model bets a
        // fraction of candles, so a bets-based fraction never fills. `placed` here holds candles.
        setProgress((p) => ({ ...p, [id]: { placed: evt.candlesProcessed ?? 0, total: evt.totalCandles ?? 0, kind: evt.kind } }));
        if (evt.kind === "completed" || evt.kind === "failed") {
          if (evt.kind === "failed" && evt.error) setError(evt.error);
          cleanup();
        }
      } catch { /* malformed frame — next event retries */ }
    };
    es.onerror = cleanup;
  }, [refetch]);

  // Reload-sync: whenever the history list updates, look for status="running" rows that don't
  // yet have an SSE subscription and subscribe to them. This is what makes the dashboard pick up
  // in-flight backtests after a page reload — previously the EventSource was only opened in
  // onRun, so reloading mid-run lost progress until the next manual refetch.
  useEffect(() => {
    if (!history) return;
    for (const row of history) {
      if (row.status === "running" && !subscriptions.current.has(row.id)) {
        // Seed an initial 0/0 progress entry so the row paints its running-state visuals
        // immediately — the first SSE frame (which carries real numbers) arrives ~50-200ms later.
        setProgress((p) => p[row.id] ? p : ({ ...p, [row.id]: { placed: 0, total: 0, kind: "started" } }));
        subscribeToRun(row.id);
      }
    }
  }, [history, subscribeToRun]);

  // Tear down every open stream on unmount so we don't leak EventSources when navigating away.
  useEffect(() => {
    const subs = subscriptions.current;
    return () => {
      for (const es of subs.values()) es.close();
      subs.clear();
    };
  }, []);

  // Poll the list while anything is queued/running. Bust-test rungs run sequentially server-side
  // and flip queued → running → done one at a time; polling refreshes the list so the reload-sync
  // effect above can subscribe to whichever rung is currently running (live candle progress) and
  // the collapsed batch row's "running X/N" + final verdict update as rungs complete.
  useEffect(() => {
    if (!history) return;
    const pending = history.some((r) => r.status === "running" || r.status === "queued");
    if (!pending) return;
    const id = window.setInterval(() => refetch(), 2000);
    return () => window.clearInterval(id);
  }, [history, refetch]);

  const toggleModel = (id: string) => {
    setModelIds((prev) => prev.includes(id) ? prev.filter((x) => x !== id) : [...prev, id]);
  };
  const toggleStrategy = (id: string) => {
    // Always keep at least one strategy selected so the form has a runnable shape.
    setStrategyIds((prev) =>
      prev.includes(id) ? (prev.length > 1 ? prev.filter((x) => x !== id) : prev) : [...prev, id]
    );
  };

  // A/B fan-out is the cartesian product of selected models × selected strategies. Single-pair
  // runs (1 model + 1 strategy) skip the shared batchId so the row doesn't get a grouping stripe.
  const fanout = useMemo(() =>
    modelIds.flatMap((m) => strategyIds.map((s) => ({ modelId: m, strategyId: s }))),
    [modelIds, strategyIds]);
  const isABMode = fanout.length > 1;

  const onRun = async () => {
    setError(null);
    if (modelIds.length === 0) { setError("Pick at least one deterministic model."); return; }
    if (strategyIds.length === 0) { setError("Pick at least one staking strategy."); return; }
    const endTime = Date.now();
    const startTime = endTime - days * 24 * 60 * 60 * 1000;
    const batchId = isABMode ? crypto.randomUUID() : undefined;
    // Estimated candle count so the "Processing candle X of Y" label shows IMMEDIATELY — the real
    // total arrives with the first Progress SSE frame (after the candle prefetch), which can take a
    // while on long windows; without a seed the label would stay hidden during that whole prefetch.
    const estTotal = Math.floor((days * 86_400_000) / intervalToMs(interval));
    const launches = fanout.map(async ({ modelId: mId, strategyId: sId }) => {
      try {
        const row = await run({
          modelId: mId, symbol, interval, startTime, endTime,
          initialBalance, initialBetSize, allowBorrow, batchId,
          strategyId: sId, applyGate,
        }).unwrap();
        setProgress((p) => ({ ...p, [row.id]: { placed: 0, total: estTotal, kind: "started" } }));
        refetch();
        // Shared subscribe helper — same path the reload-sync effect uses. Keeps the SSE
        // lifecycle (open / dispatch / close) in one place so a launched-then-reloaded run
        // doesn't get two competing EventSources.
        subscribeToRun(row.id);
      } catch (e: unknown) {
        const err = e as { data?: { error?: string }; status?: number };
        setError(err.data?.error ?? "Backtest failed");
      }
    });
    await Promise.allSettled(launches);
  };

  const onRunBustTest = async () => {
    setError(null);
    if (modelIds.length === 0) { setError("Pick at least one model for the bust test."); return; }
    try {
      // Fan out across EVERY selected model — each gets its own bust-test batch. The server creates
      // the rungs as "queued" and runs them sequentially (one rung at a time per batch), so we do
      // NOT open an SSE per rung here — the polling effect refreshes the list and the reload-sync
      // subscribes to whichever rung is actively running, giving live candle progress without
      // spawning hundreds of EventSources.
      for (const mId of modelIds) {
        await runBustTest({
          modelId: mId, symbol, interval,
          initialBalance, initialBetSize,
          maxLookbackDays, allowBorrow, strategyId: strategyIds[0] ?? "flat",
        }).unwrap();
      }
      refetch();
    } catch (e: unknown) {
      const err = e as { data?: { error?: string } };
      setError(err.data?.error ?? "Bust test failed");
    }
  };
  const isRunning = Object.keys(progress).length > 0;
  // Aggregate progress for the button bar — average across in-flight runs so a 2x batch shows a
  // sensible combined bar instead of jumping around as individual streams fire.
  const progressVals = Object.values(progress).filter((p) => p.total > 0);
  const progressPct = progressVals.length === 0 ? 0 :
    Math.min(100, progressVals.reduce((sum, p) => sum + (p.placed / p.total) * 100, 0) / progressVals.length);
  const aggPlaced = Object.values(progress).reduce((sum, p) => sum + p.placed, 0);
  const aggTotal = Object.values(progress).reduce((sum, p) => sum + p.total, 0);

  if (eligible.length === 0) {
    return (
      <div className="fa-card px-6 py-12 text-center space-y-3">
        <AlertTriangle className="h-6 w-6 text-amber-300 mx-auto" />
        <p className="text-fa-frost-bright">No backtestable models</p>
        <p className="text-fa-frost-dim text-sm max-w-md mx-auto">
          LLM-based models can't be backtested (non-deterministic). Build a deterministic
          model in Definitions — a linear or logistic regression on indicator features — to enable
          backtesting.
        </p>
      </div>
    );
  }

  return (
    <div className="flex-1 min-h-0 flex flex-col gap-6">
      <div className="fa-card px-6 py-5 shrink-0">
        <div className="flex items-center justify-between mb-4">
          <div className="text-fa-frost-bright text-sm font-medium">Run a new backtest</div>
          <div className="flex items-center gap-2">
            {isABMode && mode === "standard" && (
              <span className="text-[10px] uppercase tracking-wider text-amber-300 bg-amber-300/10 border border-amber-300/30 rounded-full px-2 py-0.5">
                A/B · {fanout.length} runs · {modelIds.length}m × {strategyIds.length}s
              </span>
            )}
            {/* Mode toggle — Standard (one window) vs Bust test (a sweep of windows 1..max days). */}
            <div className="flex gap-0.5 rounded-md border border-fa-edge p-0.5">
              {(["standard", "bust-test"] as const).map((m) => (
                <button key={m} type="button" onClick={() => setMode(m)}
                  className={cn("px-2.5 py-0.5 text-[10px] uppercase tracking-wider rounded transition",
                    mode === m ? "bg-fa-frost/20 text-fa-frost-bright" : "text-fa-frost-dim hover:text-fa-frost-bright")}>
                  {m === "standard" ? "Standard" : "Bust test"}
                </button>
              ))}
            </div>
          </div>
        </div>
        <div className="grid grid-cols-1 md:grid-cols-2 gap-4 mb-4">
          <Field label={`Models${modelIds.length > 1 ? ` (${modelIds.length} selected)` : ""}`}
            info={{ title: "Models", body: "Pick one model to backtest its hit-rate on the chosen window, or several to fan out runs in parallel. Selecting multiple here AND multiple strategies below produces an A/B grid — every (model × strategy) pair runs as one backtest, grouped under a shared batchId." }}>
            <div className="flex flex-wrap gap-2">
              {eligible.map((m) => {
                const selected = modelIds.includes(m.id);
                return (
                  <button
                    key={m.id}
                    type="button"
                    onClick={() => toggleModel(m.id)}
                    className={cn(
                      "inline-flex items-center gap-1.5 px-2.5 py-1 rounded-full text-xs border transition",
                      selected
                        ? "bg-fa-frost-bright/20 border-fa-frost-bright/50 text-fa-frost-bright"
                        : "bg-fa-glass border-fa-edge text-fa-frost-dim hover:text-fa-frost-bright hover:border-fa-frost-bright/30"
                    )}
                  >
                    {selected && <span className="text-[9px]">●</span>}
                    {m.name}
                  </button>
                );
              })}
            </div>
          </Field>
          <Field label={`Staking strategies${strategyIds.length > 1 ? ` (${strategyIds.length} selected)` : ""}`}
            info={{ title: "Staking strategy", body: "How each bet is sized. Flat = constant stake (the project canon for BTC). Martingale = double after a loss (off the recommended path; included for parity). Kelly variants size off bankroll. Selecting several here fans the chosen models across all selected strategies — the model × strategy cross-product." }}>
            <div className="flex flex-wrap gap-2">
              {availableStrategies.map((s) => {
                const selected = strategyIds.includes(s.id);
                return (
                  <button
                    key={s.id}
                    type="button"
                    onClick={() => toggleStrategy(s.id)}
                    title={s.description}
                    className={cn(
                      "inline-flex items-center gap-1.5 px-2.5 py-1 rounded-full text-xs border transition",
                      selected
                        ? "bg-fa-frost-bright/20 border-fa-frost-bright/50 text-fa-frost-bright"
                        : "bg-fa-glass border-fa-edge text-fa-frost-dim hover:text-fa-frost-bright hover:border-fa-frost-bright/30"
                    )}
                  >
                    {selected && <span className="text-[9px]">●</span>}
                    {s.name}
                  </button>
                );
              })}
            </div>
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
          {mode === "standard" ? (
            <Field label="Lookback (days)"
              info={{ title: "Lookback window", body: "How many days back to backtest, ending now. Longer windows give more statistical power but include older regimes that may not reflect current market behaviour. The 5m default is tuned to land roughly 1–10k candles — enough for a meaningful hit-rate without diluting recent signal." }}>
              <input type="number" min={1} max={730} value={days}
                onChange={(e) => setDays(Math.min(730, Math.max(1, Number(e.target.value))))}
                className="fa-input w-full bg-fa-glass border border-fa-edge rounded-md px-2 py-1.5 text-fa-frost-bright text-sm" />
            </Field>
          ) : (
            <Field label="Max lookback (days)"
              info={{ title: "Bust test sweep", body: "The bust test runs one backtest per day from 1…max. It tells you how far back the strategy survives without busting — survive day 1, then 2, then 3 …, until a window kills it. The output is a survival curve rather than a single hit-rate." }}>
              {/* Bust test runs one backtest per day from 1..max — survive day 1, then 2, then 3 … */}
              <input type="number" min={1} max={365} value={maxLookbackDays}
                onChange={(e) => setMaxLookbackDays(Math.min(365, Math.max(1, Number(e.target.value))))}
                className="fa-input w-full bg-fa-glass border border-fa-edge rounded-md px-2 py-1.5 text-fa-frost-bright text-sm" />
            </Field>
          )}
          <Field label="Bankroll ($)"
            info={{ title: "Starting bankroll", body: "Notional starting balance the backtest plays with. Doesn't have to match reality — pick something close to what you'd actually trade so the drawdown/P&L numbers are interpretable. The 'initial bet' is automatically capped at the bankroll." }}>
            <input type="number" min={10} value={initialBalance}
              onChange={(e) => {
                const next = Math.max(10, Number(e.target.value));
                setInitialBalance(next);
                // If the bet now exceeds the new (smaller) bankroll, snap it down to match —
                // bet > bankroll is an unrunnable config (strict-bust would fail on the very
                // first bet) and we don't want the form to be invalid in the cracks between
                // edits. Snapping to exactly bankroll (rather than e.g. half) keeps the
                // intent if the user was sizing bet ≈ bankroll.
                if (initialBetSize > next) setInitialBetSize(next);
              }}
              className="fa-input w-full bg-fa-glass border border-fa-edge rounded-md px-2 py-1.5 text-fa-frost-bright text-sm" />
          </Field>
          <Field label="Initial bet ($)"
            info={{ title: "Initial bet size", body: "The stake on the first bet. Flat staking keeps it constant. Martingale doubles it after a loss. Kelly variants scale it as a fraction of bankroll. Clamped to [$1, bankroll]." }}>
            {/* Bet is clamped to [1, bankroll]. The HTML max attr makes the spinner stop at
                bankroll; the onChange handler enforces the upper bound on typed values too. */}
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
            <label
              className="inline-flex items-center gap-2 cursor-pointer select-none"
              title={allowBorrow
                ? "Run continues even when a staking step exceeds the bankroll; balance dips negative and is recorded as borrowed shortfall."
                : "Run halts the moment the next sized bet would exceed the bankroll — matches live paper-trading bankruptcy."}
            >
              <span className="relative inline-block w-9 h-5">
                <input type="checkbox" checked={allowBorrow}
                  onChange={(e) => setAllowBorrow(e.target.checked)}
                  className="peer sr-only" />
                <span className={cn(
                  "absolute inset-0 rounded-full transition-colors",
                  allowBorrow ? "bg-fa-frost-bright/40" : "bg-fa-edge"
                )} />
                <span className={cn(
                  "absolute top-0.5 left-0.5 h-4 w-4 rounded-full bg-fa-frost-bright transition-transform",
                  allowBorrow ? "translate-x-4" : "translate-x-0"
                )} />
              </span>
              <span className="text-fa-frost-dim text-xs">
                Allow borrow {allowBorrow ? <span className="text-emerald-300">(on)</span> : <span className="text-amber-300">(strict bust)</span>}
              </span>
            </label>
            <label
              className="inline-flex items-center gap-2 cursor-pointer select-none"
              title={applyGate
                ? "Confidence gate ON: candles with pUp in the ±2pp no-bet band are SKIPPED (no bet placed) — same equation as the live paper gate + chart GATE. Measures P&L of only the conviction bets."
                : "Confidence gate OFF: bet every candle (the always-bet baseline). Toggle on to A/B whether sitting out the coin-flips improves profit over time."}
            >
              <span className="relative inline-block w-9 h-5">
                <input type="checkbox" checked={applyGate}
                  onChange={(e) => setApplyGate(e.target.checked)}
                  className="peer sr-only" />
                <span className={cn(
                  "absolute inset-0 rounded-full transition-colors",
                  applyGate ? "bg-fa-frost-bright/40" : "bg-fa-edge"
                )} />
                <span className={cn(
                  "absolute top-0.5 left-0.5 h-4 w-4 rounded-full bg-fa-frost-bright transition-transform",
                  applyGate ? "translate-x-4" : "translate-x-0"
                )} />
              </span>
              <span className="text-fa-frost-dim text-xs">
                Confidence gate {applyGate ? <span className="text-emerald-300">(skip no-bets)</span> : <span className="text-amber-300">(always bet)</span>}
              </span>
            </label>
            <button onClick={mode === "bust-test" ? onRunBustTest : onRun}
              disabled={isRunning || bustLoading || modelIds.length === 0 || strategyIds.length === 0}
              className="inline-flex items-center gap-2 px-4 py-2 rounded-md bg-fa-frost-bright/20 hover:bg-fa-frost-bright/30 text-fa-frost-bright text-sm border border-fa-frost-bright/30 disabled:opacity-50 disabled:cursor-not-allowed transition relative overflow-hidden">
              {isRunning && (
                <span
                  aria-hidden
                  className="absolute inset-y-0 left-0 bg-emerald-300/30 transition-[width] duration-300"
                  style={{ width: `${progressPct}%` }}
                />
              )}
              <span className="relative inline-flex items-center gap-2">
                {(isRunning || bustLoading) ? <Loader2 className="h-4 w-4 animate-spin" /> : <Play className="h-4 w-4" />}
                {mode === "bust-test"
                  ? (bustLoading || isRunning
                      ? `Bust test…`
                      : `Run bust test${modelIds.length > 1 ? ` · ${modelIds.length}×${maxLookbackDays}` : ` · ${maxLookbackDays} rungs`}`)
                  : isRunning
                    ? `Running ${Object.keys(progress).length}…`
                    : isABMode ? `Run ${fanout.length}-run A/B` : "Run backtest"}
              </span>
            </button>
          </div>
        </div>
        <p className="text-fa-frost-dim/70 text-[11px] mt-3">
          Backtest replays the selected staking strategy against historical candles. {allowBorrow
            ? "With Allow borrow on, balance can dip negative and the shortfall is recorded."
            : "With strict-bust on, the run halts the moment the next sized bet would exceed the bankroll — same contract as live paper trading."}
          {isABMode && ` A/B mode posts ${fanout.length} runs in parallel (cross product of ${modelIds.length} model${modelIds.length > 1 ? "s" : ""} × ${strategyIds.length} strateg${strategyIds.length > 1 ? "ies" : "y"}), sharing a batch id so siblings group in the recent-runs table.`}
        </p>
      </div>

      {/* Real-time risk preview — reacts to every form change. Uses the selected model's
          empirical hit-rate (backtest or training-WF fallback) plus the bankroll + bet to show
          gambler's-ruin / Kelly numbers so the user can see when their sizing is unsafe BEFORE
          they spend compute on the run. Educational by design; the explainer panel is open by
          default and the user can collapse it once they've internalised the math. */}
      <RiskPreview
        models={models}
        modelIds={modelIds}
        interval={interval}
        bankroll={initialBalance}
        bet={initialBetSize}
        strategyIds={strategyIds}
        availableStrategyNames={Object.fromEntries(availableStrategies.map((s) => [s.id, s.name]))}
      />

      {/* The single-run report card is only meaningful when one run was launched and finished.
          In A/B mode the comparison lives in the recent-runs table (one row per model × strategy,
          grouped by batch colour). For a single run we look up the *latest* state from the row
          list rather than the frozen POST-time snapshot, so the card refreshes as the run
          completes instead of staying stuck on dashes. */}
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

function Field({ label, info, children }: { label: string; info?: { title: string; body: React.ReactNode }; children: React.ReactNode }) {
  return (
    <label className="block">
      <div className="text-fa-frost-dim text-[10px] uppercase tracking-wider mb-1 flex items-center gap-1">
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

function BacktestReport({ bt, models }: { bt: Backtest; models: Model[] }) {
  const model = models.find((m) => m.id === bt.modelId);
  const pnl = (bt.finalBalance ?? 0) - bt.initialBalance;
  const pnlClass = pnl > 0 ? "text-emerald-300" : pnl < 0 ? "text-rose-300" : "text-fa-frost-dim";
  return (
    <div className="fa-card px-6 py-5">
      <div className="flex items-center justify-between gap-3 mb-4">
        <div className="min-w-0">
          <div className="text-fa-frost-bright text-sm font-medium truncate">
            {model?.name ?? "Model"} · {bt.symbol} · {bt.interval}
          </div>
          <div className="text-fa-frost-dim text-xs">
            {new Date(bt.startTime).toLocaleDateString()} → {new Date(bt.endTime).toLocaleDateString()} ·{" "}
            <span className={cn("uppercase text-[10px] tracking-wider",
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
        <Stat label="Final balance"
              value={bt.finalBalance == null ? "—" : `$${bt.finalBalance.toFixed(2)}`}
              valueClass={pnlClass} />
        <Stat label="Max drawdown" value={bt.maxDrawdown == null ? "—" : `$${bt.maxDrawdown.toFixed(2)}`} />
        <Stat label="Peak borrowed"
              value={bt.peakBorrowed == null ? "$0.00" : `$${bt.peakBorrowed.toFixed(2)}`}
              hint="Maximum |negative balance| reached during the run. Live paper trading enforces bust-on-zero, so this only matters for backtests." />
        <Stat label="Zero crossings"
              value={bt.zeroCrossingsCount.toString()}
              hint="Effective bankruptcy events — counts every step where the next bet exceeded the available bankroll (a borrow was needed) or where the balance literally passed through zero. A winning oversized bet still counts: the moment of being unable to place it is bankruptcy, regardless of subsequent recovery." />
      </div>
      {bt.error && (
        <div className="mt-3 text-rose-300 text-xs">Error: {bt.error}</div>
      )}
    </div>
  );
}

function Stat({ label, value, valueClass, hint }: { label: string; value: string; valueClass?: string; hint?: string }) {
  return (
    <div title={hint}>
      <div className="text-fa-frost-dim text-[10px] uppercase tracking-wider">{label}</div>
      <div className={cn("text-fa-frost-bright text-lg font-light tabular-nums", valueClass)}>{value}</div>
    </div>
  );
}

function BacktestHistory({ rows, models, runningProgress }:
    { rows: Backtest[]; models: Model[]; runningProgress: Record<string, { placed: number; total: number; kind: string }> }) {
  // Stable accent palette for batch grouping — same batchId across multiple rows lights up the
  // same glowing stripe on the left edge so A/B siblings are visually paired. The palette is
  // five on-theme classes defined in index.css (fa-batch + fa-batch-N) using soft frost-family
  // colours (cyan, indigo, violet, emerald, rose) rather than warm yellows.
  const batchIds = Array.from(new Set(rows.map((r) => r.batchId).filter((b): b is string => !!b)));
  const batchClassFor = (id?: string | null) => id ? `fa-batch fa-batch-${(batchIds.indexOf(id) % 5) + 1}` : "";
  // Sort within each batch group so siblings stay coloured-together but reorder by the active
  // column. Default sort = chronological desc (matches the existing "freshest first" intent of
  // the recent-runs feed) — third click on any header restores chronological order.
  type RunKey = "started" | "model" | "strategy" | "symbol" | "interval" | "duration" | "hitRate" | "finalBalance" | "finalPct" | "maxMartingale";
  const modelNameById = useMemo(() => {
    const m = new Map<string, string>();
    for (const x of models) m.set(x.id, x.name);
    return m;
  }, [models]);
  // Global sort across all rows — batch siblings (same batchId) remain visually identifiable
  // via their shared coloured left-edge stripe (fa-batch-N), so we don't need to preserve
  // positional adjacency too. Originally we sorted within batches, but mixed grouped/ungrouped
  // tables produced fragmented order (a 54.9% row sitting between 53.x rows just because it
  // belonged to a different batch). Colour alone carries the grouping signal.
  // Collapse bust-test sweeps: every rung shares a batchId + batchKind="bust-test". We keep ONE
  // representative row per such batch (the deepest lookback) so a 30-rung sweep is a single
  // clickable row instead of flooding the table; clicking it opens the batch report (survival
  // curve + per-rung drill-down). Ordinary runs and A/B batches pass through untouched.
  const collapsedRows = useMemo(() => {
    // Group bust-test rungs by batch; pass ordinary runs straight through.
    const out: Backtest[] = [];
    const byBatch = new Map<string, Backtest[]>();
    for (const r of rows) {
      if (r.batchKind === "bust-test" && r.batchId) {
        const arr = byBatch.get(r.batchId); if (arr) arr.push(r); else byBatch.set(r.batchId, [r]);
      } else {
        out.push(r);
      }
    }
    // One representative row per bust-test batch, chosen so its OWN stats match the sweep verdict:
    //   • if any rung busted → the FIRST-bust rung (its real bust balance/Δ%/hit-rate — the failure
    //     the sweep surfaced), so we never show a deeper surviving rung's +% next to a bust verdict;
    //   • otherwise → the deepest rung (the full-span survival result).
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
  // Per-batch survival summary so the collapsed bust-test row can state its verdict outright
  // (how many rungs, did ANY bust, and the first lookback day that busted) instead of showing a
  // single rung's stats that read like a lone run. A rung "busted" iff its bankroll crossed zero
  // (zeroCrossingsCount > 0) — only possible under strict-bust if Allow-borrow is on.
  const bustSummary = useMemo(() => {
    const m = new Map<string, { rungs: number; done: number; pending: boolean; anyBust: boolean; firstBustDay: number | null; maxDay: number }>();
    for (const r of rows) {
      if (r.batchKind !== "bust-test" || !r.batchId) continue;
      const cur = m.get(r.batchId) ?? { rungs: 0, done: 0, pending: false, anyBust: false, firstBustDay: null as number | null, maxDay: 0 };
      cur.rungs += 1;
      cur.maxDay = Math.max(cur.maxDay, r.lookbackDay ?? 0);
      // A rung is "done" once it has a terminal status; while any is queued/running the sweep is
      // still in progress (rungs run sequentially on the server).
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
    model:   (r) => modelNameById.get(r.modelId) ?? "",
    strategy:(r) => r.strategyId ?? "",
    symbol:  (r) => r.symbol,
    interval:(r) => r.interval,
    // Duration in milliseconds — rendered as days in the cell, but sorted on the underlying ms
    // value so two same-day rounded rows still order correctly by their finer-grained spans.
    duration:(r) => r.endTime - r.startTime,
    hitRate: (r) => r.hitRate ?? null,
    finalBalance: (r) => r.finalBalance ?? null,
    finalPct: (r) => r.finalBalance == null ? null : (r.finalBalance - r.initialBalance) / r.initialBalance,
    maxMartingale: (r) => r.maxMartingaleStep ?? 0,
  }, {
    defaultKey: "started",
    defaultDir: "desc",
  });
  const [clearBacktests, { isLoading: isClearing }] = useClearBacktestsMutation();
  const confirm = useConfirm();
  const onClear = async () => {
    // Clear matches what the table shows — every run for the tenant. Scoping to a single model
    // would leave sibling rows from other models / batches behind and look like the button
    // silently failed.
    await confirm({
      title: `Clear ${rows.length} backtest run${rows.length === 1 ? "" : "s"}?`,
      description: "Every row in the recent-runs table will be removed permanently, including their per-bet ledger entries. This cannot be undone.",
      confirmLabel: `Delete ${rows.length} run${rows.length === 1 ? "" : "s"}`,
      destructive: true,
      onConfirm: async () => { await clearBacktests({}).unwrap(); },
    });
  };
  if (rows.length === 0) return null;
  return (
    <div className="fa-card px-6 py-5 flex-1 min-h-0 flex flex-col">
      <div className="flex items-center justify-between mb-3 shrink-0">
        <div className="text-fa-frost-bright text-sm font-medium">Recent runs</div>
        <button
          onClick={onClear}
          disabled={isClearing}
          className="inline-flex items-center gap-1.5 text-xs text-fa-frost-dim hover:text-rose-300 transition disabled:opacity-50"
          title="Delete every backtest run shown in the table"
        >
          {isClearing ? <Loader2 className="h-3 w-3 animate-spin" /> : <Trash2 className="h-3 w-3" />}
          {isClearing ? "Clearing…" : "Clear all"}
        </button>
      </div>
      <div className="flex-1 min-h-0 overflow-auto">
        {/* Fills the rest of the card; the header row stays pinned (sticky) while rows scroll. Cells
            never wrap — the table grows to its content width and scrolls horizontally instead
            of cramming dates/model names into unreadable multi-line stacks (AI-chat-table style). */}
        <table className="fa-table-bordered min-w-full text-xs [&_th]:whitespace-nowrap [&_td]:whitespace-nowrap">
          <thead className="text-fa-frost-dim sticky top-0 z-10 bg-fa-ink/95 backdrop-blur">
            {/* All header text is centred per UX brief — the inline-flex SortHeader inside each
                <th> picks up the text-center on the cell, so the header label + arrow sit centred
                regardless of how the data cells below align (which stay left/right per column
                semantics — e.g. numeric columns stay right-aligned for ledger readability). */}
            <tr>
              {/* Leading column: status orb (orange running / green clean / red bankrupt). Empty
                  header keeps the column quiet — the orb's colour IS the legend. The orb also
                  replaces the explicit Status column, since the same three states map cleanly. */}
              <th className="font-normal pb-2 px-2 w-8 text-center" aria-label="Run status"></th>
              <th className="font-normal pb-2 pr-4 text-center"><SortHeader<RunKey> {...headerProps("started")}>Started</SortHeader></th>
              {/* Symbol moved up next to Started so the eye picks "which market is this?" before
                  scanning model + strategy. Renders as a Bitcoin icon (the SymbolIcon helper
                  carries the title-tooltip so the underlying BTCUSDT ticker is still readable). */}
              <th className="font-normal pb-2 pr-4 text-center"><SortHeader<RunKey> {...headerProps("symbol")}>Symbol</SortHeader></th>
              {/* Range is a [start → end] range visualisation, not naturally a single sortable
                  scalar. Left as a non-sortable display column. */}
              <th className="font-normal pb-2 pr-4 text-center">Range</th>
              {/* Duration in whole days, derived from the same end-start span. Sortable so the user
                  can compare runs by lookback length without eyeballing the dates above. */}
              <th className="font-normal pb-2 pr-4 text-center"><SortHeader<RunKey> {...headerProps("duration")}>Duration</SortHeader></th>
              <th className="font-normal pb-2 pr-4 text-center"><SortHeader<RunKey> {...headerProps("model")}>Model</SortHeader></th>
              <th className="font-normal pb-2 pr-4 text-center"><SortHeader<RunKey> {...headerProps("strategy")}>Strategy</SortHeader></th>
              <th className="font-normal pb-2 pr-4 text-center"><SortHeader<RunKey> {...headerProps("interval")}>Interval</SortHeader></th>
              <th className="font-normal pb-2 pr-4 text-center"><SortHeader<RunKey> {...headerProps("hitRate")}>Hit rate</SortHeader></th>
              {/* Final bank balance — the single source of truth for "where did the bankroll end".
                  We dropped the old PnL $ + % pair: the dollar figure WAS the final balance (not a
                  PnL), and the % rounds to -0.0% at large bankrolls, which read as "no change" next
                  to a big balance number. One neutral-white balance column is unambiguous. */}
              <th className="font-normal pb-2 pr-4 text-right"><SortHeader<RunKey> {...headerProps("finalBalance")} align="right">Balance</SortHeader></th>
              {/* Absolute % delta from the initial bankroll — (final - initial) / initial. The
                  bare balance ($61) doesn't say how far the bankroll moved; this column makes
                  "$81 → $61 = -24.7%" explicit and colour-codes it green/red. */}
              <th className="font-normal pb-2 pr-4 text-right"><SortHeader<RunKey> {...headerProps("finalPct")} align="right">Δ %</SortHeader></th>
              {/* Max chain depth — risk signal. The DISPLAY value is 2^step (the multiple of
                  the initial bet that the strategy was wagering at the deepest doubling). Flat
                  strategy never doubles, so the multiplier reads 1× across the board. */}
              <th className="font-normal pb-2 pr-4 text-center" aria-label="Peak bet multiplier vs initial"><SortHeader<RunKey> {...headerProps("maxMartingale")}><span className="text-[10px] opacity-60">×</span></SortHeader></th>
            </tr>
          </thead>
          <tbody>
            {sortedRows.map((r, idx) => {
              const model = models.find((m) => m.id === r.modelId);
              const finalBalance = r.finalBalance ?? null;
              const finalDelta = finalBalance == null ? null : finalBalance - r.initialBalance;
              // Hit-rate colouring: above 50% is favourable; tinted dim if missing.
              const hitClass = r.hitRate == null
                ? "text-fa-frost-dim"
                : r.hitRate >= 0.5
                  ? "text-emerald-300"
                  : "text-rose-300";
              const isNoBets = r.status === "no-bets";
              // For a collapsed bust-test row the orb/verdict must reflect the WHOLE sweep (did any
              // rung bust? is any rung still pending?), not just the representative rung.
              const sweep = (r.batchKind === "bust-test" && r.batchId) ? bustSummary.get(r.batchId) : undefined;
              const isBust = sweep ? sweep.anyBust : (r.zeroCrossingsCount ?? 0) > 0;
              // A collapsed bust-test row is "in flight" while ANY rung is still queued/running;
              // an ordinary row is in flight while it's running or queued.
              const isRunning = sweep ? sweep.pending : (r.status === "running" || r.status === "queued");
              // Match the streaming-progress state to this row by id. In A/B mode multiple rows
              // may stream concurrently — each row reads its own entry from the keyed record.
              const rowProgress = isRunning && runningProgress[r.id] ? runningProgress[r.id] : null;
              const rowPct = rowProgress && rowProgress.total > 0 ? Math.min(100, (rowProgress.placed / rowProgress.total) * 100) : null;
              const batchClass = batchClassFor(r.batchId);
              // Three orb states map cleanly to the three terminal+in-flight states a run
              // can be in. Running takes precedence over bankruptcy — even if a still-running
              // row has already crossed zero, we show it as running first; the final colour
              // settles once the run completes.
              const orbClass = isRunning ? "fa-status-running" : isNoBets ? "fa-status-running" : isBust ? "fa-status-bust" : "fa-status-clean";
              const orbTitle = isRunning
                ? rowProgress && rowPct != null
                  ? `Running — ${rowPct.toFixed(0)}% complete (${rowProgress.placed.toLocaleString()} candles processed)`
                  : "Running…"
                : isNoBets
                  ? (r.error ?? "No bets were placed in this run (model abstained on every candle, or no feature data for the window).")
                : isBust
                  ? `Bankrupt — strategy could not afford the doubled-up bet on ${r.zeroCrossingsCount} step${r.zeroCrossingsCount === 1 ? "" : "s"} during this run.`
                  : "Solvent throughout — every bet was affordable from the available bankroll.";
              // Row tint follows the ORB, not the PnL. A bankrupt run that ended in nominal
              // profit (bankroll grew via wins before a forced doubling capped the run) should
              // still read red — the orb's red dot is the load-bearing risk signal, and an
              // emerald-tinted row underneath it would visually contradict that signal. Three
              // states: bust = rose, profitable solvent run = emerald, losing solvent run = rose.
              const winLossTint = isRunning
                ? ""
                : isBust
                  ? "bg-rose-400/[0.06]"
                  : finalDelta != null && finalDelta > 0
                    ? "bg-emerald-400/[0.05]"
                    : finalDelta != null && finalDelta < 0
                      ? "bg-rose-400/[0.06]"
                      : "";
              // Elegant alternating stripe — applied ONLY when there's no win/loss tint to
              // override (running rows, no-delta rows). Same-direction layering would muddy the
              // tint; this preserves the meaningful colour cue and stripes the rest.
              const stripeTint = winLossTint ? "" : (idx % 2 === 1 ? "bg-fa-frost/[0.018]" : "");
              // Peak bet as a multiplier of the initial bet. Backend stores log2(maxBetSize /
              // initialBet) — so 2^step is the actual factor. Flat strategy never doubles
              // (step=0 → 1×); a martingale chain of 7 reads 128×.
              const peakMultiplier = Math.pow(2, r.maxMartingaleStep ?? 0);
              const isBustBatch = r.batchKind === "bust-test" && !!r.batchId;
              const onRowClick = () => {
                if (isBustBatch && r.batchId) setOpenBatchId(r.batchId);
                else if (!isRunning) setOpenRun(r);
              };
              return (
                <tr key={r.id} onClick={onRowClick} className={cn(
                  "border-t border-fa-edge/40 relative cursor-pointer hover:bg-fa-frost/[0.04] transition-colors",
                  isRunning && "fa-backtest-shimmer",
                  winLossTint,
                  stripeTint,
                  batchClass,
                )}
                title={isBustBatch ? "Open bust-test report" : "Open run report"}>
                  {/* Leading status cell: glowing orb horizontally centred so it lines up
                      symmetrically under the (empty) header. Symmetric padding around the orb
                      gives visual breathing room from the batch stripe on the left and the
                      first text cell on the right. */}
                  <td className="py-1.5 px-2 text-center">
                    <span className={cn("fa-status-orb", orbClass)} title={orbTitle} />
                  </td>
                  <td className="py-1.5 pr-4 text-fa-frost-dim" title={new Date(r.startedAt).toLocaleString()}>{fmtRunTime(r.startedAt)}</td>
                  {/* Symbol cell — centred so the icon sits directly under the centred header. */}
                  <td className="py-1.5 pr-4 text-fa-frost-bright text-center">
                    <SymbolIcon symbol={r.symbol} className="h-5 w-5" />
                  </td>
                  <td className="py-1.5 pr-4 tabular-nums leading-tight">
                    {/* Range stacks vertically with the start date as the primary signal: most
                        backtests run up to "today", so the end date carries little information
                        — what matters is how far back the window opened. From-line is large +
                        bright; to-line is small + dim. */}
                    <div className="text-fa-frost-bright text-[12px]">from {fmtRunDate(r.startTime)}</div>
                    <div className="text-fa-frost-dim text-[10px]">to {fmtRunDate(r.endTime)}</div>
                  </td>
                  {/* Duration cell — whole days between start and end. Rounded so a 179.9-day span
                      reads as 180. Centred + tabular so a long column scans cleanly. */}
                  <td className="py-1.5 pr-4 text-center text-fa-frost-bright tabular-nums">
                    {sweep ? `1–${sweep.maxDay} d` : `${Math.round((r.endTime - r.startTime) / 86_400_000)} d`}
                  </td>
                  <td className="py-1.5 pr-4 text-fa-frost-bright">
                   {/* Model name left, verdict badge floated to the RIGHT edge of the column so the
                       badges line up vertically across rows. */}
                   <div className="flex items-center justify-between gap-3">
                    <span>{model?.name ?? "?"}</span>
                    {/* Verdict chip on every finished row. Bust-test sweeps get the richer
                        per-window verdict; an ordinary run just shows whether THAT round survived
                        or went bust (or placed no bets). Running rows show nothing — the orb already
                        signals in-flight. */}
                    {isBustBatch && sweep ? (() => {
                      const n = sweep.rungs;
                      if (sweep.pending) {
                        return (
                          <span
                            className="ml-1.5 text-[9px] uppercase tracking-wider rounded-full px-1.5 py-0.5 border text-cyan-300 bg-cyan-300/10 border-cyan-300/30"
                            title={`Bust test in progress — running ${sweep.done} of ${n} windows (each rung runs to completion before the next).`}
                          >
                            bust test · running {sweep.done}/{n}
                          </span>
                        );
                      }
                      const survived = !sweep.anyBust;
                      const verdict = survived ? `no bust ≤ ${n}d` : `bust @ day ${sweep.firstBustDay}`;
                      const cls = survived
                        ? "text-emerald-300 bg-emerald-300/10 border-emerald-300/30"
                        : "text-rose-300 bg-rose-300/10 border-rose-300/30";
                      return (
                        <span
                          className={cn("ml-1.5 text-[9px] uppercase tracking-wider rounded-full px-1.5 py-0.5 border", cls)}
                          title={`Bust test: ${n} backtest${n === 1 ? "" : "s"} over the last 1, 2, … ${n} days (one per window). ${survived ? `The bankroll never went bust in any of those windows — so starting the bot up to ${n} days ago would have survived.` : `The bankroll first went bust in the ${sweep.firstBustDay}-day window — starting ${sweep.firstBustDay} days ago would have busted.`} Click to open the full per-window report.`}
                        >
                          bust test · {verdict}
                        </span>
                      );
                    })() : !isRunning ? (() => {
                      const cls = isNoBets
                        ? "text-amber-300 bg-amber-300/10 border-amber-300/30"
                        : isBust
                          ? "text-rose-300 bg-rose-300/10 border-rose-300/30"
                          : "text-emerald-300 bg-emerald-300/10 border-emerald-300/30";
                      const label = isNoBets ? "no bets" : isBust ? "went bust" : "survived";
                      const title = isNoBets
                        ? "No bets were placed in this run."
                        : isBust
                          ? "The bankroll went bust (crossed zero) during this run."
                          : "The bankroll stayed solvent the whole run — it never went bust.";
                      return (
                        <span className={cn("text-[9px] uppercase tracking-wider rounded-full px-1.5 py-0.5 border whitespace-nowrap", cls)} title={title}>
                          {label}
                        </span>
                      );
                    })() : null}
                   </div>
                  </td>
                  <td className="py-1.5 pr-4 text-fa-frost-dim capitalize">{r.strategyId ?? "flat"}</td>
                  <td className="py-1.5 pr-4 text-fa-frost-bright">{r.interval}</td>
                  {/* For a bust-test row these stats come from the REPRESENTATIVE rung (the first
                      that busted, or the deepest if none did — see collapsedRows), so the numbers
                      always agree with the verdict badge: a busted sweep shows that rung's real
                      bust balance/Δ%, not a deeper surviving rung's gain. */}
                  <td className={cn("py-1.5 pr-4 text-right tabular-nums", hitClass)}>{r.hitRate == null ? "—" : `${(r.hitRate * 100).toFixed(1)}%`}</td>
                  {/* Final bank balance — neutral white (NOT sign-coloured). It's "where the
                      bankroll ended", not a gain/loss: under flat staking the loss is bankroll-
                      independent, so a big balance next to a red -0.0% used to mislead. The
                      win/loss signal lives in the row tint + status orb. */}
                  <td className="py-1.5 pr-4 text-right tabular-nums text-fa-frost-bright">
                    {finalBalance == null ? "—" : `$${finalBalance.toFixed(2)}`}
                  </td>
                  {/* Absolute % delta from the initial bankroll — colour-coded green (up) / red
                      (down). A busted run shows its near-total loss here (e.g. $81 → $1 ≈ -98.8%). */}
                  {(() => {
                    const pct = finalBalance == null ? null : (finalBalance - r.initialBalance) / r.initialBalance * 100;
                    const pctClass = pct == null ? "text-fa-frost-dim" : pct > 0 ? "text-emerald-300" : pct < 0 ? "text-rose-300" : "text-fa-frost-dim";
                    return (
                      <td className={cn("py-1.5 pr-4 text-right tabular-nums", pctClass)}>
                        {pct == null ? "—" : `${pct >= 0 ? "+" : ""}${pct.toFixed(1)}%`}
                      </td>
                    );
                  })()}
                  {/* Peak bet multiplier — 2^step. Flat strategy never doubles, so reads 1×
                      across the board (no chain → step=0 → 1×). Martingale 7-chain reads 128×.
                      Tier colours stay tied to the underlying step (the actual risk signal):
                      step ≥ 8 rose, 5-7 amber, ≤ 4 dim. */}
                  <td className={cn("py-1.5 pr-4 text-right tabular-nums",
                    r.maxMartingaleStep >= 8 ? "text-rose-300" :
                    r.maxMartingaleStep >= 5 ? "text-amber-300" :
                    "text-fa-frost-dim")} title={`Peak bet was ${peakMultiplier}× the initial bet (chain depth ${r.maxMartingaleStep ?? 0}).`}>
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

/**
 * Bust-test batch report: the survival curve across the sweep (one rung per lookback day, 1..N),
 * plus the per-rung list. Each rung is itself clickable into the standard run report. "Bust" =
 * a rung that crossed zero (couldn't afford the next bet); the curve shows final balance + Δ% by
 * lookback day so you can see the first day the strategy would have busted.
 */
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


/** Candle duration in ms for the supported intervals (used to estimate candle counts for progress). */
function intervalToMs(interval: string): number {
  return interval === "1m" ? 60_000 : interval === "15m" ? 900_000 : 300_000;
}

/**
 * Per-interval default lookback. Tuned for "enough candles to find a statistical edge without
 * spending all day on the run". The 1m / 5m / 15m defaults come from user spec: 90d / 180d /
 * 24 months. Other intervals scale similarly. All values fit under the 730-day cap.
 */
function optimalLookbackDays(interval: string): number {
  switch (interval) {
    case "1m":  return 90;    // ~129,600 candles
    case "5m":  return 180;   // ~51,840
    case "15m": return 720;   // 24 months · ~69,120
    default:    return 180;
  }
}

