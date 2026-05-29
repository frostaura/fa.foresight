import { useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState } from "react";
import { useSearchParams } from "react-router-dom";
import PageHeader from "../components/PageHeader";
import { Card, Input, Button, Spinner, Empty, Select, Checkbox } from "../components/ui";
import { useDiscoverMarketsQuery, type MarketSummary } from "../store/api";
import { Search, TrendingUp, Clock, Layers, Sparkles } from "lucide-react";
import MarketCard from "../components/MarketCard";
import FavoritesBar from "../components/FavoritesBar";
import { cn } from "../lib/cn";

type ProviderTab = "polymarket" | "coming-soon";

// Future provider integrations surfaced under the Coming Soon tab. Order matches the project
// charter's adapter priority (Kalshi/Manifold/Augur).
const UPCOMING_PROVIDERS: { name: string; blurb: string }[] = [
  { name: "Kalshi", blurb: "Regulated US event contracts — CFTC-registered exchange." },
  { name: "Manifold", blurb: "Play-money community markets with deep long-tail coverage." },
  { name: "Augur", blurb: "Decentralized, on-chain prediction markets." }
];

const PAGE_SIZE = 50;

const CATEGORIES: { label: string; slug: string }[] = [
  { label: "All categories", slug: "" },
  ...[
    { label: "Business", slug: "business" },
    { label: "Climate", slug: "climate" },
    { label: "Crypto", slug: "crypto" },
    { label: "Culture", slug: "culture" },
    { label: "Economy", slug: "economy" },
    { label: "Geopolitics", slug: "geopolitics" },
    { label: "Politics", slug: "politics" },
    { label: "Science", slug: "science" },
    { label: "Sports", slug: "sports" },
    { label: "Tech", slug: "tech" }
  ].sort((a, b) => a.label.localeCompare(b.label))
];

const SORTS: { label: string; value: string }[] = [
  { label: "Deepest liquidity", value: "Liquidity" },
  { label: "Highest volume", value: "Volume" },
  { label: "Most active (24h)", value: "Volume24h" },
  { label: "Newest", value: "Newest" },
  { label: "Resolving soonest", value: "EndDate" }
].sort((a, b) => a.label.localeCompare(b.label));

const HORIZONS: { label: string; days: number | undefined }[] = [
  { label: "Any time", days: undefined },
  ...[
    { label: "Within 24h", days: 1 },
    { label: "Within 30d", days: 30 },
    { label: "Within 7d", days: 7 },
    { label: "Within 90d", days: 90 }
  ].sort((a, b) => a.label.localeCompare(b.label))
];

const MIN_VOLUMES: { label: string; value: number | undefined }[] = [
  { label: "Any volume", value: undefined },
  ...[
    { label: "≥ $1k", value: 1_000 },
    { label: "≥ $10k", value: 10_000 },
    { label: "≥ $100k", value: 100_000 },
    { label: "≥ $1M", value: 1_000_000 }
  ].sort((a, b) => a.label.localeCompare(b.label))
];

function fmtUsd(n?: number | null): string {
  if (n == null || !isFinite(n)) return "—";
  if (n >= 1_000_000_000) return `$${(n / 1_000_000_000).toFixed(2)}B`;
  if (n >= 1_000_000) return `$${(n / 1_000_000).toFixed(2)}M`;
  if (n >= 1_000) return `$${(n / 1_000).toFixed(1)}k`;
  return `$${n.toFixed(0)}`;
}

export default function Markets() {
  const [q, setQ] = useState("");
  const [debouncedQ, setDebouncedQ] = useState("");
  const [category, setCategory] = useState<string>("");
  const [sort, setSort] = useState<string>("Volume24h");
  const [horizonDays, setHorizonDays] = useState<number | undefined>(undefined);
  const [minVolume, setMinVolume] = useState<number | undefined>(undefined);
  const [includeClosed, setIncludeClosed] = useState(false);
  const [offset, setOffset] = useState(0);
  const [accumulated, setAccumulated] = useState<MarketSummary[]>([]);
  const [lastPageFull, setLastPageFull] = useState(false);

  useEffect(() => {
    const t = setTimeout(() => setDebouncedQ(q), 250);
    return () => clearTimeout(t);
  }, [q]);

  const filterKey = `${debouncedQ}|${category}|${sort}|${horizonDays ?? ""}|${minVolume ?? ""}|${includeClosed}`;
  const lastFilterKey = useRef(filterKey);
  useEffect(() => {
    if (lastFilterKey.current !== filterKey) {
      lastFilterKey.current = filterKey;
      setOffset(0);
      setAccumulated([]);
      setLastPageFull(false);
    }
  }, [filterKey]);

  const params = useMemo(
    () => ({
      q: debouncedQ || undefined,
      category: category || undefined,
      sort,
      resolvesWithinDays: horizonDays,
      minVolume,
      includeClosed: includeClosed || undefined,
      take: PAGE_SIZE,
      skip: offset
    }),
    [debouncedQ, category, sort, horizonDays, minVolume, includeClosed, offset]
  );

  const { data: page, isFetching } = useDiscoverMarketsQuery(params);

  useEffect(() => {
    if (page === undefined) return;
    setLastPageFull(page.length >= PAGE_SIZE);
    if (page.length === 0) return;
    setAccumulated((prev) => {
      const seen = new Set(prev.map((m) => `${m.providerId}:${m.externalId}`));
      const next = prev.slice();
      for (const m of page) {
        const key = `${m.providerId}:${m.externalId}`;
        if (!seen.has(key)) {
          seen.add(key);
          next.push(m);
        }
      }
      return next;
    });
  }, [page]);

  const markets = accumulated;
  const canLoadMore = !isFetching && lastPageFull;
  const totalVolume = useMemo(() => markets.reduce((acc, m) => acc + (m.volume ?? 0), 0), [markets]);

  // Provider tab — `polymarket` is the only live integration today; `coming-soon` is the
  // staging area announcing planned adapters. URL-backed so the choice survives reloads and
  // links share cleanly.
  const [searchParams, setSearchParams] = useSearchParams();
  const rawProvider = searchParams.get("provider");
  const providerTab: ProviderTab =
    rawProvider === "coming-soon" ? "coming-soon" : "polymarket";
  const setProviderTab = useCallback(
    (next: ProviderTab) => {
      setSearchParams(
        (prev) => {
          const updated = new URLSearchParams(prev);
          updated.set("provider", next);
          return updated;
        },
        { replace: true }
      );
    },
    [setSearchParams]
  );

  // Sticky-band offset measurement — mirrors PaperTrading so the sub-tab row sits flush under
  // the page header regardless of subtitle wraps or future header content.
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

  const tabs: { id: ProviderTab; label: string }[] = [
    { id: "polymarket", label: "Polymarket" },
    { id: "coming-soon", label: "Coming Soon" }
  ];

  return (
    <div>
      <div ref={headerRef} className="sticky top-0 z-30 bg-fa-ink/95 backdrop-blur">
        <PageHeader
          title="Markets"
          subtitle="Unified market discovery across pluggable prediction-market integrations."
        />
      </div>

      <div
        className="sticky z-20 bg-fa-ink/95 backdrop-blur border-b border-fa-edge px-8 pt-3"
        style={{ top: headerHeight }}
      >
        <div className="flex items-center gap-1">
          {tabs.map((t) => (
            <button
              key={t.id}
              onClick={() => setProviderTab(t.id)}
              className={cn(
                "px-4 py-2 text-sm border-b-2 -mb-px transition flex items-center gap-2",
                providerTab === t.id
                  ? "text-fa-frost-bright border-fa-frost-bright"
                  : "text-fa-frost-dim border-transparent hover:text-fa-frost-bright"
              )}
            >
              {t.id === "polymarket" && (
                <PolymarketMark
                  className={`h-3.5 w-3.5 ${
                    providerTab === t.id ? "text-fa-frost-bright" : "text-fa-frost-dim"
                  }`}
                />
              )}
              {t.id === "coming-soon" && (
                <Sparkles
                  className={`h-3.5 w-3.5 ${providerTab === t.id ? "text-amber-300" : ""}`}
                />
              )}
              {t.label}
            </button>
          ))}
        </div>
      </div>

      {providerTab === "coming-soon" ? (
        <div className="p-8">
          <ComingSoonProviders />
        </div>
      ) : (
      <div className="p-8 space-y-6">
        <p className="text-fa-frost-dim text-sm">
          Live discovery from Polymarket via the Gamma API.
        </p>
        <FavoritesBar />

        <Card>
          <div className="relative">
            <Search className="h-4 w-4 absolute left-3 top-1/2 -translate-y-1/2 text-fa-frost-dim" />
            <Input
              value={q}
              onChange={(e) => setQ(e.target.value)}
              placeholder="Search Polymarket markets…"
              className="pl-10"
            />
          </div>

          <div className="mt-4 grid grid-cols-2 md:grid-cols-4 gap-2">
            <div>
              <label className="text-[10px] uppercase tracking-wider text-fa-frost-dim flex items-center gap-1 mb-1">
                <Layers className="h-3 w-3" /> Category
              </label>
              <Select value={category} onChange={(e) => setCategory(e.target.value)}>
                {CATEGORIES.map((c) => (
                  <option key={c.slug} value={c.slug}>{c.label}</option>
                ))}
              </Select>
            </div>
            <div>
              <label className="text-[10px] uppercase tracking-wider text-fa-frost-dim flex items-center gap-1 mb-1">
                <TrendingUp className="h-3 w-3" /> Sort
              </label>
              <Select value={sort} onChange={(e) => setSort(e.target.value)}>
                {SORTS.map((s) => (
                  <option key={s.value} value={s.value}>{s.label}</option>
                ))}
              </Select>
            </div>
            <div>
              <label className="text-[10px] uppercase tracking-wider text-fa-frost-dim flex items-center gap-1 mb-1">
                <Clock className="h-3 w-3" /> Resolves
              </label>
              <Select
                value={horizonDays?.toString() ?? ""}
                onChange={(e) => setHorizonDays(e.target.value ? Number(e.target.value) : undefined)}
              >
                {HORIZONS.map((h) => (
                  <option key={h.label} value={h.days?.toString() ?? ""}>{h.label}</option>
                ))}
              </Select>
            </div>
            <div>
              <label className="text-[10px] uppercase tracking-wider text-fa-frost-dim mb-1 block">
                Min volume
              </label>
              <Select
                value={minVolume?.toString() ?? ""}
                onChange={(e) => setMinVolume(e.target.value ? Number(e.target.value) : undefined)}
              >
                {MIN_VOLUMES.map((v) => (
                  <option key={v.label} value={v.value?.toString() ?? ""}>{v.label}</option>
                ))}
              </Select>
            </div>
          </div>

          <div className="mt-3">
            <Checkbox
              label="Include closed markets"
              checked={includeClosed}
              onChange={(e) => setIncludeClosed(e.target.checked)}
            />
          </div>
        </Card>

        {markets.length > 0 && (
          <div className="text-fa-frost-dim text-xs flex items-center gap-3">
            <span className="flex items-center gap-2">
              {isFetching && <Spinner />}
              {markets.length} markets
            </span>
            <span>•</span>
            <span>{fmtUsd(totalVolume)} total volume</span>
          </div>
        )}

        {isFetching && markets.length === 0 ? (
          <div className="text-fa-frost-dim text-sm flex items-center gap-2"><Spinner /> Loading markets…</div>
        ) : markets.length === 0 ? (
          <Empty title="No markets found" hint="Try a broader search term, a different category, or relax the filters above." />
        ) : (
          <div className="grid grid-cols-1 md:grid-cols-2 xl:grid-cols-3 gap-4">
            {markets.map((m) => (
              <MarketCard key={`${m.providerId}-${m.externalId}`} market={m} />
            ))}
          </div>
        )}

        {markets.length > 0 && canLoadMore && (
          <div className="flex justify-center pt-2">
            <Button variant="ghost" onClick={() => setOffset((o) => o + PAGE_SIZE)}>
              Load more
            </Button>
          </div>
        )}
      </div>
      )}
    </div>
  );
}

function PolymarketMark({ className }: { className?: string }) {
  return (
    <svg
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      strokeWidth="1.75"
      strokeLinecap="round"
      strokeLinejoin="round"
      aria-hidden
      className={className}
    >
      <rect x="3" y="3" width="18" height="18" rx="4.5" />
      <path d="M9 17 V 8 H 13.25 A 2.75 2.75 0 0 1 13.25 13.5 H 9" />
    </svg>
  );
}

function ComingSoonProviders() {
  return (
    <div className="mx-auto max-w-2xl">
      <div className="fa-card px-8 py-10 text-center">
        <div className="mx-auto inline-flex items-center justify-center h-12 w-12 rounded-full border border-fa-edge bg-fa-glass">
          <Sparkles className="h-5 w-5 text-amber-300" />
        </div>
        <h2 className="mt-5 text-fa-frost-bright text-lg font-light tracking-tight">
          More integrations coming soon
        </h2>
        <p className="mt-3 text-fa-frost-dim text-sm max-w-md mx-auto">
          Polymarket is the only supported markets integration today. We're building adapters
          for the next wave of providers — calibration, evidence, and forecasting tooling will
          work across all of them.
        </p>

        <div className="mt-8 grid grid-cols-1 sm:grid-cols-3 gap-3 text-left">
          {UPCOMING_PROVIDERS.map((p) => (
            <div
              key={p.name}
              className="rounded-md border border-fa-edge/60 px-4 py-3"
            >
              <div className="flex items-center justify-between mb-1">
                <span className="text-fa-frost-bright text-sm font-medium">{p.name}</span>
                <span className="inline-flex items-center rounded-md border border-fa-edge bg-fa-glass px-1.5 py-0.5 text-[10px] uppercase tracking-wider tabular-nums text-fa-frost-dim">
                  Planned
                </span>
              </div>
              <p className="text-fa-frost-dim text-xs leading-snug">{p.blurb}</p>
            </div>
          ))}
        </div>
      </div>
    </div>
  );
}
