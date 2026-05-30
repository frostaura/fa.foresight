import { useEffect } from "react";
import { useDispatch } from "react-redux";
import { api } from "../store/api";

const apiBase = (import.meta.env.VITE_API_BASE as string | undefined) ?? "/api";

function tenant() {
  return encodeURIComponent(localStorage.getItem("fa.tenant") ?? "default");
}

/**
 * Push-based cache invalidation for everything that used to be polled.
 *
 * Two server-sent-event streams, one EventSource each (auto-reconnecting), shared for the whole tab:
 *   - `/api/models/stream`      → model training started/completed/failed → invalidate "Model"
 *   - `/api/live/events/stream`  → arm state + live-session changes → invalidate "GoLive" / "Session"
 *
 * Invalidating a tag only refetches queries currently mounted, so this is strictly cheaper than the
 * old fixed-interval polls: nothing fetches unless a relevant page is open AND the server actually
 * reported a change. Mounted once near the app root via <RealtimeSync/>.
 */
export function useRealtimeSync() {
  const dispatch = useDispatch();

  useEffect(() => {
    const t = tenant();

    // Model lifecycle — replaces the ModelTrainGate / Models page poll of /api/models.
    const models = new EventSource(`${apiBase}/models/stream?tenant=${t}`);
    const invalidateModels = () => dispatch(api.util.invalidateTags(["Model"]));
    models.addEventListener("training", invalidateModels);
    models.addEventListener("trained", invalidateModels);
    models.addEventListener("failed", invalidateModels);

    // Live control-plane — replaces the Live page polls of /api/golive/status and /api/sessions.
    const live = new EventSource(`${apiBase}/live/events/stream?tenant=${t}`);
    live.addEventListener("arm", () => dispatch(api.util.invalidateTags(["GoLive"])));
    live.addEventListener("session", () => dispatch(api.util.invalidateTags(["Session"])));

    return () => {
      models.close();
      live.close();
    };
  }, [dispatch]);
}

/** Headless mount point: opens the realtime streams for the lifetime of the app shell. */
export function RealtimeSync() {
  useRealtimeSync();
  return null;
}
