import { Star } from "lucide-react";
import MarketCard from "./MarketCard";
import { useGetMarketQuery, type MarketSummary } from "../store/api";
import { useFavorites, type FavoriteRef } from "../lib/favorites";

function FavoriteCard({ fav }: { fav: FavoriteRef }) {
  const { data } = useGetMarketQuery({ providerId: fav.providerId, externalId: fav.externalId });

  const summary: MarketSummary = data
    ? {
        providerId: data.providerId,
        externalId: data.externalId,
        question: data.question,
        category: data.category,
        resolvesAt: data.resolvesAt,
        status: data.status,
        resolutionCriteria: data.resolutionCriteria,
        imageUrl: data.imageUrl,
        iconUrl: data.iconUrl,
        yesPrice: data.price?.yes ?? null,
        noPrice: data.price?.no ?? null,
        volume24h: data.price?.volume24h ?? null
      }
    : {
        providerId: fav.providerId,
        externalId: fav.externalId,
        question: "Loading…",
        category: "general",
        status: "Open"
      };

  return (
    <div className="w-[340px] shrink-0 snap-start">
      <MarketCard market={summary} />
    </div>
  );
}

export default function FavoritesBar() {
  const { favorites } = useFavorites();
  if (favorites.length === 0) return null;

  return (
    <div>
      <div className="flex items-center gap-2 mb-2 text-fa-frost-dim text-[11px] uppercase tracking-wider">
        <Star className="h-3 w-3 text-amber-300" fill="currentColor" />
        Favorites
        <span className="text-fa-frost-dim/60">· {favorites.length}</span>
      </div>
      <div className="flex gap-4 overflow-x-auto snap-x snap-mandatory pb-2 -mx-2 px-2 [scrollbar-width:thin]">
        {favorites.map((f) => (
          <FavoriteCard key={`${f.providerId}-${f.externalId}`} fav={f} />
        ))}
      </div>
    </div>
  );
}
