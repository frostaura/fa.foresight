import { useEffect, useRef, useState } from "react";

export interface Candle {
  openTime: number;
  open: number;
  high: number;
  low: number;
  close: number;
  volume: number;
  closeTime: number;
}

export type BinanceInterval = "1m" | "5m" | "15m";

export const INTERVAL_MS: Record<BinanceInterval, number> = {
  "1m": 60_000,
  "5m": 5 * 60_000,
  "15m": 15 * 60_000
};

export const INTERVAL_LABEL: Record<BinanceInterval, string> = {
  "1m": "1 minute",
  "5m": "5 minutes",
  "15m": "15 minutes"
};

const REST_BASE = "https://api.binance.com";
const WS_BASE = "wss://stream.binance.com:9443/ws";

// In-flight + short-TTL dedup for the klines history fetch. The chart only fetches history once per
// (symbol, interval) and then rides the WS stream — but React StrictMode (dev) double-invokes the
// mount effect, and rapid interval/model flips can re-fire it, producing the "cancelled + refetched"
// pairs seen in the network panel. Coalescing identical requests within a few seconds collapses those
// into one real call. TTL is short because the live candle is kept fresh by the WS stream regardless.
const KLINES_TTL_MS = 4_000;
const klinesCache = new Map<string, { ts: number; promise: Promise<Candle[]> }>();

async function fetchKlinesUncached(symbol: string, interval: BinanceInterval, limit: number, signal?: AbortSignal): Promise<Candle[]> {
  const res = await fetch(
    `${REST_BASE}/api/v3/klines?symbol=${symbol}&interval=${interval}&limit=${limit}`,
    { signal }
  );
  if (!res.ok) throw new Error(`Binance klines ${res.status}`);
  const raw = (await res.json()) as unknown[][];
  return raw.map((row) => ({
    openTime: Number(row[0]),
    open: Number(row[1]),
    high: Number(row[2]),
    low: Number(row[3]),
    close: Number(row[4]),
    volume: Number(row[5]),
    closeTime: Number(row[6])
  }));
}

export function fetchKlines(symbol: string, interval: BinanceInterval, limit = 120, _signal?: AbortSignal): Promise<Candle[]> {
  const key = `${symbol}|${interval}|${limit}`;
  const hit = klinesCache.get(key);
  if (hit && Date.now() - hit.ts < KLINES_TTL_MS) return hit.promise;
  // Not aborted by an individual caller's signal — the result is shared; unmounted callers drop it
  // via their own `alive` guard. A failed fetch is evicted so the next mount retries cleanly.
  const promise = fetchKlinesUncached(symbol, interval, limit).catch((e) => {
    klinesCache.delete(key);
    throw e;
  });
  klinesCache.set(key, { ts: Date.now(), promise });
  return promise;
}

export interface Ticker24h {
  symbol: string;
  lastPrice: number;
  priceChange: number;
  priceChangePercent: number;
  highPrice: number;
  lowPrice: number;
  volume: number;
  quoteVolume: number;
}

// Binance WebSocket helper. Opens one socket per stream path, reconnects with capped exponential
// backoff on disconnect, and tears down cleanly on unmount. The caller's handler is routed
// through a ref so the socket isn't recycled when only the closure identity changes between
// renders — reconnects happen only on real input changes (streamPath swap, unmount).
function useBinanceStream<T>(
  streamPath: string | null,
  onMessage: (data: T) => void
): void {
  const handlerRef = useRef(onMessage);
  handlerRef.current = onMessage;

  useEffect(() => {
    if (!streamPath) return;
    let closed = false;
    let ws: WebSocket | null = null;
    let reconnectTimer: number | null = null;
    let attempts = 0;

    const connect = () => {
      if (closed) return;
      ws = new WebSocket(`${WS_BASE}/${streamPath}`);
      ws.onopen = () => {
        attempts = 0;
      };
      ws.onmessage = (e) => {
        try {
          handlerRef.current(JSON.parse(e.data) as T);
        } catch {
          // swallow malformed frames — next message retries
        }
      };
      ws.onclose = () => {
        if (closed) return;
        // Cap backoff at 15s so reconnects stay responsive after long pauses (laptop sleep, network
        // flap) without hammering the gateway during a partial outage.
        const delay = Math.min(15_000, 500 * 2 ** attempts++);
        reconnectTimer = window.setTimeout(connect, delay);
      };
      ws.onerror = () => {
        // Force close; onclose handles backoff.
        ws?.close();
      };
    };
    connect();

    return () => {
      closed = true;
      if (reconnectTimer != null) window.clearTimeout(reconnectTimer);
      if (ws) {
        ws.onmessage = null;
        ws.onclose = null;
        ws.onerror = null;
        if (ws.readyState === WebSocket.CONNECTING) {
          // Closing a still-CONNECTING socket makes Safari log "WebSocket is closed before the
          // connection is established" (Chrome aborts it silently). React StrictMode's dev
          // mount→unmount→remount hits this on every stream. Defer the close until the socket
          // opens so the handshake completes cleanly first.
          const socket = ws;
          socket.onopen = () => socket.close();
        } else {
          ws.onopen = null;
          ws.close();
        }
      }
    };
  }, [streamPath]);
}

/**
 * Live klines via Binance WebSocket. Initial history is fetched once via REST; the WS stream
 * pushes per-trade updates that refresh the trailing candle and append new ones as the boundary
 * crosses. One socket per (symbol, interval) — released the moment the consuming card unmounts.
 */
export function useLiveKlines(symbol: string, interval: BinanceInterval, limit = 120) {
  const [candles, setCandles] = useState<Candle[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);

  // Initial history backfill via REST so the chart has context the moment it mounts.
  useEffect(() => {
    setLoading(true);
    setError(null);
    let alive = true;
    const controller = new AbortController();
    fetchKlines(symbol, interval, limit, controller.signal)
      .then((hist) => {
        if (!alive) return;
        setCandles(hist);
        setLoading(false);
      })
      .catch((e) => {
        if ((e as Error).name === "AbortError" || !alive) return;
        setError((e as Error).message);
        setLoading(false);
      });
    return () => {
      alive = false;
      controller.abort();
    };
  }, [symbol, interval, limit]);

  const stream = `${symbol.toLowerCase()}@kline_${interval}`;
  useBinanceStream<KlineFrame>(stream, (msg) => {
    const k = msg.k;
    if (!k) return;
    const updated: Candle = {
      openTime: k.t,
      closeTime: k.T,
      open: Number(k.o),
      high: Number(k.h),
      low: Number(k.l),
      close: Number(k.c),
      volume: Number(k.v)
    };
    setCandles((prev) => {
      if (prev.length === 0) return [updated];
      const last = prev[prev.length - 1];
      if (updated.openTime === last.openTime) {
        // Same candle, fresher tick: replace in place.
        const next = prev.slice();
        next[next.length - 1] = updated;
        return next;
      }
      if (updated.openTime > last.openTime) {
        // Candle boundary crossed: append, trim to limit.
        const next = [...prev, updated];
        return next.length > limit ? next.slice(-limit) : next;
      }
      // Out-of-order/stale frame: ignore.
      return prev;
    });
  });

  return { candles, loading, error };
}

interface KlineFrame {
  e?: string;
  E?: number;
  s?: string;
  k?: {
    t: number; // kline start time
    T: number; // kline close time
    o: string;
    c: string;
    h: string;
    l: string;
    v: string;
    x: boolean; // is closed
  };
}

/**
 * Live spot-price hook backed by Binance's @miniTicker stream (one push per second per symbol).
 * Returns the latest close + the previous value for flash-on-change UX.
 */
export function useLivePrice(symbol: string) {
  const [price, setPrice] = useState<number | null>(null);
  const [prev, setPrev] = useState<number | null>(null);

  const stream = `${symbol.toLowerCase()}@miniTicker`;
  useBinanceStream<MiniTickerFrame>(stream, (msg) => {
    if (msg.c == null) return;
    const next = Number(msg.c);
    setPrice((current) => {
      setPrev(current);
      return next;
    });
  });

  // Symbol switch: clear stale price so the UI doesn't show another asset's last value while the
  // new stream warms up.
  useEffect(() => {
    setPrice(null);
    setPrev(null);
  }, [symbol]);

  return { price, prev };
}

interface MiniTickerFrame {
  e?: string;
  s?: string;
  c?: string; // close
  o?: string;
  h?: string;
  l?: string;
  v?: string;
  q?: string;
}

/**
 * Rolling 24h ticker via Binance's @ticker stream (one push per second). Replaces the old 10s
 * REST poll — the high/low/volume/pct readouts are now smooth and live.
 */
export function use24hTicker(symbol: string) {
  const [data, setData] = useState<Ticker24h | null>(null);

  const stream = `${symbol.toLowerCase()}@ticker`;
  useBinanceStream<TickerFrame>(stream, (msg) => {
    if (msg.s == null || msg.c == null) return;
    setData({
      symbol: msg.s,
      lastPrice: Number(msg.c),
      priceChange: Number(msg.p ?? 0),
      priceChangePercent: Number(msg.P ?? 0),
      highPrice: Number(msg.h ?? 0),
      lowPrice: Number(msg.l ?? 0),
      volume: Number(msg.v ?? 0),
      quoteVolume: Number(msg.q ?? 0)
    });
  });

  useEffect(() => {
    setData(null);
  }, [symbol]);

  return data;
}

interface TickerFrame {
  e?: string;
  s?: string;
  p?: string; // priceChange
  P?: string; // priceChangePercent
  c?: string; // lastPrice
  h?: string;
  l?: string;
  v?: string;
  q?: string;
}
