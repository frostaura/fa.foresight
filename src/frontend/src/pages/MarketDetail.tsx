import { useNavigate, useParams } from "react-router-dom";
import { Card, Stat, Badge, Spinner, LivePulse } from "../components/ui";
import { useGetMarketQuery } from "../store/api";
import { useState } from "react";
import { ChevronDown, X } from "lucide-react";
import MarketChat from "../components/MarketChat";
import PriceChart from "../components/PriceChart";
import SentimentSection from "../components/SentimentSection";

const LIVE_POLL_MS = 5_000;

export default function MarketDetail() {
  const { providerId = "polymarket", externalId = "" } = useParams();
  const decoded = decodeURIComponent(externalId);
  const { data: market, isFetching, isSuccess } = useGetMarketQuery(
    { providerId, externalId: decoded },
    { pollingInterval: LIVE_POLL_MS }
  );
  const [criteriaOpen, setCriteriaOpen] = useState(false);
  const nav = useNavigate();

  const close = () => nav("/markets");

  const iconSrc = market?.iconUrl ?? market?.imageUrl;
  const fallback = (market?.question ?? "??").slice(0, 2).toUpperCase();
  const leading = iconSrc ? (
    <img
      src={iconSrc}
      alt=""
      className="h-10 w-10 shrink-0 rounded-full object-cover border border-fa-frost/20 bg-fa-frost/5"
      onError={(e) => { (e.currentTarget as HTMLImageElement).style.display = "none"; }}
      loading="lazy"
    />
  ) : (
    <div className="h-10 w-10 shrink-0 rounded-full bg-gradient-to-br from-fa-frost/30 to-fa-frost/5 border border-fa-frost/20 flex items-center justify-center text-fa-frost-bright text-xs font-semibold">
      {fallback}
    </div>
  );

  const isLive = isSuccess && !!market;

  return (
    <>
      <div className="px-5 py-4 border-b border-fa-edge flex items-start justify-between gap-3 shrink-0">
        <div className="min-w-0 flex-1 flex items-center gap-3">
          {leading}
          <div className="min-w-0">
            <h2 className="text-lg font-light text-fa-frost-bright tracking-tight truncate">
              {market?.question ?? "Loading market…"}
            </h2>
            {market && (
              <div className="text-fa-frost-dim text-xs mt-0.5 truncate">
                {market.providerId} · {market.category}
              </div>
            )}
          </div>
        </div>
        <div className="flex items-center gap-2 shrink-0">
          {isLive ? <LivePulse title="Live — refreshing every 5s" /> : isFetching ? <Spinner /> : null}
          <button
            type="button"
            aria-label="Close"
            onClick={close}
            className="h-8 w-8 rounded-full border border-fa-edge bg-fa-glass hover:bg-fa-glass-strong text-fa-frost-bright flex items-center justify-center transition"
          >
            <X className="h-4 w-4" />
          </button>
        </div>
      </div>

      {isFetching && !market ? (
        <div className="p-8 text-fa-frost-dim flex items-center gap-2"><Spinner /> Loading…</div>
      ) : !market ? (
        <div className="p-8 text-fa-frost-dim">Market not found.</div>
      ) : (
        <div className="flex-1 min-h-0 flex flex-col gap-5 p-5 overflow-y-auto">
          <div className="grid grid-cols-2 md:grid-cols-4 gap-3 shrink-0">
            <Stat label="Yes Price" value={market.price ? market.price.yes.toFixed(3) : "—"} />
            <Stat label="No Price" value={market.price ? market.price.no.toFixed(3) : "—"} />
            <Stat label="24h Volume" value={market.price ? `$${Math.round(market.price.volume24h).toLocaleString()}` : "—"} />
            <Stat label="Status" value={<Badge tone={market.status === "Open" ? "success" : "warn"}>{market.status}</Badge>} />
          </div>

          {market.status === "Open" && (
            <div className="shrink-0">
              <PriceChart providerId={market.providerId} externalId={market.externalId} livePrice={market.price?.yes} />
            </div>
          )}

          <MarketChat providerId={market.providerId} externalId={market.externalId} />

          {market.resolutionCriteria && (
            <Card className="shrink-0">
              <button
                type="button"
                onClick={() => setCriteriaOpen((v) => !v)}
                aria-expanded={criteriaOpen}
                className="w-full flex items-center justify-between gap-4 text-left group"
              >
                <div>
                  <h3 className="text-fa-frost-bright text-base font-medium">Resolution criteria</h3>
                  <p className="text-fa-frost-dim text-xs mt-0.5">
                    {criteriaOpen ? "Tap to collapse" : "Tap to expand"}
                  </p>
                </div>
                <ChevronDown
                  className={`h-4 w-4 text-fa-frost-dim group-hover:text-fa-frost-bright transition-transform duration-[var(--fa-duration)] ${criteriaOpen ? "rotate-180" : ""}`}
                  style={{ transitionTimingFunction: "var(--fa-ease)" }}
                />
              </button>
              <div
                className={`grid transition-[grid-template-rows,opacity] duration-[var(--fa-duration)] ${
                  criteriaOpen ? "grid-rows-[1fr] opacity-100 mt-4" : "grid-rows-[0fr] opacity-0"
                }`}
                style={{ transitionTimingFunction: "var(--fa-ease)" }}
              >
                <div className="overflow-hidden">
                  <p className="text-sm text-fa-frost/80 leading-relaxed whitespace-pre-line">
                    {market.resolutionCriteria}
                  </p>
                </div>
              </div>
            </Card>
          )}

          <SentimentSection providerId={market.providerId} externalId={market.externalId} />
        </div>
      )}
    </>
  );
}
