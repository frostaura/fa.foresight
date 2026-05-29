/**
 * Trading → Live
 *
 * Drives off the in-memory go-live arm gate (GET /api/golive/status, polled). When disarmed the
 * page surfaces the arm flow (request code → confirm) and a contextual amber "disarmed" cue. When
 * armed it shows an emerald pill + killswitch and focuses the active live sessions, each rendered
 * as a LiveBitcoinChart card with a real-money numbers strip. The new-session form lives inside a
 * collapsible disclosure. No always-on yellow banners — caution appears only while disarmed.
 */
import { useState, type ReactNode } from "react";
import { createPortal } from "react-dom";
import {
  ChevronDown,
  CircleDollarSign,
  Info,
  Plug,
  Plus,
  ShieldAlert,
  ShieldCheck,
  Square,
  X,
} from "lucide-react";
import PageHeader from "../components/PageHeader";
import LiveBitcoinChart from "../components/LiveBitcoinChart";
import { ResizableChartGrid, ResizableChartCard } from "../components/ResizableChartGrid";
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
  useGetPlatformConnectionQuery,
  useUpdatePlatformConnectionMutation,
  type NormalizedSession,
  type CreateSessionRequest,
  type PlatformConnection,
  type UpdatePlatformConnectionRequest,
} from "../store/api";

const SYMBOLS = ["BTCUSDT"];
const INTERVALS = ["5m", "15m", "1m"];

// Polymarket signature-type enum → human label. 0 = EOA, 1 = POLY_PROXY, 2 = POLY_GNOSIS_SAFE.
const SIGNATURE_TYPES: { value: number; label: string }[] = [
  { value: 0, label: "EOA" },
  { value: 1, label: "POLY_PROXY" },
  { value: 2, label: "POLY_GNOSIS_SAFE" },
];
const sigTypeLabel = (v: number) => SIGNATURE_TYPES.find((s) => s.value === v)?.label ?? String(v);

// "Configured · 0x…last4" when a wallet key is set, else "Not configured".
const walletStatusText = (conn: Pick<PlatformConnection, "hasPrivateKey" | "walletAddress">) => {
  if (!conn.hasPrivateKey) return "Not configured";
  const addr = conn.walletAddress;
  const tail = addr && addr.length >= 4 ? `0x…${addr.slice(-4)}` : "address derived";
  return `Configured · ${tail}`;
};

// ── Arm flow ──────────────────────────────────────────────────────────────────
function ArmStatusStrip({ armed, onShowSetup }: { armed: boolean; onShowSetup: () => void }) {
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
        <button onClick={onShowSetup} className="ml-auto fa-caption text-fa-frost-dim hover:text-fa-frost-bright transition inline-flex items-center gap-1">
          <Info className="h-3 w-3" /> Setup guide
        </button>
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
        <button onClick={onShowSetup} className="ml-auto fa-caption text-fa-frost-dim hover:text-fa-frost-bright transition inline-flex items-center gap-1">
          <Info className="h-3 w-3" /> Setup guide
        </button>
        {!codeResp && (
          <Button
            onClick={() => void requestCode()}
            disabled={requesting}
            variant="primary"
            size="sm"
          >
            {requesting ? "Requesting…" : "Request code"}
          </Button>
        )}
      </div>

      {codeResp && (
        <div className="flex flex-wrap items-end gap-3 pt-1">
          <div>
            <div className="fa-overline text-fa-frost-dim mb-1">Your code</div>
            <code className="flex h-9 items-center rounded-md border border-fa-edge bg-fa-glass-strong px-3 font-mono text-base tracking-[0.3em] text-fa-frost-bright tabular-nums">
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
              className="h-9 w-36 font-mono tracking-[0.2em] text-sm"
            />
          </div>
          {/* Invisible spacer label keeps the button on the same label+control baseline as the
              two fields beside it; h-9 matches their control height so all three align. */}
          <div>
            <div className="fa-overline mb-1 select-none opacity-0" aria-hidden="true">arm</div>
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
              className="h-9"
            >
              {confirming ? "Confirming…" : "Confirm & arm"}
            </Button>
          </div>
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
      <div className="mt-3 pt-3 border-t border-rose-300/30 flex items-center justify-between gap-x-3 pr-5 fa-caption tabular-nums">
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
      {/* Row 2 — hit rate / bets (left) · reserved · Stop (right). pr-5 keeps the Stop button clear
          of the card's bottom-right resize grip. */}
      <div className="mt-1.5 flex items-center gap-3 pr-5 fa-caption tabular-nums text-fa-frost-dim">
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
    <div className="fa-live-accent rounded-xl overflow-hidden h-full flex flex-col">
      <div className="flex-1 min-h-0">
        <LiveBitcoinChart
          symbol={session.symbol}
          interval={session.interval as BinanceInterval}
          kind="candle"
          hidePaperPanel
          fill
        />
      </div>
      <div className="px-4 pb-4 -mt-1 shrink-0">
        <LiveNumbers
          session={session}
          stopping={stopping}
          onStop={() => { void stopSession(session.id); }}
        />
      </div>
    </div>
  );
}

// ── Connection summary card ───────────────────────────────────────────────────
// Shows the active Polymarket connection at a glance (wallet status, live-trading flag, endpoints)
// with an "Edit connection" button. The connection now lives in the DB per tenant and is edited
// in-app via the dialog below — env is only a one-time bootstrap default.
function ConnectionCard({ onEdit }: { onEdit: () => void }) {
  const { data: conn, isLoading, isError } = useGetPlatformConnectionQuery();

  return (
    <div className="fa-card px-4 py-3 space-y-3">
      <div className="flex flex-wrap items-center gap-3">
        <span className="inline-flex items-center gap-1.5 fa-section-title">
          <Plug className="h-4 w-4 text-fa-frost-dim" />
          Connection
        </span>
        {conn && (
          <span
            className={cn(
              "inline-flex items-center gap-1.5 rounded-full border px-2.5 py-1 fa-overline",
              conn.liveTrading
                ? "border-fa-warning/40 bg-fa-warning/10 text-fa-warning"
                : "border-fa-edge bg-fa-glass text-fa-frost-dim"
            )}
          >
            Live trading {conn.liveTrading ? "on" : "off"}
          </span>
        )}
        <Button onClick={onEdit} variant="outline" size="sm" className="ml-auto">
          Edit connection
        </Button>
      </div>

      {isLoading && <p className="fa-caption text-fa-frost-dim">Loading connection…</p>}
      {isError && (
        <p className="fa-caption text-fa-danger">
          Connection could not be loaded. Verify the backend is running.
        </p>
      )}
      {conn && (
        <div className="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-4 gap-x-6 gap-y-2 fa-caption">
          <div>
            <div className="fa-overline text-fa-frost-dim">Connector</div>
            <div className="text-fa-frost capitalize">{conn.connectorId}</div>
          </div>
          <div>
            <div className="fa-overline text-fa-frost-dim">Wallet</div>
            <div className={conn.hasPrivateKey ? "text-fa-frost" : "text-fa-frost-dim"}>
              {walletStatusText(conn)}
            </div>
          </div>
          <div>
            <div className="fa-overline text-fa-frost-dim">Signature type</div>
            <div className="text-fa-frost">{sigTypeLabel(conn.signatureType)}</div>
          </div>
          <div>
            <div className="fa-overline text-fa-frost-dim">Per-trade cap</div>
            <div className="text-fa-frost tabular-nums">${conn.maxTradeUsd.toFixed(2)}</div>
          </div>
          <div className="sm:col-span-2">
            <div className="fa-overline text-fa-frost-dim">CLOB endpoint</div>
            <div className="text-fa-frost-dim truncate font-mono text-xs">{conn.clobBaseUrl}</div>
          </div>
          <div className="sm:col-span-2">
            <div className="fa-overline text-fa-frost-dim">Gamma endpoint</div>
            <div className="text-fa-frost-dim truncate font-mono text-xs">{conn.gammaBaseUrl}</div>
          </div>
        </div>
      )}
    </div>
  );
}

// ── Connection editor dialog ──────────────────────────────────────────────────
// Edit the per-tenant Polymarket connection in-app. The private key is write-only — the API never
// returns it, so the field is blank by default and only submitted when non-empty (blank = keep
// current key). All other fields are prefilled from the GET and only changed values are sent.
function ConnectionEditorDialog({ onClose }: { onClose: () => void }) {
  const { data: conn } = useGetPlatformConnectionQuery();
  const [updateConnection, { isLoading: saving, error: saveError, isSuccess }] =
    useUpdatePlatformConnectionMutation();

  // Form state, seeded once from the connection. `privateKey` stays empty unless the user types one.
  const [privateKey, setPrivateKey] = useState("");
  const [signatureType, setSignatureType] = useState(conn?.signatureType ?? 0);
  const [funder, setFunder] = useState(conn?.funder ?? "");
  const [clobBaseUrl, setClobBaseUrl] = useState(conn?.clobBaseUrl ?? "");
  const [gammaBaseUrl, setGammaBaseUrl] = useState(conn?.gammaBaseUrl ?? "");
  const [chainId, setChainId] = useState(conn?.chainId ?? 137);
  const [maxTradeUsd, setMaxTradeUsd] = useState(conn?.maxTradeUsd ?? 0);
  const [liveTrading, setLiveTrading] = useState(conn?.liveTrading ?? false);

  const saveErrMsg =
    saveError && "data" in saveError
      ? (saveError.data as { error?: string })?.error ?? "Save failed"
      : saveError
        ? "Save failed"
        : null;

  const save = async () => {
    // Send only changed fields; omit privateKey entirely when blank (keeps the existing key).
    const body: UpdatePlatformConnectionRequest = {};
    const trimmedKey = privateKey.trim();
    if (trimmedKey.length > 0) body.privateKey = trimmedKey;
    if (signatureType !== (conn?.signatureType ?? 0)) body.signatureType = signatureType;
    if (funder.trim() !== (conn?.funder ?? "")) body.funder = funder.trim();
    if (clobBaseUrl.trim() !== (conn?.clobBaseUrl ?? "")) body.clobBaseUrl = clobBaseUrl.trim();
    if (gammaBaseUrl.trim() !== (conn?.gammaBaseUrl ?? "")) body.gammaBaseUrl = gammaBaseUrl.trim();
    if (chainId !== (conn?.chainId ?? 137)) body.chainId = chainId;
    if (maxTradeUsd !== (conn?.maxTradeUsd ?? 0)) body.maxTradeUsd = maxTradeUsd;
    if (liveTrading !== (conn?.liveTrading ?? false)) body.liveTrading = liveTrading;

    try {
      await updateConnection(body).unwrap();
      setPrivateKey(""); // never retain the typed key in state after a successful save
    } catch {
      // surfaced inline below
    }
  };

  const labelCls = "fa-overline text-fa-frost-dim mb-1.5 block";

  return createPortal(
    <div
      className="fixed inset-0 z-[70] bg-fa-ink/70 backdrop-blur-sm flex items-center justify-center p-6 fa-confirm-enter"
      onClick={onClose}
      role="dialog"
      aria-modal="true"
      aria-labelledby="fa-conn-editor-title"
    >
      <div
        className="fa-card w-full max-w-xl max-h-[85vh] overflow-y-auto p-6 space-y-5 relative shadow-[0_20px_60px_-15px_rgba(0,0,0,0.6)] border border-fa-edge"
        onClick={(e) => e.stopPropagation()}
      >
        <div className="flex items-start gap-3">
          <div className="shrink-0 flex items-center justify-center w-10 h-10 rounded-full bg-fa-frost-bright/10 text-fa-frost-bright ring-1 ring-fa-frost-bright/30">
            <Plug className="h-5 w-5" />
          </div>
          <div className="min-w-0 flex-1">
            <h2 id="fa-conn-editor-title" className="text-fa-frost-bright text-base font-light tracking-tight">
              Edit connection
            </h2>
            <p className="fa-caption text-fa-frost-dim mt-1">
              Your Polymarket connection is stored per tenant. Leave the wallet key blank to keep the
              current one — it is never displayed.
            </p>
          </div>
          <button onClick={onClose} aria-label="Dismiss" className="shrink-0 -mt-1 -mr-1 text-fa-frost-dim hover:text-fa-frost-bright transition">
            <X className="h-4 w-4" />
          </button>
        </div>

        <div className="space-y-4">
          {/* Wallet private key — write-only. */}
          <div>
            <label htmlFor="fa-conn-pk" className={labelCls}>Wallet private key</label>
            <p className="fa-caption text-fa-frost-dim mb-1.5">
              {conn?.hasPrivateKey ? (
                <span className="text-fa-success">
                  Configured · {conn.walletAddress ? `0x…${conn.walletAddress.slice(-4)}` : "address derived"} ✓
                </span>
              ) : (
                <span className="text-fa-frost-dim">Not set</span>
              )}
            </p>
            <Input
              id="fa-conn-pk"
              type="password"
              autoComplete="off"
              value={privateKey}
              onChange={(e) => setPrivateKey(e.target.value)}
              placeholder="Leave blank to keep current"
              className="text-sm font-mono"
            />
          </div>

          <div className="grid grid-cols-1 sm:grid-cols-2 gap-4">
            <div>
              <label htmlFor="fa-conn-sig" className={labelCls}>Signature type</label>
              <select
                id="fa-conn-sig"
                value={signatureType}
                onChange={(e) => setSignatureType(Number(e.target.value))}
                className="fa-input w-full text-sm"
              >
                {SIGNATURE_TYPES.map((s) => (
                  <option key={s.value} value={s.value}>{s.value} = {s.label}</option>
                ))}
              </select>
            </div>
            <div>
              <label htmlFor="fa-conn-funder" className={labelCls}>Funder address (optional)</label>
              <Input
                id="fa-conn-funder"
                value={funder}
                onChange={(e) => setFunder(e.target.value)}
                placeholder="0x…"
                className="text-sm font-mono"
              />
            </div>
            <div>
              <label htmlFor="fa-conn-clob" className={labelCls}>CLOB base URL</label>
              <Input
                id="fa-conn-clob"
                value={clobBaseUrl}
                onChange={(e) => setClobBaseUrl(e.target.value)}
                className="text-sm font-mono"
              />
            </div>
            <div>
              <label htmlFor="fa-conn-gamma" className={labelCls}>Gamma base URL</label>
              <Input
                id="fa-conn-gamma"
                value={gammaBaseUrl}
                onChange={(e) => setGammaBaseUrl(e.target.value)}
                className="text-sm font-mono"
              />
            </div>
            <div>
              <label htmlFor="fa-conn-chain" className={labelCls}>Chain ID</label>
              <Input
                id="fa-conn-chain"
                type="number"
                value={chainId}
                onChange={(e) => setChainId(Number(e.target.value))}
                className="text-sm"
              />
            </div>
            <div>
              <label htmlFor="fa-conn-cap" className={labelCls}>Per-trade cap (USD)</label>
              <Input
                id="fa-conn-cap"
                type="number"
                min={0}
                step="0.01"
                value={maxTradeUsd}
                onChange={(e) => setMaxTradeUsd(Number(e.target.value))}
                className="text-sm tabular-nums"
              />
            </div>
          </div>

          <div>
            <label className="inline-flex items-center gap-2 text-sm text-fa-frost-dim cursor-pointer select-none">
              <input
                type="checkbox"
                checked={liveTrading}
                onChange={(e) => setLiveTrading(e.target.checked)}
                className="rounded border-fa-edge bg-fa-glass"
              />
              Live trading
            </label>
            {liveTrading && !(conn?.liveTrading ?? false) && (
              <p className="fa-caption text-fa-warning mt-1.5">
                Turning this on places real orders once a session is armed and running. Validate with a
                small per-trade cap first.
              </p>
            )}
          </div>
        </div>

        {saveErrMsg && <p className="fa-caption text-fa-danger">{saveErrMsg}</p>}
        {isSuccess && !saveErrMsg && (
          <p className="fa-caption text-fa-success">Connection saved.</p>
        )}

        <div className="flex items-center justify-end gap-3 pt-1">
          <Button onClick={onClose} variant="outline" size="sm">Close</Button>
          <Button onClick={save} disabled={saving} variant="primary" size="sm">
            {saving ? "Saving…" : "Save connection"}
          </Button>
        </div>
      </div>
    </div>,
    document.body,
  );
}

// ── Setup guide dialog ──────────────────────────────────────────────────────────
// The full Polymarket connection setup is non-trivial (fund a Polygon wallet, wrap USDC.e → pUSD,
// approve the exchanges, set env keys, derive CLOB creds, do the $1 validation). A tooltip can't
// carry that, so it lives in a proper dialog. Exact shell commands are in docs/live-setup.md.
function SetupStep({ n, title, children }: { n: number; title: string; children: ReactNode }) {
  return (
    <div className="flex gap-3">
      <span className="shrink-0 flex items-center justify-center w-5 h-5 rounded-full border border-fa-frost/30 text-fa-frost fa-overline">{n}</span>
      <div className="min-w-0 flex-1">
        <div className="fa-section-title">{title}</div>
        <div className="fa-caption text-fa-frost-dim mt-0.5 leading-relaxed [&_code]:font-mono [&_code]:text-fa-frost [&_code]:text-xs">{children}</div>
      </div>
    </div>
  );
}

function EnvRow({ k, v }: { k: string; v: string }) {
  return (
    <li className="flex flex-wrap items-baseline gap-x-2">
      <code className="font-mono text-fa-frost text-xs">{k}</code>
      <span className="text-fa-frost-dim/80">— {v}</span>
    </li>
  );
}

function LiveSetupDialog({ onClose, onOpenConnection }: { onClose: () => void; onOpenConnection: () => void }) {
  return createPortal(
    <div
      className="fixed inset-0 z-[70] bg-fa-ink/70 backdrop-blur-sm flex items-center justify-center p-6 fa-confirm-enter"
      onClick={onClose}
      role="dialog"
      aria-modal="true"
      aria-labelledby="fa-live-setup-title"
    >
      <div
        className="fa-card w-full max-w-2xl max-h-[85vh] overflow-y-auto p-6 space-y-5 relative shadow-[0_20px_60px_-15px_rgba(0,0,0,0.6)] border border-fa-edge"
        onClick={(e) => e.stopPropagation()}
      >
        <div className="flex items-start gap-3">
          <div className="shrink-0 flex items-center justify-center w-10 h-10 rounded-full bg-fa-frost-bright/10 text-fa-frost-bright ring-1 ring-fa-frost-bright/30">
            <Info className="h-5 w-5" />
          </div>
          <div className="min-w-0 flex-1">
            <h2 id="fa-live-setup-title" className="text-fa-frost-bright text-base font-light tracking-tight">
              Set up live trading
            </h2>
            <p className="fa-caption text-fa-frost-dim mt-1">
              Foresight trades on Polymarket CLOB V2 with your own Polygon wallet. The exact shell
              commands are in <code className="font-mono text-fa-frost text-xs">docs/live-setup.md</code>.
            </p>
          </div>
          <button onClick={onClose} aria-label="Dismiss" className="shrink-0 -mt-1 -mr-1 text-fa-frost-dim hover:text-fa-frost-bright transition">
            <X className="h-4 w-4" />
          </button>
        </div>

        <div className="space-y-4">
          <SetupStep n={1} title="Fund a Polygon wallet">
            Create an EOA on Polygon, hold USDC.e, and wrap it into <span className="text-fa-frost">pUSD</span>{" "}
            (Polymarket collateral). Approve the CTF exchange and neg-risk exchange to spend your pUSD and
            CTF outcome tokens. (Doc steps 1–3, run with <code>cast</code>.)
          </SetupStep>
          <SetupStep n={2} title="Set your connection here">
            Open the <span className="text-fa-frost">Connection</span> card on this page and enter your
            wallet signing key, signature type, funder, and per-trade cap. The connection is stored
            securely per tenant — no <code>.env</code> editing.
            <div className="mt-2">
              <Button
                onClick={() => { onClose(); onOpenConnection(); }}
                variant="outline"
                size="sm"
              >
                <Plug className="h-3.5 w-3.5" /> Open connection editor
              </Button>
            </div>
            <ul className="mt-3 space-y-1 opacity-70">
              <li className="fa-caption text-fa-frost-dim/80">
                These were the old env defaults — now configured in-app via the editor above:
              </li>
              <EnvRow k="KeyVault__PrivateKey" v="wallet signing key (hex)" />
              <EnvRow k="KeyVault__SignatureType" v="0 = EOA · 1 = proxy · 2 = Gnosis Safe" />
              <EnvRow k="Polymarket__MaxTradeUsd" v="per-trade cap — start at 1 for validation" />
              <EnvRow k="Polymarket__LiveTrading" v="keep off until validated" />
            </ul>
          </SetupStep>
          <SetupStep n={3} title="Credentials derive automatically">
            Once the wallet key is saved, the backend derives its CLOB API credentials via the L1 auth
            flow on first use — no manual key creation needed.
          </SetupStep>
          <SetupStep n={4} title="Arm">
            Request a confirmation code and confirm it on this page. Arming is in-memory and resets if the
            backend restarts.
          </SetupStep>
          <SetupStep n={5} title="Supervised $1 validation">
            With the $1 cap in place, place one real order and confirm it fills and settles on Polymarket
            before trusting automation.
          </SetupStep>
          <SetupStep n={6} title="Go live">
            Turn on <span className="text-fa-frost">Live trading</span> in the Connection editor, then
            create a session below — it trades automatically each qualifying candle while armed. The
            killswitch (or stopping a session) halts it anytime.
          </SetupStep>
        </div>

        <div className="rounded-md border border-fa-edge bg-fa-glass px-3 py-2 fa-caption text-fa-frost-dim">
          <span className="text-fa-frost">Data is automatic.</span> Each model pipeline fetches and
          backfills its own candle and order-flow history before it trains or trades — there is no manual
          data-import step to make the app work.
        </div>
      </div>
    </div>,
    document.body,
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

  const [showSetup, setShowSetup] = useState(false);
  const [showConnection, setShowConnection] = useState(false);

  return (
    <div>
      <div className="sticky top-0 z-30 bg-fa-ink/95 backdrop-blur">
        <PageHeader
          title={
            <span className="inline-flex items-center gap-2">
              Live
              <button
                onClick={() => setShowSetup(true)}
                aria-label="How to set up live trading"
                title="How to set up live trading"
                className="inline-flex items-center justify-center rounded-full border border-fa-edge text-fa-frost-dim hover:text-fa-frost-bright hover:border-fa-frost/40 transition h-5 w-5"
              >
                <Info className="h-3 w-3" />
              </button>
            </span>
          }
          subtitle="Automated live-trading sessions against Polymarket CLOB V2."
        />
      </div>

      <div className="p-4 sm:p-8 space-y-6">
        {/* Arm status + flow */}
        <ArmStatusStrip armed={armed} onShowSetup={() => setShowSetup(true)} />

        {/* Connection — view/edit the per-tenant Polymarket connection in-app */}
        <ConnectionCard onEdit={() => setShowConnection(true)} />

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
            <ResizableChartGrid>
              {liveSessions.map((s) => (
                <ResizableChartCard key={s.id} id={`live-${s.id}`}>
                  <LiveSessionCard session={s} />
                </ResizableChartCard>
              ))}
            </ResizableChartGrid>
          )}
        </div>
      </div>

      {showSetup && (
        <LiveSetupDialog
          onClose={() => setShowSetup(false)}
          onOpenConnection={() => setShowConnection(true)}
        />
      )}
      {showConnection && <ConnectionEditorDialog onClose={() => setShowConnection(false)} />}
    </div>
  );
}
