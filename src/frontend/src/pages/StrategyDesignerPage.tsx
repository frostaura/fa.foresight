import { useNavigate, useParams } from "react-router-dom";
import { Code2, Layers, Lock } from "lucide-react";
import FlowDesigner from "../components/FlowDesigner";
import PageHeader from "../components/PageHeader";
import {
  useGetNodeCatalogueQuery,
  useGetStrategyQuery,
  useUpdateStrategyMutation,
} from "../store/api";

/**
 * Full-page route for the strategy flow designer.
 *
 * Route: /strategies/:strategyId/designer
 *
 * Renders inside the normal Layout shell (sidebar + main content area via <Outlet />), so the
 * sidebar stays visible while authoring — mirroring ModelDesignerPage.
 *
 * Edge cases:
 *   - Loading / not-found: handled with appropriate states.
 *   - Built-in strategies (kind="code", definition=null): rendered as a read-only info panel
 *     rather than an empty FlowDesigner canvas, because there is no DAG to display.
 *   - Custom strategies (kind="dag", definition present): full FlowDesigner with save.
 */

// Minimal empty DAG shown in FlowDesigner when a custom strategy somehow has no definition.
const EMPTY_DAG = JSON.stringify({
  schemaVersion: 1,
  modelKind: "deterministic",
  supportsBacktesting: true,
  warmupCandles: 0,
  nodes: [],
  edges: [],
});

export default function StrategyDesignerPage() {
  const { strategyId } = useParams<{ strategyId: string }>();
  const navigate = useNavigate();

  const { data: strategy, isLoading: strategyLoading } = useGetStrategyQuery(strategyId ?? "", {
    skip: !strategyId,
  });
  const { data: catalogue, isLoading: catalogueLoading } = useGetNodeCatalogueQuery();
  const [updateStrategy] = useUpdateStrategyMutation();

  const isLoading = strategyLoading || catalogueLoading;

  // ── Loading state ──────────────────────────────────────────────────────────
  if (isLoading) {
    return (
      <div className="h-full flex items-center justify-center text-fa-frost-dim text-sm">
        Loading strategy…
      </div>
    );
  }

  // ── Not-found state ────────────────────────────────────────────────────────
  if (!strategy) {
    return (
      <div className="h-full flex items-center justify-center text-fa-frost-dim text-sm">
        Strategy not found.{" "}
        <button
          onClick={() => navigate("/strategies")}
          className="ml-2 text-fa-frost-bright underline hover:no-underline"
        >
          Back to Strategies
        </button>
      </div>
    );
  }

  // ── Code-defined strategy (no DAG) — read-only info panel ─────────────────
  // Only strategies WITHOUT a flow definition (the built-in staking strategies, which live in
  // server-side code) get the text panel. A built-in that DOES have a DAG (e.g. the demo) renders
  // the real canvas in read-only mode so the graph + Design|Code views are visible.
  if (strategy.definition == null) {
    const displayDescription =
      strategy.simpleDescription ?? strategy.technicalDescription ?? strategy.description ?? null;

    return (
      <div className="h-full flex flex-col min-h-0">
        <div className="shrink-0 z-30 bg-fa-ink/95 backdrop-blur">
          <PageHeader
            title={strategy.name}
            subtitle="Built-in staking strategy"
          />
        </div>

        <div className="px-4 sm:px-8 py-6 flex-1 min-h-0 overflow-y-auto">
          <div className="max-w-2xl space-y-6">
            {/* Identity row */}
            <div className="fa-card px-5 py-4 flex flex-col gap-4">
              <div className="flex items-center gap-3">
                <Layers className="h-5 w-5 text-fa-frost-bright shrink-0" />
                <div>
                  <div className="text-fa-frost-bright font-medium">{strategy.name}</div>
                  <div className="flex items-center gap-1.5 mt-0.5">
                    <Lock className="h-3 w-3 text-fa-frost-dim" />
                    <span className="text-fa-frost-dim text-xs">Built-in · read-only</span>
                  </div>
                </div>
              </div>

              {/* Kind */}
              <div className="flex items-center gap-2 text-xs text-fa-frost-dim">
                <Code2 className="h-3.5 w-3.5 shrink-0" />
                <span>
                  Kind: <span className="text-fa-frost-bright font-medium">code</span>
                </span>
              </div>

              {/* Description */}
              {displayDescription && (
                <p className="text-fa-frost-dim text-sm leading-relaxed">{displayDescription}</p>
              )}

              {/* Technical description (if different from simple) */}
              {strategy.technicalDescription &&
                strategy.technicalDescription !== displayDescription && (
                  <div className="border-t border-fa-edge pt-3">
                    <div className="text-[10px] uppercase tracking-wider text-fa-frost-dim/60 mb-1">
                      Technical detail
                    </div>
                    <p className="text-fa-frost-dim text-sm leading-relaxed">
                      {strategy.technicalDescription}
                    </p>
                  </div>
                )}

              {/* Not-editable notice */}
              <div className="rounded-md border border-fa-edge bg-fa-glass px-4 py-3 text-xs text-fa-frost-dim leading-relaxed">
                This strategy is implemented directly in server-side code and cannot be edited as a
                flow. To create a customizable variant, use{" "}
                <span className="text-fa-frost-bright">New strategy</span> on the Strategies page.
              </div>
            </div>

            {/* Stats card (if any backtest data) */}
            {(strategy.averageScore != null ||
              (strategy.scoresByInterval &&
                Object.keys(strategy.scoresByInterval).length > 0)) && (
              <div className="fa-card px-5 py-4 flex flex-col gap-3">
                <div className="text-[10px] uppercase tracking-wider text-fa-frost-dim/60">
                  Performance
                </div>
                <div className="flex flex-wrap gap-x-6 gap-y-3 text-[11px] text-fa-frost-dim">
                  {strategy.averageScore != null && (
                    <div>
                      <div className="uppercase tracking-wider text-[10px]">Avg score</div>
                      <div className="text-fa-frost-bright tabular-nums">
                        {strategy.averageScore.toFixed(1)}%
                      </div>
                    </div>
                  )}
                  {strategy.backtestsRun != null && strategy.backtestsRun > 0 && (
                    <div>
                      <div className="uppercase tracking-wider text-[10px]">Backtests</div>
                      <div className="text-fa-frost-bright">{strategy.backtestsRun}</div>
                    </div>
                  )}
                  {strategy.scoresByInterval &&
                    Object.entries(strategy.scoresByInterval).map(([iv, score]) => (
                      <div key={iv}>
                        <div className="uppercase tracking-wider text-[10px]">{iv.toUpperCase()}</div>
                        <div className="text-fa-frost-bright tabular-nums">
                          {score.toFixed(1)}%
                        </div>
                      </div>
                    ))}
                </div>
              </div>
            )}

            {/* Back button */}
            <button
              type="button"
              onClick={() => navigate("/strategies")}
              className="inline-flex items-center gap-2 px-3 py-2 rounded-md border border-fa-edge bg-fa-glass hover:bg-fa-glass-strong text-fa-frost-bright text-sm transition"
            >
              Back to Strategies
            </button>
          </div>
        </div>
      </div>
    );
  }

  // ── DAG strategy — full FlowDesigner (read-only when built-in) ─────────────
  return (
    <div className="h-full flex flex-col overflow-hidden">
      <FlowDesigner
        title={strategy.name}
        definitionJson={strategy.definition ?? EMPTY_DAG}
        isBuiltIn={strategy.isBuiltIn}
        catalogue={catalogue ?? {}}
        entityKind="strategy"
        onSave={async (defJson) => {
          await updateStrategy({
            id: strategy.id,
            body: { definition: defJson },
          }).unwrap();
          navigate("/strategies");
        }}
        onClose={() => navigate("/strategies")}
      />
    </div>
  );
}
