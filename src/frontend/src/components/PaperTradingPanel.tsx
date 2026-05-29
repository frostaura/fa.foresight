import { useEffect, useMemo, useRef, useState } from "react";
import { createPortal } from "react-dom";
import ShimmerOnChange from "./ShimmerOnChange";
import InfoTip, { TipBody } from "./InfoTip";
import { Tooltip } from "./ui";
import { CircleDollarSign, Info, Play, ScrollText, Square } from "lucide-react";
import {
  usePaperSession,
  rescorePaperSession,
  type PaperBet,
  type PaperSession
} from "../lib/paperTrading";
import { pnlClass } from "../lib/pnl";
import { useSort, SortHeader } from "../lib/sort";
import { useGetStakingStrategiesQuery } from "../store/api";
import { cn } from "../lib/cn";

interface PaperTradingPanelProps {
  symbol: string;
  interval: string;
  /**
   * Chart's authoritative close-by-openTime map for this card. When supplied, the panel
   * rescores resolved bets against these candles instead of trusting the server-recorded
   * `bet.outcome` (which is computed off the drifty `anchorClose`). Keeps the ledger,
   * P&L strip, and balance number aligned with the chart's PREV chip and accuracy
   * headline — see `rescorePaperSession` in lib/paperTrading.ts.
   */
  closeByOpenTime?: Map<number, number>;
  intervalMs?: number;
}

/**
 * Thin client over the server-side paper-trading engine. Start/stop go through REST; live state
 * arrives via the shared SSE stream in `lib/paperTrading.ts`. Trading runs server-side regardless
 * of whether this panel is mounted — closing the tab does not stop the session.
 */
export default function PaperTradingPanel({ symbol, interval, closeByOpenTime, intervalMs }: PaperTradingPanelProps) {
  const { session, start, stop, error } = usePaperSession(symbol, interval);
  const displaySession = useMemo(() => {
    if (!session || !closeByOpenTime || intervalMs == null) return session;
    return rescorePaperSession(session, closeByOpenTime, intervalMs);
  }, [session, closeByOpenTime, intervalMs]);
  const { data: strategiesResp } = useGetStakingStrategiesQuery();
  const [showStart, setShowStart] = useState(false);
  const [balance, setBalance] = useState(1000);
  const [initialBet, setInitialBet] = useState(10);
  // Default strategy comes from the API catalogue (`flat` today), so when a new strategy ships and
  // the backend declares it the default, this picker follows along without a frontend change.
  const [strategyId, setStrategyId] = useState<string>("flat");
  const strategies = strategiesResp?.strategies ?? [];
  const defaultStrategyId = strategiesResp?.default ?? "flat";
  // Once the catalogue arrives, swap to the declared default if our current selection isn't in the
  // catalogue (handles renames / removals server-side). Side-effect in useEffect to avoid the
  // setState-in-render-body trap.
  useEffect(() => {
    if (strategies.length > 0 && !strategies.some((s) => s.id === strategyId)) {
      setStrategyId(defaultStrategyId);
    }
  }, [strategies, strategyId, defaultStrategyId]);
  const [starting, setStarting] = useState(false);
  // Confidence gate: when on, the session skips low-conviction candles (±2pp no-bet band) instead
  // of betting every candle — same equation as the backtest gate. Lets the user A/B "always bet"
  // vs "sit out the coin-flips" on the live engine. Off by default = always-bet baseline.
  const [gated, setGated] = useState(false);

  if (!displaySession) {
    return (
      <div className="mt-3 pt-3 border-t border-fa-edge/60">
        {showStart ? (
          <StartDialog
            balance={balance}
            initialBet={initialBet}
            strategyId={strategyId}
            strategies={strategies}
            gated={gated}
            starting={starting}
            error={error}
            onBalance={setBalance}
            onInitialBet={setInitialBet}
            onStrategy={setStrategyId}
            onGated={setGated}
            onCancel={() => setShowStart(false)}
            onStart={async () => {
              setStarting(true);
              try {
                await start(balance, initialBet, strategyId, gated);
                setShowStart(false);
              } catch {
                // error surfaced via the `error` state in usePaperSession's next render
              } finally {
                setStarting(false);
              }
            }}
          />
        ) : (
          <div className="flex items-center justify-between gap-3">
            <span className="text-fa-frost-dim fa-overline inline-flex items-center gap-1.5">
              <CircleDollarSign className="h-3 w-3" />
              Paper trading
            </span>
            <InfoTip
              content={
                <TipBody title="Start paper trading">
                  Begins a server-side session that places one flat virtual bet per candle and tracks
                  the running balance. It keeps trading even if you close this tab, so you can compare
                  the live result against the model's hit-rate.
                </TipBody>
              }
            >
              <button
                onClick={() => setShowStart(true)}
                className="inline-flex items-center gap-1.5 rounded-md border border-fa-edge bg-fa-glass-strong hover:border-fa-frost/40 hover:bg-fa-frost/10 hover:text-fa-frost-bright text-fa-frost text-xs px-2.5 py-1 transition"
              >
                <Play className="h-3 w-3" />
                Start
              </button>
            </InfoTip>
          </div>
        )}
      </div>
    );
  }

  return <ActiveSession session={displaySession} onStop={() => { void stop(); }} streamError={error} />;
}

function StartDialog({
  balance, initialBet, strategyId, strategies, gated, starting, error,
  onBalance, onInitialBet, onStrategy, onGated, onStart, onCancel
}: {
  balance: number;
  initialBet: number;
  strategyId: string;
  strategies: { id: string; name: string; description: string }[];
  gated: boolean;
  starting: boolean;
  error: string | null;
  onBalance: (v: number) => void;
  onInitialBet: (v: number) => void;
  onStrategy: (id: string) => void;
  onGated: (v: boolean) => void;
  onStart: () => void;
  onCancel: () => void;
}) {
  return (
    <div className="flex flex-wrap items-end gap-3">
      <div>
        <div className="text-fa-frost-dim fa-overline mb-1">Balance ($)</div>
        <input
          type="number" min={1} step={1} value={balance}
          onChange={(e) => onBalance(Number(e.target.value) || 0)}
          className="bg-fa-glass border border-fa-edge rounded px-2 py-1 text-sm w-28 tabular-nums text-fa-frost-bright"
        />
      </div>
      <div>
        <div className="text-fa-frost-dim fa-overline mb-1">Initial bet ($)</div>
        <input
          type="number" min={1} step={1} value={initialBet}
          onChange={(e) => onInitialBet(Number(e.target.value) || 0)}
          className="bg-fa-glass border border-fa-edge rounded px-2 py-1 text-sm w-24 tabular-nums text-fa-frost-bright"
        />
      </div>
      <div>
        <div className="text-fa-frost-dim fa-overline mb-1">Strategy</div>
        {/* Pill selector mirroring the Backtesting tab so the two surfaces stay visually
            consistent. Tooltip on each pill carries the strategy's catalogue description. */}
        <div className="flex flex-wrap gap-1.5">
          {strategies.map((s) => (
            <Tooltip key={s.id} content={s.description}>
              <button
                type="button"
                onClick={() => onStrategy(s.id)}
                className={cn(
                  "inline-flex items-center gap-1.5 text-xs px-2.5 py-1 rounded border transition",
                  strategyId === s.id
                    ? "border-fa-frost/50 bg-fa-frost/10 text-fa-frost-bright"
                    : "border-fa-edge text-fa-frost-dim hover:text-fa-frost-bright hover:border-fa-frost/30"
                )}
              >
                <span className={cn(
                  "inline-block h-1.5 w-1.5 rounded-full",
                  strategyId === s.id ? "bg-fa-frost-bright" : "bg-fa-frost-dim/40"
                )} />
                {s.name}
              </button>
            </Tooltip>
          ))}
        </div>
      </div>
      <div>
        <div className="text-fa-frost-dim fa-overline mb-1">Confidence gate</div>
        <Tooltip content="On: skip low-conviction candles (pUp within 48–52%) instead of betting every candle — the same gate the backtest + chart use. Off: bet every candle (baseline). Lets you compare whether sitting out the coin-flips improves live P&L.">
          <button
            type="button"
            onClick={() => onGated(!gated)}
            aria-pressed={gated}
            className={cn(
              "inline-flex items-center gap-1.5 text-xs px-2.5 py-1 rounded border transition",
              gated
                ? "border-fa-frost/50 bg-fa-frost/10 text-fa-frost-bright"
                : "border-fa-edge text-fa-frost-dim hover:text-fa-frost-bright hover:border-fa-frost/30"
            )}
          >
            <span className={cn("inline-block h-1.5 w-1.5 rounded-full", gated ? "bg-fa-frost-bright" : "bg-fa-frost-dim/40")} />
            {gated ? "Gated (skip no-bets)" : "Always bet"}
          </button>
        </Tooltip>
      </div>
      <div className="flex gap-1.5">
        <button
          onClick={onStart}
          disabled={starting || balance <= 0 || initialBet <= 0 || initialBet > balance || !strategyId}
          className="inline-flex items-center gap-1.5 text-xs px-3 py-1.5 rounded border border-emerald-300/50 text-emerald-300 hover:bg-emerald-300/10 disabled:opacity-40 disabled:cursor-not-allowed transition"
        >
          <Play className="h-3 w-3" />
          {starting ? "Starting…" : "Start"}
        </button>
        <button
          onClick={onCancel}
          className="text-xs px-3 py-1.5 rounded text-fa-frost-dim hover:text-fa-frost-bright transition"
        >
          Cancel
        </button>
      </div>
      {error && (
        <div className="basis-full text-rose-300 fa-caption -mt-1">{error}</div>
      )}
    </div>
  );
}

/**
 * Title-case a kebab id ("anti-martingale" → "Anti Martingale"). Used to render the strategy
 * label on the active session strip without an extra round-trip to the catalogue — every current
 * strategy id ("flat", "martingale") survives this transform unchanged-looking.
 */
function prettifyStrategyId(id: string): string {
  return id
    .split("-")
    .map((p) => p.length === 0 ? p : p[0].toUpperCase() + p.slice(1))
    .join(" ");
}

function ActiveSession({
  session,
  onStop,
  streamError
}: {
  session: PaperSession;
  onStop: () => void;
  streamError: string | null;
}) {
  const pnl = session.currentBalance - session.initialBalance;
  const pnlPct = session.initialBalance > 0 ? (pnl / session.initialBalance) * 100 : 0;
  const pnlUp = pnl >= 0;

  // The currently in-flight bet — strict 1-at-a-time on the server side guarantees at most one.
  const openBet = useMemo(
    () => session.bets.find((b) => !b.resolved),
    [session.bets]
  );

  // W/L counts and hit-rate are derivable from the bet ledger and visible inside the hover popover;
  // surfacing them on the strip duplicated information and crowded the row, so we keep them in the
  // ledger drawer only.

  // Bankrupt session: lock further bets, finalise the row with a clear Bankrupt banner. The
  // ledger button still works so the user can review the history; Dismiss clears and resets.
  if (session.bust) {
    return (
      <div className="mt-3 pt-3 border-t border-rose-300/30">
        <div className="flex items-center justify-between gap-x-3 fa-caption tabular-nums">
          <span className="text-rose-300 uppercase tracking-wider inline-flex items-center gap-1 font-semibold">
            <CircleDollarSign className="h-3 w-3" />
            Bankrupt · <span className="text-rose-300/70 normal-case tracking-normal font-normal">{prettifyStrategyId(session.strategyId)}</span>
          </span>
          <span className="text-fa-frost-bright">${session.currentBalance.toFixed(2)}</span>
          <PnLDisplay pnl={pnl} pnlPct={pnlPct} pnlUp={false} />
        </div>
        <div className="mt-1.5 flex items-center gap-3 fa-caption tabular-nums">
          <div className="ml-auto flex items-center gap-3 shrink-0">
            <LedgerButton session={session} />
            <button
              onClick={onStop}
              className="inline-flex items-center gap-1 text-fa-frost-dim hover:text-fa-frost-bright transition"
              title="Dismiss this bankrupt session and start fresh"
            >
              <Square className="h-3 w-3" />
              Dismiss
            </button>
          </div>
        </div>
      </div>
    );
  }

  return (
    <div className="mt-3 pt-3 border-t border-fa-edge/60">
      {/* Row 1 — data only. Fans across the full content width via justify-between so the leftmost
          item hugs the card edge and the rightmost item ends at the right edge — aligning with the
          Stop button on row 2 below. */}
      <div className="flex items-center justify-between gap-x-3 fa-caption tabular-nums">
        <span className="text-fa-frost-dim uppercase tracking-wider inline-flex items-center gap-1">
          <CircleDollarSign className="h-3 w-3 fa-paper-pulse" aria-label="Paper trading is active" />
          Paper · <span className="text-fa-frost normal-case tracking-normal">{prettifyStrategyId(session.strategyId)}</span>
          {streamError && (
            <span
              className="ml-1.5 text-amber-300 normal-case tracking-normal"
              title={`Live stream interrupted: ${streamError}. Falling back to 30s REST refresh; updates may lag.`}
            >
              ⚠ reconnecting
            </span>
          )}
        </span>
        <ShimmerOnChange value={session.currentBalance}>
          <span className="text-fa-frost-bright">${session.currentBalance.toFixed(2)}</span>
        </ShimmerOnChange>
        <ShimmerOnChange value={session.currentBetSize}>
          <span className="text-fa-frost-dim">
            ${session.currentBetSize.toFixed(2)}
            {session.currentBetSize > session.initialBetSize && (
              <Tooltip content="Martingale escalation: bet size doubles after each loss until the next win resets it back to the initial stake.">
                <span className="text-amber-300 ml-1 cursor-help">
                  ×{Math.round(session.currentBetSize / session.initialBetSize)}
                </span>
              </Tooltip>
            )}
          </span>
        </ShimmerOnChange>
        <PnLDisplay pnl={pnl} pnlPct={pnlPct} pnlUp={pnlUp} />
      </div>

      {/* Row 2 — open-bet status (left, optional) and the action buttons (right, always present).
          Buttons share this row with the betting text per design; when no bet is live, the left
          side is empty and the buttons sit alone at the right. */}
      <div className="mt-1.5 flex items-center gap-3 fa-caption tabular-nums">
        {openBet && (
          <span className="text-amber-300">
            betting ${openBet.size.toFixed(2)} on price {openBet.side === "UP" ? "↑" : "↓"}
          </span>
        )}
        {(session.zeroCrossingsCount ?? 0) > 0 && (
          <Tooltip content="Balance passed through zero this many times in either direction during this session. Counts every sign change — going positive → negative and back counts twice.">
            <span className="text-fa-frost-dim cursor-help inline-flex items-center gap-1">
              <span className="tabular-nums text-fa-frost-bright">{session.zeroCrossingsCount}×</span>
              crossings
            </span>
          </Tooltip>
        )}
        <div className="ml-auto flex items-center gap-3 shrink-0">
          <LedgerButton session={session} />
          <button
            onClick={onStop}
            className="inline-flex items-center gap-1 text-fa-frost-dim hover:text-rose-300 transition"
            title="Stop and clear this paper-trading session"
          >
            <Square className="h-3 w-3" />
            Stop
          </button>
        </div>
      </div>
    </div>
  );
}

function PnLDisplay({ pnl, pnlPct }: { pnl: number; pnlPct: number; pnlUp: boolean }) {
  return (
    <span className="inline-flex items-baseline gap-1.5">
      <Tooltip content="Profit and loss for this paper-trading session. Calculated as current balance minus the initial balance you started with. Only settled bets are reflected; open bets are still in flight.">
        <span className="text-fa-frost-dim uppercase tracking-wider inline-flex items-center gap-1 cursor-help">
          PnL
          <Info className="h-3 w-3 opacity-70" />
        </span>
      </Tooltip>
      <ShimmerOnChange value={pnl}>
        <span className={pnlClass(pnl)}>
          {pnl >= 0 ? "+" : "-"}${Math.abs(pnl).toFixed(2)} ({pnl >= 0 ? "+" : ""}{pnlPct.toFixed(1)}%)
        </span>
      </ShimmerOnChange>
    </span>
  );
}

function LedgerButton({ session }: { session: PaperSession }) {
  const [open, setOpen] = useState(false);
  const [pos, setPos] = useState<{ top: number; left: number } | null>(null);
  const ref = useRef<HTMLButtonElement>(null);
  // Close-timer ref. When the cursor leaves either the button or the popover we schedule a
  // close; entering the *other* element cancels it. This bridges the 4px visual gap between
  // the button and the popover that otherwise made the popover unreachable with the mouse.
  const closeTimer = useRef<number | null>(null);
  // Show the full history — overflow handled by the scrollable container below.
  // Server returns `session.bets` via EF `.Include(s => s.Bets)` with no `OrderBy`, so the array
  // order from the API isn't guaranteed to be chronological. A simple `.reverse()` could put the
  // open (newest-placed) bet anywhere in the list depending on EF's row return order. Sort by
  // placedAt DESC explicitly so the active bet sits at the top, followed by the most-recent
  // resolved bets — matches what the user expects from a "newest activity first" ledger.
  const recentBets = useMemo(
    () => [...session.bets].sort(
      (a, b) => new Date(b.placedAt).getTime() - new Date(a.placedAt).getTime()
    ),
    [session.bets]
  );
  type LedgerKey = "placed" | "side" | "size" | "payout" | "balance" | "outcome";
  const { sortedRows, headerProps } = useSort<PaperBet, LedgerKey>(recentBets, {
    placed:  (b) => new Date(b.placedAt),
    side:    (b) => b.side,
    size:    (b) => b.size,
    payout:  (b) => b.payout ?? null,
    balance: (b) => b.balanceAfter ?? null,
    outcome: (b) => b.outcome ?? null,
  });

  // Position the popover relative to the viewport using getBoundingClientRect. Rendered via a
  // portal at document.body so it overlays sibling cards regardless of the grid's stacking
  // context. When the right edge would overflow the viewport (right-column cards), flip the
  // anchor so the popover aligns to the trigger's right edge instead of its left.
  const showPopover = () => {
    if (closeTimer.current != null) {
      window.clearTimeout(closeTimer.current);
      closeTimer.current = null;
    }
    const rect = ref.current?.getBoundingClientRect();
    if (rect) {
      const ledgerWidth = Math.min(460, window.innerWidth * 0.9);
      const margin = 8;
      let left = rect.left;
      if (left + ledgerWidth > window.innerWidth - margin) {
        // Flip to align the popover's right edge with the trigger's right edge.
        left = Math.max(margin, rect.right - ledgerWidth);
      }
      setPos({ top: rect.bottom + 4, left });
    }
    setOpen(true);
  };

  const scheduleClose = () => {
    if (closeTimer.current != null) window.clearTimeout(closeTimer.current);
    closeTimer.current = window.setTimeout(() => {
      setOpen(false);
      closeTimer.current = null;
    }, 180);
  };

  return (
    <button
      ref={ref}
      type="button"
      onMouseEnter={showPopover}
      onMouseLeave={scheduleClose}
      onFocus={showPopover}
      onBlur={scheduleClose}
      onClick={() => (open ? setOpen(false) : showPopover())}
      className="inline-flex items-center gap-1 text-fa-frost-dim hover:text-fa-frost-bright transition"
      title="View bet ledger"
      aria-expanded={open}
    >
      <ScrollText className="h-3 w-3" />
      Ledger
      {open && pos && createPortal(
        <div
          style={{ position: "fixed", top: pos.top, left: pos.left, zIndex: 9999 }}
          className="w-[min(460px,90vw)] rounded-md border border-fa-edge bg-fa-ink/95 backdrop-blur p-3 shadow-2xl fa-caption tabular-nums"
          onMouseEnter={showPopover}
          onMouseLeave={scheduleClose}
        >
          <div className="text-fa-frost-dim fa-overline mb-2">
            Bet ledger · {session.bets.length} total
          </div>
          {recentBets.length === 0 ? (
            <div className="text-fa-frost-dim italic">No bets placed yet.</div>
          ) : (
            <div className="max-h-[280px] overflow-y-auto">
              <table className="fa-table-bordered w-full">
                <thead className="text-fa-frost-dim sticky top-0 bg-fa-ink/95 backdrop-blur">
                  <tr className="text-left">
                    <th className="font-normal py-0.5 pr-3"><SortHeader<LedgerKey> {...headerProps("placed")}>Placed</SortHeader></th>
                    <th className="font-normal py-0.5 pr-3"><SortHeader<LedgerKey> {...headerProps("side")}>Side</SortHeader></th>
                    <th className="font-normal py-0.5 pr-3 text-right"><SortHeader<LedgerKey> {...headerProps("size")} align="right">Size</SortHeader></th>
                    <th className="font-normal py-0.5 pr-3 text-right"><SortHeader<LedgerKey> {...headerProps("payout")} align="right">P&amp;L</SortHeader></th>
                    <th className="font-normal py-0.5 pr-3 text-right"><SortHeader<LedgerKey> {...headerProps("balance")} align="right">Balance</SortHeader></th>
                    <th className="font-normal py-0.5 text-center"><SortHeader<LedgerKey> {...headerProps("outcome")}>Result</SortHeader></th>
                  </tr>
                </thead>
                <tbody>
                  {sortedRows.map((b) => (
                    <LedgerRow key={b.id} bet={b} />
                  ))}
                </tbody>
              </table>
            </div>
          )}
        </div>,
        document.body
      )}
    </button>
  );
}

function LedgerRow({ bet }: { bet: PaperBet }) {
  const sideColor = bet.side === "UP" ? "text-emerald-300" : "text-rose-300";
  // Server-side bets resolve as "win" or "loss" only; bust is a session-level flag, not a per-bet
  // outcome (the doomed bet is never placed — see PaperTradingService escalation guard).
  const dotClass =
    bet.outcome === "win"  ? "bg-emerald-400 shadow-[0_0_6px_rgba(52,211,153,0.6)]" :
    bet.outcome === "loss" ? "bg-rose-400 shadow-[0_0_6px_rgba(251,113,133,0.6)]" :
                             "bg-fa-frost-dim/50 animate-pulse";
  const dotTitle = bet.outcome ?? "open";
  const placedAt = new Date(bet.placedAt);
  const placedStr = placedAt.toLocaleTimeString(undefined, { hour: "2-digit", minute: "2-digit", second: "2-digit", hourCycle: "h23" });
  return (
    <tr className="border-t border-fa-edge/40">
      <td className="py-0.5 pr-3 text-fa-frost-dim" title={placedAt.toLocaleString()}>{placedStr}</td>
      <td className={`py-0.5 pr-3 ${sideColor}`}>{bet.side === "UP" ? "↑ UP" : "↓ DOWN"}</td>
      <td className="py-0.5 pr-3 text-right">${bet.size.toFixed(2)}</td>
      <td className={`py-0.5 pr-3 text-right ${bet.payout == null ? "text-fa-frost-dim" : pnlClass(bet.payout)}`}>
        {bet.payout == null ? "—" : `${bet.payout >= 0 ? "+" : "-"}$${Math.abs(bet.payout).toFixed(2)}`}
      </td>
      <td className="py-0.5 pr-3 text-right">
        {bet.balanceAfter != null
          ? `$${bet.balanceAfter.toFixed(2)}`
          : <span className="text-fa-frost-dim">—</span>}
      </td>
      <td className="py-0.5 text-center">
        <span
          className={`inline-block h-2 w-2 rounded-full ${dotClass}`}
          title={dotTitle}
          aria-label={dotTitle}
        />
      </td>
    </tr>
  );
}
