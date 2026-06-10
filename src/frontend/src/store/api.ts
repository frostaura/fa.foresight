import { createApi, fetchBaseQuery } from "@reduxjs/toolkit/query/react";

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

/**
 * Masked view of a tenant's platform connection, returned by GET/PUT
 * /api/platform-connections/default. The raw private key is NEVER returned — only `hasPrivateKey`
 * and the derived `walletAddress`. `walletAddress`/`funder` may be absent when unset.
 */
export interface PlatformConnection {
  connectorId: string;
  isDefault: boolean;
  hasPrivateKey: boolean;
  walletAddress?: string | null;
  /** 0 = EOA, 1 = POLY_PROXY, 2 = POLY_GNOSIS_SAFE. */
  signatureType: number;
  funder?: string | null;
  clobBaseUrl: string;
  gammaBaseUrl: string;
  chainId: number;
  liveTrading: boolean;
  maxTradeUsd: number;
  /** Conservative effective entry price ∈ (0.50,0.95) — the fee/payoff model for the BTC up/down contract. */
  effectivePrice: number;
  /** Polygon JSON-RPC endpoint used for the on-chain pUSD balance read during reconciliation. */
  rpcUrl?: string | null;
}

/**
 * Partial body for PUT /api/platform-connections/default. All fields optional — omitted = unchanged.
 * Omit `privateKey` (or send blank/undefined) to keep the existing wallet key. A supplied key is
 * validated + encrypted server-side; its wallet address is derived.
 */
export interface UpdatePlatformConnectionRequest {
  privateKey?: string;
  signatureType?: number;
  funder?: string;
  clobBaseUrl?: string;
  gammaBaseUrl?: string;
  chainId?: number;
  liveTrading?: boolean;
  maxTradeUsd?: number;
  effectivePrice?: number;
  rpcUrl?: string;
}

/**
 * Account-level live-trading balance. `walletPusd` is the on-chain pUSD (0 when no wallet/unconfirmed);
 * `reserved` is Σ active live-session balances (meaningful even pre-funding); `free = wallet − reserved`.
 */
export interface AccountBalance {
  walletPusd: number;
  reserved: number;
  free: number;
}

/**
 * Per-tenant notification settings. The Telegram bot is global (`botConfigured` = a token is set on
 * the server); the destination `telegramChatId` is per-tenant and editable. Null = use the global
 * default chat (seeded from env for the admin/dev tenant).
 */
export interface NotificationSettings {
  telegramChatId: number | null;
  botConfigured: boolean;
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
  tagTypes: ["Tenant", "LivePrediction", "Model", "ActiveModel", "Backtest", "Session", "Chaos", "Strategy", "GoLive", "PlatformConnection", "Notifications"],
  endpoints: (b) => ({
    getMe: b.query<TenantInfo, void>({ query: () => "tenants/me", providesTags: ["Tenant"] }),
    listTenants: b.query<TenantInfo[], void>({ query: () => "tenants", providesTags: ["Tenant"] }),

    // ── Trading sessions (Status / Live pages) ──────────────────────────────
    listSessions: b.query<NormalizedSession[], { kind?: "paper" | "live"; active?: boolean }>({
      query: (params) => ({ url: "sessions", params }),
      providesTags: ["Session"],
    }),
    createSession: b.mutation<NormalizedSession, CreateSessionRequest>({
      query: (body) => ({ url: "sessions", method: "POST", body }),
      invalidatesTags: ["Session"],
    }),
    stopSession: b.mutation<NormalizedSession, string>({
      query: (id) => ({ url: `sessions/${id}`, method: "DELETE" }),
      invalidatesTags: ["Session"],
    }),
    listChaosRuns: b.query<ChaosRunNormalized[], { batchId?: string }>({
      query: (params) => ({ url: "chaos", params }),
      providesTags: ["Chaos"],
    }),
    // Start a chaos run. Body = ChaosRequest. Fans out over ModelIds × StrategyIds × window
    // lengths internally; returns the shared batchId. Invalidates Chaos so the list re-fetches.
    runChaos: b.mutation<{ batchId: string }, ChaosRequest>({
      query: (body) => ({ url: "chaos", method: "POST", body }),
      invalidatesTags: ["Chaos"],
    }),
    // Per-window sample rows for one chaos run (capped server-side). Drives the drill-in drawer.
    getChaosSamples: b.query<ChaosSample[], string>({
      query: (id) => ({ url: `chaos/${id}/samples` }),
      providesTags: ["Chaos"],
    }),
    // Bulk-clear chaos runs (samples cascade). No modelId → wipes every run for the tenant.
    // Invalidates Chaos so the history list re-fetches empty.
    clearChaos: b.mutation<{ deleted: number }, { modelId?: string } | void>({
      query: (params) => ({ url: "chaos", method: "DELETE", params: params ?? undefined }),
      invalidatesTags: ["Chaos"],
    }),

    // ── Flow sandbox execution ───────────────────────────────────────────────
    runFlowNode: b.mutation<RunNodeResult, RunNodeRequest>({
      query: (body) => ({ url: "flows/run-node", method: "POST", body }),
    }),

    // ── Strategies catalogue — delegates to the real /api/staking-strategies endpoint ──
    listStrategies: b.query<Strategy[], void>({
      query: () => "staking-strategies",
    }),

    // ── Strategies CRUD — /api/strategies (enriched StrategyDetail list) ──────────────────
    getStrategies: b.query<StrategyDetail[], void>({
      query: () => "strategies",
      providesTags: ["Strategy"],
    }),
    getStrategy: b.query<StrategyDetail, string>({
      query: (id) => `strategies/${id}`,
      providesTags: (_r, _e, id) => [{ type: "Strategy", id }],
    }),
    createStrategy: b.mutation<StrategyDetail, { name: string; description?: string | null; definition?: string | null; params?: Record<string, unknown> | null }>({
      query: (body) => ({ url: "strategies", method: "POST", body }),
      invalidatesTags: ["Strategy"],
    }),
    updateStrategy: b.mutation<StrategyDetail, { id: string; body: { name?: string; description?: string | null; definition?: string | null; params?: Record<string, unknown> | null } }>({
      query: ({ id, body }) => ({ url: `strategies/${id}`, method: "PUT", body }),
      invalidatesTags: (_r, _e, arg) => ["Strategy", { type: "Strategy", id: arg.id }],
    }),
    deleteStrategy: b.mutation<void, string>({
      query: (id) => ({ url: `strategies/${id}`, method: "DELETE" }),
      invalidatesTags: ["Strategy"],
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

    // ── Go-live arm flow ─────────────────────────────────────────────────────
    // In-memory, per-tenant arm gate (separate from the Polymarket__LiveTrading .env flag — both
    // are required for real orders). Resets on backend restart. `armed === true` ⇒ "set up" for UI.
    getGoLiveStatus: b.query<{ armed: boolean }, void>({
      query: () => "golive/status",
      providesTags: ["GoLive"],
    }),
    requestGoLiveCode: b.mutation<{ message: string; code: string }, void>({
      query: () => ({ url: "golive/request-code", method: "POST" }),
    }),
    confirmGoLive: b.mutation<{ armed: boolean; message: string }, { code: string }>({
      query: (body) => ({ url: "golive/confirm", method: "POST", body }),
      invalidatesTags: ["GoLive"],
    }),
    killswitch: b.mutation<{ armed: boolean; message: string }, void>({
      query: () => ({ url: "golive/killswitch", method: "POST" }),
      invalidatesTags: ["GoLive"],
    }),

    // ── Platform connection (per-tenant Polymarket connection, edited in-app) ──
    // The connection (wallet key, signature type, endpoints, live-trading flag, per-trade cap) now
    // lives in the DB per tenant; env is a one-time bootstrap default. Secrets are masked: the GET
    // never returns the raw private key (only hasPrivateKey + the derived walletAddress).
    getPlatformConnection: b.query<PlatformConnection, void>({
      query: () => "platform-connections/default",
      providesTags: ["PlatformConnection"],
    }),
    // Partial update — omitted fields stay unchanged. Omitting privateKey keeps the existing key.
    updatePlatformConnection: b.mutation<PlatformConnection, UpdatePlatformConnectionRequest>({
      query: (body) => ({ url: "platform-connections/default", method: "PUT", body }),
      invalidatesTags: ["PlatformConnection"],
    }),
    // Account-level wallet / reserved / free for the live-trading view. Refetches when live sessions
    // change (reserved is derived from active live-session balances) or the connection changes.
    getAccountBalance: b.query<AccountBalance, void>({
      query: () => "account/balance",
      providesTags: ["Session", "PlatformConnection"],
    }),
    // Per-tenant notification settings (Telegram chat id; bot is global). Test send is fire-and-forget.
    getNotificationSettings: b.query<NotificationSettings, void>({
      query: () => "notifications/settings",
      providesTags: ["Notifications"],
    }),
    updateNotificationSettings: b.mutation<NotificationSettings, { telegramChatId: number | null }>({
      query: (body) => ({ url: "notifications/settings", method: "PUT", body }),
      invalidatesTags: ["Notifications"],
    }),
    testNotification: b.mutation<{ sent: boolean }, void>({
      query: () => ({ url: "notifications/test", method: "POST" }),
    }),

    // iter-4 — prediction models CRUD.
    listModels: b.query<Model[], { includeArchived?: boolean } | void>({
      query: (arg) => {
        const params = arg && (arg as { includeArchived?: boolean }).includeArchived ? "?includeArchived=true" : "";
        return `models${params}`;
      },
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
          api.util.updateQueryData("listModels", void 0, (draft) => {
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
    archiveModel: b.mutation<void, string>({
      query: (id) => ({ url: `models/${id}/archive`, method: "POST" }),
      invalidatesTags: ["Model"]
    }),
    unarchiveModel: b.mutation<void, string>({
      query: (id) => ({ url: `models/${id}/unarchive`, method: "POST" }),
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
    // runs on a background task that survives the browser closing; the UI tracks progress via the
    // /api/models SSE stream, which invalidates the model cache on each training transition (see
    // RealtimeSync) — no polling. Invalidates Model so the card flips to "Training…" right after the click.
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

  })
});

export const {
  useGetMeQuery,
  usePredictLiveMutation,
  useListLivePredictionsQuery,
  useGetPolymarketReferenceQuery,
  useGetGoLiveStatusQuery,
  useRequestGoLiveCodeMutation,
  useConfirmGoLiveMutation,
  useKillswitchMutation,
  useGetPlatformConnectionQuery,
  useUpdatePlatformConnectionMutation,
  useGetAccountBalanceQuery,
  useGetNotificationSettingsQuery,
  useUpdateNotificationSettingsMutation,
  useTestNotificationMutation,
  useListModelsQuery,
  useGetModelQuery,
  useCreateModelMutation,
  useUpdateModelMutation,
  useDeleteModelMutation,
  useDuplicateModelMutation,
  useSetDefaultModelMutation,
  useArchiveModelMutation,
  useUnarchiveModelMutation,
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
  // New endpoints for Trading → Status / Live
  useListSessionsQuery,
  useCreateSessionMutation,
  useStopSessionMutation,
  useListChaosRunsQuery,
  useRunChaosMutation,
  useGetChaosSamplesQuery,
  useClearChaosMutation,
  useRunFlowNodeMutation,
  useListStrategiesQuery,
  useGetStrategiesQuery,
  useGetStrategyQuery,
  useCreateStrategyMutation,
  useUpdateStrategyMutation,
  useDeleteStrategyMutation,
} = api;

// ── Trading Session types ─────────────────────────────────────────────────────

/** Normalised session shape returned by GET/POST /api/sessions. */
export interface NormalizedSession {
  id: string;
  mode: "paper" | "live";
  symbol: string;
  interval: string;
  modelId?: string | null;
  strategyId: string;
  startedAt: string;
  stoppedAt?: string | null;
  initialBalance: number;
  currentBalance: number;
  currentBetSize: number;
  betsPlaced: number;
  betsWon: number;
  bust: boolean;
  reservedAmount?: number | null;
}

/** @deprecated Use NormalizedSession instead — kept for existing consumers during transition. */
export type TradingSession = NormalizedSession & {
  kind: "paper" | "live";
  tenantId: string;
  initialBetSize: number;
  hitRate?: number | null;
  status: "active" | "paused" | "stopped";
  createdAt: string;
  updatedAt: string;
};

export interface CreateSessionRequest {
  mode: "paper" | "live";
  symbol: string;
  interval: string;
  initialBalance: number;
  initialBetSize: number;
  strategyId?: string | null;
  gated: boolean;
}

/** Normalised ChaosRun shape from GET /api/chaos. Fields match the ChaosRun domain entity. */
export interface ChaosRunNormalized {
  id: string;
  tenantId: string;
  batchId: string;
  modelId: string;
  strategyId: string;
  symbol: string;
  interval: string;
  windowLength: number;
  sampleCount: number;
  initialBalance?: number | null;
  bustRate?: number | null;
  profitP50?: number | null;
  profitMean?: number | null;
  worstDrawdown?: number | null;
  pass: boolean;
  status: "running" | "complete" | "failed";
  startedAt: string;
  completedAt?: string | null;
  error?: string | null;
}

/**
 * Body for POST /api/chaos. Matches the backend `ChaosRequest` record (ASP.NET deserialises
 * camelCase by default). The chaos engine fans out one run row per (model × strategy × window
 * length) and replays SampleCount random windows of WindowLengthCandles each.
 */
export interface ChaosRequest {
  modelIds: string[];
  strategyIds: string[];
  symbol: string;
  interval: string;
  windowLengthCandles: number;
  /** Optional extra window lengths to sweep alongside the primary. null = primary only. */
  lengthSweep: number[] | null;
  sampleCount: number;
  initialBalance: number;
  initialBetSize: number;
  allowBorrow: boolean;
  /** Reproducibility seed; null lets the server pick a fixed default (0). */
  seed: number | null;
}

/** One random-window outcome within a chaos run. From GET /api/chaos/{id}/samples. */
export interface ChaosSample {
  id: string;
  chaosRunId: string;
  /** OpenTime (ms) of the first candidate in this window. */
  startMs: number;
  survived: boolean;
  finalBalance: number;
  maxDrawdown: number;
  zeroCrossings: number;
}

/** @deprecated Use ChaosRunNormalized instead. */
export interface ChaosRun {
  id: string;
  tenantId: string;
  modelId: string;
  batchId: string;
  runCount: number;
  bustCount: number;
  bustRate: number;
  medianProfit: number;
  status: "running" | "complete" | "failed";
  startedAt: string;
  completedAt?: string | null;
}

export interface RunNodeRequest {
  nodeTypeId: string;
  params: Record<string, unknown>;
  inputs: Record<string, unknown>;
}

export interface RunNodeResult {
  outputs: Record<string, unknown>;
  stdout: string;
  error?: string | null;
  durationMs: number;
}

export interface Strategy {
  id: string;
  name: string;
  description: string;
}

/** Enriched strategy DTO returned by GET /api/strategies and GET /api/strategies/{id}. */
export interface StrategyDetail {
  id: string;
  name: string;
  description?: string | null;
  isBuiltIn: boolean;
  kind: "code" | "dag";
  /** DAG definition JSON (FlowDefinition). null for built-in code-defined strategies. */
  definition?: string | null;
  params?: Record<string, unknown> | null;
  tenantId?: string | null;
  createdAt: string;
  updatedAt: string;
  /** AI-generated plain-language description. */
  simpleDescription?: string | null;
  /** AI-generated technical description. */
  technicalDescription?: string | null;
  /** Per-interval hit-rate (0–100) from the most-recent completed backtest. */
  scoresByInterval?: Record<string, number>;
  /** Mean of scoresByInterval across intervals that have a completed backtest. */
  averageScore?: number | null;
  /** Total number of backtests run against this strategy. */
  backtestsRun?: number | null;
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
  // "strategy" applies to staking-strategy DAGs; "llm"/"deterministic" to model DAGs.
  modelKind: "llm" | "deterministic" | "strategy";
  // Terminal-node + endpoint validation key: "model" → output.prediction, "strategy" → output.stake.
  definitionKind?: "model" | "strategy";
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
  /** AI-generated plain-language description for non-technical users. Null until generation completes. */
  simpleDescription?: string | null;
  /** AI-generated technical description for data-scientists. Null until generation completes. */
  technicalDescription?: string | null;
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
  // the /api/models SSE stream invalidates the cache as it transitions, so a training run started in
  // one session shows live progress in another and survives closing the browser — without polling.
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
  isArchived?: boolean;
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
