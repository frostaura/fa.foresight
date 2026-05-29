import { useEffect, useRef, useState } from "react";
import { Link } from "react-router-dom";
import { Star } from "lucide-react";
import { Badge, LivePulse, ConnectingPulse } from "./ui";
import MarketSparkline from "./MarketSparkline";
import { useGetMarketQuery, type MarketSummary } from "../store/api";
import { useFavorites } from "../lib/favorites";

const LIVE_POLL_MS = 5_000;

function fmtUsd(n?: number | null): string {
  if (n == null || !isFinite(n)) return "—";
  if (n >= 1_000_000_000) return `$${(n / 1_000_000_000).toFixed(2)}B`;
  if (n >= 1_000_000) return `$${(n / 1_000_000).toFixed(2)}M`;
  if (n >= 1_000) return `$${(n / 1_000).toFixed(1)}k`;
  return `$${n.toFixed(0)}`;
}

function fmtResolves(iso?: string | null): string {
  if (!iso) return "no end date";
  const d = new Date(iso);
  const days = Math.round((d.getTime() - Date.now()) / (1000 * 60 * 60 * 24));
  if (days < 0) return `closed ${Math.abs(days)}d ago`;
  if (days === 0) return "resolves today";
  if (days === 1) return "resolves tomorrow";
  if (days < 30) return `resolves in ${days}d`;
  return `resolves ${d.toLocaleDateString(undefined, { month: "short", day: "numeric", year: "numeric" })}`;
}

function MarketAvatar({ url, fallback }: { url?: string | null; fallback: string }) {
  const [errored, setErrored] = useState(false);
  if (!url || errored) {
    return (
      <div className="h-12 w-12 shrink-0 rounded-full bg-gradient-to-br from-fa-frost/30 to-fa-frost/5 border border-fa-frost/20 flex items-center justify-center text-fa-frost-bright text-sm font-semibold">
        {fallback.slice(0, 2).toUpperCase()}
      </div>
    );
  }
  return (
    <img
      src={url}
      alt=""
      onError={() => setErrored(true)}
      className="h-12 w-12 shrink-0 rounded-full object-cover border border-fa-frost/20 bg-fa-frost/5"
      loading="lazy"
    />
  );
}

function OddsBar({ yes }: { yes?: number | null }) {
  const yesPct = yes != null ? Math.max(0, Math.min(1, yes)) : null;
  if (yesPct == null) return <div className="h-1.5 rounded-full bg-fa-frost/10" />;
  return (
    <div className="h-1.5 rounded-full bg-rose-400/20 overflow-hidden">
      <div
        className="h-full bg-emerald-400/80 rounded-full transition-all duration-500"
        style={{ width: `${yesPct * 100}%` }}
      />
    </div>
  );
}

export default function MarketCard({ market }: { market: MarketSummary }) {
  const cardRef = useRef<HTMLAnchorElement | null>(null);
  const [inView, setInView] = useState(false);
  const { isFav, toggle } = useFavorites();
  const fav = isFav(market.providerId, market.externalId);

  useEffect(() => {
    const el = cardRef.current;
    if (!el) return;
    const io = new IntersectionObserver(
      (entries) => {
        const visible = entries.some((e) => e.isIntersecting);
        setInView(visible);
      },
      { rootMargin: "100px 0px" }
    );
    io.observe(el);
    return () => io.disconnect();
  }, []);

  const { data: live, isSuccess: liveReady, isFetching: liveFetching } = useGetMarketQuery(
    { providerId: market.providerId, externalId: market.externalId },
    { skip: !inView, pollingInterval: inView ? LIVE_POLL_MS : 0 }
  );

  const yes = live?.price?.yes ?? market.yesPrice ?? null;
  const no = live?.price?.no ?? market.noPrice ?? null;
  const yesPct = yes != null ? Math.round(yes * 100) : null;
  const noPct = no != null ? Math.round(no * 100) : null;
  const vol24 = live?.price?.volume24h ?? market.volume24h ?? null;
  const status = live?.status ?? market.status;

  const isLive = inView && liveReady;
  const isConnecting = inView && !liveReady && liveFetching;

  return (
    <Link
      ref={cardRef}
      key={`${market.providerId}-${market.externalId}`}
      to={`/markets/${market.providerId}/${encodeURIComponent(market.externalId)}`}
      className="fa-card p-4 hover:border-fa-frost/40 transition group flex flex-col relative"
    >
      <button
        type="button"
        aria-label={fav ? "Remove from favorites" : "Add to favorites"}
        title={fav ? "Remove from favorites" : "Add to favorites"}
        onClick={(e) => {
          e.preventDefault();
          e.stopPropagation();
          toggle(market.providerId, market.externalId);
        }}
        className={`absolute top-2 right-2 h-7 w-7 rounded-full flex items-center justify-center border transition ${
          fav
            ? "bg-amber-300/15 border-amber-300/40 text-amber-300"
            : "bg-fa-glass border-fa-edge text-fa-frost-dim hover:text-amber-300 hover:border-amber-300/40"
        }`}
      >
        <Star className="h-3.5 w-3.5" fill={fav ? "currentColor" : "none"} strokeWidth={2} />
      </button>

      <div className="flex items-start gap-3 mb-3 pr-9">
        <MarketAvatar url={market.iconUrl ?? market.imageUrl} fallback={market.question} />
        <div className="flex-1 min-w-0">
          <div className="text-fa-frost-bright group-hover:text-white transition text-sm leading-snug font-medium line-clamp-2 mb-2">
            {market.question}
          </div>
          <div className="flex items-center justify-between gap-2 flex-wrap">
            <div className="flex items-center gap-2 min-w-0">
              <Badge>{market.category || "general"}</Badge>
              <Badge tone={status === "Open" ? "default" : "warn"}>{status}</Badge>
            </div>
            <div className="flex items-center gap-2">
              {isLive && <LivePulse title="Live — auto-refreshing every 5s while visible" />}
              {isConnecting && <ConnectingPulse />}
              {yesPct != null && (
                <span className="text-sm font-medium tabular-nums text-fa-frost-bright leading-none">
                  {yesPct}%
                </span>
              )}
            </div>
          </div>
        </div>
      </div>

      <div className="mt-auto space-y-2">
        <MarketSparkline providerId={market.providerId} externalId={market.externalId} />
        {(yesPct != null || noPct != null) && (
          <>
            <div className="flex items-center justify-between text-xs">
              <span className="text-emerald-300/90 font-medium tabular-nums">YES {yesPct ?? "—"}%</span>
              <span className="text-rose-300/80 font-medium tabular-nums">NO {noPct ?? "—"}%</span>
            </div>
            <OddsBar yes={yes} />
          </>
        )}

        <div className="flex items-center justify-between text-[11px] text-fa-frost-dim pt-1">
          <div className="flex items-center gap-3">
            <span title="Total volume"><span className="text-fa-frost/70">vol</span> {fmtUsd(market.volume)}</span>
            <span title="24h volume"><span className="text-fa-frost/70">24h</span> {fmtUsd(vol24)}</span>
            {market.liquidity != null && (
              <span title="Liquidity"><span className="text-fa-frost/70">liq</span> {fmtUsd(market.liquidity)}</span>
            )}
          </div>
          <span>{fmtResolves(market.resolvesAt)}</span>
        </div>
      </div>
    </Link>
  );
}
