/**
 * Trading → Live
 *
 * Drives off the in-memory go-live arm gate (GET /api/golive/status, polled). When disarmed the
 * page surfaces the arm flow (request code → confirm) and a contextual amber "disarmed" cue. When
 * armed it shows an emerald pill + killswitch and focuses the active live sessions, each rendered
 * as a LiveBitcoinChart card with a real-money numbers strip. The new-session form lives inside a
 * collapsible disclosure. No always-on yellow banners — caution appears only while disarmed.
 */
import { useState } from "react";
import {
  ChevronDown,
  CircleDollarSign,
  Info,
  Plus,
  ShieldAlert,
  ShieldCheck,
  Square,
} from "lucide-react";
import PageHeader from "../components/PageHeader";
import InfoTip, { TipBody } from "../components/InfoTip";
import LiveBitcoinChart from "../components/LiveBitcoinChart";
import { type BinanceInterval } from "../lib/binance";
import { Button } from "../components/ui/button";
import { Input } from "../components/ui/input";
import { cn } from "../lib/cn";
import { pnlClass } from "../lib/pnl";

// "flat" → "Flat", "edge_kelly" → "Edge kelly". Mirrors the paper strip's label treatment.
const prettyStrategy = (id: string) =>
  id ? id.replace(/[_-]+/g, " ").replace(/^\w/, (c) => c.toUpperCase()) : id;
import {
  useListSessionsQuery,
  useCreateSessionMutation,
  useStopSessionMutation,
  useListModelsQuery,
  useGetStakingStrategiesQuery,
  useGetGoLiveStatusQuery,
  useRequestGoLiveCodeMutation,
  useConfirmGoLiveMutation,
  useKillswitchMutation,
  type NormalizedSession,
  type CreateSessionRequest,
} from "../store/api";

const SYMBOLS = ["BTCUSDT"];
const INTERVALS = ["5m", "15m", "1m"];

// ── Arm flow ──────────────────────────────────────────────────────────────────
function ArmStatusStrip({ armed }: { armed: boolean }) {
  const [requestCode, { data: codeResp, isLoading: requesting, reset: resetRequest }] =
    useRequestGoLiveCodeMutation();
  const [confirmGoLive, { isLoading: confirming, error: confirmError, reset: resetConfirm }] =
    useConfirmGoLiveMutation();
  const [killswitch, { isLoading: killing }] = useKillswitchMutation();
  const [code, setCode] = useState("");

  const confirmErrMsg =
    confirmError && "data" in confirmError
      ? (confirmError.data as { error?: string })?.error ?? "Confirmation failed"
      : confirmError
        ? "Confirmation failed"
        : null;

  if (armed) {
    return (
      <div className="fa-card px-4 py-3 flex flex-wrap items-center gap-3">
        <span className="inline-flex items-center gap-1.5 rounded-full border border-fa-success/40 bg-fa-success/10 px-2.5 py-1 fa-overline text-fa-success">
          <ShieldCheck className="h-3.5 w-3.5" />
          Armed
        </span>
        <span className="fa-caption text-fa-frost-dim">
          Live trading is set up. Sessions trade automatically each qualifying candle.
        </span>
        <Button
          onClick={async () => {
            await killswitch();
            // Clear the local arm flow so a later re-arm starts from a clean slate.
            setCode("");
            resetRequest();
            resetConfirm();
          }}
          disabled={killing}
          variant="destructive"
          size="sm"
          className="ml-auto"
        >
          <ShieldAlert className="h-3.5 w-3.5" />
          {killing ? "Disarming…" : "Disarm (killswitch)"}
        </Button>
      </div>
    );
  }

  return (
    <div className="fa-card px-4 py-3 space-y-3">
      <div className="flex flex-wrap items-center gap-3">
        <span className="inline-flex items-center gap-1.5 rounded-full border border-fa-warning/40 bg-fa-warning/10 px-2.5 py-1 fa-overline text-fa-warning">
          <ShieldAlert className="h-3.5 w-3.5" />
          Disarmed
        </span>
        <span className="fa-caption text-fa-frost-dim">
          Request a confirmation code, then confirm it to arm live trading.
        </span>
        {!codeResp && (
          <Button
            onClick={() => void requestCode()}
            disabled={requesting}
            variant="primary"
            size="sm"
            className="ml-auto"
          >
            {requesting ? "Requesting…" : "Request code"}
          </Button>
        )}
      </div>

      {codeResp && (
        <div className="flex flex-wrap items-end gap-3 pt-1">
          <div>
            <div className="fa-overline text-fa-frost-dim mb-1">Your code</div>
            <code className="inline-flex items-center rounded-md border border-fa-edge bg-fa-glass-strong px-3 py-1.5 font-mono text-base tracking-[0.3em] text-fa-frost-bright tabular-nums">
              {codeResp.code}
            </code>
          </div>
          <div>
            <div className="fa-overline text-fa-frost-dim mb-1">Confirm code</div>
            <Input
              value={code}
              onChange={(e) => setCode(e.target.value)}
              placeholder="Enter code"
              inputMode="numeric"
              className="w-36 font-mono tracking-[0.2em] text-sm"
            />
          </div>
          <Button
            onClick={async () => {
              try {
                await confirmGoLive({ code: code.trim() }).unwrap();
              } catch {
                /* surfaced inline below */
              }
            }}
            disabled={confirming || code.trim().length === 0}
            variant="primary"
            size="sm"
          >
            {confirming ? "Confirming…" : "Confirm & arm"}
          </Button>
          {confirmErrMsg && (
            <p className="basis-full fa-caption text-fa-danger -mt-1">{confirmErrMsg}</p>
          )}
        </div>
      )}
    </div>
  );
}

// ── New live session form (collapsible) ─────────────────────────────────────────
function CreateSessionForm({ armed, onCreated }: { armed: boolean; onCreated?: () => void }) {
  const { data: models = [] } = useListModelsQuery();
  const { data: strategiesResp } = useGetStakingStrategiesQuery();
  const [createSession, { isLoading, error }] = useCreateSessionMutation();

  const [form, setForm] = useState<CreateSessionRequest>({
    mode: "live",
    strategyId: "flat",
    symbol: SYMBOLS[0],
    interval: "5m",
    initialBalance: 1000,
    initialBetSize: 10,
    gated: true,
  });
  const [selectedModelId, setSelectedModelId] = useState("");

  const strategies = strategiesResp?.strategies ?? [{ id: "flat", name: "Flat", description: "" }];
  const availableModels = models.filter((m) => m.supportsBacktesting);
  const errMsg =
    error && "data" in error
      ? (error.data as { error?: string })?.error ?? "Request failed"
      : error
        ? "Request failed"
        : null;
  // 409 = config-hash dedup (same model+strategy+symbol+interval already running)
  // 422 = disarmed — live trading gate not armed
  const is409 = error && "status" in error && error.status === 409;
  const is422 = error && "status" in error && error.status === 422;

  const submit = async () => {
    if (!selectedModelId) return;
    try {
      await createSession(form).unwrap();
      onCreated?.();
    } catch {
      // error state handled below
    }
  };

  const labelCls = "fa-overline text-fa-frost-dim mb-1.5 block";

  return (
    <div className="space-y-4">
      <div className="grid grid-cols-1 sm:grid-cols-2 gap-4">
        <div>
          <label className={labelCls}>Model</label>
          <select
            value={selectedModelId}
            onChange={(e) => setSelectedModelId(e.target.value)}
            className="fa-input w-full text-sm"
          >
            <option value="">Select model…</option>
            {availableModels.map((m) => (
              <option key={m.id} value={m.id}>{m.name}</option>
            ))}
          </select>
        </div>

        <div>
          <label className={labelCls}>Strategy</label>
          <select
            value={form.strategyId ?? ""}
            onChange={(e) => setForm((f) => ({ ...f, strategyId: e.target.value }))}
            className="fa-input w-full text-sm"
          >
            {strategies.map((s) => (
              <option key={s.id} value={s.id}>{s.name}</option>
            ))}
          </select>
        </div>

        <div>
          <label className={labelCls}>Symbol</label>
          <select
            value={form.symbol}
            onChange={(e) => setForm((f) => ({ ...f, symbol: e.target.value }))}
            className="fa-input w-full text-sm"
          >
            {SYMBOLS.map((s) => (
              <option key={s} value={s}>{s}</option>
            ))}
          </select>
        </div>

        <div>
          <label className={labelCls}>Interval</label>
          <select
            value={form.interval}
            onChange={(e) => setForm((f) => ({ ...f, interval: e.target.value }))}
            className="fa-input w-full text-sm"
          >
            {INTERVALS.map((iv) => (
              <option key={iv} value={iv}>{iv}</option>
            ))}
          </select>
        </div>

        <div>
          <label className={labelCls}>Starting balance ($)</label>
          <Input
            type="number"
            min={1}
            value={form.initialBalance}
            onChange={(e) => setForm((f) => ({ ...f, initialBalance: Number(e.target.value) }))}
            className="text-sm"
          />
        </div>

        <div>
          <label className={labelCls}>Initial bet ($)</label>
          <Input
            type="number"
            min={1}
            max={form.initialBalance}
            value={form.initialBetSize}
            onChange={(e) => setForm((f) => ({ ...f, initialBetSize: Number(e.target.value) }))}
            className="text-sm"
          />
        </div>
      </div>

      <label className="inline-flex items-center gap-2 text-sm text-fa-frost-dim cursor-pointer select-none">
        <input
          type="checkbox"
          checked={form.gated}
          onChange={(e) => setForm((f) => ({ ...f, gated: e.target.checked }))}
          className="rounded border-fa-edge bg-fa-glass"
        />
        Apply confidence gate (skip ±2pp no-bet band)
      </label>

      {is409 && (
        <p className="fa-caption text-fa-warning">
          A session with this configuration is already active. Stop it first or change the configuration.
        </p>
      )}
      {is422 && (
        <p className="fa-caption text-fa-warning">
          Live trading is disarmed. Arm it above before creating a live session.
        </p>
      )}
      {errMsg && !is409 && !is422 && (
        <p className="fa-caption text-fa-danger">{errMsg}</p>
      )}

      <div className="flex items-center gap-3">
        <Button
          onClick={submit}
          disabled={isLoading || !selectedModelId}
          variant="primary"
          size="sm"
        >
          {isLoading ? "Creating…" : "Create session"}
        </Button>
        {!armed && (
          <span className="fa-caption text-fa-frost-dim">Arm live trading first.</span>
        )}
      </div>
    </div>
  );
}

// ── Active live session card — chart + real-money numbers strip ──────────────────
// The strip deliberately mirrors PaperTradingPanel's ActiveSession layout: a single headline row
// (identity · balance · bet size · P&L) and a thin secondary row (hit rate / bets · Stop). The
// only differences vs paper are the amber "Live" identity (real-money cue) and the source data
// (a NormalizedSession rather than a PaperSession).
function LiveNumbers({ session, onStop, stopping }: {
  session: NormalizedSession;
  onStop: () => void;
  stopping: boolean;
}) {
  const pnl = session.currentBalance - session.initialBalance;
  const pnlPct = session.initialBalance > 0 ? (pnl / session.initialBalance) * 100 : 0;
  const hitRate = session.betsPlaced > 0 ? (session.betsWon / session.betsPlaced) * 100 : null;
  const pnlText = `${pnl >= 0 ? "+" : "-"}$${Math.abs(pnl).toFixed(2)}`;

  if (session.bust) {
    return (
      <div className="mt-3 pt-3 border-t border-rose-300/30 flex items-center justify-between gap-x-3 fa-caption tabular-nums">
        <span className="inline-flex items-center gap-1 text-rose-300 uppercase tracking-wider font-semibold">
          <CircleDollarSign className="h-3 w-3" /> Bankrupt
        </span>
        <span className="text-fa-frost-bright">${session.currentBalance.toFixed(2)}</span>
        <span className={pnlClass(pnl)}>{pnlText} ({pnl >= 0 ? "+" : ""}{pnlPct.toFixed(1)}%)</span>
        <button
          onClick={onStop}
          disabled={stopping}
          className="ml-auto inline-flex items-center gap-1 text-fa-frost-dim hover:text-fa-frost-bright transition disabled:opacity-50"
          title="Dismiss this bankrupt session"
        >
          <Square className="h-3 w-3" /> {stopping ? "Stopping…" : "Dismiss"}
        </button>
      </div>
    );
  }

  return (
    <div className="mt-3 pt-3 border-t border-fa-edge/60">
      {/* Row 1 — identity + headline numbers (fans across the full width like the paper strip). */}
      <div className="flex items-center justify-between gap-x-3 fa-caption tabular-nums">
        <span className="inline-flex items-center gap-1 text-amber-300 uppercase tracking-wider">
          <CircleDollarSign className="h-3 w-3" />
          Live · <span className="text-fa-frost normal-case tracking-normal">{prettyStrategy(session.strategyId)}</span>
        </span>
        <span className="text-fa-frost-bright">${session.currentBalance.toFixed(2)}</span>
        <span className="text-fa-frost-dim">${session.currentBetSize.toFixed(2)}</span>
        <span className={pnlClass(pnl)}>
          {pnlText}
          <span className="ml-1 opacity-80">({pnl >= 0 ? "+" : ""}{pnlPct.toFixed(1)}%)</span>
        </span>
      </div>
      {/* Row 2 — hit rate / bets (left) · reserved · Stop (right). */}
      <div className="mt-1.5 flex items-center gap-3 fa-caption tabular-nums text-fa-frost-dim">
        <span>
          {hitRate != null ? `${hitRate.toFixed(0)}% hit` : "— hit"} · {session.betsWon}/{session.betsPlaced} bets
        </span>
        {session.reservedAmount != null && session.reservedAmount > 0 && (
          <span>${session.reservedAmount.toFixed(2)} reserved</span>
        )}
        <button
          onClick={onStop}
          disabled={stopping}
          className="ml-auto inline-flex items-center gap-1 hover:text-rose-300 transition disabled:opacity-50"
          title="Stop this live session"
        >
          <Square className="h-3 w-3" /> {stopping ? "Stopping…" : "Stop"}
        </button>
      </div>
    </div>
  );
}

function LiveSessionCard({ session }: { session: NormalizedSession }) {
  const [stopSession, { isLoading: stopping }] = useStopSessionMutation();
  // The amber ring (fa-live-accent) is the real-money cue — no overlay pill, so the chart's own
  // top-right controls (fullscreen/expand) stay clear. rounded-xl + overflow-hidden keep the ring
  // flush to the chart's own rounded card edge.
  return (
    <div className="fa-live-accent rounded-xl overflow-hidden">
      <LiveBitcoinChart
        symbol={session.symbol}
        interval={session.interval as BinanceInterval}
        kind="candle"
        hidePaperPanel
      />
      <div className="px-4 pb-4 -mt-1">
        <LiveNumbers
          session={session}
          stopping={stopping}
          onStop={() => { void stopSession(session.id); }}
        />
      </div>
    </div>
  );
}

export default function Live() {
  const { data: status } = useGetGoLiveStatusQuery(undefined, { pollingInterval: 5000 });
  const armed = status?.armed ?? false;

  const {
    data: allSessions,
    isLoading,
    isError,
  } = useListSessionsQuery({ kind: "live", active: true }, { pollingInterval: 4000 });

  const liveSessions = (allSessions ?? []).filter((s) => !s.stoppedAt);
  const hasSessions = liveSessions.length > 0;

  // Collapsed by default when sessions exist; expanded when there are none to set up the first.
  // `null` = follow the default (derived from hasSessions); a boolean = an explicit user override
  // that wins until the user toggles again. This keeps the default reactive to session count
  // without a setState-in-effect cascade.
  const [formOverride, setFormOverride] = useState<boolean | null>(null);
  const formOpen = formOverride ?? !hasSessions;
  const toggleForm = () => setFormOverride(!formOpen);

  return (
    <div>
      <div className="sticky top-0 z-30 bg-fa-ink/95 backdrop-blur">
        <PageHeader
          title={
            <span className="inline-flex items-center gap-2">
              Live
              <InfoTip
                content={
                  <TipBody title="Set up live trading">
                    <ol className="mt-1 space-y-1.5 list-decimal pl-4 text-fa-frost-dim">
                      <li>
                        In the backend <code className="font-mono text-fa-frost">.env</code>, set{" "}
                        <code className="font-mono text-fa-frost">Polymarket__LiveTrading=true</code> and configure the trading wallet key.
                      </li>
                      <li>
                        Arm the session: request a confirmation code, then confirm it (below). Arming is in-memory; it resets if the backend restarts.
                      </li>
                      <li>
                        Place a supervised <span className="text-fa-frost-bright">$1 validation order</span> and confirm it fills and settles on Polymarket before trusting automation.
                      </li>
                      <li>
                        Create a live session below — it trades automatically each qualifying candle while armed.
                      </li>
                      <li>
                        Use the killswitch (or stop the session) to halt anytime.
                      </li>
                    </ol>
                  </TipBody>
                }
                width={320}
              >
                <button
                  aria-label="How to set up live trading"
                  className="inline-flex items-center justify-center rounded-full border border-fa-edge text-fa-frost-dim hover:text-fa-frost-bright hover:border-fa-frost/40 transition h-5 w-5"
                >
                  <Info className="h-3 w-3" />
                </button>
              </InfoTip>
            </span>
          }
          subtitle="Automated live-trading sessions against Polymarket CLOB V2."
        />
      </div>

      <div className="p-4 sm:p-8 space-y-6">
        {/* Arm status + flow */}
        <ArmStatusStrip armed={armed} />

        {/* New live session — collapsible */}
        <div className="fa-card overflow-hidden">
          <button
            onClick={toggleForm}
            className="w-full flex items-center justify-between px-4 py-3 text-left transition hover:bg-fa-glass-strong"
            aria-expanded={formOpen}
          >
            <span className="fa-section-title inline-flex items-center gap-2">
              <Plus className="h-4 w-4" />
              New live session
            </span>
            <ChevronDown
              className={cn(
                "h-4 w-4 text-fa-frost-dim transition-transform",
                formOpen && "rotate-180"
              )}
            />
          </button>
          {formOpen && (
            <div className="px-4 pb-4 border-t border-fa-edge/60 pt-4">
              <CreateSessionForm armed={armed} onCreated={() => setFormOverride(false)} />
            </div>
          )}
        </div>

        {/* Active sessions — the focus */}
        <div>
          <h2 className="fa-overline text-fa-frost-dim mb-3">Active sessions</h2>
          {isLoading && (
            <p className="text-sm text-fa-frost-dim">Loading sessions…</p>
          )}
          {isError && (
            <p className="text-sm text-fa-frost-dim">
              Sessions could not be loaded. Verify the backend is running and try again.
            </p>
          )}
          {!isLoading && !isError && !hasSessions && (
            <div className="fa-card px-6 py-12 text-center">
              <p className="text-fa-frost text-sm">No active live sessions.</p>
              <p className="text-fa-frost-dim text-xs mt-1.5">
                {armed
                  ? "Open “New live session” above to start trading automatically."
                  : "Arm live trading above, then create a session to start trading automatically."}
              </p>
            </div>
          )}
          {hasSessions && (
            <div className="grid grid-cols-1 lg:grid-cols-2 2xl:grid-cols-3 gap-4">
              {liveSessions.map((s) => (
                <LiveSessionCard key={s.id} session={s} />
              ))}
            </div>
          )}
        </div>
      </div>
    </div>
  );
}
