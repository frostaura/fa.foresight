import { useCallback, useEffect, useState } from "react";

export interface FavoriteRef {
  providerId: string;
  externalId: string;
}

const STORAGE_KEY = "fa.foresight.favorites";
const EVENT = "fa-favorites-changed";

function read(): FavoriteRef[] {
  if (typeof localStorage === "undefined") return [];
  try {
    const raw = localStorage.getItem(STORAGE_KEY);
    if (!raw) return [];
    const parsed = JSON.parse(raw);
    if (!Array.isArray(parsed)) return [];
    return parsed.filter(
      (e): e is FavoriteRef =>
        e && typeof e.providerId === "string" && typeof e.externalId === "string"
    );
  } catch {
    return [];
  }
}

function write(list: FavoriteRef[]): void {
  localStorage.setItem(STORAGE_KEY, JSON.stringify(list));
  window.dispatchEvent(new Event(EVENT));
}

export function useFavorites() {
  const [list, setList] = useState<FavoriteRef[]>(() => read());

  useEffect(() => {
    const sync = () => setList(read());
    window.addEventListener(EVENT, sync);
    window.addEventListener("storage", sync);
    return () => {
      window.removeEventListener(EVENT, sync);
      window.removeEventListener("storage", sync);
    };
  }, []);

  const isFav = useCallback(
    (providerId: string, externalId: string) =>
      list.some((f) => f.providerId === providerId && f.externalId === externalId),
    [list]
  );

  const toggle = useCallback((providerId: string, externalId: string) => {
    const current = read();
    const idx = current.findIndex(
      (f) => f.providerId === providerId && f.externalId === externalId
    );
    if (idx >= 0) {
      current.splice(idx, 1);
    } else {
      current.unshift({ providerId, externalId });
    }
    write(current);
  }, []);

  return { favorites: list, isFav, toggle };
}
