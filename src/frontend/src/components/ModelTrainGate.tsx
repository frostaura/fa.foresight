import { createContext, useCallback, useContext, useEffect, useRef, useState } from "react";
import { createPortal } from "react-dom";
import { AlertTriangle, Cpu, Loader2, X } from "lucide-react";
import { cn } from "../lib/cn";
import { useTrainingProgress } from "../lib/trainingStream";
import { ProgressInline } from "./ProgressInline";
import { useListModelsQuery, useTrainModelMutation, type Model } from "../store/api";

// Every deterministic model is fit against BTCUSDT — the only instrument the trainer supports today.
const TRAIN_SYMBOL = "BTCUSDT";

/**
 * A model must be trained before it can be selected anywhere it actually drives predictions
 * (backtests, chaos tests, live cards). Only deterministic models have coefficients to fit; an
 * LLM model has none, so it never needs training. A deterministic model is "untrained" until it
 * has a `trainedState` blob — which also covers the mid-training and previously-failed states.
 */
export function modelNeedsTraining(m: Pick<Model, "kind" | "trainedState">): boolean {
  return m.kind === "deterministic" && !m.trainedState;
}

type GateFn = (model: Model) => Promise<boolean>;

const TrainGateContext = createContext<GateFn | null>(null);

/**
 * Mirrors `useConfirm`: returns a function that, given a model, resolves true once the model is
 * trained and may be selected — or false if the user backs out. Already-trained (or non-trainable)
 * models resolve true immediately with no dialog.
 *
 * Usage:
 *   const ensureTrained = useModelTrainGate();
 *   if (await ensureTrained(model)) setSelected((s) => [...s, model.id]);
 */
export function useModelTrainGate() {
  const ctx = useContext(TrainGateContext);
  if (!ctx) throw new Error("useModelTrainGate must be used inside <ModelTrainGateProvider>");
  return ctx;
}

type Pending = { model: Model; resolve: (ok: boolean) => void };

export function ModelTrainGateProvider({ children }: { children: React.ReactNode }) {
  const [pending, setPending] = useState<Pending | null>(null);

  const gate = useCallback<GateFn>((model) => {
    return new Promise<boolean>((resolve) => {
      if (!modelNeedsTraining(model)) { resolve(true); return; }
      setPending({ model, resolve });
    });
  }, []);

  const close = useCallback((ok: boolean) => {
    setPending((p) => {
      p?.resolve(ok);
      return null;
    });
  }, []);

  return (
    <TrainGateContext.Provider value={gate}>
      {children}
      {pending && <TrainDialog key={pending.model.id} model={pending.model} onResolve={close} />}
    </TrainGateContext.Provider>
  );
}

type Phase = "prompt" | "training" | "failed";

function TrainDialog({ model: initial, onResolve }: { model: Model; onResolve: (ok: boolean) => void }) {
  const [train] = useTrainModelMutation();
  const [phase, setPhase] = useState<Phase>(
    initial.trainingStatus === "training" ? "training"
      : initial.trainingStatus === "failed" ? "failed"
      : "prompt",
  );
  const [error, setError] = useState<string | null>(initial.trainingError ?? null);
  const resolvedRef = useRef(false);

  // While a background fit is in flight we react to the server-side TrainingStatus transition. The
  // model cache is invalidated push-style by the /api/models SSE stream (see RealtimeSync) the moment
  // training starts/completes/fails — no polling. This shares the `listModels(undefined)` cache key
  // with the rest of the app, so the dropdown that triggered us refreshes its score on the same event.
  const { data: models } = useListModelsQuery(void 0);
  const live = models?.find((m) => m.id === initial.id) ?? initial;

  const resolve = useCallback((ok: boolean) => {
    if (resolvedRef.current) return;
    resolvedRef.current = true;
    onResolve(ok);
  }, [onResolve]);

  // React to server-side training transitions.
  useEffect(() => {
    if (phase !== "training") return;
    if (live.trainedState && live.trainingStatus !== "training") {
      resolve(true);
    } else if (live.trainingStatus === "failed") {
      setError(live.trainingError ?? "Training failed.");
      setPhase("failed");
    }
  }, [phase, live.trainedState, live.trainingStatus, live.trainingError, resolve]);

  const start = useCallback(async () => {
    setError(null);
    setPhase("training");
    try {
      await train({ id: initial.id, symbol: TRAIN_SYMBOL }).unwrap();
      // 202 Accepted — the fit runs server-side. The model SSE stream invalidates the model cache on
      // completion (see RealtimeSync), which re-runs the effect below and resolves us.
    } catch (e) {
      const err = e as { data?: { error?: string }; message?: string };
      setError(err.data?.error ?? err.message ?? "Could not start training.");
      setPhase("failed");
    }
  }, [train, initial.id]);

  // Esc / click-outside backs out (training, if started, continues server-side).
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key === "Escape") { e.preventDefault(); resolve(false); }
    };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [resolve]);

  const isTraining = phase === "training";
  const isFailed = phase === "failed";
  // Live per-phase progress while the fit runs server-side, so the dialog shows real movement
  // instead of just a spinning icon.
  const trainProgress = useTrainingProgress(initial.id, isTraining);

  return createPortal(
    <div
      className="fixed inset-0 z-[70] bg-fa-ink/70 backdrop-blur-sm flex items-center justify-center p-6 fa-confirm-enter"
      onClick={() => resolve(false)}
      role="dialog"
      aria-modal="true"
      aria-labelledby="fa-train-title"
    >
      <div
        className={cn(
          "fa-card w-full max-w-md p-6 space-y-5 relative",
          "shadow-[0_20px_60px_-15px_rgba(0,0,0,0.6)] border border-fa-edge",
          isFailed && "ring-1 ring-rose-400/20",
        )}
        onClick={(e) => e.stopPropagation()}
      >
        <div className="flex items-start gap-3">
          <div
            className={cn(
              "shrink-0 flex items-center justify-center w-10 h-10 rounded-full",
              isFailed
                ? "bg-rose-400/10 text-rose-300 ring-1 ring-rose-400/30"
                : "bg-fa-frost-bright/10 text-fa-frost-bright ring-1 ring-fa-frost-bright/30",
            )}
          >
            {isFailed ? <AlertTriangle className="h-5 w-5" />
              : isTraining ? <Loader2 className="h-5 w-5 animate-spin" />
              : <Cpu className="h-5 w-5" />}
          </div>
          <div className="min-w-0 flex-1">
            <h2 id="fa-train-title" className="text-fa-frost-bright text-base font-light tracking-tight">
              {isTraining ? "Training" : isFailed ? "Training failed" : "Train"} <span className="font-medium">{initial.name}</span>
            </h2>
            <p className="text-fa-frost-dim text-sm mt-1.5 leading-relaxed">
              {isTraining ? (
                <>Fitting walk-forward variants on {TRAIN_SYMBOL} across each interval. This runs server-side and continues even if you close this dialog.</>
              ) : isFailed ? (
                <>The fit didn't complete. Review the error below and retry, or pick a different model.</>
              ) : (
                <><span className="text-fa-frost-bright">{initial.name}</span> hasn't been trained yet, so it has no score and can't be selected. Training fits its walk-forward variants on {TRAIN_SYMBOL} — it takes about a minute and runs server-side.</>
              )}
            </p>
          </div>
          <button
            onClick={() => resolve(false)}
            aria-label="Dismiss"
            className="shrink-0 -mt-1 -mr-1 text-fa-frost-dim hover:text-fa-frost-bright transition"
          >
            <X className="h-4 w-4" />
          </button>
        </div>

        {isTraining && (
          <ProgressInline pct={trainProgress.pct} label={trainProgress.label ?? "Starting…"} tone="frost" />
        )}

        {error && (
          <div className="text-rose-300 text-xs bg-rose-400/5 border border-rose-400/20 rounded-md px-3 py-2 break-words">
            {error}
          </div>
        )}

        <div className="flex items-center justify-end gap-2 pt-1">
          <button
            onClick={() => resolve(false)}
            className="px-3 py-1.5 rounded-md text-fa-frost-dim hover:text-fa-frost-bright text-sm transition"
          >
            {isTraining ? "Run in background" : "Cancel"}
          </button>
          {!isTraining && (
            <button
              onClick={start}
              className="inline-flex items-center gap-2 px-4 py-1.5 rounded-md text-sm border transition bg-fa-frost-bright/20 hover:bg-fa-frost-bright/30 text-fa-frost-bright border-fa-frost-bright/30"
            >
              <Cpu className="h-3.5 w-3.5" />
              {isFailed ? "Retry training" : "Train now"}
            </button>
          )}
        </div>
      </div>
    </div>,
    document.body,
  );
}
