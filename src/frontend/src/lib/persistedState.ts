import { useState, useEffect, useRef } from "react";

/**
 * Drop-in replacement for `useState` that mirrors the value into `localStorage` under a fixed key.
 * On mount: reads localStorage first, falls back to `initial` if missing/unreadable. On every set:
 * writes the new value back as JSON. Used for sticky UI selections (form fields, picker choices,
 * tab cursors) that should survive a page reload without round-tripping through the backend.
 *
 * Keep keys descriptive and namespaced (e.g. `fa.backtesting.symbol`) so unrelated features can't
 * accidentally clobber each other in shared localStorage.
 */
export function useLocalStorageState<T>(
  key: string,
  initial: T | (() => T),
): [T, (next: T | ((prev: T) => T)) => void] {
  const [value, setValue] = useState<T>(() => {
    if (typeof window === "undefined") {
      return typeof initial === "function" ? (initial as () => T)() : initial;
    }
    try {
      const raw = window.localStorage.getItem(key);
      if (raw == null) return typeof initial === "function" ? (initial as () => T)() : initial;
      return JSON.parse(raw) as T;
    } catch {
      // Corrupt JSON / disabled storage — fall back to the initial value.
      return typeof initial === "function" ? (initial as () => T)() : initial;
    }
  });

  // Persist on every change. Skip the first paint so we don't rewrite the same value we just read.
  const firstRun = useRef(true);
  useEffect(() => {
    if (firstRun.current) { firstRun.current = false; return; }
    try {
      window.localStorage.setItem(key, JSON.stringify(value));
    } catch {
      // Quota exceeded / cookies disabled — best-effort persistence; non-fatal.
    }
  }, [key, value]);

  return [value, setValue];
}
