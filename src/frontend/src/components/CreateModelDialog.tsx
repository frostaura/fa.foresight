import { useState } from "react";
import { createPortal } from "react-dom";
import { X, Wand2, AlertTriangle } from "lucide-react";
import { useCreateModelMutation } from "../store/api";

/**
 * Minimal "Create model" dialog. Until the reactflow visual designer lands (task #70 follow-up),
 * users author models by picking a seed template and tweaking the JSON in a textarea. The seed
 * templates are intentionally small: one LinearRegression + indicator example, one LogisticRegression
 * example. Both compose only nodes that have shipped in iter-4, so creating + training + backtesting
 * works end-to-end from this UI alone.
 *
 * Server-side `FlowValidator` runs on save — invalid JSON returns a user-readable error string into
 * `error`. The user iterates until the validator passes, then the model row drops into the list.
 */
export default function CreateModelDialog({ onClose }: { onClose: () => void }) {
  const [name, setName] = useState("");
  const [description, setDescription] = useState("");
  const [template, setTemplate] = useState<TemplateId>("logreg");
  const [definition, setDefinition] = useState(TEMPLATES.logreg.json);
  const [error, setError] = useState<string | null>(null);
  const [create, { isLoading }] = useCreateModelMutation();

  const applyTemplate = (id: TemplateId) => {
    setTemplate(id);
    setDefinition(TEMPLATES[id].json);
  };

  const onSubmit = async () => {
    setError(null);
    if (!name.trim()) { setError("Name is required."); return; }
    try {
      await create({
        name: name.trim(),
        description: description.trim() || null,
        kind: "deterministic",
        supportsBacktesting: true,
        definition
      }).unwrap();
      onClose();
    } catch (e: unknown) {
      const err = e as { data?: { error?: string } };
      setError(err.data?.error ?? "Create failed");
    }
  };

  // Portal to body so the modal overlay isn't trapped inside the model card's containing block
  // (cards have transition/transform CSS which captures `position: fixed` to the card's bounds).
  return createPortal(
    <div className="fixed inset-0 z-50 bg-fa-ink/80 backdrop-blur-sm flex items-start justify-center p-6 overflow-y-auto" onClick={(e) => e.stopPropagation()}>
      <div className="fa-card w-full max-w-2xl p-6 space-y-4" onClick={(e) => e.stopPropagation()}>
        <div className="flex items-start justify-between gap-3">
          <div>
            <h2 className="text-fa-frost-bright text-lg font-light tracking-tight">Create model</h2>
            <p className="text-fa-frost-dim text-xs mt-1">
              Pick a template, tweak the flow JSON, save. Train + backtest from the model card after creation.
            </p>
          </div>
          <button onClick={onClose} aria-label="Close" className="text-fa-frost-dim hover:text-fa-frost-bright transition">
            <X className="h-5 w-5" />
          </button>
        </div>

        <div className="grid grid-cols-1 sm:grid-cols-2 gap-3">
          <label className="block">
            <div className="fa-overline text-fa-frost-dim mb-1">Name</div>
            <input value={name} onChange={(e) => setName(e.target.value)} maxLength={200}
              className="w-full bg-fa-glass border border-fa-edge rounded-md px-2 py-1.5 text-fa-frost-bright text-sm" placeholder="e.g. RSI + EMA logistic" />
          </label>
          <label className="block">
            <div className="fa-overline text-fa-frost-dim mb-1">Description (optional)</div>
            <input value={description} onChange={(e) => setDescription(e.target.value)} maxLength={2000}
              className="w-full bg-fa-glass border border-fa-edge rounded-md px-2 py-1.5 text-fa-frost-bright text-sm" placeholder="Short summary" />
          </label>
        </div>

        <div>
          <div className="fa-overline text-fa-frost-dim mb-2">Seed template</div>
          <div className="flex flex-wrap gap-2">
            {(Object.keys(TEMPLATES) as TemplateId[]).map((id) => (
              <button key={id} onClick={() => applyTemplate(id)}
                className={`inline-flex items-center gap-1.5 px-3 py-1.5 rounded-md border text-xs transition ${
                  template === id
                    ? "border-fa-frost-bright/40 bg-fa-frost-bright/10 text-fa-frost-bright"
                    : "border-fa-edge bg-fa-glass text-fa-frost-dim hover:text-fa-frost-bright"
                }`}>
                <Wand2 className="h-3 w-3" />
                {TEMPLATES[id].label}
              </button>
            ))}
          </div>
        </div>

        <div>
          <div className="fa-overline text-fa-frost-dim mb-1">Flow JSON</div>
          <textarea value={definition} onChange={(e) => setDefinition(e.target.value)} rows={16}
            spellCheck={false}
            className="w-full font-mono fa-caption bg-fa-glass border border-fa-edge rounded-md p-3 text-fa-frost-bright resize-y" />
          <p className="text-fa-frost-dim/70 fa-caption mt-1">
            Validated server-side on save (DAG check, port type-tags, backtest-source restriction).
          </p>
        </div>

        {error && (
          <div className="flex items-start gap-2 p-3 rounded-md border border-rose-300/30 bg-rose-300/5 text-rose-300 text-xs">
            <AlertTriangle className="h-4 w-4 shrink-0 mt-0.5" />
            <pre className="whitespace-pre-wrap font-mono">{error}</pre>
          </div>
        )}

        <div className="flex items-center justify-end gap-3 pt-2">
          <button onClick={onClose} className="px-3 py-1.5 text-sm text-fa-frost-dim hover:text-fa-frost-bright transition">
            Cancel
          </button>
          <button onClick={onSubmit} disabled={isLoading || !name.trim()}
            className="inline-flex items-center gap-2 px-4 py-2 rounded-md bg-fa-frost-bright/20 hover:bg-fa-frost-bright/30 text-fa-frost-bright text-sm border border-fa-frost-bright/30 disabled:opacity-50 disabled:cursor-not-allowed transition">
            {isLoading ? "Creating…" : "Create"}
          </button>
        </div>
      </div>
    </div>,
    document.body
  );
}

type TemplateId = "logreg" | "linreg";

const TEMPLATES: Record<TemplateId, { label: string; json: string }> = {
  logreg: {
    label: "Logistic Regression on RSI+EMA",
    // Logistic regression predicts P(up) directly from RSI14 + EMA12 + log-return + z-score. All
    // inputs come from a single Binance klines fetch on the card's interval → indicator pack →
    // matrix builder → logreg → output.
    json: JSON.stringify({
      schemaVersion: 1,
      modelKind: "deterministic",
      supportsBacktesting: true,
      warmupCandles: 30,
      nodes: [
        { id: "candles", type: "source.binance.klines", params: { tf: "target", limit: 60 }, position: { x: 40, y: 80 } },
        { id: "tech",    type: "indicator.tech_pack",     params: {},                        position: { x: 280, y: 40 } },
        { id: "feat",    type: "indicator.feature_pack",  params: {},                        position: { x: 280, y: 160 } },
        { id: "matrix",  type: "feature.matrix_builder",  params: { columns: ["rsi14", "ema12", "logret", "z20"] }, position: { x: 540, y: 100 } },
        { id: "logreg",  type: "model.logistic_regression", params: { l2: 0.01 },            position: { x: 800, y: 100 } },
        { id: "out",     type: "output.prediction",       params: {},                        position: { x: 1060, y: 100 } }
      ],
      edges: [
        { from: "candles.candles", to: "tech.candles" },
        { from: "candles.candles", to: "feat.candles" },
        { from: "tech.rsi14",      to: "matrix.rsi14" },
        { from: "feat.ema12",      to: "matrix.ema12" },
        { from: "feat.logret",     to: "matrix.logret" },
        { from: "feat.z20",        to: "matrix.z20" },
        { from: "matrix.matrix",   to: "logreg.matrix" },
        { from: "matrix.ready",    to: "logreg.ready" },
        { from: "logreg.pUp",      to: "out.pUp" },
        { from: "logreg.confidence", to: "out.confidence" }
      ]
    }, null, 2)
  },
  linreg: {
    label: "Linear Regression (close prediction)",
    // Linear regression predicts next close from EMA/MACD/Bollinger features. Direction derives
    // from sign(predicted - anchor) — the anchor edge feeds the previous close in directly.
    json: JSON.stringify({
      schemaVersion: 1,
      modelKind: "deterministic",
      supportsBacktesting: true,
      warmupCandles: 30,
      nodes: [
        { id: "candles", type: "source.binance.klines", params: { tf: "target", limit: 60 }, position: { x: 40, y: 80 } },
        { id: "feat",    type: "indicator.feature_pack", params: {},                         position: { x: 280, y: 80 } },
        { id: "matrix",  type: "feature.matrix_builder", params: { columns: ["ema12", "ema26", "macd", "logret"] }, position: { x: 540, y: 100 } },
        { id: "linreg",  type: "model.linear_regression", params: { l2: 0.0 },              position: { x: 800, y: 100 } },
        { id: "out",     type: "output.prediction",      params: {},                         position: { x: 1060, y: 100 } }
      ],
      edges: [
        { from: "candles.candles", to: "feat.candles" },
        { from: "feat.ema12",      to: "matrix.ema12" },
        { from: "feat.ema26",      to: "matrix.ema26" },
        { from: "feat.macd",       to: "matrix.macd" },
        { from: "feat.logret",     to: "matrix.logret" },
        { from: "matrix.matrix",   to: "linreg.matrix" },
        { from: "matrix.ready",    to: "linreg.ready" },
        { from: "linreg.predicted", to: "out.predicted" },
        { from: "linreg.pUp",      to: "out.pUp" },
        { from: "linreg.confidence", to: "out.confidence" }
      ]
    }, null, 2)
  }
};
