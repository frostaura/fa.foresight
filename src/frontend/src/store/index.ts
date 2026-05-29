import { configureStore } from "@reduxjs/toolkit";
import { setupListeners } from "@reduxjs/toolkit/query";
import { api } from "./api";
import { favoritesSlice, saveToStorage, selectFavorites } from "./favoritesSlice";

export const store = configureStore({
  reducer: {
    [api.reducerPath]: api.reducer,
    favorites: favoritesSlice.reducer,
  },
  middleware: (gDM) => gDM().concat(api.middleware),
});

// Persist favorites to localStorage whenever they change.
// We subscribe after store creation so we can compare prev vs next and only write on real changes.
let prevFavItems = selectFavorites(store.getState());
store.subscribe(() => {
  const next = selectFavorites(store.getState());
  if (next !== prevFavItems) {
    prevFavItems = next;
    saveToStorage(next);
  }
});

setupListeners(store.dispatch);

export type RootState = ReturnType<typeof store.getState>;
export type AppDispatch = typeof store.dispatch;
