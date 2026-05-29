import { useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState } from "react";
import { useSearchParams } from "react-router-dom";
import PageHeader from "../components/PageHeader";
import LiveBitcoinChart, { type ChartKind } from "../components/LiveBitcoinChart";
import { useLivePrice, type BinanceInterval } from "../lib/binance";
import { useLiveTimeframeFavorites } from "../lib/liveFavorites";
import { Star } from "lucide-react";
import { Empty } from "../components/ui";
import { cn } from "../lib/cn";
import { SymbolPicker } from "../components/SymbolIcon";

// Only BTC for now per the brief. Structured so adding ETH/SOL later is just a list extension.
const ASSETS: { symbol: string; label: string }[] = [
  { symbol: "BTCUSDT", label: "Bitcoin (BTC/USDT)" }
];

const INTERVALS: BinanceInterval[] = ["1m", "5m", "15m"];

const CHART_KINDS: { value: ChartKind; label: string }[] = [
  { value: "line", label: "Line" },
  { value: "bar", label: "Bar" },
  { value: "candle", label: "Candle" }
];

const MIN_VISIBLE = 5;
const MAX_VISIBLE = 150;
const DEFAULT_VISIBLE = 15;

type SubTab = "all" | "favorites";

export default function PaperTrading() {
  const [symbol, setSymbol] = useState<string>(ASSETS[0].symbol);
  const [kind, setKind] = useState<ChartKind>("candle");
  const [visibleCount, setVisibleCount] = useState<number>(DEFAULT_VISIBLE);
  const [searchParams, setSearchParams] = useSearchParams();
  const { isFav, favorites } = useLiveTimeframeFavorites();

  // The view defaults to Favorites whenever the user has any pinned timeframes for the current
  // symbol; otherwise it falls back to All. An explicit ?view= param always wins so the URL stays
  // sharable and the user's last click survives a reload.
  const favCountForSymbol = useMemo(
    () => favorites.filter((f) => f.symbol === symbol).length,
    [favorites, symbol]
  );
  const rawView = searchParams.get("view");
  const subTab: SubTab =
    rawView === "favorites" || rawView === "all"
      ? rawView
      : favCountForSymbol > 0
        ? "favorites"
        : "all";
  const setSubTab = useCallback(
    (next: SubTab) => {
      setSearchParams(
        (prev) => {
          const updated = new URLSearchParams(prev);
          updated.set("view", next);
          return updated;
        },
        { replace: true }
      );
    },
    [setSearchParams]
  );

  const shownIntervals = useMemo(
    () => (subTab === "favorites" ? INTERVALS.filter((iv) => isFav(symbol, iv)) : INTERVALS),
    [subTab, isFav, symbol]
  );

  // Track the rendered height of the sticky page header so the sub-tab bar can stick directly
  // beneath it without overlap or a fragile hard-coded offset. The initial value matches the
  // PageHeader's known dimensions (px-8 py-6 + text-2xl title + text-sm subtitle + 1px border =
  // ~105px) so the tab band is positioned correctly on the very first paint, before the layout
  // effect measures the real value. useLayoutEffect updates synchronously after DOM mutations
  // and the ResizeObserver covers subtitle wraps or any future header content changes.
  const headerRef = useRef<HTMLDivElement | null>(null);
  const [headerHeight, setHeaderHeight] = useState(105);
  useLayoutEffect(() => {
    const el = headerRef.current;
    if (!el) return;
    const update = () => setHeaderHeight(el.offsetHeight);
    update();
    const ro = new ResizeObserver(update);
    ro.observe(el);
    return () => ro.disconnect();
  }, []);

  // Quick visual flash on price change — green up, red down — so the ticker reads as alive even
  // without a chart in view.
  const { price, prev } = useLivePrice(symbol);
  const [flash, setFlash] = useState<"up" | "down" | null>(null);
  useEffect(() => {
    if (price == null || prev == null) return;
    if (price === prev) return;
    setFlash(price > prev ? "up" : "down");
    const id = window.setTimeout(() => setFlash(null), 400);
    return () => window.clearTimeout(id);
  }, [price, prev]);

  return (
    <div>
      {/* Two independently-sticky bands: the page header pins to top:0; the sub-tab bar pins to
          top:<header height>. The controls card between them scrolls under the header normally. */}
      <div ref={headerRef} className="sticky top-0 z-30 bg-fa-ink/95 backdrop-blur">
        <PageHeader
          title="Paper Trading"
          subtitle="Live spot prices with candle-aligned predictions and paper-trade ledger."
        />
      </div>

      <div className="px-8 pt-5 space-y-5">
        {/* Single horizontal row, no wrap. Each item is a flex column with the label at the top
            (so all labels share a y-baseline regardless of control height) and the control at the
            bottom. Vertical hairlines separate items. */}
        <div className="fa-card px-6 py-4 flex items-stretch overflow-x-auto">
          <div className="flex flex-col justify-between shrink-0">
            <div className="text-fa-frost-dim text-[10px] uppercase tracking-wider mb-2">Asset</div>
            <SymbolPicker
              symbols={ASSETS.map((a) => a.symbol)}
              value={symbol}
              onChange={setSymbol}
              size="md"
              labelFn={(s) => ASSETS.find((a) => a.symbol === s)?.label ?? s}
            />
          </div>

          <div className="w-px bg-fa-edge mx-5 self-stretch shrink-0" aria-hidden />

          <div className="flex flex-col justify-between shrink-0">
            <div className="text-fa-frost-dim text-[10px] uppercase tracking-wider mb-2">Last price</div>
            <div
              className={`text-2xl font-light tabular-nums leading-none transition-colors duration-200 ${
                flash === "up" ? "text-emerald-300" : flash === "down" ? "text-rose-300" : "text-fa-frost-bright"
              }`}
            >
              {price != null ? `$${price.toLocaleString(undefined, { minimumFractionDigits: 2, maximumFractionDigits: 2 })}` : "—"}
            </div>
          </div>

          <div className="w-px bg-fa-edge mx-5 self-stretch shrink-0" aria-hidden />

          <div className="flex flex-col justify-between flex-1 min-w-[220px]">
            <div className="flex items-baseline justify-between mb-2">
              <span className="text-fa-frost-dim text-[10px] uppercase tracking-wider">Visible candles</span>
              <span className="text-fa-frost-bright text-xs tabular-nums">{visibleCount}</span>
            </div>
            <input
              type="range"
              min={MIN_VISIBLE}
              max={MAX_VISIBLE}
              step={1}
              value={visibleCount}
              onChange={(e) => setVisibleCount(Number(e.target.value))}
              className="fa-range"
              // --fa-range-fill drives the filled portion of the WebKit track gradient. Recomputed
              // every render against the current min/max so the fill always aligns with the thumb.
              style={{
                ["--fa-range-fill" as string]:
                  `${((visibleCount - MIN_VISIBLE) / (MAX_VISIBLE - MIN_VISIBLE)) * 100}%`
              }}
              aria-label="Visible candles"
            />
            <div className="flex justify-between text-[10px] text-fa-frost-dim tabular-nums mt-0.5">
              <span>{MIN_VISIBLE}</span>
              <span>{MAX_VISIBLE}</span>
            </div>
          </div>

          <div className="w-px bg-fa-edge mx-5 self-stretch shrink-0" aria-hidden />

          <div className="flex flex-col justify-between shrink-0">
            <div className="text-fa-frost-dim text-[10px] uppercase tracking-wider mb-2">Chart type</div>
            <div className="flex gap-1 rounded-md border border-fa-edge p-0.5">
              {CHART_KINDS.map((k) => (
                <button
                  key={k.value}
                  onClick={() => setKind(k.value)}
                  className={`px-4 py-1.5 text-xs rounded transition ${
                    kind === k.value
                      ? "bg-fa-frost/20 text-fa-frost-bright"
                      : "text-fa-frost-dim hover:text-fa-frost-bright"
                  }`}
                >
                  {k.label}
                </button>
              ))}
            </div>
          </div>
        </div>
      </div>

      {/* Sticky sub-tab band: pins right below the sticky page header. `top` is set from the
          measured header height so the two bands sit flush regardless of subtitle wraps. */}
      <div
        className="sticky z-20 bg-fa-ink/95 backdrop-blur border-b border-fa-edge px-8 pt-3"
        style={{ top: headerHeight }}
      >
        <div className="flex items-center gap-1">
          {(() => {
            const allTab = { id: "all" as SubTab, label: "All", count: INTERVALS.length };
            const favTab = { id: "favorites" as SubTab, label: "Favorites", count: favCountForSymbol };
            // Favorites tab is hidden entirely when nothing's pinned for this symbol. When at
            // least one is pinned it sits in the first slot so the user lands on it by default.
            return favCountForSymbol > 0 ? [favTab, allTab] : [allTab];
          })().map((t) => (
            <button
              key={t.id}
              onClick={() => setSubTab(t.id)}
              className={cn(
                "px-4 py-2 text-sm border-b-2 -mb-px transition flex items-center gap-2",
                subTab === t.id
                  ? "text-fa-frost-bright border-fa-frost-bright"
                  : "text-fa-frost-dim border-transparent hover:text-fa-frost-bright"
              )}
            >
              {t.id === "favorites" && <Star className={`h-3.5 w-3.5 ${subTab === t.id ? "text-amber-300" : ""}`} fill={subTab === t.id ? "currentColor" : "none"} />}
              {t.label}
              <span className="text-[10px] text-fa-frost-dim tabular-nums">{t.count}</span>
            </button>
          ))}
        </div>
      </div>

      <div className="p-8">
        {shownIntervals.length === 0 ? (
          <Empty
            title="No favorited timeframes yet"
            hint="Click the star on any timeframe card under All to pin it here."
          />
        ) : (
          <div className="grid grid-cols-1 lg:grid-cols-2 2xl:grid-cols-3 gap-4">
            {shownIntervals.map((iv) => (
              <LiveBitcoinChart key={`${symbol}-${iv}`} symbol={symbol} interval={iv} kind={kind} visibleCount={visibleCount} />
            ))}
          </div>
        )}
      </div>
    </div>
  );
}
