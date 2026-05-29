import { useEffect, useMemo, useRef, useState } from "react";
import type { LivePrediction } from "../store/api";

const apiBase = (import.meta.env.VITE_API_BASE as string | undefined) ?? "/api";

type Listener = (p: LivePrediction) => void;

interface SharedStream {
  refCount: number;
  es: EventSource;
  // listeners keyed by `${symbol}:${interval}` so we route incoming events without iterating every
  // subscriber on every frame.
  listeners: Map<string, Set<Listener>>;
}

// Module-level singleton: one EventSource per browser tab, shared by every card that subscribes.
// Reference-counted so we close the socket when the last subscriber unmounts.
let shared: SharedStream | null = null;

function bucketKey(symbol: string, interval: string) {
  return `${symbol}:${interval}`;
}

function ensureShared(): SharedStream {
  if (shared) return shared;
  const tenant = localStorage.getItem("fa.tenant") ?? "default";
  const url = `${apiBase}/live/predictions/stream?tenant=${encodeURIComponent(tenant)}`;
  const listeners = new Map<string, Set<Listener>>();
  const es = new EventSource(url);
  const dispatch = (e: MessageEvent) => {
    try {
      const p = JSON.parse(e.data) as LivePrediction;
      const set = listeners.get(bucketKey(p.symbol, p.interval));
      if (!set) return;
      for (const fn of set) fn(p);
    } catch {
      // malformed frame — ignore
    }
  };
  es.addEventListener("created", dispatch as EventListener);
  es.addEventListener("resolved", dispatch as EventListener);
  shared = { refCount: 0, es, listeners };
  return shared;
}

function releaseShared() {
  if (!shared) return;
  shared.refCount -= 1;
  if (shared.refCount <= 0) {
    shared.es.close();
    shared = null;
  }
}

/**
 * Push-based predictions feed. Backfills via one REST call on mount, then subscribes to the shared
 * SSE stream and upserts as `created`/`resolved` events fire. The EventSource itself is module-
 * scoped and ref-counted — every card on the page rides the same connection.
 */
export function useLivePredictionStream(symbol: string, interval: string, take = 200, modelName?: string) {
  // Store ALL models' predictions for the (symbol, interval); the per-model view is a derived filter
  // below. This decouples the model selection from the network layer: switching the active model
  // re-filters in memory and does NOT re-fetch or re-subscribe (the effect deps below exclude
  // modelName), which was a source of redundant /predictions + /backfill calls on every switch.
  const [raw, setRaw] = useState<LivePrediction[]>([]);
  const refetchRef = useRef<() => Promise<void>>(async () => {});

  useEffect(() => {
    let alive = true;
    const controller = new AbortController();
    const tenant = localStorage.getItem("fa.tenant") ?? "default";

    const upsert = (incoming: LivePrediction) => {
      if (!alive) return;
      setRaw((prev) => {
        const ix = prev.findIndex((p) => p.id === incoming.id);
        if (ix < 0) return [incoming, ...prev].slice(0, take);
        const next = prev.slice();
        next[ix] = { ...prev[ix], ...incoming };
        return next;
      });
    };

    const backfill = async () => {
      const url = `${apiBase}/live/predictions?symbol=${encodeURIComponent(symbol)}&interval=${encodeURIComponent(interval)}&take=${take}`;
      const res = await fetch(url, {
        headers: { "X-Tenant-Slug": tenant },
        signal: controller.signal
      });
      if (!res.ok) throw new Error(`predictions ${res.status}`);
      const rows = (await res.json()) as LivePrediction[];
      if (alive) setRaw(rows);
    };
    refetchRef.current = backfill;
    backfill().catch((e) => {
      if ((e as Error).name !== "AbortError") {
        console.warn("predictions backfill failed", e);
      }
    });

    // Fill hit/miss dots across the whole visible window, not just candles we were live for. The
    // active model is deterministic, so the server replays it over the recent `take` candles
    // (leakage-free) and persists the gaps as resolved predictions — then we re-pull so they render.
    // Idempotent server-side: candles already predicted are skipped, so re-mounts cost little.
    fetch(
      `${apiBase}/live/predictions/backfill?symbol=${encodeURIComponent(symbol)}&interval=${encodeURIComponent(interval)}&candles=${take}`,
      { method: "POST", headers: { "X-Tenant-Slug": tenant }, signal: controller.signal }
    )
      .then((res) => (res.ok ? res.json() : null))
      .then((r: { added?: number } | null) => {
        if (alive && r && (r.added ?? 0) > 0) return backfill();
      })
      .catch((e) => {
        if ((e as Error).name !== "AbortError") {
          console.debug("prediction history backfill skipped", e);
        }
      });

    // Subscribe to the shared EventSource.
    const s = ensureShared();
    s.refCount += 1;
    const key = bucketKey(symbol, interval);
    let bucket = s.listeners.get(key);
    if (!bucket) {
      bucket = new Set();
      s.listeners.set(key, bucket);
    }
    bucket.add(upsert);

    return () => {
      alive = false;
      controller.abort();
      bucket!.delete(upsert);
      if (bucket!.size === 0) s.listeners.delete(key);
      releaseShared();
    };
    // modelName intentionally excluded — it only affects the derived filter, not the fetch/subscription.
  }, [symbol, interval, take]);

  // Per-model view: filter the shared raw feed in memory. Switching models is now free (no network).
  const predictions = useMemo(
    () => (modelName ? raw.filter((p) => p.model === modelName) : raw),
    [raw, modelName]
  );

  return {
    predictions,
    refetch: async () => {
      await refetchRef.current();
    }
  };
}
