import { useEffect, useRef, useState } from "react";

const apiBase = (import.meta.env.VITE_API_BASE as string | undefined) ?? "/api";

type Phase = "queued" | "hydrating" | "building-features" | "validating" | "fitting" | "done" | "failed";

interface TrainFrame {
  kind: "started" | "progress" | "completed" | "failed";
  interval: string;
  phase: Phase;
  processed: number;
  total: number;
  error: string | null;
}

interface IntervalState {
  phase: Phase;
  processed: number;
  total: number;
}

/** Friendly phase copy — the user reads *what* is happening, not just that something is. */
const PHASE_LABEL: Record<Phase, string> = {
  queued: "Queued",
  hydrating: "Fetching candles",
  "building-features": "Building features",
  validating: "Walk-forward validation",
  fitting: "Fitting final model",
  done: "Finalising",
  failed: "Failed",
};

/**
 * Maps one interval-variant's phase + within-phase fraction onto an overall 0–1 completion. The
 * bands reflect real wall-time: the per-candle feature replay dominates, so it owns the widest band
 * (5%→80%); hydration is a quick lead-in, walk-forward and the final fit are short tails.
 */
function fractionFor(s: IntervalState): number {
  const f = s.total > 0 ? Math.min(1, s.processed / s.total) : 0;
  switch (s.phase) {
    case "queued":
      return 0.01;
    case "hydrating":
      return 0.03;
    case "building-features":
      return 0.05 + 0.75 * f;
    case "validating":
      return 0.8 + 0.15 * f;
    case "fitting":
      return 0.96;
    case "done":
      return 1;
    case "failed":
      return f;
  }
}

export interface TrainingProgress {
  /** Overall completion 0–100, or null when training is live but no per-interval tick has arrived yet (→ indeterminate bar). */
  pct: number | null;
  /** Human-readable phase line, e.g. "Building features · 5m 62%" or "Walk-forward validation · fold 3/5". */
  label: string | null;
  error: string | null;
}

/**
 * Subscribes to a model's training SSE stream and aggregates the per-interval phase ticks into one
 * overall bar + a phase label. Opens the EventSource only while `enabled` (the model is training);
 * closes on completed/failed/unmount. The caller keeps polling listModels for the authoritative
 * completion + final score — this hook is purely the live progress overlay, so behaviour is
 * unchanged if the stream drops.
 */
export function useTrainingProgress(modelId: string, enabled: boolean): TrainingProgress {
  const [progress, setProgress] = useState<TrainingProgress>({ pct: null, label: null, error: null });
  const intervalsRef = useRef<Map<string, IntervalState>>(new Map());

  useEffect(() => {
    if (!enabled) {
      intervalsRef.current = new Map();
      setProgress({ pct: null, label: null, error: null });
      return;
    }

    intervalsRef.current = new Map();
    const es = new EventSource(`${apiBase}/models/${modelId}/train/stream`);

    const recompute = (error: string | null) => {
      const states = [...intervalsRef.current.values()];
      if (states.length === 0) {
        setProgress({ pct: null, label: "Starting…", error });
        return;
      }
      const overall = (states.reduce((sum, s) => sum + fractionFor(s), 0) / states.length) * 100;
      // Surface the trailing variant (lowest fraction) — it's the honest "we're as done as our
      // slowest interval" signal and gives the most informative phase line.
      let trailing: [string, IntervalState] | null = null;
      let trailingFrac = Infinity;
      for (const [iv, s] of intervalsRef.current) {
        const fr = fractionFor(s);
        if (fr < trailingFrac) {
          trailingFrac = fr;
          trailing = [iv, s];
        }
      }
      let label: string | null = null;
      if (trailing) {
        const [iv, s] = trailing;
        const phaseTxt = PHASE_LABEL[s.phase];
        if (s.phase === "validating" && s.total > 0) label = `${phaseTxt} · ${iv} fold ${s.processed}/${s.total}`;
        else if (s.phase === "building-features" && s.total > 0)
          label = `${phaseTxt} · ${iv} ${Math.round((s.processed / s.total) * 100)}%`;
        else label = `${phaseTxt} · ${iv}`;
      }
      setProgress({ pct: overall, label, error });
    };

    es.onmessage = (e) => {
      let frame: TrainFrame;
      try {
        frame = JSON.parse(e.data) as TrainFrame;
      } catch {
        return; // malformed frame — ignore
      }
      if (frame.kind === "failed") {
        setProgress((p) => ({ ...p, error: frame.error ?? "Training failed" }));
        es.close();
        return;
      }
      if (frame.kind === "completed") {
        setProgress({ pct: 100, label: PHASE_LABEL.done, error: null });
        es.close();
        return;
      }
      if (frame.kind === "progress") {
        intervalsRef.current.set(frame.interval, {
          phase: frame.phase,
          processed: frame.processed,
          total: frame.total,
        });
        recompute(null);
      }
      // "started" carries no per-interval data — leave the indeterminate "Starting…" state.
    };

    es.onerror = () => {
      // Transient drop — the EventSource auto-reconnects. listModels polling is the real fallback,
      // so we just leave the last-known progress on screen rather than flashing an error.
    };

    return () => es.close();
  }, [modelId, enabled]);

  return progress;
}
