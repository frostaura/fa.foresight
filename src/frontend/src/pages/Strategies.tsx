import { useState } from "react";
import { useNavigate } from "react-router-dom";
import { BookOpen, FlaskConical, Layers, Lock, Plus, Trash2 } from "lucide-react";
import { useConfirm } from "../components/ConfirmDialog";
import PageHeader from "../components/PageHeader";
import { cn } from "../lib/cn";
import { useLocalStorageState } from "../lib/persistedState";
import { pnlClass } from "../lib/pnl";
import {
  useCreateStrategyMutation,
  useDeleteStrategyMutation,
  useGetStrategiesQuery,
  type StrategyDetail,
} from "../store/api";

// ── Minimal default DAG skeleton used when creating a new custom strategy ─────────────────────

const DEFAULT_DAG_SKELETON = JSON.stringify({
  schemaVersion: 1,
  modelKind: "deterministic",
  supportsBacktesting: true,
  warmupCandles: 0,
  nodes: [],
  edges: [],
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
  const navigate = useNavigate();

  const ordered = [...(strategies ?? [])].sort((a, b) => {
    // Built-ins first, then alphabetical within each group
    if (a.isBuiltIn !== b.isBuiltIn) return a.isBuiltIn ? -1 : 1;
    return a.name.localeCompare(b.name);
  });

  const handleNew = async () => {
    try {
      const created = await createStrategy({
        name: "Untitled strategy",
        description: null,
        definition: DEFAULT_DAG_SKELETON,
        params: null,
      }).unwrap();
      navigate(`/strategies/${created.id}/designer`);
    } catch {
      // silently fail — user can retry
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
                className="inline-flex items-center rounded-md border border-fa-edge bg-fa-glass overflow-hidden text-[11px]"
                title="Switch the AI-generated description shown on each card between plain-language (Simple) and technical (Data-scientist). Falls back to the static description when the AI variant is not yet available."
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

          {/* Empty state */}
          {!isLoading && ordered.length === 0 && (
            <div className="fa-card px-6 py-12 text-center">
              <p className="text-fa-frost-dim">
                No strategies available. Built-in strategies should appear here on first boot.
              </p>
            </div>
          )}

          {/* Card grid */}
          <div className="grid grid-cols-1 sm:grid-cols-2 xl:grid-cols-3 gap-4">
            {ordered.map((s) => (
              <StrategyCard key={s.id} strategy={s} descMode={descMode} />
            ))}
          </div>
        </div>
      </div>
    </div>
  );
}

// ── Strategy card ─────────────────────────────────────────────────────────────────────────────

function StrategyCard({
  strategy,
  descMode,
}: {
  strategy: StrategyDetail;
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
    <div
      className="fa-card px-5 py-4 flex flex-col gap-3 cursor-pointer hover:border-fa-frost/30 transition"
      onClick={() => navigate(`/strategies/${strategy.id}/designer`)}
      title="Open designer"
    >
      {/* Card header */}
      <div className="flex items-start justify-between gap-3 min-w-0">
        <div className="flex items-center gap-2 min-w-0">
          <Layers className="h-4 w-4 text-fa-frost-bright shrink-0" />
          <div
            className="text-fa-frost-bright text-sm font-medium truncate"
            title={strategy.name}
          >
            {strategy.name}
          </div>
          {strategy.isBuiltIn && (
            <Lock
              className="h-3.5 w-3.5 text-fa-frost-dim shrink-0"
              aria-label="Built-in (read-only)"
            />
          )}
        </div>

        {/* Kind badge */}
        <span
          className={cn(
            "shrink-0 text-[10px] uppercase tracking-wider px-1.5 py-0.5 rounded border",
            strategy.kind === "code"
              ? "border-fa-edge text-fa-frost-dim bg-fa-glass"
              : "border-fa-frost/20 text-fa-frost-bright bg-fa-glass-strong",
          )}
        >
          {strategy.kind}
        </span>
      </div>

      {/* Description */}
      {displayDescription ? (
        <p className="text-fa-frost-dim text-xs leading-relaxed line-clamp-2">
          {displayDescription}
        </p>
      ) : (
        <p className="text-fa-frost-dim/40 text-xs italic">No description available.</p>
      )}

      {/* Stats row */}
      {hasScore && (
        <div className="flex flex-wrap items-center gap-x-4 gap-y-2 text-[11px] text-fa-frost-dim">
          {strategy.averageScore != null && (
            <div title="Mean hit-rate across all intervals that have a completed backtest.">
              <div className="uppercase tracking-wider text-[10px]">Score</div>
              <div
                className={cn(
                  "tabular-nums",
                  pnlClass(strategy.averageScore - 50),
                )}
              >
                {strategy.averageScore.toFixed(1)}%
              </div>
            </div>
          )}
          {strategy.backtestsRun != null && strategy.backtestsRun > 0 && (
            <div title="Total number of backtests run against this strategy.">
              <div className="uppercase tracking-wider text-[10px]">Backtests</div>
              <div className="text-fa-frost-bright">{strategy.backtestsRun}</div>
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
                  <div className="uppercase tracking-wider text-[10px]">{iv.toUpperCase()}</div>
                  <div className={cn("tabular-nums", pnlClass(score - 50))}>
                    {score.toFixed(1)}%
                  </div>
                </div>
              ))}
        </div>
      )}

      {/* Action row */}
      {!strategy.isBuiltIn && (
        <div className="flex items-center gap-2 pt-1">
          <button
            type="button"
            onClick={onDelete}
            className="inline-flex items-center gap-1.5 px-2.5 py-1 rounded-md border border-fa-edge bg-fa-glass hover:bg-rose-300/10 hover:border-rose-300/30 text-fa-frost-dim hover:text-rose-300 text-[11px] transition ml-auto"
          >
            <Trash2 className="h-3 w-3" />
            Delete
          </button>
        </div>
      )}

      {error && <div className="text-rose-300 text-[11px]">{error}</div>}
    </div>
  );
}
