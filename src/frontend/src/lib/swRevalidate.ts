import type { Store } from "@reduxjs/toolkit";
import { api } from "../store/api";

const ALL_TAGS = ["Tenant", "Market"] as const;

const URL_TO_TAGS: { match: RegExp; tags: (typeof ALL_TAGS)[number][] }[] = [
  { match: /\/api\/markets(\/|\?|$)/, tags: ["Market"] },
  { match: /\/api\/tenants(\/|\?|$)/, tags: ["Tenant"] }
];

export function attachSwRevalidate(store: Store): void {
  if (typeof window === "undefined" || typeof BroadcastChannel === "undefined") return;
  const channel = new BroadcastChannel("foresight-api-updates");
  channel.addEventListener("message", (event) => {
    const url: string | undefined = event?.data?.payload?.updatedURL;
    if (!url) return;
    const path = (() => {
      try { return new URL(url).pathname + new URL(url).search; } catch { return url; }
    })();
    const hit = URL_TO_TAGS.find((r) => r.match.test(path));
    const tags = hit ? hit.tags : ALL_TAGS.slice();
    store.dispatch(api.util.invalidateTags(tags as Parameters<typeof api.util.invalidateTags>[0]));
  });
}
