# fa.foresight — FrostAura Foresight

## What this project is

FrostAura Foresight is a lean, automated **directional-trading platform**. Deterministic ML models (logistic and GBT families) predict short-horizon BTC up/down on 5m and 15m intervals. A strategy layer sizes each bet against **real Polymarket CLOB V2 odds** using edge-aware true Kelly (and four baseline strategies). The system runs automatically — proven first in a faithful paper simulation and chaos/bust test, then live against Polymarket's on-chain CLOB. First instrument: BTC up/down on Polymarket, 5m & 15m.

**This is a cash engine, not a research tool.** Carry only what makes the trader win. Full R&D depth on models and strategies stays in; everything off-mission (markets browser, LLM model, B2B forecast feed, whale analytics) is explicitly out of scope.

The platform uses a Logic-Apps-style **dual-view DAG authoring** surface (design view ↔ code view, losslessly round-tripping) for both models and strategies. Executable Python nodes run in a **sandboxed, network-isolated sidecar** (`src/sandbox`) that enforces purity: same definition + inputs = identical output in live, step-through, and bust test.

Multi-tenant data scaffolding is in place from day one (every entity tenant-scoped) for B2B optionality, but the MVP runs single-user.

Graduated from **FrostAura Labs → FrostAura Technologies** on 2026-05-29 as a directional-trading product and cash engine per the greenfield MVP plan (`docs/mvp-plan.md`).

## Stack & repo

- **Backend:** .NET 10, ASP.NET Core (Minimal APIs + SSE), EF Core 10, PostgreSQL 16. Hexagonal architecture (domain core + ports + adapters).
- **Frontend:** React 19 + TypeScript, Redux Toolkit (RTK Query), Tailwind CSS, shadcn/ui, Vite, PWA.
- **Sandbox sidecar:** Python 3.12, FastAPI, uvicorn. Containerized, network-isolated (`src/sandbox`).
- **Database:** PostgreSQL 16 (via Docker / Portainer in production).
- **Deploy:** Docker Compose locally; GitHub Actions → multi-arch Docker Hub build → Portainer re-pull → Cloudflared tunnel (mirrors `fa.startup` CI pattern).
- **Solution file:** `fa.foresight.slnx` (project root).
- **Repo:** `github.com/frostaura/fa.foresight` (intended remote). Directory: `Technologies/projects/fa.foresight/`.

## Owner & key people

- **Owner:** Dean Martin (founder)
- Additional contributors: to be assigned as the product matures.

## Current phase

`building`

## Success metrics

- Phase 1 exit: at least one model + strategy combo demonstrably **survives and profits** across random windows in the chaos/bust test (bust rate = 0 over a representative sample; median positive profit).
- Phase 2 exit: live session places, fills, and settles real Polymarket BTC orders automatically within reservation and guardrail limits; supervised $1 order validated before automation is armed.
- Ongoing: paper session P&L positive over rolling 30-day windows; edge-aware Kelly sizing measurably outperforms flat baseline over the same window.

## Dependencies on other FrostAura projects

- No hard runtime dependency on other FrostAura projects.
- Shares FrostAura brand tokens and the mandated tech stack (`.NET 10 + EF Core + PostgreSQL + React 19 + Tailwind + shadcn/ui`) with `fa.startup`, `fa.lifeos`, and other Technologies products.
- Portainer deploy infrastructure shared with Technologies division (same Portainer instance; separate stack ID).
- Docker Hub images: `${DOCKERHUB_USERNAME}/foresight-backend`, `foresight-frontend`, `foresight-sandbox`.

## Project-specific framing

- **Prediction-market red-line carve-out applies:** this platform is positioned as an automated trading system operating on information markets (Polymarket). It is greenlit under the FrostAura red-lines carve-out for prediction markets / event futures (`docs/governance/red-lines.md` §1, revised 2026-05-06). It must never be repositioned as a gambling product.
- **Polymarket CLOB V2 only.** V1 was decommissioned 2026-04-28. All execution code targets V2 wire facts (see `docs/mvp-plan.md` §6).
- **Purity contract is non-negotiable.** Code nodes are pure functions of declared inputs. Any deviation breaks the chaos/bust test faithfulness guarantee.
- **Auth posture:** multi-tenant data model, single-user auth for MVP (no passkey/JWT build yet). Single tenant resolved via header/local identity.
- **Live trading gate:** `Polymarket__LiveTrading=false` in `.env.example`. This must be explicitly set to `true` only after the supervised $1 validation order passes.

## Working notes

- **Do not touch `src/backend/` or `src/frontend/`** unless you are explicitly working on backend or frontend tasks. These dirs are actively developed by separate workstreams.
- `src/sandbox/` is the Python execution sidecar — Workstream C will flesh it out. The initial stub (`app/main.py`) must remain runnable at all times.
- Backend DI reads the Postgres connection string under the key `"Postgres"` (i.e. `ConnectionStrings__Postgres` in env).
- The solution file is `fa.foresight.slnx` at the project root, not inside `src/backend/`.
- Nginx gateway config lives at `nginx/nginx.conf` and is mounted as a template. The backend service is on port 5000 internally; the gateway exposes port 8088 by default.
- Sandbox is on an `internal: true` bridge (`sandbox_internal`) — it cannot reach the internet. The backend joins both `default` and `sandbox_internal` to reach it.
- See `docs/mvp-plan.md` for the full implementation contract: architecture, data model, wire facts, per-workstream checklists, and sequencing.
