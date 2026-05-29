import { useCallback, useEffect } from "react";
import {
  useListFavoritesQuery,
  useAddFavoriteMutation,
  useRemoveFavoriteMutation
} from "../store/api";

// Per-(symbol, interval) favoriting for the Live Forecasts view. Server-backed in iter-3 — the
// presence of a favorite is what tells the backend gap-filler to keep running predictions for
// this bucket (along with any active paper session). Removes wasted LLM spend on cards nobody is
// looking at. Replaces the prior localStorage-only implementation.

export interface LiveTimeframeFav {
  symbol: string;
  interval: string;
}

const LEGACY_STORAGE_KEY = "fa.foresight.liveTimeframeFavorites";
const MIGRATION_MARKER = "fa.foresight.liveTimeframeFavorites.migrated";

export function useLiveTimeframeFavorites() {
  const { data: favs = [] } = useListFavoritesQuery();
  const [add] = useAddFavoriteMutation();
  const [remove] = useRemoveFavoriteMutation();

  // One-time migration from the old localStorage-only impl. Runs once per browser; if the server
  // already has favorites we still mark migrated so we don't keep retrying with stale local data.
  useEffect(() => {
    if (typeof localStorage === "undefined") return;
    if (localStorage.getItem(MIGRATION_MARKER) === "1") return;
    try {
      const raw = localStorage.getItem(LEGACY_STORAGE_KEY);
      if (!raw) {
        localStorage.setItem(MIGRATION_MARKER, "1");
        return;
      }
      const parsed = JSON.parse(raw);
      if (!Array.isArray(parsed)) {
        localStorage.setItem(MIGRATION_MARKER, "1");
        return;
      }
      const valid = parsed.filter(
        (e): e is LiveTimeframeFav =>
          e && typeof e.symbol === "string" && typeof e.interval === "string"
      );
      // Fire-and-forget; the server treats duplicates as no-ops.
      Promise.all(valid.map((f) => add({ symbol: f.symbol, interval: f.interval }).unwrap().catch(() => undefined)))
        .finally(() => {
          localStorage.setItem(MIGRATION_MARKER, "1");
          localStorage.removeItem(LEGACY_STORAGE_KEY);
        });
    } catch {
      localStorage.setItem(MIGRATION_MARKER, "1");
    }
  }, [add]);

  const favorites: LiveTimeframeFav[] = favs.map((f) => ({ symbol: f.symbol, interval: f.interval }));

  const isFav = useCallback(
    (symbol: string, interval: string) =>
      favorites.some((f) => f.symbol === symbol && f.interval === interval),
    [favorites]
  );

  const toggle = useCallback(
    (symbol: string, interval: string) => {
      const exists = favorites.some((f) => f.symbol === symbol && f.interval === interval);
      if (exists) {
        void remove({ symbol, interval });
      } else {
        void add({ symbol, interval });
      }
    },
    [favorites, add, remove]
  );

  return { favorites, isFav, toggle };
}
