// Per-(symbol, interval) favoriting for the Paper Trading view.
// Source of truth is the Redux favorites slice — reads via useSelector for instant cross-component
// updates, writes via dispatch(toggleFavorite). localStorage persistence is handled by the store
// subscriber in store/index.ts so it survives reload.

export type { LiveTimeframeFav } from "../store/favoritesSlice";

import { useCallback } from "react";
import { useSelector, useDispatch } from "react-redux";
import { toggleFavorite, selectFavorites } from "../store/favoritesSlice";

export function useLiveTimeframeFavorites() {
  const favorites = useSelector(selectFavorites);
  const dispatch = useDispatch();

  const isFav = useCallback(
    (symbol: string, interval: string) =>
      favorites.some((f) => f.symbol === symbol && f.interval === interval),
    [favorites]
  );

  const toggle = useCallback(
    (symbol: string, interval: string) => {
      dispatch(toggleFavorite({ symbol, interval }));
    },
    [dispatch]
  );

  return { favorites, isFav, toggle };
}
