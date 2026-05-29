/**
 * Trading → Live
 *
 * Live session list + create form scaffold. Wired to /api/sessions optimistically; gracefully
 * handles 404 / backend-not-yet-available until WS D lands. Live actions are intentionally
 * inert (buttons visible, operations fire optimistically but don't crash on backend absence).
 */
import { useState } from "react";
import { Activity, Plus, AlertTriangle } from "lucide-react";
import PageHeader from "../components/PageHeader";
import { Card, CardContent, CardHeader, CardTitle } from "../components/ui/card";
import { Badge } from "../components/ui/badge";
import { Button } from "../components/ui/button";
import { Input } from "../components/ui/input";
import { Label } from "../components/ui/label";
import {
  useListSessionsQuery,
  useCreateSessionMutation,
  useListModelsQuery,
  useGetStakingStrategiesQuery,
  type NormalizedSession,
  type CreateSessionRequest,
} from "../store/api";

const SYMBOLS = ["BTCUSDT"];
const INTERVALS = ["5m", "15m", "1m"];

function SessionCard({ session }: { session: NormalizedSession }) {
  const pnl = session.currentBalance - session.initialBalance;
  const pnlSign = pnl >= 0 ? "+" : "";
  const hitRate = session.betsPlaced > 0
    ? ((session.betsWon / session.betsPlaced) * 100).toFixed(1) + "%"
    : "—";
  const status = session.stoppedAt
    ? session.bust ? "bust" : "stopped"
    : "active";

  return (
    <Card>
      <CardHeader className="flex-row items-center justify-between pb-2">
        <div className="flex items-center gap-2">
          <Activity className="h-4 w-4 text-fa-frost-dim" />
          <CardTitle className="text-sm">
            {session.symbol} · {session.interval}
          </CardTitle>
        </div>
        <Badge variant={status === "active" ? "success" : status === "bust" ? "danger" : "outline"}>
          {status}
        </Badge>
      </CardHeader>
      <CardContent>
        <div className="grid grid-cols-3 gap-3 text-center">
          <div>
            <div className="text-[10px] uppercase tracking-wider text-fa-frost-dim">Balance</div>
            <div className="text-sm font-medium text-fa-frost-bright tabular-nums">
              ${session.currentBalance.toFixed(2)}
            </div>
            <div className={`text-[10px] tabular-nums ${pnl >= 0 ? "text-fa-success" : "text-fa-danger"}`}>
              {pnlSign}{pnl.toFixed(2)}
            </div>
          </div>
          <div>
            <div className="text-[10px] uppercase tracking-wider text-fa-frost-dim">Hit rate</div>
            <div className="text-sm font-medium text-fa-frost tabular-nums">{hitRate}</div>
            <div className="text-[10px] text-fa-frost-dim">{session.betsPlaced} bets</div>
          </div>
          <div>
            <div className="text-[10px] uppercase tracking-wider text-fa-frost-dim">Strategy</div>
            <div className="text-sm font-medium text-fa-frost truncate">{session.strategyId}</div>
          </div>
        </div>
      </CardContent>
    </Card>
  );
}

function CreateSessionForm({ onCreated }: { onCreated?: () => void }) {
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

  const strategies = strategiesResp?.strategies ?? [{ id: "flat", name: "Flat" }];
  const availableModels = models.filter((m) => m.supportsBacktesting);
  const errMsg = error && "data" in error
    ? (error.data as { error?: string })?.error ?? "Request failed"
    : error ? "Request failed" : null;
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

  return (
    <Card>
      <CardHeader className="pb-3">
        <CardTitle className="flex items-center gap-2">
          <Plus className="h-4 w-4" />
          New live session
        </CardTitle>
      </CardHeader>
      <CardContent className="space-y-4">
        <div className="p-3 rounded-md border border-fa-warning/30 bg-fa-warning/5 flex items-start gap-2 text-xs text-fa-warning">
          <AlertTriangle className="h-4 w-4 shrink-0 mt-0.5" />
          <span>
            Live trading gate is controlled by <code className="font-mono text-fa-frost">Polymarket__LiveTrading</code> in{" "}
            <code className="font-mono text-fa-frost">.env</code>. Keep it <code className="font-mono text-fa-frost">false</code> until
            a supervised $1 validation order has been placed and confirmed.
          </span>
        </div>

        <div className="grid grid-cols-1 sm:grid-cols-2 gap-4">
          <div className="space-y-1.5">
            <Label>Model</Label>
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

          <div className="space-y-1.5">
            <Label>Strategy</Label>
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

          <div className="space-y-1.5">
            <Label>Symbol</Label>
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

          <div className="space-y-1.5">
            <Label>Interval</Label>
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

          <div className="space-y-1.5">
            <Label>Starting balance ($)</Label>
            <Input
              type="number"
              min={1}
              value={form.initialBalance}
              onChange={(e) => setForm((f) => ({ ...f, initialBalance: Number(e.target.value) }))}
              className="text-sm"
            />
          </div>

          <div className="space-y-1.5">
            <Label>Initial bet ($)</Label>
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

        <div className="flex items-center gap-2">
          <label className="inline-flex items-center gap-2 text-sm text-fa-frost-dim cursor-pointer select-none">
            <input
              type="checkbox"
              checked={form.gated}
              onChange={(e) => setForm((f) => ({ ...f, gated: e.target.checked }))}
              className="rounded border-fa-edge bg-fa-glass"
            />
            Apply confidence gate (skip ±2pp no-bet band)
          </label>
        </div>

        {is409 && (
          <p className="text-xs text-fa-warning">
            A session with this configuration is already active. Stop it first or change the configuration.
          </p>
        )}
        {is422 && (
          <p className="text-xs text-fa-warning">
            Live trading is disarmed. Complete the arm flow at Trading → Live → arm via <code className="font-mono">/api/golive</code> before creating a live session.
          </p>
        )}
        {errMsg && !is409 && !is422 && (
          <p className="text-xs text-fa-danger">{errMsg}</p>
        )}

        <Button
          onClick={submit}
          disabled={isLoading || !selectedModelId}
          variant="primary"
          size="sm"
        >
          {isLoading ? "Creating…" : "Create session"}
        </Button>
      </CardContent>
    </Card>
  );
}

export default function Live() {
  const {
    data: allSessions,
    isLoading,
    isError,
  } = useListSessionsQuery({ kind: "live", active: true });

  const liveSessions = (allSessions ?? []).filter((s) => !s.stoppedAt);

  return (
    <div>
      <div className="sticky top-0 z-30 bg-fa-ink/95 backdrop-blur">
        <PageHeader
          title="Live"
          subtitle="Automated live-trading sessions against Polymarket CLOB V2."
        />
      </div>

      <div className="p-8 space-y-6">
        {/* Warning banner — always shown */}
        <div className="p-3 rounded-md border border-fa-warning/30 bg-fa-warning/5 flex items-start gap-2 text-sm text-fa-warning">
          <AlertTriangle className="h-4 w-4 shrink-0 mt-0.5" />
          <span>
            Live trading requires the arm flow to be completed first (<code className="font-mono text-fa-frost">POST /api/golive/request-code</code> →{" "}
            <code className="font-mono text-fa-frost">POST /api/golive/confirm</code>). Keep{" "}
            <code className="font-mono text-fa-frost">Polymarket__LiveTrading=false</code> until a supervised $1 validation order has been placed.
          </span>
        </div>

        <CreateSessionForm />

        {/* Session list */}
        <div>
          <h2 className="text-sm font-medium text-fa-frost-bright mb-3 uppercase tracking-wider">
            Active sessions
          </h2>
          {isLoading && (
            <p className="text-sm text-fa-frost-dim">Loading sessions…</p>
          )}
          {isError && (
            <p className="text-sm text-fa-frost-dim">
              Unable to load sessions — check backend connectivity.
            </p>
          )}
          {!isLoading && !isError && liveSessions.length === 0 && (
            <div className="fa-card px-6 py-8 text-center">
              <p className="text-fa-frost-dim text-sm">No live sessions yet.</p>
              <p className="text-fa-frost-dim/60 text-xs mt-1">
                Fill in the form above to create one after the backend gate is enabled.
              </p>
            </div>
          )}
          {liveSessions.length > 0 && (
            <div className="grid grid-cols-1 md:grid-cols-2 xl:grid-cols-3 gap-4">
              {liveSessions.map((s) => (
                <SessionCard key={s.id} session={s} />
              ))}
            </div>
          )}
        </div>
      </div>
    </div>
  );
}
