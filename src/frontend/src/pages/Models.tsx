import { useMemo, useState } from "react";
import { useNavigate } from "react-router-dom";
import { Star, Lock, FlaskConical, Brain, Plus, Loader2, Cpu, Trash2, Archive, ArchiveRestore, BookOpen } from "lucide-react";
import { useConfirm } from "../components/ConfirmDialog";
import CreateModelDialog from "../components/CreateModelDialog";
import PageHeader from "../components/PageHeader";
import { RichList, RichListRow } from "../components/RichList";
import Ticker, { type TickerItem } from "../components/Ticker";
import { cn } from "../lib/cn";
import { fmtRunDate } from "../lib/format";
import { pnlClass } from "../lib/pnl";
import { useLocalStorageState } from "../lib/persistedState";
import {
  useArchiveModelMutation,
  useDeleteModelMutation,
  useListModelsQuery,
  useSetDefaultModelMutation,
  useTrainModelMutation,
  useUnarchiveModelMutation,
  type Model
} from "../store/api";

// ── Shared helpers — also imported by Testing.tsx ─────────────────────────────────────────────

/**
 * Per-interval walk-forward stats: the point-estimate accuracy (`mean`, the best engine's WF mean)
 * AND a conservative `worst` = the lowest single-fold accuracy (the weakest regime we actually
 * observed). Gambler's-ruin is hyper-sensitive near 50% — plugging the optimistic mean in badly
 * understates bust risk — so the Risk preview sizes/scores against `worst`, the honest downside.
 */
export function parseIntervalWfStats(trainedState: string | null | undefined): Record<string, { mean: number; worst: number }> {
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

export function parseIntervalWfAccs(trainedState: string | null | undefined): Record<string, number> {
  const out: Record<string, number> = {};
  const stats = parseIntervalWfStats(trainedState);
  for (const [iv, s] of Object.entries(stats)) out[iv] = s.mean;
  return out;
}

/**
 * Single canonical score for a model — the arithmetic mean of the per-interval walk-forward
 * accuracies displayed in the 1M / 5M / 15M cells on the card.
 */
export function computeModelScore(model: Model): number | null {
  const vals = Object.values(parseIntervalWfAccs(model.trainedState));
  if (vals.length === 0) return null;
  return (vals.reduce((a, b) => a + b, 0) / vals.length) * 100;
}

// ── Main page ────────────────────────────────────────────────────────────────────────────────

type DescriptionMode = "simple" | "technical";
type ArchiveView = "active" | "archived";

/**
 * Models page — CRUD surface for prediction models. Collection-only view with archived/active
 * toggle. The Backtesting / Chaos testing functionality has moved to /testing.
 */
export default function Models() {
  const [archiveView, setArchiveView] = useLocalStorageState<ArchiveView>("fa.models.archiveView", "active");

  // Fetch all models when showing archived view, otherwise only active. Training-status changes are
  // pushed by the /api/models SSE stream (see RealtimeSync), which invalidates this cache — so a
  // training model flips to trained here with no polling.
  const includeArchived = archiveView === "archived";
  const { data: allModels, isLoading } = useListModelsQuery(
    includeArchived ? { includeArchived: true } : void 0
  );

  // Filter to the correct view
  const models = useMemo(() => {
    if (!allModels) return [];
    if (archiveView === "archived") return allModels.filter((m) => m.isArchived);
    return allModels.filter((m) => !m.isArchived);
  }, [allModels, archiveView]);

  const ordered = useMemo(() => {
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
      </div>

      <div className="px-4 sm:px-8 py-4 sm:py-6 flex-1 min-h-0 overflow-y-auto flex flex-col">
        <DefinitionsTab
          models={ordered}
          isLoading={isLoading}
          archiveView={archiveView}
          onArchiveViewChange={setArchiveView}
        />
      </div>
    </div>
  );
}

function DefinitionsTab({
  models,
  isLoading,
  archiveView,
  onArchiveViewChange,
}: {
  models: Model[];
  isLoading: boolean;
  archiveView: ArchiveView;
  onArchiveViewChange: (v: ArchiveView) => void;
}) {
  const [showCreate, setShowCreate] = useState(false);
  // The "effective default" is the SINGLE model the resolver would pick if no per-card override
  // exists: tenant-owned default beats the global built-in. Computing it here means exactly one
  // card paints a DEFAULT badge even when both rows happen to have isDefault=true.
  const tenantDefault = models.find((m) => m.isDefault && m.tenantId !== null);
  const globalDefault = models.find((m) => m.isDefault && m.tenantId === null);
  const effectiveDefaultId = tenantDefault?.id ?? globalDefault?.id ?? null;

  // AI description mode — persisted so the user's choice survives reloads.
  const [descMode, setDescMode] = useLocalStorageState<DescriptionMode>("fa.models.descMode", "simple");

  // Card sort. Two keys (score / name) × two directions (asc / desc). Default is score desc so
  // the strongest model is first to read.
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
      if (sa == null && sb == null) return 0;
      if (sa == null) return 1;
      if (sb == null) return -1;
      return (sa - sb) * dir;
    });
    return arr;
  }, [models, sortKey, sortDir]);

  const toggleSort = (key: CardSortKey) => {
    if (sortKey !== key) {
      setSortKey(key);
      setSortDir(key === "score" ? "desc" : "asc");
    } else {
      setSortDir((prev) => (prev === "asc" ? "desc" : "asc"));
    }
  };

  return (
    <div className="space-y-4">
      {/* Leaderboard ticker — only shown on active view */}
      {archiveView === "active" && <TopPerformersTicker models={models} />}

      {/* Controls row */}
      <div className="flex flex-wrap items-center gap-x-3 gap-y-2">
        <div className="text-fa-frost-dim text-sm shrink-0">
          {isLoading ? "Loading…" : `${models.length} model${models.length === 1 ? "" : "s"}`}
        </div>
        <div className="flex flex-wrap items-center gap-2 ml-auto">
          {/* Active | Archived segmented switch */}
          <div
            className="inline-flex items-center rounded-md border border-fa-edge bg-fa-glass overflow-hidden fa-caption"
            title="Switch between active models and archived models."
          >
            {(["active", "archived"] as ArchiveView[]).map((view) => {
              const active = archiveView === view;
              const Icon = view === "active" ? BookOpen : Archive;
              const label = view === "active" ? "Active" : "Archived";
              return (
                <button
                  key={view}
                  type="button"
                  onClick={() => onArchiveViewChange(view)}
                  className={cn(
                    "inline-flex items-center gap-1.5 px-2.5 py-1 transition",
                    active
                      ? "bg-fa-glass-strong text-fa-frost-bright"
                      : "text-fa-frost-dim hover:text-fa-frost-bright",
                  )}
                >
                  <Icon className="h-3 w-3 shrink-0" />
                  <span>{label}</span>
                </button>
              );
            })}
          </div>

          {/* Description mode — Simple | Data-scientist segmented control (active view only) */}
          {archiveView === "active" && (
            <div
              className="inline-flex items-center rounded-md border border-fa-edge bg-fa-glass overflow-hidden fa-caption"
              title="Switch the AI-generated description shown on each row between plain-language (Simple) and technical (Data-scientist). Falls back to the static description when the AI variant is not yet available."
            >
              {(["simple", "technical"] as DescriptionMode[]).map((mode) => {
                const active = descMode === mode;
                const Icon = mode === "simple" ? BookOpen : FlaskConical;
                const label = mode === "simple" ? "Simple" : "Data-scientist";
                return (
                  <button
                    key={mode}
                    type="button"
                    onClick={() => setDescMode(mode)}
                    className={cn(
                      "inline-flex items-center gap-1.5 px-2.5 py-1 transition",
                      active
                        ? "bg-fa-glass-strong text-fa-frost-bright"
                        : "text-fa-frost-dim hover:text-fa-frost-bright",
                    )}
                  >
                    <Icon className="h-3 w-3 shrink-0" />
                    <span>{label}</span>
                  </button>
                );
              })}
            </div>
          )}

          {/* Sort picker */}
          <div className="inline-flex items-center gap-1 fa-caption text-fa-frost-dim">
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
                  {arrow && <span className="fa-caption">{arrow}</span>}
                </button>
              );
            })}
          </div>

          {archiveView === "active" && (
            <button
              type="button"
              onClick={() => setShowCreate(true)}
              className="inline-flex items-center gap-2 px-3 py-2 rounded-md border border-fa-edge bg-fa-glass hover:bg-fa-glass-strong text-fa-frost-bright text-sm transition"
            >
              <Plus className="h-4 w-4" />
              New model
            </button>
          )}
          {showCreate && <CreateModelDialog onClose={() => setShowCreate(false)} />}
        </div>
      </div>

      {!isLoading && models.length === 0 && (
        <div className="fa-card px-6 py-12 text-center">
          <p className="text-fa-frost-dim">
            {archiveView === "archived"
              ? "No archived models. Archive a model from the Active view to see it here."
              : "No models yet. The seeded \"Foresight Default LLM\" should appear here on first boot."}
          </p>
        </div>
      )}

      {sortedModels.length > 0 && (
        <RichList>
          {sortedModels.map((m, i) => (
            <ModelRow
              key={m.id}
              model={m}
              index={i}
              isEffectiveDefault={m.id === effectiveDefaultId}
              descMode={descMode}
              isArchivedView={archiveView === "archived"}
            />
          ))}
        </RichList>
      )}
    </div>
  );
}

/**
 * TopPerformersTicker — replaces the boxed leaderboard table.
 *
 * Renders a single-line auto-scrolling tape with per-interval leaders and all model scores.
 */
function TopPerformersTicker({ models }: { models: Model[] }) {
  const intervals = ["1m", "5m", "15m"] as const;

  const items = useMemo<TickerItem[]>(() => {
    const out: TickerItem[] = [];

    for (const iv of intervals) {
      const ranked = models
        .map((m) => {
          const backtestScore = m.scoresByInterval?.[iv];
          const wfScore =
            backtestScore == null
              ? parseIntervalWfAccs(m.trainedState)[iv] * 100
              : undefined;
          const score = backtestScore ?? wfScore;
          const source: "backtest" | "wf" | null =
            backtestScore != null
              ? "backtest"
              : wfScore != null && !Number.isNaN(wfScore)
                ? "wf"
                : null;
          return { m, score, source };
        })
        .filter(
          (x): x is { m: Model; score: number; source: "backtest" | "wf" } =>
            x.score != null && !Number.isNaN(x.score) && x.source != null
        )
        .sort((a, b) => b.score - a.score || a.m.name.localeCompare(b.m.name));

      if (ranked.length === 0) continue;

      const leader = ranked[0];
      const hitPct =
        leader.score.toFixed(1) + "%" + (leader.source === "wf" ? "~" : "");
      const hueClass =
        leader.score >= 50.5
          ? "text-emerald-300"
          : leader.score < 49.5
            ? "text-rose-300"
            : "text-fa-frost";
      out.push({
        key: `leader-${iv}`,
        content: (
          <span className="inline-flex items-baseline gap-2 text-xs">
            <span className="font-mono fa-overline text-fa-frost-dim">
              {iv}
            </span>
            <span className="text-fa-frost-bright">{leader.m.name}</span>
            <span className={cn("tabular-nums font-medium", hueClass)}>{hitPct}</span>
          </span>
        ),
      });
    }

    for (const m of models) {
      const score = computeModelScore(m);
      if (score == null) continue;
      const scoreStr = score.toFixed(1) + "%";
      const hueClass = pnlClass(score - 50);
      out.push({
        key: `model-${m.id}`,
        content: (
          <span className="inline-flex items-baseline gap-1.5 text-xs">
            <span className="text-fa-frost/80 truncate max-w-[140px]">{m.name}</span>
            <span className={cn("tabular-nums fa-caption", hueClass)}>{scoreStr}</span>
          </span>
        ),
      });
    }

    if (out.length === 0) {
      out.push({
        key: "no-data-hint",
        content: (
          <span className="fa-caption text-fa-frost-dim/70 italic">
            Run a backtest to populate the leaderboard
          </span>
        ),
      });
    }

    return out;
  }, [models]);

  return (
    <div
      className="rounded-lg border border-fa-edge/40 bg-fa-ink-2/60 px-4 py-0 flex items-center gap-3"
      style={{ height: "2.25rem" }}
      title="Top performers — best model per interval (hit rate) and all scored models. ~ = walk-forward estimate; plain % = backtest out-of-sample."
    >
      <span className="fa-overline text-fa-frost-dim shrink-0 select-none">
        Top performers
      </span>
      <div className="flex-1 min-w-0 overflow-hidden">
        <Ticker items={items} heightClass="h-full" />
      </div>
    </div>
  );
}

function ModelRow({
  model,
  index,
  isEffectiveDefault,
  descMode,
  isArchivedView,
}: {
  model: Model;
  index: number;
  isEffectiveDefault: boolean;
  descMode: DescriptionMode;
  isArchivedView: boolean;
}) {
  const KindIcon = model.kind === "llm" ? Brain : FlaskConical;
  const [train, { isLoading: isTraining }] = useTrainModelMutation();
  const [setDefault] = useSetDefaultModelMutation();
  const [deleteModel] = useDeleteModelMutation();
  const [archiveModel, { isLoading: isArchiving }] = useArchiveModelMutation();
  const [unarchiveModel, { isLoading: isUnarchiving }] = useUnarchiveModelMutation();
  const [error, setError] = useState<string | null>(null);
  const navigate = useNavigate();
  const confirm = useConfirm();

  const intervalAccs = useMemo(() => parseIntervalWfAccs(model.trainedState), [model.trainedState]);
  const hasTrainingRange = model.trainStartMs != null && model.trainEndMs != null;
  const isTrainingNow = isTraining || model.trainingStatus === "training";

  const displayDescription = useMemo(() => {
    if (descMode === "simple") {
      return model.simpleDescription ?? model.description ?? null;
    }
    return model.technicalDescription ?? model.description ?? null;
  }, [descMode, model.simpleDescription, model.technicalDescription, model.description]);

  const onTrain = async () => {
    setError(null);
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

  const onArchive = async () => {
    const ok = await confirm({
      title: "Archive model",
      description: <>Archive <span className="text-fa-frost-bright">"{model.name}"</span>? It will be hidden from the active list but not deleted. You can unarchive it any time.</>,
      confirmLabel: "Archive",
      destructive: false,
    });
    if (!ok) return;
    setError(null);
    try { await archiveModel(model.id).unwrap(); }
    catch (e: unknown) {
      const err = e as { data?: { error?: string } };
      setError(err.data?.error ?? "Archive failed");
    }
  };

  const onUnarchive = async () => {
    setError(null);
    try { await unarchiveModel(model.id).unwrap(); }
    catch (e: unknown) {
      const err = e as { data?: { error?: string } };
      setError(err.data?.error ?? "Unarchive failed");
    }
  };

  return (
    <RichListRow
      index={index}
      onClick={!isArchivedView ? () => navigate(`/models/${model.id}/designer`) : undefined}
      className={cn(isArchivedView && "opacity-70")}
    >
      {/* Responsive row layout: sm+ = side-by-side main + actions; mobile = stacked */}
      <div className="flex flex-col sm:flex-row sm:items-start gap-3 sm:gap-4 min-w-0">
        {/* ── Main column ── */}
        <div className="flex-1 min-w-0 space-y-1.5">
          {/* Title line */}
          <div className="flex items-center gap-2 flex-wrap">
            <KindIcon className="h-4 w-4 text-fa-frost-bright shrink-0" />
            <span className="fa-section-title" title={model.name}>
              {model.name}
            </span>
            {model.isBuiltIn && (
              <Lock className="h-3.5 w-3.5 text-fa-frost-dim shrink-0" aria-label="Built-in (read-only)" />
            )}
            {isArchivedView && (
              <Archive className="h-3.5 w-3.5 text-fa-frost-dim/60 shrink-0" aria-label="Archived" />
            )}
            {/* Default badge */}
            {!isArchivedView && isEffectiveDefault && (
              <span className="text-amber-300 flex items-center gap-1 fa-overline">
                <Star className="h-3 w-3 fill-amber-300" /> Default
              </span>
            )}
          </div>

          {/* Description — full text, no clamp */}
          {displayDescription ? (
            <p className="text-fa-frost-dim text-sm leading-relaxed">{displayDescription}</p>
          ) : (
            <p className="text-fa-frost-dim/40 text-xs italic">No description available.</p>
          )}

          {/* Stats row */}
          <div className="flex flex-wrap items-center gap-x-4 gap-y-1.5 fa-caption text-fa-frost-dim pt-0.5">
            <div>
              <span className="fa-overline">Kind</span>
              <span className="text-fa-frost-bright ml-1.5">{model.kind === "llm" ? "LLM" : "Deterministic"}</span>
            </div>
            {["1m", "5m", "15m"].map((iv) => (
              <div key={iv} title={`Walk-forward validation accuracy of the ${iv} variant.`}>
                <span className="fa-overline">{iv.toUpperCase()}</span>
                <span className="text-fa-frost-bright tabular-nums ml-1.5">
                  {intervalAccs[iv] == null ? "—" : `${(intervalAccs[iv] * 100).toFixed(1)}%`}
                </span>
              </div>
            ))}
            {(() => {
              const score = computeModelScore(model);
              const tooltip = score == null
                ? "No trained state — train the model to see a score."
                : "Arithmetic mean of the per-interval walk-forward accuracies shown above.";
              return (
                <div title={tooltip}>
                  <span className="fa-overline">Score</span>
                  <span className={cn("tabular-nums ml-1.5", score == null ? "text-fa-frost-dim" : pnlClass(score - 50))}>
                    {score == null ? "—" : `${score.toFixed(1)}%`}
                  </span>
                </div>
              );
            })()}
          </div>

          {/* Training range / status footnotes */}
          {hasTrainingRange && (
            <div className="fa-caption text-fa-frost-dim/70 break-words" title="The candle range the model was trained on.">
              Trained {model.trainSymbol}/{model.trainInterval} · {fmtRunDate(new Date(model.trainStartMs as number))} → {fmtRunDate(new Date(model.trainEndMs as number))}
            </div>
          )}
          {isTrainingNow && (
            <div className="fa-caption text-fa-frost-dim/70" title="Training runs on the server and keeps going if you close this page.">
              Training in progress (server-side){model.trainingStartedAt ? ` · started ${fmtRunDate(new Date(model.trainingStartedAt))}` : ""} — safe to leave this page.
            </div>
          )}
          {!isTrainingNow && model.trainingStatus === "failed" && (
            <div className="text-rose-300 fa-caption" title={model.trainingError ?? undefined}>
              Last training failed: {model.trainingError ?? "unknown error"}
            </div>
          )}
          {error && <div className="text-rose-300 fa-caption">{error}</div>}
        </div>

        {/* ── Actions cluster ── */}
        <div
          className="flex flex-wrap items-center gap-2 shrink-0"
          onClick={(e) => e.stopPropagation()}
        >
          {/* Set-default star (active, non-default only) */}
          {!isArchivedView && !isEffectiveDefault && (
            <button
              onClick={() => setDefault(model.id)}
              className="text-fa-frost-dim hover:text-amber-300 transition shrink-0"
              title="Set as the tenant default"
            >
              <Star className="h-3.5 w-3.5" />
            </button>
          )}

          {/* Train (active, deterministic, non-built-in) */}
          {!isArchivedView && model.kind === "deterministic" && !model.isBuiltIn && (
            <button
              onClick={onTrain}
              disabled={isTrainingNow}
              className="inline-flex items-center gap-1.5 px-2.5 py-1 rounded-md border border-fa-edge bg-fa-glass hover:bg-fa-glass-strong text-fa-frost-bright fa-caption transition disabled:opacity-50"
            >
              {isTrainingNow ? <Loader2 className="h-3 w-3 animate-spin" /> : <Cpu className="h-3 w-3" />}
              {isTrainingNow ? "Training…" : "Train"}
            </button>
          )}

          {/* Archive / Unarchive */}
          {isArchivedView ? (
            <button
              onClick={onUnarchive}
              disabled={isUnarchiving}
              className="inline-flex items-center gap-1.5 px-2.5 py-1 rounded-md border border-fa-edge bg-fa-glass hover:bg-fa-glass-strong text-fa-frost-bright fa-caption transition disabled:opacity-50"
            >
              {isUnarchiving ? <Loader2 className="h-3 w-3 animate-spin" /> : <ArchiveRestore className="h-3 w-3" />}
              Unarchive
            </button>
          ) : (
            <button
              onClick={onArchive}
              disabled={isArchiving}
              className="inline-flex items-center gap-1.5 px-2.5 py-1 rounded-md border border-fa-edge bg-fa-glass hover:bg-fa-glass-strong text-fa-frost-dim hover:text-fa-frost-bright fa-caption transition disabled:opacity-50"
            >
              {isArchiving ? <Loader2 className="h-3 w-3 animate-spin" /> : <Archive className="h-3 w-3" />}
              Archive
            </button>
          )}

          {/* Delete (active, non-built-in only) */}
          {!isArchivedView && !model.isBuiltIn && (
            <button
              onClick={onDelete}
              className="inline-flex items-center gap-1.5 px-2.5 py-1 rounded-md border border-fa-edge bg-fa-glass hover:bg-rose-300/10 hover:border-rose-300/30 text-fa-frost-dim hover:text-rose-300 fa-caption transition"
            >
              <Trash2 className="h-3 w-3" />
              Delete
            </button>
          )}
        </div>
      </div>
    </RichListRow>
  );
}
