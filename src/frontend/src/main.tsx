import React from "react";
import ReactDOM from "react-dom/client";
import { Provider } from "react-redux";
import App from "./App";
import { store } from "./store";
import { attachSwRevalidate } from "./lib/swRevalidate";
import "./index.css";

attachSwRevalidate(store);

// Dev-only: unregister any stale service worker from previous sessions. The SW was caching `/api/`
// responses with StaleWhileRevalidate, which silently broke the SSE stream
// (`/api/live/predictions/stream`). We've disabled the dev SW in vite.config.ts; this scrub
// guarantees a tab that ran the old build cleans itself up on first reload.
if (import.meta.env.DEV && "serviceWorker" in navigator) {
  navigator.serviceWorker.getRegistrations().then((regs) => {
    if (regs.length > 0) {
      Promise.all(regs.map((r) => r.unregister()))
        .then(() => caches.keys())
        .then((keys) => Promise.all(keys.map((k) => caches.delete(k))))
        .then(() => location.reload());
    }
  });
}

ReactDOM.createRoot(document.getElementById("root")!).render(
  <React.StrictMode>
    <Provider store={store}>
      <App />
    </Provider>
  </React.StrictMode>
);
