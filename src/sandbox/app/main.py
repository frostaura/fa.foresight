"""
fa.foresight sandbox sidecar.

Executes deterministic code nodes on behalf of the backend.
No network egress, no filesystem writes, no wall-clock or unseeded RNG.
All node bodies are pure functions of their declared inputs.
"""

from __future__ import annotations

import sys

from fastapi import FastAPI

from app.executor import compute_fingerprint_hash, run_node
from app.protocol import ExecuteRequest, ExecuteResponse, ErrorDetail, PortValue

app = FastAPI(
    title="fa.foresight Sandbox",
    description="Deterministic Python execution sidecar for flow nodes.",
    version="1.0.0",
)


@app.get("/healthz")
async def healthz() -> dict:
    """Liveness probe."""
    return {"status": "ok"}


@app.get("/fingerprint")
async def fingerprint() -> dict:
    """Return the sandbox runtime fingerprint for determinism validation.

    The .NET backend calls this during startup (and after any sidecar upgrade)
    to:
      1. Confirm the sidecar is reachable.
      2. Record Python / package versions.
      3. Detect silent float drift: `hash` is SHA-256 over the canonical
         outputs of a fixed deterministic snippet.  If the hash changes between
         deployments, a numpy or BLAS upgrade has silently altered float results.
    """
    import numpy as np
    import pandas as pd

    fingerprint_hash = compute_fingerprint_hash()

    return {
        "status": "ok",
        "python": sys.version,
        "numpy": np.__version__,
        "pandas": pd.__version__,
        "hash": fingerprint_hash,
    }


@app.post("/execute", response_model=ExecuteResponse)
async def execute(request: ExecuteRequest) -> ExecuteResponse:
    """Execute a single deterministic code node.

    The request body must conform to the sandbox protocol (version 1).
    See app/protocol.py for the full Pydantic schema.

    Step vs. batch
    --------------
    Both modes use the same `code`.  In step mode, N=1: series are
    length-1 numpy arrays.  In batch mode, N=seriesLength.  The same
    code must work for both.

    Returns
    -------
    ExecuteResponse with ok=True and populated `outputs` on success, or
    ok=False with an `error` detail on failure.
    """
    # Convert pydantic PortValue objects to plain dicts for the executor
    inputs_raw: dict = {}
    for port_name, port_value in request.inputs.items():
        inputs_raw[port_name] = port_value.model_dump()

    result = run_node(
        code=request.code,
        seed=request.seed,
        params=request.params,
        inputs_raw=inputs_raw,
        output_schema=request.outputSchema,
        series_length=request.seriesLength,
        timeout_ms=request.limits.timeoutMs,
        mem_mb=request.limits.memMb,
    )

    if result["ok"]:
        # Re-parse encoded outputs into typed PortValue models
        typed_outputs: dict[str, PortValue] = {}
        for port_name, port_dict in result["outputs"].items():
            typed_outputs[port_name] = _parse_port_value(port_dict)

        return ExecuteResponse(
            ok=True,
            outputs=typed_outputs,
            stdout=result["stdout"],
            stderr=result["stderr"],
            durationMs=result["durationMs"],
            outputHash=result["outputHash"],
        )
    else:
        error_info = result.get("error") or {}
        return ExecuteResponse(
            ok=False,
            error=ErrorDetail(
                kind=error_info.get("kind", "UserException"),
                message=error_info.get("message", "Unknown error"),
            ),
            stdout=result.get("stdout", ""),
            stderr=result.get("stderr", ""),
            durationMs=result.get("durationMs", 0.0),
        )


def _parse_port_value(port_dict: dict) -> PortValue:
    """Deserialise an executor result dict into the correct PortValue subtype."""
    from app.protocol import ScalarValue, SeriesValue, CandlesValue, MatrixValue

    tag = port_dict["tag"]
    value = port_dict["value"]

    if tag == "scalar":
        return ScalarValue(tag="scalar", value=value)
    elif tag == "series":
        return SeriesValue(tag="series", value=value)
    elif tag == "candles":
        return CandlesValue(tag="candles", value=value)
    elif tag == "matrix":
        return MatrixValue(tag="matrix", value=value)
    else:
        raise ValueError(f"Unknown tag: {tag!r}")
