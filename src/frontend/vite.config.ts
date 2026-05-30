import { defineConfig, type PluginOption } from "vite";
import react from "@vitejs/plugin-react";
import { VitePWA } from "vite-plugin-pwa";

// Dev-only kill switch for a stale production service worker. In dev the PWA SW is disabled, so a
// browser that once registered the prod `sw.js` (Safari especially) keeps serving its old Workbox
// precache forever — an outdated app shell that pins a second copy of React and 404s on dead chunk
// hashes, producing "Invalid hook call". Serving a self-destroying SW at /sw.js means that browser's
// next update check fetches this tombstone, unregisters, purges caches, and reloads into a clean app.
function devSwKillSwitch(): PluginOption {
  return {
    name: "dev-sw-kill-switch",
    apply: "serve",
    configureServer(server) {
      server.middlewares.use((req, res, next) => {
        if (req.url === "/sw.js" || req.url?.startsWith("/sw.js?")) {
          res.setHeader("Content-Type", "text/javascript");
          res.setHeader("Cache-Control", "no-cache, no-store, must-revalidate");
          res.end(
            "self.addEventListener('install',()=>self.skipWaiting());\n" +
              "self.addEventListener('activate',(e)=>e.waitUntil((async()=>{\n" +
              "  try{await self.registration.unregister();}catch(_){}\n" +
              "  try{const ks=await caches.keys();await Promise.all(ks.map(k=>caches.delete(k)));}catch(_){}\n" +
              "  const cs=await self.clients.matchAll({type:'window'});\n" +
              "  cs.forEach(c=>{try{c.navigate(c.url);}catch(_){}});\n" +
              "})()));\n"
          );
          return;
        }
        next();
      });
    }
  };
}

export default defineConfig({
  resolve: { dedupe: ["react", "react-dom"] },
  plugins: [
    react(),
    devSwKillSwitch(),
    VitePWA({
      registerType: "autoUpdate",
      injectRegister: "auto",
      devOptions: { enabled: false, type: "module", navigateFallback: "index.html" },
      manifest: {
        name: "FrostAura Foresight",
        short_name: "Foresight",
        theme_color: "#0b1220",
        background_color: "#020617",
        display: "standalone"
      },
      workbox: {
        globPatterns: ["**/*.{js,css,html,svg,png,ico,webp,woff2}"],
        navigateFallback: "/index.html",
        runtimeCaching: [
          {
            urlPattern: ({ url, request }) => request.method === "GET" && url.pathname.startsWith("/api/") && !url.pathname.endsWith("/sentiment") && !url.pathname.endsWith("/discover") && !url.pathname.endsWith("/history") && !url.pathname.includes("/stream"),
            handler: "StaleWhileRevalidate",
            options: {
              cacheName: "foresight-api",
              broadcastUpdate: { channelName: "foresight-api-updates", options: { headersToCheck: ["content-length", "etag", "last-modified"] } },
              expiration: { maxEntries: 300, maxAgeSeconds: 60 * 60 * 24 * 7 },
              cacheableResponse: { statuses: [0, 200] }
            }
          },
          {
            urlPattern: ({ url }) => /polymarket-upload\.s3/.test(url.hostname) || /polymarket\.com$/.test(url.hostname),
            handler: "CacheFirst",
            options: {
              cacheName: "foresight-images",
              expiration: { maxEntries: 500, maxAgeSeconds: 60 * 60 * 24 * 30 }
            }
          }
        ]
      }
    })
  ],
  server: {
    port: 5173,
    host: "0.0.0.0",
    watch: { usePolling: true, interval: 500 },
    proxy: {
      "/api": {
        // 127.0.0.1 (not "localhost") on purpose: on macOS "localhost" resolves to IPv6 ::1 first,
        // where the AirPlay Receiver squats on port 5000 and returns 403. Forcing IPv4 hits the
        // backend's 127.0.0.1 bind. Override with VITE_API_TARGET for non-local backends.
        target: process.env.VITE_API_TARGET ?? "http://127.0.0.1:5000",
        changeOrigin: true,
        rewrite: (path) => path.replace(/^\/api/, "/api")
      }
    }
  },
  build: { outDir: "dist", sourcemap: true }
});
