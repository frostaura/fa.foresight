# fa.foresight Sandbox Sidecar

Isolated Python execution service for deterministic flow nodes.

## Purpose

The sandbox runs code nodes from the model and strategy DAGs. Every node is a pure function of its declared inputs — no network egress, no filesystem writes, no wall-clock reads, no unseeded RNG. This purity contract guarantees that live, step-through, and chaos/bust-test runs produce byte-identical results for the same definition and inputs.

## Running locally

```bash
pip install -r requirements.txt
uvicorn app.main:app --reload --port 8000
```

## Running tests

```bash
python -m pytest tests/ -v
```

## Endpoints

| Method | Path | Description |
|--------|------|-------------|
| GET | `/healthz` | Liveness probe — returns `{"status":"ok"}` |
| GET | `/fingerprint` | Runtime versions for determinism audit |
| POST | `/execute` | Execute a single node (stub; Workstream C implements) |

## Security model

In compose, the sandbox container runs with:
- `read_only: true` root filesystem + tmpfs at `/tmp`
- `cap_drop: ALL`
- `no-new-privileges: true`
- `mem_limit: 256m`, `pids_limit: 64`
- Non-root uid 1001
- `sandbox_internal` network (internal bridge, no gateway) — the container cannot make outbound internet connections
