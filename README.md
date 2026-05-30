<p align="center">
  <img src="./README.icon.png" alt="FrostAura Foresight" width="220" />
</p>

<h1 align="center"><b>FrostAura Foresight</b></h1>
<h3 align="center">predict the next candle. size the bet. trade it automatically.</h3>
<p align="center"><i>A lean, automated directional-trading platform for short-horizon BTC up/down on Polymarket.</i></p>

---

<p align="center">
  <a href="https://github.com/frostaura/fa.foresight/actions/workflows/ci.yml"><img src="https://github.com/frostaura/fa.foresight/actions/workflows/ci.yml/badge.svg" alt="CI" /></a>
  <img src="https://img.shields.io/badge/.NET-10-512BD4.svg" alt=".NET 10" />
  <img src="https://img.shields.io/badge/React-19-61DAFB.svg" alt="React 19" />
  <img src="https://img.shields.io/badge/Postgres-16-336791.svg" alt="PostgreSQL 16" />
  <a href="./LICENSE"><img src="https://img.shields.io/badge/License-MIT-yellow.svg" alt="License: MIT" /></a>
</p>

---

## What is Foresight?

FrostAura Foresight is an **automated directional-trading platform** — a cash engine, not a research toy.

Deterministic ML models (logistic and gradient-boosted-tree families) predict short-horizon **BTC up/down on 5m and 15m intervals**. A strategy layer then sizes each bet against **real Polymarket CLOB V2 odds** using edge-aware true Kelly, alongside four baseline strategies for comparison.

The system runs on its own: proven first in a faithful **paper simulation** and a **chaos/bust test**, then armed to trade **live against Polymarket's on-chain CLOB**. First instrument: BTC up/down, 5m and 15m.

---

## How it works

```
  market data ──▶ model DAG ──▶ direction + confidence ──▶ strategy DAG ──▶ sized order ──▶ Polymarket CLOB V2
                    │                                          │
                    └──────────── sandboxed Python nodes ──────┘
                            (pure · network-isolated · deterministic)
```

- **Dual-view DAG authoring.** Models and strategies are built on a Logic-Apps-style surface that round-trips losslessly between a **design view** and a **code view** — drag the graph or edit the code, never lose either.
- **Sandboxed execution.** Executable Python nodes run in a **network-isolated sidecar** that enforces purity: the same definition and inputs produce identical output in live trading, step-through debugging, and the bust test. This faithfulness guarantee is non-negotiable.
- **Edge-aware sizing.** True Kelly sizes each position against live order-book odds, with four baseline strategies for honest benchmarking.
- **Prove before you arm.** Live trading stays disarmed (`Polymarket__LiveTrading=false`) until a supervised $1 validation order passes. Paper and chaos tests gate everything upstream.

---

## Stack

| Layer | Technology |
| --- | --- |
| **Backend** | .NET 10 · ASP.NET Core (Minimal APIs + SSE) · EF Core 10 · hexagonal architecture |
| **Frontend** | React 19 · TypeScript · Redux Toolkit (RTK Query) · Tailwind CSS · shadcn/ui · Vite · PWA |
| **Sandbox** | Python 3.12 · FastAPI · uvicorn · containerized & network-isolated |
| **Data** | PostgreSQL 16 |
| **Deploy** | Docker Compose → GitHub Actions → multi-arch Docker Hub → Portainer → Cloudflared |

Multi-tenant data scaffolding is in place from day one (every entity is tenant-scoped) for B2B optionality, but the MVP runs single-user.

---

## Quick start

```bash
# clone
git clone https://github.com/frostaura/fa.foresight.git
cd fa.foresight

# configure
cp .env.example .env   # then fill in the required values

# run the full stack (backend + frontend + sandbox + postgres + gateway)
docker compose up --build
```

The gateway is exposed on **http://localhost:8088** by default. The backend listens on port `5000` internally; the sandbox sits on a network-isolated bridge and cannot reach the internet.

Going live on Polymarket CLOB V2 is a deliberate, gated process — see [`docs/live-setup.md`](./docs/live-setup.md).

---

## Repository layout

```
src/backend    .NET 10 API — domain core, ports, adapters
src/frontend   React 19 PWA — DAG authoring, charts, session control
src/sandbox    Python execution sidecar — pure, network-isolated nodes
nginx/         gateway config (template-mounted)
docs/          setup & operational guides
```

> `fa.foresight.slnx` (project root) is the .NET solution file — not inside `src/backend/`.

---

## Disclaimers

Foresight is positioned as an automated trading system operating on **information markets** (Polymarket prediction markets / event futures). It is **not** a gambling product and must never be repositioned as one.

Trading involves risk of loss. Nothing here is financial advice. Live trading remains disarmed until explicitly validated and enabled by the operator.

---

<p align="center">
  <i>From ambition to enduring progress. — FrostAura Technologies</i>
</p>
