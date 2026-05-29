import { useState, useMemo, useCallback } from "react";

export type SortDir = "asc" | "desc";

export type SortAccessor<T> = (row: T) => unknown;

export interface SortState<Key extends string> {
  key: Key | null;
  dir: SortDir;
}

/**
 * Generic column-sort hook. Caller defines a map of `{ columnKey: accessor }` returning whatever
 * value the column displays (number, string, Date, null). Click cycling: unsorted → desc → asc →
 * unsorted (back to natural order). Nulls / undefineds always sink to the bottom regardless of
 * direction so a sparse column doesn't dominate the head of the table.
 *
 * For grouped tables (e.g. backtest runs grouped by batchId) pass a `groupBy` accessor — sort
 * happens *within* each group, group order is preserved by first-appearance in the input array.
 * Groupless tables omit it and get a normal full-array sort.
 */
export function useSort<T, Key extends string>(
  rows: readonly T[],
  accessors: Record<Key, SortAccessor<T>>,
  options?: { defaultKey?: Key; defaultDir?: SortDir; groupBy?: (row: T) => string | null | undefined },
) {
  const [state, setState] = useState<SortState<Key>>({
    key: options?.defaultKey ?? null,
    dir: options?.defaultDir ?? "desc",
  });

  const onHeaderClick = useCallback((key: Key) => {
    setState((prev) => {
      if (prev.key !== key) return { key, dir: "desc" };
      if (prev.dir === "desc") return { key, dir: "asc" };
      // Third click resets to natural order — same as clicking another column then clearing.
      return { key: null, dir: "desc" };
    });
  }, []);

  const sortedRows = useMemo(() => {
    if (state.key == null) return rows.slice();
    const accessor = accessors[state.key];
    const dir = state.dir === "asc" ? 1 : -1;
    const compare = (a: T, b: T) => {
      const va = accessor(a);
      const vb = accessor(b);
      // Nulls always sink, regardless of direction. (asc-with-nulls-at-bottom is what users
      // actually want — sorting "Hit rate ascending" shouldn't bury the not-yet-completed
      // rows at the top.)
      const aNull = va == null;
      const bNull = vb == null;
      if (aNull && bNull) return 0;
      if (aNull) return 1;
      if (bNull) return -1;
      if (typeof va === "number" && typeof vb === "number") return (va - vb) * dir;
      if (va instanceof Date && vb instanceof Date) return (va.getTime() - vb.getTime()) * dir;
      const sa = String(va);
      const sb = String(vb);
      return sa.localeCompare(sb) * dir;
    };

    if (!options?.groupBy) return rows.slice().sort(compare);

    // Group-aware: preserve group order by first-appearance, sort within each group.
    const groups: Map<string, T[]> = new Map();
    const groupOrder: string[] = [];
    for (const r of rows) {
      const g = options.groupBy(r) ?? "__ungrouped__";
      if (!groups.has(g)) {
        groups.set(g, []);
        groupOrder.push(g);
      }
      groups.get(g)!.push(r);
    }
    for (const g of groupOrder) groups.get(g)!.sort(compare);
    return groupOrder.flatMap((g) => groups.get(g)!);
  }, [rows, state, accessors, options]);

  const headerProps = useCallback((key: Key) => ({
    sortKey: key,
    activeKey: state.key,
    dir: state.dir,
    onClick: () => onHeaderClick(key),
  }), [state, onHeaderClick]);

  return { state, sortedRows, headerProps, onHeaderClick };
}

/**
 * Headless sort-header rendering helper. Caller wraps their own `<th>` and passes the props from
 * `headerProps(key)` plus the column label. Renders an indicator chevron when this column is the
 * active sort. Keeps the existing per-table `<th>` className freedom (some tables left-align, some
 * right-align, some have custom widths) — we only own the inner content + the cursor / hover hint.
 */
import type { ReactNode } from "react";

export function SortHeader<Key extends string>(props: {
  sortKey: Key;
  activeKey: Key | null;
  dir: SortDir;
  onClick: () => void;
  children: ReactNode;
  align?: "left" | "right";
}) {
  const active = props.activeKey === props.sortKey;
  const arrow = !active ? "" : props.dir === "asc" ? "↑" : "↓";
  return (
    <button
      type="button"
      onClick={props.onClick}
      className={`inline-flex items-center gap-1 cursor-pointer select-none transition hover:text-fa-frost-bright ${active ? "text-fa-frost-bright" : ""}`}
      style={{ flexDirection: props.align === "right" ? "row-reverse" : "row" }}
    >
      <span>{props.children}</span>
      {arrow && <span className="text-[10px] leading-none">{arrow}</span>}
    </button>
  );
}
