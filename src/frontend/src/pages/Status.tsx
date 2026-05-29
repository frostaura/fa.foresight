/**
 * Trading → Status
 *
 * Overview of all trading sessions — overlaid balance curves, hit/miss totals, split into a Live
 * block and a Paper block. The unified sessions + chaos APIs are NOT yet built on the backend; the
 * page calls them optimistically and degrades gracefully on empty / 404.
 */
import { Activity, FlaskConical, TrendingUp } from "lucide-react";
import PageHeader from "../components/PageHeader";
import { Card, CardContent, CardHeader, CardTitle, CardDescription } from "../components/ui/card";
import { Badge } from "../components/ui/badge";
import { useListSessionsQuery, useListChaosRunsQuery, type NormalizedSession } from "../store/api";

function StatTile({ label, value, hint }: { label: string; value: string; hint?: string }) {
  return (
    <div className="fa-card p-4 space-y-1">
      <div className="text-[10px] uppercase tracking-widest text-fa-frost-dim">{label}</div>
      <div className="text-2xl font-light text-fa-frost-bright tabular-nums">{value}</div>
      {hint && <div className="text-xs text-fa-frost-dim/80">{hint}</div>}
    </div>
  );
}

function SessionRow({ session }: { session: NormalizedSession }) {
  const pnl = session.currentBalance - session.initialBalance;
  const pnlSign = pnl >= 0 ? "+" : "";
  const hitRate = session.betsPlaced > 0
    ? ((session.betsWon / session.betsPlaced) * 100).toFixed(1) + "%"
    : "—";
  const status = session.stoppedAt
    ? session.bust ? "bust" : "stopped"
    : "active";

  return (
    <div className="flex items-center gap-4 py-3 px-4 border-b border-fa-edge/50 last:border-0 hover:bg-fa-glass/40 transition">
      <div className="flex flex-col gap-0.5 flex-1 min-w-0">
        <div className="text-sm text-fa-frost-bright font-medium">
          {session.symbol} · {session.interval}
        </div>
        <div className="text-[11px] text-fa-frost-dim">
          {session.strategyId}
        </div>
      </div>
      <div className="text-right tabular-nums shrink-0">
        <div className="text-sm text-fa-frost-bright">${session.currentBalance.toFixed(2)}</div>
        <div className={`text-xs ${pnl >= 0 ? "text-fa-success" : "text-fa-danger"}`}>
          {pnlSign}{pnl.toFixed(2)}
        </div>
      </div>
      <div className="text-right tabular-nums shrink-0 w-14">
        <div className="text-sm text-fa-frost">{hitRate}</div>
        <div className="text-[10px] text-fa-frost-dim">{session.betsPlaced} bets</div>
      </div>
      <Badge
        variant={status === "active" ? "success" : status === "bust" ? "danger" : "outline"}
        className="shrink-0"
      >
        {status}
      </Badge>
    </div>
  );
}

function SessionBlock({
  title,
  icon: Icon,
  sessions,
  isLoading,
  error,
}: {
  title: string;
  icon: React.ElementType;
  sessions: NormalizedSession[];
  isLoading: boolean;
  error: boolean;
}) {
  const totalBets = sessions.reduce((s, r) => s + r.betsPlaced, 0);
  const totalWon = sessions.reduce((s, r) => s + r.betsWon, 0);
  const totalBalance = sessions.reduce((s, r) => s + r.currentBalance, 0);
  const totalInitial = sessions.reduce((s, r) => s + r.initialBalance, 0);
  const netPnl = totalBalance - totalInitial;

  return (
    <Card>
      <CardHeader className="flex-row items-center justify-between gap-3 pb-4">
        <div className="flex items-center gap-2">
          <Icon className="h-4 w-4 text-fa-frost-bright" />
          <CardTitle>{title}</CardTitle>
        </div>
        {!error && !isLoading && (
          <CardDescription>
            {sessions.length} session{sessions.length !== 1 ? "s" : ""}
          </CardDescription>
        )}
      </CardHeader>

      {!error && sessions.length > 0 && (
        <div className="px-5 pb-4 grid grid-cols-3 gap-3">
          <StatTile label="Net P&L" value={`${netPnl >= 0 ? "+" : ""}$${netPnl.toFixed(2)}`} />
          <StatTile label="Hit rate" value={totalBets > 0 ? `${((totalWon / totalBets) * 100).toFixed(1)}%` : "—"} hint={`${totalWon}/${totalBets} bets`} />
          <StatTile label="Sessions" value={String(sessions.length)} />
        </div>
      )}

      <CardContent className="p-0">
        {isLoading && (
          <div className="px-5 py-6 text-sm text-fa-frost-dim">Loading sessions…</div>
        )}
        {error && (
          <div className="px-5 py-6 text-sm text-fa-frost-dim">
            Unable to load sessions — check backend connectivity.
          </div>
        )}
        {!isLoading && !error && sessions.length === 0 && (
          <div className="px-5 py-8 text-center">
            <div className="text-fa-frost-dim text-sm">No {title.toLowerCase()} sessions yet.</div>
            <div className="text-fa-frost-dim/60 text-xs mt-1">
              {title === "Live" ? "Start a live session from Trading → Live." : "Start a paper session from Trading → Paper."}
            </div>
          </div>
        )}
        {!isLoading && !error && sessions.length > 0 && (
          <div>
            {sessions.map((s) => (
              <SessionRow key={s.id} session={s} />
            ))}
          </div>
        )}
      </CardContent>
    </Card>
  );
}

export default function Status() {
  const {
    data: allSessions,
    isLoading: sessionsLoading,
    isError: sessionsError,
  } = useListSessionsQuery({});

  const {
    data: chaosRuns,
    isLoading: chaosLoading,
    isError: chaosError,
  } = useListChaosRunsQuery({});

  const liveSessions = (allSessions ?? []).filter((s) => s.mode === "live");
  const paperSessions = (allSessions ?? []).filter((s) => s.mode === "paper");

  const latestChaos = chaosRuns?.[0];

  return (
    <div>
      <div className="sticky top-0 z-30 bg-fa-ink/95 backdrop-blur">
        <PageHeader
          title="Status"
          subtitle="Overview of all trading sessions — P&L, hit rates, and chaos sweep results."
        />
      </div>

      <div className="p-8 space-y-6">
        {/* Chaos sweep summary */}
        <Card>
          <CardHeader className="flex-row items-center gap-3 pb-3">
            <TrendingUp className="h-4 w-4 text-fa-frost-bright" />
            <CardTitle>Chaos sweep</CardTitle>
          </CardHeader>
          <CardContent>
            {chaosLoading && <p className="text-sm text-fa-frost-dim">Loading…</p>}
            {chaosError && (
              <p className="text-sm text-fa-frost-dim">
                Unable to load chaos runs — check backend connectivity.
              </p>
            )}
            {!chaosLoading && !chaosError && !latestChaos && (
              <p className="text-sm text-fa-frost-dim">
                No chaos sweeps run yet. Run a bust-test from Models → Backtesting to see results here.
              </p>
            )}
            {latestChaos && (
              <div className="grid grid-cols-2 sm:grid-cols-4 gap-3">
                <StatTile
                  label="Bust rate"
                  value={latestChaos.bustRate != null ? `${(latestChaos.bustRate * 100).toFixed(1)}%` : "—"}
                  hint={`${latestChaos.sampleCount} samples`}
                />
                <StatTile
                  label="Median profit"
                  value={latestChaos.profitP50 != null
                    ? `${latestChaos.profitP50 >= 0 ? "+" : ""}$${latestChaos.profitP50.toFixed(2)}`
                    : "—"}
                />
                <StatTile label="Window" value={`${latestChaos.windowLength} candles`} hint={`${latestChaos.symbol} ${latestChaos.interval}`} />
                <StatTile
                  label="Pass"
                  value={latestChaos.status === "running" ? "Running…" : latestChaos.pass ? "Yes" : "No"}
                />
              </div>
            )}
          </CardContent>
        </Card>

        {/* Session blocks */}
        <div className="grid grid-cols-1 xl:grid-cols-2 gap-6">
          <SessionBlock
            title="Live"
            icon={Activity}
            sessions={liveSessions}
            isLoading={sessionsLoading}
            error={sessionsError}
          />
          <SessionBlock
            title="Paper"
            icon={FlaskConical}
            sessions={paperSessions}
            isLoading={sessionsLoading}
            error={sessionsError}
          />
        </div>
      </div>
    </div>
  );
}
