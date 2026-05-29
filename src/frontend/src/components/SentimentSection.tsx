import { useEffect, useMemo, useRef, useState } from "react";
import { ExternalLink, Minus, Newspaper, TrendingDown, TrendingUp } from "lucide-react";
import { Spinner } from "./ui";

const YES_FILL = "var(--fa-success, #7CE3B6)";
const NO_FILL = "var(--fa-danger, #F08484)";
const NEUTRAL = "var(--fa-frost-dim, #5C8AB4)";

interface ArticleState {
  index: number;
  url: string;
  title: string;
  source: string;
  publishedAt: string | null;
  score: number | null;
  label: string | null;
}

interface DayBucket {
  day: string;
  averageScore: number;
  count: number;
}

interface Summary {
  overallScore: number;
  positiveCount: number;
  negativeCount: number;
  neutralCount: number;
  buckets: DayBucket[];
}

function fmtAge(iso: string | null): string {
  if (!iso) return "?";
  const d = new Date(iso);
  const h = (Date.now() - d.getTime()) / 36e5;
  if (h < 1) return `${Math.max(1, Math.round(h * 60))}m`;
  if (h < 24) return `${Math.round(h)}h`;
  return `${Math.round(h / 24)}d`;
}

function scoreColor(score: number): string {
  if (score > 0.2) return YES_FILL;
  if (score < -0.2) return NO_FILL;
  return NEUTRAL;
}

function ScoreChip({ score, label }: { score: number; label: string }) {
  const Icon = score > 0.2 ? TrendingUp : score < -0.2 ? TrendingDown : Minus;
  const color = scoreColor(score);
  return (
    <span
      className="inline-flex items-center gap-1 rounded-full border px-1.5 py-0.5 text-[10px] tabular-nums shrink-0"
      style={{ borderColor: `${color}33`, color }}
    >
      <Icon className="h-3 w-3" />
      {label.toUpperCase()}
    </span>
  );
}

function ScoringDot() {
  return (
    <span className="inline-flex items-center gap-1 rounded-full border border-fa-edge px-1.5 py-0.5 text-[10px] text-fa-frost-dim shrink-0">
      <span className="h-1 w-1 rounded-full bg-fa-frost-dim animate-pulse" />
      …
    </span>
  );
}

function SentimentStrip({ buckets }: { buckets: DayBucket[] }) {
  if (buckets.length === 0) return null;
  return (
    <div className="flex items-stretch gap-px h-6 rounded overflow-hidden border border-fa-edge">
      {buckets.map((b) => {
        const intensity = Math.min(1, Math.abs(b.averageScore)) * 0.85 + 0.15;
        const color = scoreColor(b.averageScore);
        return (
          <div
            key={b.day}
            className="flex-1 min-w-[6px]"
            style={{ background: color, opacity: intensity }}
            title={`${new Date(b.day).toLocaleDateString()} · avg ${b.averageScore.toFixed(2)} · ${b.count} article${b.count === 1 ? "" : "s"}`}
          />
        );
      })}
    </div>
  );
}

function SummaryBar({ s }: { s: Summary }) {
  const total = s.positiveCount + s.negativeCount + s.neutralCount;
  return (
    <div className="flex items-center gap-2 text-xs tabular-nums text-fa-frost-dim">
      <span>of {total} · avg {s.overallScore.toFixed(2)}</span>
    </div>
  );
}

function StackedSentimentBar({ pos, neu, neg }: { pos: number; neu: number; neg: number }) {
  const total = Math.max(1, pos + neu + neg);
  const segments = [
    { key: "neg", count: neg, color: NO_FILL },
    { key: "neu", count: neu, color: NEUTRAL },
    { key: "pos", count: pos, color: YES_FILL }
  ];
  return (
    <div className="h-2 rounded-full overflow-hidden flex bg-fa-glass border border-fa-edge">
      {segments.map((s) => (
        <div
          key={s.key}
          className="h-full transition-all duration-[var(--fa-duration)]"
          style={{
            width: `${(s.count / total) * 100}%`,
            background: s.color,
            opacity: s.count > 0 ? 0.9 : 0,
            transitionTimingFunction: "var(--fa-ease)"
          }}
          title={`${s.key === "pos" ? "Positive" : s.key === "neg" ? "Negative" : "Neutral"}: ${s.count}`}
        />
      ))}
    </div>
  );
}

type LabelKey = "pos" | "neu" | "neg";

function FilterChip({
  active, color, icon: Icon, label, count, onToggle
}: {
  active: boolean; color: string; icon: React.ComponentType<{ className?: string }>; label: string; count: number; onToggle: () => void;
}) {
  return (
    <button
      type="button"
      onClick={onToggle}
      aria-pressed={active}
      className="inline-flex items-center gap-1.5 rounded-full border px-2 py-0.5 text-[11px] tabular-nums transition"
      style={{
        borderColor: active ? color : "var(--fa-edge, rgba(164, 212, 244, 0.18))",
        color: active ? color : "var(--fa-frost-dim, #5C8AB4)",
        background: active ? `${color}1A` : "transparent",
        opacity: active ? 1 : 0.65
      }}
    >
      <Icon className="h-3 w-3" />
      <span>{label}</span>
      <span className="text-[10px] opacity-80">{count}</span>
    </button>
  );
}

interface PhaseEvent { phase: string; detail: string }
interface ArticleEvent { index: number; url: string; title: string; source: string; publishedAt: string | null }
interface ScoreEvent { index: number; score: number; label: string }
interface DoneEvent { overallScore: number; positiveCount: number; negativeCount: number; neutralCount: number; buckets: DayBucket[] }

type ParsedEvent =
  | { type: "phase"; data: PhaseEvent }
  | { type: "article"; data: ArticleEvent }
  | { type: "score"; data: ScoreEvent }
  | { type: "done"; data: DoneEvent }
  | { type: "error"; data: { message: string } };

export default function SentimentSection({ providerId, externalId }: { providerId: string; externalId: string }) {
  const [phase, setPhase] = useState<string>("Connecting…");
  const [articles, setArticles] = useState<ArticleState[]>([]);
  const [summary, setSummary] = useState<Summary | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [attempt, setAttempt] = useState(0);
  const [filters, setFilters] = useState<Set<LabelKey>>(() => new Set<LabelKey>(["pos", "neu", "neg"]));
  const abortRef = useRef<AbortController | null>(null);

  const toggleFilter = (k: LabelKey) => {
    setFilters((prev) => {
      const next = new Set(prev);
      if (next.has(k)) next.delete(k);
      else next.add(k);
      // Empty filter set is treated as "show all" so the UI never appears broken with zero rows.
      return next.size === 0 ? new Set<LabelKey>(["pos", "neu", "neg"]) : next;
    });
  };

  useEffect(() => {
    setPhase("Connecting…");
    setArticles([]);
    setSummary(null);
    setError(null);

    const ac = new AbortController();
    abortRef.current?.abort();
    abortRef.current = ac;

    (async () => {
      try {
        const url = `/api/markets/${providerId}/${encodeURIComponent(externalId)}/sentiment`;
        const res = await fetch(url, { signal: ac.signal, headers: { Accept: "text/event-stream" } });
        if (!res.ok || !res.body) throw new Error(`HTTP ${res.status}`);

        const reader = res.body.getReader();
        const decoder = new TextDecoder();
        let buffer = "";
        let currentEvent = "";

        while (true) {
          const { done, value } = await reader.read();
          if (done) break;
          buffer += decoder.decode(value, { stream: true });

          let idx: number;
          while ((idx = buffer.indexOf("\n\n")) >= 0) {
            const chunk = buffer.slice(0, idx);
            buffer = buffer.slice(idx + 2);

            currentEvent = "";
            let dataLine = "";
            for (const line of chunk.split("\n")) {
              if (line.startsWith("event:")) currentEvent = line.slice(6).trim();
              else if (line.startsWith("data:")) dataLine = line.slice(5).trim();
            }
            if (!dataLine) continue;
            try {
              const data = JSON.parse(dataLine);
              applyEvent({ type: currentEvent, data } as ParsedEvent);
            } catch (e) {
              // Skip malformed frames silently — partial chunks would have been handled above.
              console.warn("sentiment SSE parse failed", e);
            }
          }
        }
      } catch (e) {
        if ((e as DOMException)?.name === "AbortError") return;
        setError((e as Error)?.message ?? "stream failed");
        setPhase("");
      }
    })();

    return () => ac.abort();

    function applyEvent(e: ParsedEvent) {
      switch (e.type) {
        case "phase":
          setPhase(e.data.detail);
          break;
        case "article":
          setArticles((prev) => {
            const next = prev.slice();
            next[e.data.index] = {
              index: e.data.index,
              url: e.data.url,
              title: e.data.title,
              source: e.data.source,
              publishedAt: e.data.publishedAt,
              score: null,
              label: null
            };
            return next;
          });
          break;
        case "score":
          setArticles((prev) => {
            const next = prev.slice();
            const cur = next[e.data.index];
            if (cur) next[e.data.index] = { ...cur, score: e.data.score, label: e.data.label };
            return next;
          });
          setPhase((p) => (p.startsWith("Scoring") || p.startsWith("Found") ? `Scoring ${e.data.index + 1}…` : p));
          break;
        case "done":
          setSummary(e.data);
          setPhase("");
          break;
        case "error":
          setError(e.data.message);
          setPhase("");
          break;
      }
    }
    // attempt is in the deps to allow a forced re-trigger on retry.
  }, [providerId, externalId, attempt]);

  const visibleArticles = useMemo(() => {
    const present = articles.filter(Boolean);
    // Filter by selected sentiment labels (unscored articles match if their eventual label is allowed
    // — for now, show unscored only when "neu" is selected, since they default to neutral).
    const filtered = present.filter((a) => {
      const key = (a.label ?? "neu") as LabelKey;
      return filters.has(key);
    });
    // Latest → oldest. Articles without a date sink to the bottom.
    return filtered.slice().sort((a, b) => {
      const ta = a.publishedAt ? Date.parse(a.publishedAt) : 0;
      const tb = b.publishedAt ? Date.parse(b.publishedAt) : 0;
      return tb - ta;
    });
  }, [articles, filters]);
  const inFlight = !summary && !error;

  return (
    <div className="fa-card p-4 flex flex-col shrink-0">
      <div className="flex items-center justify-between gap-3 mb-3 flex-wrap shrink-0">
        <div className="flex items-center gap-2 min-w-0">
          <Newspaper className="h-4 w-4 text-fa-frost-dim shrink-0" />
          <h3 className="text-fa-frost-bright text-base font-medium">News sentiment</h3>
          <span className="text-fa-frost-dim text-xs">last 14 days</span>
        </div>
        <div
          className="transition-all duration-[var(--fa-duration)]"
          style={{ transitionTimingFunction: "var(--fa-ease)", opacity: summary ? 1 : 0, transform: summary ? "translateY(0)" : "translateY(-4px)" }}
        >
          {summary && <SummaryBar s={summary} />}
        </div>
      </div>

      <div
        className="grid transition-[grid-template-rows,opacity] duration-[var(--fa-duration)] shrink-0"
        style={{
          transitionTimingFunction: "var(--fa-ease)",
          gridTemplateRows: inFlight && phase ? "1fr" : "0fr",
          opacity: inFlight && phase ? 1 : 0
        }}
      >
        <div className="overflow-hidden">
          <div className="flex items-center gap-2 text-sm text-fa-frost-dim py-1">
            <Spinner />
            <span className="truncate">{phase}</span>
          </div>
        </div>
      </div>

      {summary && summary.buckets.length > 1 && (
        <div className="mt-2 shrink-0">
          <SentimentStrip buckets={summary.buckets} />
        </div>
      )}

      {summary && (summary.positiveCount + summary.neutralCount + summary.negativeCount) > 0 && (
        <div className="mt-3 shrink-0 space-y-2">
          <StackedSentimentBar pos={summary.positiveCount} neu={summary.neutralCount} neg={summary.negativeCount} />
          <div className="flex items-center gap-1.5 flex-wrap">
            <FilterChip
              active={filters.has("pos")}
              color={YES_FILL}
              icon={TrendingUp}
              label="Positive"
              count={summary.positiveCount}
              onToggle={() => toggleFilter("pos")}
            />
            <FilterChip
              active={filters.has("neu")}
              color={NEUTRAL}
              icon={Minus}
              label="Neutral"
              count={summary.neutralCount}
              onToggle={() => toggleFilter("neu")}
            />
            <FilterChip
              active={filters.has("neg")}
              color={NO_FILL}
              icon={TrendingDown}
              label="Negative"
              count={summary.negativeCount}
              onToggle={() => toggleFilter("neg")}
            />
            <span className="text-[10px] text-fa-frost-dim ml-auto">latest first</span>
          </div>
        </div>
      )}

      {error ? (
        <div className="flex items-center justify-between gap-3 text-sm py-2 shrink-0">
          <span className="text-fa-danger">Sentiment stream failed: {error}</span>
          <button
            type="button"
            onClick={() => setAttempt((a) => a + 1)}
            className="text-fa-frost-bright hover:underline text-xs"
          >
            Retry
          </button>
        </div>
      ) : summary && visibleArticles.length === 0 ? (
        <div className="flex items-center justify-between gap-3 text-sm py-2 shrink-0">
          <span className="text-fa-frost-dim">No relevant articles found in the last 14 days.</span>
          <button
            type="button"
            onClick={() => setAttempt((a) => a + 1)}
            className="text-fa-frost-bright hover:underline text-xs"
          >
            Retry
          </button>
        </div>
      ) : (
        <div className="mt-2 -mx-2 px-2">
          <div className="divide-y divide-fa-edge/40">
            {visibleArticles.map((a, i) => (
              <a
                key={a.url}
                href={a.url}
                target="_blank"
                rel="noreferrer noopener"
                className="flex items-start gap-3 py-2 px-2 rounded hover:bg-fa-glass-strong transition group transition-all"
                style={{
                  transitionDuration: "var(--fa-duration)",
                  transitionTimingFunction: "var(--fa-ease)",
                  transitionDelay: `${Math.min(i, 9) * 30}ms`,
                  animation: "fa-fade-up var(--fa-duration) var(--fa-ease) both"
                }}
              >
                {a.score != null && a.label ? <ScoreChip score={a.score} label={a.label} /> : <ScoringDot />}
                <div className="min-w-0 flex-1">
                  <div className="text-sm text-fa-frost-bright group-hover:text-white transition leading-snug line-clamp-2">
                    {a.title}
                  </div>
                  <div className="text-[11px] text-fa-frost-dim mt-0.5 truncate">
                    {a.source} · {fmtAge(a.publishedAt)} ago
                  </div>
                </div>
                <ExternalLink className="h-3 w-3 text-fa-frost-dim opacity-0 group-hover:opacity-100 transition shrink-0 mt-1" />
              </a>
            ))}
          </div>
        </div>
      )}
    </div>
  );
}
