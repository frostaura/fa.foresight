import { useEffect, useState } from "react";

const apiBase = (import.meta.env.VITE_API_BASE as string | undefined) ?? "/api";

interface ChaosFrame {
  kind: "started" | "progress" | "completed" | "failed";
  comboIndex: number;
  totalCombos: number;
  sampleIndex: number;
  totalSamples: number;
  error: string | null;
}

export interface ChaosProgress {
  /** Overall completion 0–100, or null when the batch is live but no tick has arrived yet. */
  pct: number | null;
  /** e.g. "Combo 2/6 · sample 140/200". */
  label: string | null;
  error: string | null;
}

/**
 * Subscribes to a chaos batch's progress stream (the backend already publishes combo/sample ticks —
 * this consumes them so the UI shows real movement instead of a 2s-polled "running" orb). Pass
 * `null` for batchId to stay idle. Polling the chaos list remains the authoritative completion
 * signal, so behaviour is unchanged if the stream drops.
 */
export function useChaosProgress(batchId: string | null): ChaosProgress {
  const [progress, setProgress] = useState<ChaosProgress>({ pct: null, label: null, error: null });

  useEffect(() => {
    if (!batchId) {
      setProgress({ pct: null, label: null, error: null });
      return;
    }
    setProgress({ pct: null, label: "Starting…", error: null });
    const es = new EventSource(`${apiBase}/chaos/stream?batchId=${encodeURIComponent(batchId)}`);

    es.onmessage = (e) => {
      let frame: ChaosFrame;
      try {
        frame = JSON.parse(e.data) as ChaosFrame;
      } catch {
        return;
      }
      if (frame.kind === "failed") {
        setProgress((p) => ({ ...p, error: frame.error ?? "Chaos run failed" }));
        es.close();
        return;
      }
      if (frame.kind === "completed") {
        setProgress({ pct: 100, label: "Finalising", error: null });
        es.close();
        return;
      }
      if (frame.kind === "progress") {
        const within = frame.totalSamples > 0 ? frame.sampleIndex / frame.totalSamples : 0;
        const pct = frame.totalCombos > 0 ? ((frame.comboIndex + within) / frame.totalCombos) * 100 : null;
        const label =
          frame.totalCombos > 1
            ? `Combo ${Math.min(frame.comboIndex + 1, frame.totalCombos)}/${frame.totalCombos} · sample ${frame.sampleIndex}/${frame.totalSamples}`
            : `Sample ${frame.sampleIndex}/${frame.totalSamples}`;
        setProgress({ pct, label, error: null });
      }
    };

    es.onerror = () => {
      // Transient drop — EventSource auto-reconnects; the chaos-list poll is the real fallback.
    };

    return () => es.close();
  }, [batchId]);

  return progress;
}
