import { createSlice, type PayloadAction } from "@reduxjs/toolkit";
import type { RootState } from "./index";

// ── Persistence helpers ──────────────────────────────────────────────────────────────────────
// Source of truth is Redux; localStorage is the persistence layer loaded on startup and synced
// via a store subscriber. Every consumer gets instant re-renders via useSelector.

export interface LiveTimeframeFav {
  symbol: string;
  interval: string;
}

const STORAGE_KEY = "fa.foresight.liveTimeframeFavorites";

function loadFromStorage(): LiveTimeframeFav[] {
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

export function saveToStorage(list: LiveTimeframeFav[]) {
  try {
    localStorage.setItem(STORAGE_KEY, JSON.stringify(list));
  } catch {
    /* ignore quota errors */
  }
}

// ── Slice ────────────────────────────────────────────────────────────────────────────────────

interface FavoritesState {
  items: LiveTimeframeFav[];
}

const initialState: FavoritesState = {
  items: loadFromStorage(),
};

export const favoritesSlice = createSlice({
  name: "favorites",
  initialState,
  reducers: {
    toggleFavorite(state, action: PayloadAction<{ symbol: string; interval: string }>) {
      const { symbol, interval } = action.payload;
      const idx = state.items.findIndex(
        (f) => f.symbol === symbol && f.interval === interval
      );
      if (idx >= 0) {
        state.items.splice(idx, 1);
      } else {
        state.items.push({ symbol, interval });
      }
    },
    setFavorites(state, action: PayloadAction<LiveTimeframeFav[]>) {
      state.items = action.payload;
    },
  },
});

export const { toggleFavorite, setFavorites } = favoritesSlice.actions;

// Selectors
export const selectFavorites = (state: RootState) => state.favorites.items;
export const selectIsFav =
  (symbol: string, interval: string) =>
  (state: RootState) =>
    state.favorites.items.some(
      (f) => f.symbol === symbol && f.interval === interval
    );
