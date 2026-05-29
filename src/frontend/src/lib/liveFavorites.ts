import { useCallback, useState } from "react";

// Per-(symbol, interval) favoriting for the Paper Trading view.
// Backed by localStorage only — the server-side favorites endpoint was removed in WS F as
// part of the left-behind paradigm cleanup. The chart still uses this to drive the
// Favorites sub-tab in Paper Trading.

export interface LiveTimeframeFav {
  symbol: string;
  interval: string;
}

const STORAGE_KEY = "fa.foresight.liveTimeframeFavorites";

function read(): LiveTimeframeFav[] {
  try {
    const raw = localStorage.getItem(STORAGE_KEY);
    if (!raw) return [];
    const parsed = JSON.parse(raw);
    if (!Array.isArray(parsed)) return [];
    return parsed.filter(
      (e): e is LiveTimeframeFav =>
        e && typeof e.symbol === "string" && typeof e.interval === "string"
    );
  } catch {
    return [];
  }
}

function write(list: LiveTimeframeFav[]) {
  try {
    localStorage.setItem(STORAGE_KEY, JSON.stringify(list));
  } catch { /* ignore */ }
}

export function useLiveTimeframeFavorites() {
  const [favorites, setFavorites] = useState<LiveTimeframeFav[]>(() => read());

  const isFav = useCallback(
    (symbol: string, interval: string) =>
      favorites.some((f) => f.symbol === symbol && f.interval === interval),
    [favorites]
  );

  const toggle = useCallback(
    (symbol: string, interval: string) => {
      setFavorites((prev) => {
        const exists = prev.some((f) => f.symbol === symbol && f.interval === interval);
        const next = exists
          ? prev.filter((f) => !(f.symbol === symbol && f.interval === interval))
          : [...prev, { symbol, interval }];
        write(next);
        return next;
      });
    },
    []
  );

  return { favorites, isFav, toggle };
}
