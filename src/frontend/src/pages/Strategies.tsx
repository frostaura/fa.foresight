import { useState } from "react";
import { useNavigate } from "react-router-dom";
import { BookOpen, FlaskConical, Layers, Lock, Plus, Trash2 } from "lucide-react";
import { useConfirm } from "../components/ConfirmDialog";
import PageHeader from "../components/PageHeader";
import { RichList, RichListRow } from "../components/RichList";
import { cn } from "../lib/cn";
import { useLocalStorageState } from "../lib/persistedState";
import { pnlClass } from "../lib/pnl";
import {
  useCreateStrategyMutation,
  useDeleteStrategyMutation,
  useGetStrategiesQuery,
  type StrategyDetail,
} from "../store/api";

// ── Starter DAG for a new custom strategy ─────────────────────────────────────────────────────
// A new strategy must be a VALID flow on create (the backend FlowValidator requires
// definitionKind="strategy" + exactly one output.stake node with its required input connected),
// so we seed an editable copy of the edge-aware-Kelly DAG rather than an empty canvas. The user
// edits/deletes nodes from there. Positions are omitted — the designer auto-lays-out on load.

const DEFAULT_STRATEGY_STARTER = JSON.stringify({
  schemaVersion: 1,
  definitionKind: "strategy",
  modelKind: "strategy",
  supportsBacktesting: false,
  warmupCandles: 0,
  nodes: [
    { id: "eak", type: "strategy.edge_aware_kelly", params: {} },
    { id: "cr", type: "strategy.clamp_round", params: {} },
    { id: "gate", type: "strategy.gate", params: {} },
    { id: "out", type: "output.stake", params: {} },
  ],
  edges: [
    { from: "eak.stake", to: "cr.stake" },
    { from: "cr.stake", to: "gate.stake" },
    { from: "eak.stake", to: "gate.pUp" },
    { from: "gate.stake", to: "out.stake" },
  ],
});

// ── Types ─────────────────────────────────────────────────────────────────────────────────────

type DescriptionMode = "simple" | "technical";

// ── Page ──────────────────────────────────────────────────────────────────────────────────────

/**
 * Strategies page — CRUD surface for staking strategies. Collection-only view.
 * Built-in (code-defined) strategies are read-only; custom DAG strategies support editing and
 * deletion.
 */
export default function Strategies() {
  const { data: strategies, isLoading } = useGetStrategiesQuery();
  const [descMode, setDescMode] = useLocalStorageState<DescriptionMode>("fa.strategies.descMode", "simple");
  const [createStrategy, { isLoading: isCreating }] = useCreateStrategyMutation();
  const [createError, setCreateError] = useState<string | null>(null);
  const navigate = useNavigate();

  const ordered = [...(strategies ?? [])].sort((a, b) => {
    // Built-ins first, then alphabetical within each group
    if (a.isBuiltIn !== b.isBuiltIn) return a.isBuiltIn ? -1 : 1;
    return a.name.localeCompare(b.name);
  });

  const handleNew = async () => {
    setCreateError(null);
    try {
      const created = await createStrategy({
        name: "Untitled strategy",
        description: null,
        definition: DEFAULT_STRATEGY_STARTER,
        params: null,
      }).unwrap();
      navigate(`/strategies/${created.id}/designer`);
    } catch (e: unknown) {
      const err = e as { data?: { error?: string }; message?: string };
      setCreateError(err.data?.error ?? err.message ?? "Could not create strategy. Please try again.");
    }
  };

  return (
    <div className="h-full flex flex-col min-h-0">
      <div className="shrink-0 z-30 bg-fa-ink/95 backdrop-blur">
        <PageHeader
          title="Strategies"
          subtitle="Define and manage staking strategies for paper trading and backtesting."
        />
      </div>

      <div className="px-4 sm:px-8 py-4 sm:py-6 flex-1 min-h-0 overflow-y-auto flex flex-col">
        <div className="space-y-4">
          {/* Controls row */}
          <div className="flex flex-wrap items-center gap-x-3 gap-y-2">
            <div className="text-fa-frost-dim text-sm shrink-0">
              {isLoading
                ? "Loading…"
                : `${ordered.length} strateg${ordered.length === 1 ? "y" : "ies"}`}
            </div>
            <div className="flex flex-wrap items-center gap-2 ml-auto">
              {/* Description mode — Simple | Data-scientist */}
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

              {/* New strategy */}
              <button
                type="button"
                onClick={handleNew}
                disabled={isCreating}
                className="inline-flex items-center gap-2 px-3 py-2 rounded-md border border-fa-edge bg-fa-glass hover:bg-fa-glass-strong text-fa-frost-bright text-sm transition disabled:opacity-50"
              >
                <Plus className="h-4 w-4" />
                New strategy
              </button>
            </div>
          </div>

          {createError && (
            <div className="rounded-md border border-rose-300/30 bg-rose-300/5 text-rose-300 text-xs px-3 py-2">
              {createError}
            </div>
          )}

          {/* Empty state */}
          {!isLoading && ordered.length === 0 && (
            <div className="fa-card px-6 py-12 text-center">
              <p className="text-fa-frost-dim">
                No strategies available. Built-in strategies should appear here on first boot.
              </p>
            </div>
          )}

          {/* Rich list */}
          {ordered.length > 0 && (
            <RichList>
              {ordered.map((s, i) => (
                <StrategyRow key={s.id} strategy={s} index={i} descMode={descMode} />
              ))}
            </RichList>
          )}
        </div>
      </div>
    </div>
  );
}

// ── Strategy row ──────────────────────────────────────────────────────────────────────────────

function StrategyRow({
  strategy,
  index,
  descMode,
}: {
  strategy: StrategyDetail;
  index: number;
  descMode: DescriptionMode;
}) {
  const navigate = useNavigate();
  const confirm = useConfirm();
  const [deleteStrategy] = useDeleteStrategyMutation();
  const [error, setError] = useState<string | null>(null);

  const displayDescription =
    descMode === "simple"
      ? (strategy.simpleDescription ?? strategy.description ?? null)
      : (strategy.technicalDescription ?? strategy.description ?? null);

  const hasScore =
    strategy.averageScore != null ||
    (strategy.scoresByInterval && Object.keys(strategy.scoresByInterval).length > 0);

  const onDelete = async (e: React.MouseEvent) => {
    e.stopPropagation();
    const ok = await confirm({
      title: "Delete strategy",
      description: (
        <>
          The strategy{" "}
          <span className="text-fa-frost-bright">"{strategy.name}"</span> will be
          removed permanently. Existing backtest history rows referencing it remain.
        </>
      ),
      confirmLabel: "Delete strategy",
      destructive: true,
    });
    if (!ok) return;
    setError(null);
    try {
      await deleteStrategy(strategy.id).unwrap();
    } catch (err: unknown) {
      const e2 = err as { data?: { error?: string } };
      setError(e2.data?.error ?? "Delete failed");
    }
  };

  return (
    <RichListRow
      index={index}
      onClick={() => navigate(`/strategies/${strategy.id}/designer`)}
    >
      {/* Responsive row layout: sm+ = side-by-side main + actions; mobile = stacked */}
      <div className="flex flex-col sm:flex-row sm:items-start gap-3 sm:gap-4 min-w-0">
        {/* ── Main column ── */}
        <div className="flex-1 min-w-0 space-y-1.5">
          {/* Title line */}
          <div className="flex items-center gap-2 flex-wrap">
            <Layers className="h-4 w-4 text-fa-frost-bright shrink-0" />
            <span className="fa-section-title" title={strategy.name}>
              {strategy.name}
            </span>
            {strategy.isBuiltIn && (
              <Lock className="h-3.5 w-3.5 text-fa-frost-dim shrink-0" aria-label="Built-in (read-only)" />
            )}
            {/* Kind badge */}
            <span
              className={cn(
                "fa-overline px-1.5 py-0.5 rounded border",
                strategy.kind === "code"
                  ? "border-fa-edge text-fa-frost-dim bg-fa-glass"
                  : "border-fa-frost/20 text-fa-frost-bright bg-fa-glass-strong",
              )}
            >
              {strategy.kind}
            </span>
          </div>

          {/* Description — full text, no clamp */}
          {displayDescription ? (
            <p className="text-fa-frost-dim text-sm leading-relaxed">{displayDescription}</p>
          ) : (
            <p className="text-fa-frost-dim/40 text-xs italic">No description available.</p>
          )}

          {/* Stats row */}
          {hasScore && (
            <div className="flex flex-wrap items-center gap-x-4 gap-y-1.5 fa-caption text-fa-frost-dim pt-0.5">
              {strategy.averageScore != null && (
                <div title="Mean hit-rate across all intervals that have a completed backtest.">
                  <span className="fa-overline">Score</span>
                  <span className={cn("tabular-nums ml-1.5", pnlClass(strategy.averageScore - 50))}>
                    {strategy.averageScore.toFixed(1)}%
                  </span>
                </div>
              )}
              {strategy.backtestsRun != null && strategy.backtestsRun > 0 && (
                <div title="Total number of backtests run against this strategy.">
                  <span className="fa-overline">Backtests</span>
                  <span className="text-fa-frost-bright ml-1.5">{strategy.backtestsRun}</span>
                </div>
              )}
              {strategy.scoresByInterval &&
                Object.entries(strategy.scoresByInterval)
                  .filter(([, v]) => v != null)
                  .map(([iv, score]) => (
                    <div
                      key={iv}
                      title={`Hit-rate for the ${iv} interval from the most-recent completed backtest.`}
                    >
                      <span className="fa-overline">{iv.toUpperCase()}</span>
                      <span className={cn("tabular-nums ml-1.5", pnlClass(score - 50))}>
                        {score.toFixed(1)}%
                      </span>
                    </div>
                  ))}
            </div>
          )}

          {error && <div className="text-rose-300 fa-caption">{error}</div>}
        </div>

        {/* ── Actions cluster ── */}
        {!strategy.isBuiltIn && (
          <div
            className="flex items-center gap-2 shrink-0"
            onClick={(e) => e.stopPropagation()}
          >
            <button
              type="button"
              onClick={onDelete}
              className="inline-flex items-center gap-1.5 px-2.5 py-1 rounded-md border border-fa-edge bg-fa-glass hover:bg-rose-300/10 hover:border-rose-300/30 text-fa-frost-dim hover:text-rose-300 fa-caption transition"
            >
              <Trash2 className="h-3 w-3" />
              Delete
            </button>
          </div>
        )}
      </div>
    </RichListRow>
  );
}
