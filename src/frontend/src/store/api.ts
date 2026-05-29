import { createApi, fetchBaseQuery } from "@reduxjs/toolkit/query/react";

export interface MarketSummary {
  providerId: string;
  externalId: string;
  question: string;
  category: string;
  resolvesAt?: string | null;
  status: string;
  resolutionCriteria?: string | null;
  imageUrl?: string | null;
  iconUrl?: string | null;
  yesPrice?: number | null;
  noPrice?: number | null;
  volume?: number | null;
  volume24h?: number | null;
  liquidity?: number | null;
}

export interface MarketDetail extends MarketSummary {
  price?: { yes: number; no: number; volume24h: number; observedAt: string } | null;
}

export interface SentimentArticle {
  url: string;
  title: string;
  source: string;
  publishedAt: string | null;
  score: number;
  label: string;
}

export interface SentimentBucket {
  day: string;
  avg: number;
  count: number;
}

export interface MarketSentiment {
  overallScore: number;
  positiveCount: number;
  negativeCount: number;
  neutralCount: number;
  buckets: SentimentBucket[];
  articles: SentimentArticle[];
}

export interface TenantInfo {
  id: string;
  name: string;
  slug: string;
  createdAt: string;
  settings: { autotradeEnabled: boolean; defaultJurisdiction: string; defaultLlmProviderId: string };
}

export interface LivePrediction {
  id: string;
  tenantId: string;
  symbol: string;
  interval: string;
  targetOpenTime: number;
  anchorClose: number;
  predictedClose: number;
  predictedChangePct: number;
  directionUpProbability: number;
  /** Walk-forward calibrated p(up) — raw probability mapped through the empirical hit-rate curve
   * for this interval. UI shows this as primary; raw probability stays in `directionUpProbability`
   * for tooltip / audit. Identity-fallback on cold start (< 20 resolutions in a bucket). */
  directionUpProbabilityCalibrated?: number;
  /** Legacy field — superseded by closeP05/50/95. Retained for back-compat reads. */
  targetHitProbability?: number;
  /** 5th-percentile of the model's close distribution. */
  closeP05?: number;
  /** Median of the model's close distribution — the central estimate (replaces `predictedClose`). */
  closeP50?: number;
  /** 95th-percentile of the model's close distribution. */
  closeP95?: number;
  confidence: number;
  reasoning?: string | null;
  model: string;
  supportingDataJson?: string | null;
  createdAt: string;
  resolvedAt?: string | null;
  actualClose?: number | null;
  absoluteErrorPct?: number | null;
  directionHit?: boolean | null;
}

export interface Favorite {
  id: string;
  tenantId: string;
  symbol: string;
  interval: string;
  createdAt: string;
}

export interface PolymarketReference {
  providerId: string;
  externalId: string;
  question: string;
  yesPrice?: number | null;
  noPrice?: number | null;
  resolvesAt?: string | null;
  exactMatch: boolean;
  error?: string;
}

const baseUrl = (import.meta.env.VITE_API_BASE as string | undefined) ?? "/api";

export const api = createApi({
  reducerPath: "foresightApi",
  baseQuery: fetchBaseQuery({
    baseUrl,
    prepareHeaders: (headers) => {
      const slug = localStorage.getItem("fa.tenant") ?? "default";
      headers.set("X-Tenant-Slug", slug);
      return headers;
    }
  }),
  tagTypes: ["Tenant", "Market", "LivePrediction", "Favorite", "Model", "ActiveModel", "Backtest"],
  endpoints: (b) => ({
    getMe: b.query<TenantInfo, void>({ query: () => "tenants/me", providesTags: ["Tenant"] }),
    listTenants: b.query<TenantInfo[], void>({ query: () => "tenants", providesTags: ["Tenant"] }),

    discoverMarkets: b.query<MarketSummary[], { providerId?: string; q?: string; category?: string; minVolume?: number; sort?: string; resolvesWithinDays?: number; take?: number; skip?: number; includeClosed?: boolean }>({
      query: (params) => ({ url: "markets/discover", params })
    }),
    getMarket: b.query<MarketDetail, { providerId: string; externalId: string }>({
      query: ({ providerId, externalId }) => `markets/${providerId}/${encodeURIComponent(externalId)}`
    }),
    getMarketHistory: b.query<{ t: string; yes: number }[], { providerId: string; externalId: string; interval: string }>({
      query: ({ providerId, externalId, interval }) => ({
        url: `markets/${providerId}/${encodeURIComponent(externalId)}/history`,
        params: { interval }
      })
    }),
    chatMarket: b.mutation<
      { reply: string; model: string },
      { providerId: string; externalId: string; messages: { role: string; content: string }[] }
    >({
      query: ({ providerId, externalId, messages }) => ({
        url: `markets/${providerId}/${encodeURIComponent(externalId)}/chat`,
        method: "POST",
        body: { messages }
      })
    }),
    suggestMarketQuestions: b.mutation<
      { suggestions: string[] },
      { providerId: string; externalId: string; messages: { role: string; content: string }[] }
    >({
      query: ({ providerId, externalId, messages }) => ({
        url: `markets/${providerId}/${encodeURIComponent(externalId)}/suggestions`,
        method: "POST",
        body: { messages }
      })
    }),

    predictLive: b.mutation<LivePrediction, { symbol: string; interval: string; horizon?: number }>({
      query: (body) => ({ url: "live/predict", method: "POST", body }),
      // Optimistic cache write: the moment the mutation returns, inject the new prediction into
      // every active listLivePredictions cache entry for the same (symbol, interval). Otherwise
      // there's a roundtrip gap where the next-candle row renders "—" while the refetch is in
      // flight after a candle close. Invalidation still runs as a safety net.
      onQueryStarted: async (arg, { dispatch, queryFulfilled }) => {
        try {
          const { data } = await queryFulfilled;
          dispatch(
            api.util.updateQueryData(
              "listLivePredictions",
              { symbol: arg.symbol, interval: arg.interval, take: 200 },
              (draft) => {
                if (!draft.some((p) => p.targetOpenTime === data.targetOpenTime)) {
                  draft.unshift(data);
                } else {
                  const idx = draft.findIndex((p) => p.targetOpenTime === data.targetOpenTime);
                  if (idx >= 0) draft[idx] = data;
                }
              }
            )
          );
        } catch {
          // Mutation failed — invalidation alone will re-fetch.
        }
      },
      invalidatesTags: (_r, _e, arg) => [{ type: "LivePrediction", id: `${arg.symbol}:${arg.interval}` }]
    }),
    listLivePredictions: b.query<LivePrediction[], { symbol: string; interval: string; take?: number }>({
      query: (params) => ({ url: "live/predictions", params }),
      providesTags: (_r, _e, arg) => [{ type: "LivePrediction", id: `${arg.symbol}:${arg.interval}` }]
    }),
    getPolymarketReference: b.query<PolymarketReference | null, { symbol: string; targetOpenTimeMs: number; intervalMs: number }>({
      query: (params) => ({ url: "live/polymarket-reference", params })
    }),
    listFavorites: b.query<Favorite[], void>({
      query: () => "favorites",
      providesTags: ["Favorite"]
    }),
    addFavorite: b.mutation<Favorite, { symbol: string; interval: string }>({
      query: (body) => ({ url: "favorites", method: "POST", body }),
      invalidatesTags: ["Favorite"]
    }),
    removeFavorite: b.mutation<void, { symbol: string; interval: string }>({
      query: ({ symbol, interval }) => ({ url: `favorites/${symbol}/${interval}`, method: "DELETE" }),
      invalidatesTags: ["Favorite"]
    }),

    // iter-4 — prediction models CRUD.
    listModels: b.query<Model[], void>({
      query: () => "models",
      providesTags: ["Model"]
    }),
    getModel: b.query<Model, string>({
      query: (id) => `models/${id}`,
      providesTags: (_r, _e, id) => [{ type: "Model", id }]
    }),
    createModel: b.mutation<Model, CreateModelRequest>({
      query: (body) => ({ url: "models", method: "POST", body }),
      invalidatesTags: ["Model"]
    }),
    updateModel: b.mutation<Model, { id: string; body: UpdateModelRequest }>({
      query: ({ id, body }) => ({ url: `models/${id}`, method: "PUT", body }),
      invalidatesTags: (_r, _e, arg) => ["Model", { type: "Model", id: arg.id }]
    }),
    deleteModel: b.mutation<void, string>({
      query: (id) => ({ url: `models/${id}`, method: "DELETE" }),
      // Optimistic remove. Tag invalidation alone DOES refetch — but the user sees a delay
      // between click and disappearance while the round-trip completes; manually splicing the
      // model out of the listModels cache makes the card vanish instantly. If the DELETE fails
      // (FK constraint, race, network), we undo the patch and the card pops back in.
      onQueryStarted: async (id, { dispatch, queryFulfilled }) => {
        const patch = dispatch(
          api.util.updateQueryData("listModels", undefined, (draft) => {
            const idx = draft.findIndex((m) => m.id === id);
            if (idx >= 0) draft.splice(idx, 1);
          })
        );
        try { await queryFulfilled; } catch { patch.undo(); }
      },
      invalidatesTags: ["Model"]
    }),
    duplicateModel: b.mutation<Model, { id: string; name: string }>({
      query: ({ id, name }) => ({ url: `models/${id}/duplicate`, method: "POST", body: { name } }),
      invalidatesTags: ["Model"]
    }),
    setDefaultModel: b.mutation<Model, string>({
      query: (id) => ({ url: `models/${id}/set-default`, method: "POST" }),
      invalidatesTags: ["Model"]
    }),
    // Leakage-aware backtest. Server picks a window strictly outside the model's training range
    // and runs Flat staking + small bet so the resulting hit rate is genuinely out-of-sample.
    // Invalidates Backtest so the recent-runs table re-fetches and shows the new row immediately.
    honestBacktest: b.mutation<HonestBacktestResult, string>({
      query: (id) => ({ url: `models/${id}/honest-backtest`, method: "POST" }),
      invalidatesTags: ["Backtest"]
    }),
    getNodeCatalogue: b.query<Record<string, NodeCatalogueEntry>, void>({
      query: () => "models/catalogue"
    }),

    // iter-4 — per-card active model selection.
    listActiveModels: b.query<ActiveModel[], void>({
      query: () => "active-models",
      providesTags: ["ActiveModel"]
    }),
    setActiveModel: b.mutation<ActiveModel, { symbol: string; interval: string; modelId: string }>({
      query: (body) => ({ url: "active-models", method: "PUT", body }),
      // Optimistic update so the per-card dropdown re-renders without a roundtrip flicker.
      onQueryStarted: async (arg, { dispatch, queryFulfilled }) => {
        const patch = dispatch(
          api.util.updateQueryData("listActiveModels", undefined, (draft) => {
            const idx = draft.findIndex((a) => a.symbol === arg.symbol && a.interval === arg.interval);
            const next: ActiveModel = {
              tenantId: draft[idx]?.tenantId ?? "",
              symbol: arg.symbol,
              interval: arg.interval,
              modelId: arg.modelId,
              updatedAt: new Date().toISOString()
            };
            if (idx >= 0) draft[idx] = next; else draft.push(next);
          })
        );
        try { await queryFulfilled; } catch { patch.undo(); }
      },
      invalidatesTags: ["ActiveModel", "LivePrediction"]
    }),
    clearActiveModel: b.mutation<void, { symbol: string; interval: string }>({
      query: ({ symbol, interval }) => ({ url: `active-models/${symbol}/${interval}`, method: "DELETE" }),
      invalidatesTags: ["ActiveModel"]
    }),

    // iter-4 — backtesting.
    runBacktest: b.mutation<Backtest, BacktestRequest>({
      query: (body) => ({ url: "backtests", method: "POST", body }),
      invalidatesTags: ["Backtest", "Model"]
    }),
    listBacktests: b.query<Backtest[], { modelId?: string }>({
      query: (params) => ({ url: "backtests", params }),
      providesTags: ["Backtest"]
    }),
    // Per-run ledger for the clickable-run report modal (chart dots + balance overlay + table).
    getBacktestBets: b.query<BacktestBet[], { id: string; take?: number }>({
      query: ({ id, take }) => ({ url: `backtests/${id}/bets`, params: take ? { take } : undefined }),
      providesTags: ["Backtest"]
    }),
    // Bust-test sweep: fans out N runs (lookback 1..maxLookbackDays) sharing one batchId.
    runBustTest: b.mutation<Backtest[], BustTestRequest>({
      query: (body) => ({ url: "backtests/bust-test", method: "POST", body }),
      invalidatesTags: ["Backtest"]
    }),
    // All rungs of a bust-test batch (ordered by lookback day) for the batch report modal.
    getBacktestBatch: b.query<Backtest[], string>({
      query: (batchId) => ({ url: `backtests/batches/${batchId}` }),
      providesTags: ["Backtest"]
    }),
    // Close-price series for a window — drives the run-report chart's price line + balance overlay.
    getHistoricalCandles: b.query<{ t: number; c: number }[], { symbol: string; interval: string; start: number; end: number }>({
      query: (params) => ({ url: "historical-candles", params })
    }),
    deleteBacktest: b.mutation<void, string>({
      query: (id) => ({ url: `backtests/${id}`, method: "DELETE" }),
      invalidatesTags: ["Backtest"]
    }),
    // Bulk clear. No modelId → wipes every backtest for the tenant; with modelId → only that
    // model's runs. The 'modelId' param is encoded into the query string when provided.
    clearBacktests: b.mutation<{ deleted: number }, { modelId?: string }>({
      query: ({ modelId }) => ({ url: "backtests", method: "DELETE", params: modelId ? { modelId } : undefined }),
      invalidatesTags: ["Backtest"]
    }),
    // Kicks off training on the server and returns immediately ({ status: "training" }). The fit
    // runs on a background task that survives the browser closing; the UI tracks progress by
    // reading the model's trainingStatus (polled via listModels) until it clears. Invalidates Model
    // so the card flips to "Training…" right after the click.
    trainModel: b.mutation<{ status: string }, { id: string; symbol: string }>({
      query: ({ id, symbol }) => ({ url: `models/${id}/train`, method: "POST", body: { symbol } }),
      invalidatesTags: ["Model"]
    }),

    // iter-4 — AI flow-design assistant. Returns a structured diff plus the validated updated
    // definition; the frontend hands the new JSON to PUT /api/models/{id} when the user "Applies".
    flowAssistant: b.mutation<AssistantReply, { id: string; intent: "create" | "modify"; history: { role: string; content: string }[] }>({
      query: ({ id, ...body }) => ({ url: `models/${id}/assistant`, method: "POST", body })
    }),

    // iter-4b — supported symbols + intervals (referential-integrity whitelist).
    getSymbols: b.query<{ symbols: string[]; intervals: string[] }, void>({
      query: () => "symbols"
    }),

    // iter-4e — pluggable staking-strategy catalogue. UI picks one or many; A/B mode fans out
    // a cross product of (selected models × selected strategies) so the recent-runs table
    // groups everything under a single batchId for side-by-side comparison.
    getStakingStrategies: b.query<{ default: string; strategies: { id: string; name: string; description: string }[] }, void>({
      query: () => "staking-strategies"
    }),

    // Channel management — list supported bot channels + configured state; test connectivity live.
    listChannels: b.query<Channel[], void>({ query: () => "channels" }),
    testChannel: b.mutation<{ ok: boolean; detail: string }, string>({
      query: (id) => ({ url: `channels/${id}/test`, method: "POST" })
    }),

  })
});

export const {
  useGetMeQuery,
  useDiscoverMarketsQuery,
  useGetMarketQuery,
  useGetMarketHistoryQuery,
  useChatMarketMutation,
  useSuggestMarketQuestionsMutation,
  usePredictLiveMutation,
  useListLivePredictionsQuery,
  useGetPolymarketReferenceQuery,
  useListFavoritesQuery,
  useAddFavoriteMutation,
  useRemoveFavoriteMutation,
  useListModelsQuery,
  useGetModelQuery,
  useCreateModelMutation,
  useUpdateModelMutation,
  useDeleteModelMutation,
  useDuplicateModelMutation,
  useSetDefaultModelMutation,
  useHonestBacktestMutation,
  useGetNodeCatalogueQuery,
  useListActiveModelsQuery,
  useSetActiveModelMutation,
  useClearActiveModelMutation,
  useRunBacktestMutation,
  useListBacktestsQuery,
  useGetBacktestBetsQuery,
  useRunBustTestMutation,
  useGetBacktestBatchQuery,
  useGetHistoricalCandlesQuery,
  useDeleteBacktestMutation,
  useClearBacktestsMutation,
  useTrainModelMutation,
  useFlowAssistantMutation,
  useGetSymbolsQuery,
  useGetStakingStrategiesQuery,
  useListChannelsQuery,
  useTestChannelMutation,
} = api;

export interface Channel {
  id: string;
  name: string;
  configured: boolean;
  notifyTarget?: string | null;
  allowlistCount: number;
  supportsCommands: boolean;
  supportsRichContent: boolean;
}

export interface AssistantReply {
  rawContent: string | null;
  rationale: string;
  ops: FlowDiffOp[];
  updatedDefinition: string | null;
  error: string | null;
  model: string;
}

export interface FlowDiffOp {
  op: "add_node" | "remove_node" | "add_edge" | "remove_edge" | "update_params";
  node?: FlowNode | null;
  edge?: FlowEdge | null;
  nodeId?: string | null;
  params?: Record<string, unknown> | null;
}

export interface FlowNode {
  id: string;
  type: string;
  params: Record<string, unknown>;
  position?: { x: number; y: number } | null;
}

export interface FlowEdge {
  from: string;
  to: string;
}

export interface FlowDefinition {
  schemaVersion: number;
  modelKind: "llm" | "deterministic";
  supportsBacktesting: boolean;
  warmupCandles: number;
  nodes: FlowNode[];
  edges: FlowEdge[];
}

// iter-4 — domain types for the Models system. Kept here next to the API endpoints so the slice
// remains self-contained; consumers import these from the same module.
export interface Model {
  id: string;
  tenantId: string | null;
  name: string;
  description?: string | null;
  kind: "llm" | "deterministic";
  supportsBacktesting: boolean;
  isBuiltIn: boolean;
  isDefault: boolean;
  definition: string;
  trainedState?: string | null;
  trainingValidationAccuracy?: number | null;
  backtestAccuracy?: number | null;
  lastTrainedAt?: string | null;
  // Training window persisted on training completion. The honest-backtest endpoint uses these to
  // pick a backtest range strictly outside [trainStartMs, trainEndMs] so the resulting hit rate is
  // genuinely out-of-sample. Null for never-trained or LLM-only built-in models.
  trainStartMs?: number | null;
  trainEndMs?: number | null;
  trainSymbol?: string | null;
  trainInterval?: string | null;
  // Persistent training-job state (server-side). "training" while a background fit is in flight,
  // "failed" if it threw, null/absent when idle or complete. The Models page reads this on load and
  // polls until it clears, so a training run started in one session shows live progress in another
  // and survives closing the browser.
  trainingStatus?: string | null;
  trainingStartedAt?: string | null;
  trainingError?: string | null;
  // Per-interval hit-rate (% as 0-100) from the most-recent completed backtest, per (model,
  // BTCUSDT, interval). Computed at the endpoint by joining Backtests on (ModelId, Symbol, Interval,
  // Status="complete"). Missing intervals are not zero-filled — they're absent from the map.
  scoresByInterval?: Record<string, number>;
  // Mean of scoresByInterval over intervals that HAVE a completed backtest. Null when none.
  // Renders as the per-card Score badge; the per-interval leaderboard uses scoresByInterval directly.
  averageScore?: number | null;
  createdAt: string;
  updatedAt: string;
}

export interface ActiveModel {
  tenantId: string;
  symbol: string;
  interval: string;
  modelId: string;
  updatedAt: string;
}

// Response from POST /api/models/{id}/honest-backtest. The newly-created backtest's id is
// returned alongside the window the server picked, so the UI can navigate to the run or render
// "out-of-sample window: <range>" next to the validation accuracy for direct comparison.
export interface HonestBacktestResult {
  backtestId: string;
  trainStartMs: number;
  trainEndMs: number;
  outOfSampleStartMs: number;
  outOfSampleEndMs: number;
  trainingValidationAccuracy?: number | null;
}

// Per-variant slice of a training run. One row per supported interval; the model's coefficients
// for that interval live in TrainedState JSON under `variants[interval]`. The card reads from
// this list to render per-interval walk-forward accuracy side by side.
export interface TrainedVariant {
  interval: string;
  trainStartMs: number;
  trainEndMs: number;
  validationAccuracy: number;
}

export interface TrainResult {
  variants: TrainedVariant[];
  trainedAt: string;
}

export interface CreateModelRequest {
  name: string;
  description?: string | null;
  kind: "llm" | "deterministic";
  supportsBacktesting: boolean;
  definition: string;
}

export interface UpdateModelRequest {
  name?: string;
  description?: string | null;
  definition?: string;
}

export interface NodeCatalogueEntry {
  category: string;
  inputs: { name: string; typeTag: string; required: boolean; description?: string | null }[];
  outputs: { name: string; typeTag: string; required: boolean; description?: string | null }[];
  params: Record<string, { typeTag: string; required: boolean; default?: unknown; description?: string | null }>;
  acceptsAdditionalInputs: boolean;
  requiresLiveData: boolean;
}

export interface Backtest {
  id: string;
  tenantId: string;
  modelId: string;
  symbol: string;
  interval: string;
  startTime: number;
  endTime: number;
  initialBalance: number;
  initialBetSize: number;
  status: "running" | "queued" | "complete" | "cancelled" | "failed" | "no-bets";
  betsPlaced: number;
  betsWon: number;
  hitRate?: number | null;
  finalBalance?: number | null;
  peakBalance?: number | null;
  troughBalance?: number | null;
  maxDrawdown?: number | null;
  peakBorrowed?: number | null;
  zeroCrossingsCount: number;
  maxMartingaleStep: number;
  /** Was the run executed under no-bankruptcy (true) or strict bust-check (false) semantics. */
  allowBorrow: boolean;
  /** Optional grouping id linking sibling rows from the same A/B comparison batch. */
  batchId?: string | null;
  /** Batch classification — null for ordinary runs, "bust-test" for a sweep rung. */
  batchKind?: string | null;
  /** For a bust-test rung, the lookback in days (window = [now − k days, now]). */
  lookbackDay?: number | null;
  /** Staking strategy id (e.g. "martingale", "flat") this run used. */
  strategyId: string;
  /** Whether the run applied the confidence gate (skip the ±2pp no-bet band) vs betting every candle. */
  applyGate: boolean;
  markersJson?: string | null;
  startedAt: string;
  completedAt?: string | null;
  error?: string | null;
}

export interface BacktestBet {
  id: string;
  backtestId: string;
  targetOpenTime: number;
  side: "UP" | "DOWN";
  pUpRaw: number;
  pUpCalibrated?: number | null;
  size: number;
  balanceBefore: number;
  balanceAfter: number;
  won: boolean;
  borrowedShortfall: number;
}

export interface BacktestRequest {
  modelId: string;
  symbol: string;
  interval: string;
  startTime: number;
  endTime: number;
  initialBalance: number;
  initialBetSize: number;
  /** When true (default), Martingale steps can dip the bankroll negative and accrue borrowed
   * shortfall. When false, the run halts the moment the next doubled bet would exceed the
   * bankroll — matches the live paper-trading bankruptcy contract. */
  allowBorrow?: boolean;
  /** Optional grouping id so an A/B multi-model run can be displayed together. */
  batchId?: string;
  /** Staking strategy id (e.g. "martingale", "flat"). Defaults to "martingale" on the server. */
  strategyId?: string;
  /** When true, skip candles in the ±2pp no-bet band (the confidence gate) instead of betting
   * every candle. Default false = always-bet baseline. Same equation as the chart GATE + paper gate. */
  applyGate?: boolean;
}

/** A bust-test sweep: one model + strategy, fanned out to N runs at lookback 1..maxLookbackDays. */
export interface BustTestRequest {
  modelId: string;
  symbol: string;
  interval: string;
  initialBalance: number;
  initialBetSize: number;
  maxLookbackDays: number;
  allowBorrow?: boolean;
  strategyId?: string;
}
