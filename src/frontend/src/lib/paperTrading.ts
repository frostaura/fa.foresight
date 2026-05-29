import { useCallback, useEffect, useRef, useState } from "react";

// Server-side paper trading client. The engine now lives on the API (PaperTradingService +
// PaperTradingProcessorService): start/stop go through REST, state changes arrive via SSE, and
// — critically — bets keep placing and settling even when the browser is closed. Nothing in this
// file persists trading state to localStorage anymore; the server is the source of truth.

const apiBase = (import.meta.env.VITE_API_BASE as string | undefined) ?? "/api";

function tenantSlug(): string {
  if (typeof localStorage === "undefined") return "default";
  return localStorage.getItem("fa.tenant") ?? "default";
}

/** A single bet within a paper session. Field names mirror the API's camelCase JSON. */
export interface PaperBet {
  id: string;
  tenantId: string;
  sessionId: string;
  targetOpenTime: number;
  side: "UP" | "DOWN";
  predictedProbUp: number;
  anchorClose: number;
  size: number;
  balanceBefore: number;
  /** ISO timestamp string from .NET DateTimeOffset — accepted directly by `new Date(...)`. */
  placedAt: string;
  resolved: boolean;
  outcome?: "win" | "loss" | null;
  payout?: number | null;
  balanceAfter?: number | null;
  resolvedAt?: string | null;
  actualClose?: number | null;
}

export interface PaperSession {
  id: string;
  tenantId: string;
  symbol: string;
  interval: string;
  startedAt: string;
  stoppedAt?: string | null;
  initialBalance: number;
  initialBetSize: number;
  /** Kebab id of the staking strategy driving this session's bet-size dynamic
   *  (see /api/staking-strategies). Picked at start time, immutable thereafter. */
  strategyId: string;
  /** When true the session skips low-conviction candles (±2pp no-bet band) instead of betting every
   *  candle — the same confidence gate the backtest + chart use. Immutable after start. */
  gated: boolean;
  currentBalance: number;
  currentBetSize: number;
  bust: boolean;
  /** How many times the bankroll crossed zero in either direction during the session. With the
   *  live bust check active this is usually 0 — kept for parity with backtests where bankruptcy
   *  is disabled and the count is the user-visible "wildness" signal. */
  zeroCrossingsCount?: number;
  /** Maximum |negative balance| reached during the session. Live: 0 under bust rules.
   *  Backtest replay can write meaningful values when allowBorrow=true. */
  peakBorrowed?: number;
  lastProcessedAt?: string | null;
  bets: PaperBet[];
}

/**
 * Re-score a paper session's resolved bets against the chart's own Binance candles, then
 * recompute payouts and the cumulative balance trail.
 *
 * Why this exists: the server resolves each bet against its persisted `anchorClose`, which
 * has been observed to drift by $6-$99 from the actual Binance close of the prior candle —
 * see [[feedback-hitmiss-from-chart-candles]]. That drift means the ledger's `outcome` /
 * `payout` / `balanceAfter` / the session's `currentBalance` and the PNL strip can disagree
 * with the chart's PREV chip and accuracy headline on the same bet ("PREV says HIT but the
 * ledger row shows MISS"). Re-scoring against `closeByOpenTime` aligns every paper-trading
 * surface with what the candles on screen actually show.
 *
 * Mechanics — for each resolved bet:
 *   1. Look up `closeByOpenTime[bet.targetOpenTime]` and `closeByOpenTime[bet.targetOpenTime - intervalMs]`.
 *   2. If both exist, derive outcome from `bet.side` vs the strict `>` close-vs-prevClose rule
 *      (matches the candle body color and PREV chip exactly).
 *   3. If either is missing (bet outside the chart window), fall back to the server-recorded
 *      `bet.outcome` — same fallback the chart dots use for off-window predictions.
 *   4. Recompute `payout = +bet.size` for win / `-bet.size` for loss. This is exact under flat
 *      staking; under martingale the placed sizes themselves were a function of the (wrong)
 *      prior outcomes, but the rescored payout still honestly shows what each placed bet
 *      would have netted given the actual market move.
 *   5. Walk the bets chronologically (by `placedAt` ASC) starting from `initialBalance`,
 *      accumulating `payout` to produce the corrected `balanceAfter` per bet and the final
 *      `currentBalance`.
 *
 * Open (unresolved) bets are passed through unchanged.
 */
export function rescorePaperSession(
  session: PaperSession,
  closeByOpenTime: Map<number, number>,
  intervalMs: number
): PaperSession {
  const chronological = [...session.bets].sort(
    (a, b) => new Date(a.placedAt).getTime() - new Date(b.placedAt).getTime()
  );
  let balance = session.initialBalance;
  const rescoredBets: PaperBet[] = [];
  for (const bet of chronological) {
    if (!bet.resolved) {
      rescoredBets.push(bet);
      continue;
    }
    const targetClose = closeByOpenTime.get(bet.targetOpenTime);
    // 2-step canon: the bet resolves on close(target) vs the ANCHOR = close two bars back (the last
    // closed candle at decision time). The intervening candle was still forming when the bet was
    // decided, so it is excluded as the reference. close[T-2] equals the bet's stored anchorClose.
    const anchorClose = closeByOpenTime.get(bet.targetOpenTime - 2 * intervalMs);
    let outcome: "win" | "loss" | null;
    if (targetClose != null && anchorClose != null) {
      const sideUp = bet.side === "UP";
      const liveUp = targetClose > anchorClose;
      outcome = sideUp === liveUp ? "win" : "loss";
    } else {
      outcome = bet.outcome ?? null;
    }
    const payout = outcome === "win" ? bet.size : outcome === "loss" ? -bet.size : null;
    if (payout != null) balance += payout;
    rescoredBets.push({ ...bet, outcome, payout, balanceAfter: balance });
  }
  return { ...session, bets: rescoredBets, currentBalance: balance };
}

// ── Shared SSE stream ──────────────────────────────────────────────────────────────────────────

type Listener = (session: PaperSession) => void;

interface SharedStream {
  refCount: number;
  es: EventSource;
  // Listeners keyed by `${symbol}:${interval}` so a Bet-Placed event routes only to the matching
  // card's subscriber, not every panel on the page.
  listeners: Map<string, Set<Listener>>;
  // Called once per SSE reconnect (not the initial open) so subscribers can re-pull a snapshot to
  // recover any events missed during the disconnect window. No timer — bounded by real reconnects.
  resyncers: Set<() => void>;
}

let shared: SharedStream | null = null;

function bucketKey(symbol: string, interval: string) {
  return `${symbol}:${interval}`;
}

function ensureShared(): SharedStream {
  if (shared) return shared;
  const tenant = tenantSlug();
  const url = `${apiBase}/paper/sessions/stream?tenant=${encodeURIComponent(tenant)}`;
  const listeners = new Map<string, Set<Listener>>();
  const es = new EventSource(url);
  const dispatch = (e: MessageEvent) => {
    try {
      const payload = JSON.parse(e.data) as { session: PaperSession; bet: PaperBet | null };
      const s = payload.session;
      const set = listeners.get(bucketKey(s.symbol, s.interval));
      if (!set) return;
      for (const fn of set) fn(s);
    } catch {
      // Malformed frame — next event retries.
    }
  };
  for (const name of ["session-started", "session-stopped", "session-bust", "bet-placed", "bet-resolved"]) {
    es.addEventListener(name, dispatch as EventListener);
  }
  const stream: SharedStream = { refCount: 0, es, listeners, resyncers: new Set() };
  // The browser auto-reconnects on transient drops. The first open is the initial connect (the
  // hook already pulled its REST snapshot); every later open is a reconnect after a gap, so fan out
  // a one-shot resync to recover events missed while disconnected.
  let hasOpened = false;
  es.onopen = () => {
    if (hasOpened) for (const fn of stream.resyncers) fn();
    hasOpened = true;
  };
  es.onerror = () => {
    if (es.readyState !== EventSource.CLOSED) {
      // readyState=0 means CONNECTING (reconnect in flight); 1 means OPEN (transient blip).
      // eslint-disable-next-line no-console
      console.debug("[paperTrading] SSE drop, browser reconnecting…");
    }
  };
  shared = stream;
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

// ── Hook ───────────────────────────────────────────────────────────────────────────────────────

/**
 * Server-driven paper-trading session for the given (symbol, interval). Subscribes to the shared
 * SSE stream so updates land within milliseconds of the processor settling or placing. `start`
 * and `stop` are thin REST wrappers; the returned session always reflects the server's view.
 */
export function usePaperSession(symbol: string, interval: string) {
  const [session, setSession] = useState<PaperSession | null>(null);
  const [error, setError] = useState<string | null>(null);
  const aliveRef = useRef(true);

  useEffect(() => {
    aliveRef.current = true;
    setSession(null);
    setError(null);
    const tenant = tenantSlug();
    const controller = new AbortController();

    // Initial REST snapshot. The server is the source of truth — never read from localStorage.
    const load = async () => {
      try {
        const res = await fetch(
          `${apiBase}/paper/sessions/${encodeURIComponent(symbol)}/${encodeURIComponent(interval)}`,
          { headers: { "X-Tenant-Slug": tenant }, signal: controller.signal }
        );
        if (!res.ok) throw new Error(`paper GET ${res.status}`);
        // An empty (or null) body = no active session yet; fall back to the empty state gracefully.
        const text = await res.text();
        const s = (text ? JSON.parse(text) : null) as PaperSession | null;
        if (aliveRef.current) setSession(s);
      } catch (e) {
        if ((e as Error).name !== "AbortError") {
          if (aliveRef.current) setError((e as Error).message);
        }
      }
    };
    void load();

    // Live updates via the shared SSE stream.
    const s = ensureShared();
    s.refCount += 1;
    const key = bucketKey(symbol, interval);
    let bucket = s.listeners.get(key);
    if (!bucket) {
      bucket = new Set();
      s.listeners.set(key, bucket);
    }
    const listener: Listener = (incoming) => {
      if (!aliveRef.current) return;
      setSession(incoming);
    };
    bucket.add(listener);

    // Going forward, updates arrive via SSE only. The single fallback is a one-shot re-pull when the
    // SSE reconnects after a drop (mobile tab suspend, network blip, backend restart) — events during
    // that gap aren't replayed on the stream. This is event-driven, so HTTP load stays flat over time
    // rather than growing with a polling timer.
    const resync = () => { if (aliveRef.current) void load(); };
    s.resyncers.add(resync);

    return () => {
      aliveRef.current = false;
      controller.abort();
      s.resyncers.delete(resync);
      bucket!.delete(listener);
      if (bucket!.size === 0) s.listeners.delete(key);
      releaseShared();
    };
  }, [symbol, interval]);

  const start = useCallback(async (initialBalance: number, initialBetSize: number, strategyId: string, gated: boolean) => {
    const tenant = tenantSlug();
    const res = await fetch(`${apiBase}/paper/sessions`, {
      method: "POST",
      headers: { "Content-Type": "application/json", "X-Tenant-Slug": tenant },
      body: JSON.stringify({ symbol, interval, initialBalance, initialBetSize, strategyId, gated })
    });
    if (!res.ok) {
      const detail = await res.json().catch(() => ({}));
      throw new Error((detail as { error?: string }).error ?? `start failed (${res.status})`);
    }
    const s = (await res.json()) as PaperSession;
    setSession(s);
    return s;
  }, [symbol, interval]);

  const stop = useCallback(async () => {
    const tenant = tenantSlug();
    const res = await fetch(
      `${apiBase}/paper/sessions/${encodeURIComponent(symbol)}/${encodeURIComponent(interval)}`,
      { method: "DELETE", headers: { "X-Tenant-Slug": tenant } }
    );
    if (!res.ok && res.status !== 404) {
      throw new Error(`stop failed (${res.status})`);
    }
    setSession(null);
  }, [symbol, interval]);

  return { session, start, stop, error };
}
