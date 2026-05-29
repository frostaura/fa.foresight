# fa.foresight Sandbox Sidecar

Isolated, deterministic Python execution service for flow/DAG code nodes.

---

## Purpose

The sandbox runs code nodes authored in the strategy / model DAG editor.  Every node is a **pure function of its declared inputs** — no network egress, no filesystem writes, no wall-clock reads, no unseeded RNG.  This purity contract guarantees that live trading, step-through debugging, and batch backtests produce **byte-identical results** for the same definition and inputs.

Used two ways:

| Mode | Description |
|------|-------------|
| **step** | Notebook-style single-row execution (N=1). Useful for debugging a node interactively. |
| **batch** | Vectorized backtest: one call per node for the whole candle series (N=seriesLength). |

**KEY INVARIANT:** a step call is just a batch call with N=1.  The *same* user code must work for both.  Series are always `numpy` arrays; in step mode they are length-1 arrays.

---

## Running locally

```bash
cd src/sandbox
pip install -r requirements.txt
uvicorn app.main:app --reload --port 8000
```

## Running tests

```bash
# From the project root:
python -m pytest src/sandbox/tests -q

# Or from inside src/sandbox/:
python -m pytest tests/ -v
```

---

## HTTP API

### `GET /healthz`

Liveness probe.

**Response**
```json
{ "status": "ok" }
```

---

### `GET /fingerprint`

Returns the runtime fingerprint for determinism validation.  The .NET backend calls this on startup to detect silent float drift caused by a numpy/BLAS version change.

**Response**
```json
{
  "status": "ok",
  "python": "3.12.x ...",
  "numpy": "2.1.3",
  "pandas": "2.2.3",
  "hash": "<sha256 of canonical deterministic snippet output>"
}
```

The `hash` is SHA-256 over the canonicalized outputs of a fixed deterministic snippet (cumulative sum + rolling mean over a fixed array).  If this hash changes between deployments, a library upgrade has silently altered float results.

---

### `POST /execute`

Execute a single deterministic code node.

#### Request body (protocol version 1)

```json
{
  "protocolVersion": 1,
  "mode": "step | batch",
  "nodeId": "string",
  "code": "python source; reads `inputs` dict, writes `outputs` dict",
  "seed": 12345,
  "params": { "...": "arbitrary node params" },
  "seriesLength": 10,
  "inputs": {
    "portName": { "tag": "scalar|series|candles|matrix", "value": <see below> }
  },
  "outputSchema": { "portName": "scalar|series|candles|matrix" },
  "limits": { "timeoutMs": 5000, "memMb": 256 }
}
```

#### Port value shapes

| Tag | Wire format |
|-----|-------------|
| `scalar` | JSON number / bool / string |
| `series` | JSON array of numbers, length N |
| `candles` | `{"openTime":[...],"open":[...],"high":[...],"low":[...],"close":[...],"volume":[...]}` — parallel arrays, all length N |
| `matrix` | `{"columns":["a","b"],"rows":[[1,2],[3,4]]}` |

In step mode (`N=1`), series are length-1 arrays.  User code treats them uniformly with batch code — no branching required.

#### User code contract

The following names are pre-bound in the execution namespace:

| Name | Type | Description |
|------|------|-------------|
| `inputs` | `dict` | Decoded port values: scalars as float/int/bool/str; series/candle columns as `numpy.ndarray` float64; matrix rows as 2-D ndarray |
| `params` | `dict` | Node parameters from `request.params` |
| `seed` | `int` | RNG seed from the request |
| `outputs` | `dict` | User writes results here; must match `outputSchema` |

#### Response — success

```json
{
  "ok": true,
  "outputs": {
    "portName": { "tag": "series", "value": [1.0, 2.0, 3.0] }
  },
  "stdout": "",
  "stderr": "",
  "durationMs": 12.3,
  "outputHash": "sha256hex..."
}
```

#### Response — failure

```json
{
  "ok": false,
  "error": {
    "kind": "NondeterminismError | Timeout | MemoryLimit | UserException | SchemaMismatch",
    "message": "human-readable detail"
  },
  "stdout": "",
  "stderr": ""
}
```

#### Rolling-mean example (for .NET client mirroring)

Request:

```json
{
  "protocolVersion": 1,
  "mode": "batch",
  "nodeId": "rolling-mean-demo",
  "code": "import numpy as np\ns = inputs['price']\nresult = np.array([np.mean(s[max(0,i-2):i+1]) for i in range(len(s))], dtype=np.float64)\noutputs['mean'] = result\n",
  "seed": 0,
  "params": {},
  "seriesLength": 5,
  "inputs": {
    "price": { "tag": "series", "value": [1.0, 2.0, 3.0, 4.0, 5.0] }
  },
  "outputSchema": { "mean": "series" },
  "limits": { "timeoutMs": 5000, "memMb": 128 }
}
```

Response:

```json
{
  "ok": true,
  "outputs": {
    "mean": {
      "tag": "series",
      "value": [1.0, 1.5, 2.0, 3.0, 4.0]
    }
  },
  "stdout": "",
  "stderr": "",
  "durationMs": 85.2,
  "outputHash": "<sha256>"
}
```

---

## Security model

### Container-level (docker-compose)

| Control | Value |
|---------|-------|
| Filesystem | `read_only: true` root FS + tmpfs at `/tmp` |
| Linux capabilities | `cap_drop: ALL` |
| Privilege escalation | `no-new-privileges: true` |
| Memory | `mem_limit: 256m` |
| PIDs | `pids_limit: 64` |
| User | Non-root uid 1001 |
| Network | `sandbox_internal` — internal bridge, no gateway; container cannot reach the internet |

### Process-level (guards.py)

Enforced inside every worker subprocess before user code runs:

| Control | Mechanism |
|---------|-----------|
| Single-thread BLAS | `OMP/MKL/OPENBLAS/NUMEXPR_NUM_THREADS=1` set before numpy import |
| RNG determinism | `random.seed(seed)` + `numpy.random.seed(seed)` in preamble |
| Wall-clock access | `time.time/monotonic/perf_counter` → `NondeterminismError` |
| Datetime access | `datetime.datetime.now/utcnow` → `NondeterminismError` |
| OS entropy | `os.urandom` + all `secrets.*` → `NondeterminismError` |
| UUID generation | `uuid.uuid1/uuid4` → `NondeterminismError` |
| Network access | `socket.socket` constructor + `create_connection/getaddrinfo` → `NondeterminismError` |
| File writes | `open(file, 'w'/'a'/'x'/...)` → `NondeterminismError` |
| Memory cap | `resource.setrlimit(RLIMIT_AS, memMb)` (Linux; best-effort on macOS) |
| File size cap | `resource.setrlimit(RLIMIT_FSIZE, 0)` (Linux; best-effort on macOS) |

### macOS / local dev notes

`RLIMIT_AS` on macOS is accepted by the kernel API but not enforced for heap allocations — the memory cap is advisory locally.  `RLIMIT_FSIZE` works correctly on macOS.  Both are fully enforced in the Docker/Linux container.  The `try/except` wrappers in `guards.py` ensure local dev still runs without failure.

---

## Execution flow

```
POST /execute
   │
   └─ FastAPI (main.py)
        │   decode request
        └─ executor.run_node()
              │
              ├─ multiprocessing.Process(spawn)
              │     │
              │     ├─ guards.bootstrap_worker()   ← BLAS env, rlimits, seeds, monkeypatches
              │     ├─ decode inputs → numpy arrays
              │     ├─ exec(preamble + user_code, namespace)
              │     ├─ validate outputs (presence, tag, length, finiteness)
              │     ├─ encode outputs → JSON-serialisable dicts
              │     └─ compute SHA-256 outputHash
              │
              ├─ (timeout: kill worker, return Timeout error)
              └─ return typed ExecuteResponse
```
