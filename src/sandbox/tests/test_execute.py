"""
Integration tests for the fa.foresight sandbox sidecar.

Run with:
    python -m pytest src/sandbox/tests -q
or from inside src/sandbox/:
    python -m pytest tests/ -v

All tests use FastAPI's TestClient (httpx-backed, in-process transport).
The executor spawns real subprocesses, so these tests exercise the full
code path including guards and output hashing.
"""

from __future__ import annotations

import pytest
from fastapi.testclient import TestClient

from app.main import app

client = TestClient(app)


# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------

def _execute(payload: dict) -> dict:
    resp = client.post("/execute", json=payload)
    assert resp.status_code == 200, resp.text
    return resp.json()


def _rolling_mean_request(mode: str, series: list[float]) -> dict:
    n = len(series)
    return {
        "protocolVersion": 1,
        "mode": mode,
        "nodeId": "test-rolling-mean",
        "code": (
            "import numpy as np\n"
            "s = inputs['price']\n"
            "result = np.array([\n"
            "    np.mean(s[max(0, i-2):i+1]) for i in range(len(s))\n"
            "], dtype=np.float64)\n"
            "outputs['mean'] = result\n"
        ),
        "seed": 0,
        "params": {},
        "seriesLength": n,
        "inputs": {
            "price": {"tag": "series", "value": series},
        },
        "outputSchema": {"mean": "series"},
        "limits": {"timeoutMs": 10000, "memMb": 128},
    }


# ---------------------------------------------------------------------------
# 1. Rolling-mean batch node over a 10-element series returns correct array
# ---------------------------------------------------------------------------

def test_batch_rolling_mean_correct_values():
    """Batch mode: rolling mean (window=3) of [1..10] matches numpy reference."""
    import numpy as np

    series = list(range(1, 11))  # [1, 2, 3, ..., 10]
    req = _rolling_mean_request("batch", series)
    body = _execute(req)

    assert body["ok"] is True, body.get("error")
    outputs = body["outputs"]
    assert "mean" in outputs
    assert outputs["mean"]["tag"] == "series"

    result = outputs["mean"]["value"]
    assert len(result) == 10

    # Compute reference: backward-looking window of up-to-3 elements
    arr = np.array(series, dtype=np.float64)
    expected = [float(np.mean(arr[max(0, i-2):i+1])) for i in range(10)]

    for i, (got, exp) in enumerate(zip(result, expected)):
        assert abs(got - exp) < 1e-9, f"Index {i}: got {got}, expected {exp}"


# ---------------------------------------------------------------------------
# 2. Same code in step mode (N=1) works
# ---------------------------------------------------------------------------

def test_step_rolling_mean_works():
    """Step mode (N=1): same code produces a length-1 series output."""
    req = _rolling_mean_request("step", [5.0])
    body = _execute(req)

    assert body["ok"] is True, body.get("error")
    outputs = body["outputs"]
    assert outputs["mean"]["tag"] == "series"
    values = outputs["mean"]["value"]
    assert len(values) == 1
    assert abs(values[0] - 5.0) < 1e-9


# ---------------------------------------------------------------------------
# 3. Determinism: same request twice => identical outputHash
# ---------------------------------------------------------------------------

def test_determinism_identical_output_hash():
    """Two identical requests must produce byte-identical outputHash values."""
    req = _rolling_mean_request("batch", [1.0, 2.0, 3.0, 4.0, 5.0])

    body1 = _execute(req)
    body2 = _execute(req)

    assert body1["ok"] is True
    assert body2["ok"] is True
    assert body1["outputHash"] is not None
    assert body1["outputHash"] == body2["outputHash"], (
        f"Hash mismatch: {body1['outputHash']!r} != {body2['outputHash']!r}"
    )


# ---------------------------------------------------------------------------
# 4. time.time() in user code => ok=False, NondeterminismError
# ---------------------------------------------------------------------------

def test_time_access_raises_nondeterminism_error():
    """User code calling time.time() must fail with NondeterminismError."""
    req = {
        "protocolVersion": 1,
        "mode": "step",
        "nodeId": "test-time",
        "code": (
            "import time\n"
            "t = time.time()\n"
            "outputs['t'] = t\n"
        ),
        "seed": 0,
        "params": {},
        "seriesLength": 0,
        "inputs": {},
        "outputSchema": {"t": "scalar"},
        "limits": {"timeoutMs": 5000, "memMb": 64},
    }
    body = _execute(req)

    assert body["ok"] is False
    assert body["error"]["kind"] == "NondeterminismError"
    assert "time.time" in body["error"]["message"]


# ---------------------------------------------------------------------------
# 5. Network attempt (socket.socket()) => ok=False
# ---------------------------------------------------------------------------

def test_network_access_raises_nondeterminism_error():
    """User code opening a socket must fail with NondeterminismError."""
    req = {
        "protocolVersion": 1,
        "mode": "step",
        "nodeId": "test-network",
        "code": (
            "import socket\n"
            "s = socket.socket()\n"
            "outputs['result'] = 1.0\n"
        ),
        "seed": 0,
        "params": {},
        "seriesLength": 0,
        "inputs": {},
        "outputSchema": {"result": "scalar"},
        "limits": {"timeoutMs": 5000, "memMb": 64},
    }
    body = _execute(req)

    assert body["ok"] is False
    assert body["error"]["kind"] == "NondeterminismError"
    assert "socket" in body["error"]["message"]


# ---------------------------------------------------------------------------
# 5b. Filesystem WRITE attempt (open(..., 'w')) => NondeterminismError
# ---------------------------------------------------------------------------

def test_filesystem_write_raises_nondeterminism_error():
    """Opening a file in a write mode must fail with NondeterminismError (purity guard).

    Write-mode file access would let a code node persist state across runs, breaking the
    same-definition+inputs ⇒ identical-output contract the chaos/bust test relies on.
    """
    req = {
        "protocolVersion": 1,
        "mode": "step",
        "nodeId": "test-fs-write",
        "code": (
            "f = open('/tmp/fa_foresight_sandbox_test.txt', 'w')\n"
            "f.write('should never happen')\n"
            "outputs['result'] = 1.0\n"
        ),
        "seed": 0,
        "params": {},
        "seriesLength": 0,
        "inputs": {},
        "outputSchema": {"result": "scalar"},
        "limits": {"timeoutMs": 5000, "memMb": 64},
    }
    body = _execute(req)

    assert body["ok"] is False
    assert body["error"]["kind"] == "NondeterminismError"
    assert "open" in body["error"]["message"]


def test_filesystem_append_raises_nondeterminism_error():
    """Append mode ('a') must also be blocked, not just truncating write ('w')."""
    req = {
        "protocolVersion": 1,
        "mode": "step",
        "nodeId": "test-fs-append",
        "code": (
            "open('/tmp/fa_foresight_sandbox_test.txt', 'a').write('x')\n"
            "outputs['result'] = 1.0\n"
        ),
        "seed": 0,
        "params": {},
        "seriesLength": 0,
        "inputs": {},
        "outputSchema": {"result": "scalar"},
        "limits": {"timeoutMs": 5000, "memMb": 64},
    }
    body = _execute(req)

    assert body["ok"] is False
    assert body["error"]["kind"] == "NondeterminismError"


# ---------------------------------------------------------------------------
# 6. Missing declared output => SchemaMismatch
# ---------------------------------------------------------------------------

def test_missing_output_schema_mismatch():
    """Code that does not populate a declared output key returns SchemaMismatch."""
    req = {
        "protocolVersion": 1,
        "mode": "step",
        "nodeId": "test-missing-output",
        "code": (
            "# deliberately does not populate 'missing_key'\n"
            "outputs['other'] = 1.0\n"
        ),
        "seed": 0,
        "params": {},
        "seriesLength": 0,
        "inputs": {},
        "outputSchema": {"missing_key": "scalar"},
        "limits": {"timeoutMs": 5000, "memMb": 64},
    }
    body = _execute(req)

    assert body["ok"] is False
    assert body["error"]["kind"] == "SchemaMismatch"
    assert "missing_key" in body["error"]["message"]


# ---------------------------------------------------------------------------
# 7. /fingerprint returns a stable hash across two calls
# ---------------------------------------------------------------------------

def test_fingerprint_stable_hash():
    """/fingerprint must return a non-empty 'hash' that is identical across calls."""
    r1 = client.get("/fingerprint")
    r2 = client.get("/fingerprint")

    assert r1.status_code == 200
    assert r2.status_code == 200

    d1 = r1.json()
    d2 = r2.json()

    assert d1["status"] == "ok"
    assert "hash" in d1, "fingerprint response must include 'hash'"
    assert d1["hash"], "hash must be non-empty"
    assert d1["hash"] == d2["hash"], (
        f"Fingerprint hash changed between calls: {d1['hash']!r} != {d2['hash']!r}"
    )
    # Should be a 64-char hex SHA-256
    assert len(d1["hash"]) == 64


# ---------------------------------------------------------------------------
# Bonus: /healthz and /fingerprint smoke tests
# ---------------------------------------------------------------------------

def test_healthz():
    resp = client.get("/healthz")
    assert resp.status_code == 200
    assert resp.json() == {"status": "ok"}


def test_fingerprint_has_version_fields():
    resp = client.get("/fingerprint")
    data = resp.json()
    assert data["status"] == "ok"
    assert "python" in data
    assert "numpy" in data
    assert "pandas" in data
