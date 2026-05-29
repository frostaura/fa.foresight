"""
fa.foresight sandbox sidecar.

Executes deterministic code nodes on behalf of the backend.
No network egress, no filesystem writes, no wall-clock or unseeded RNG.
All node bodies are pure functions of their declared inputs.
"""

from fastapi import FastAPI
from pydantic import BaseModel

app = FastAPI(
    title="fa.foresight Sandbox",
    description="Deterministic Python execution sidecar for flow nodes.",
    version="0.1.0",
)


@app.get("/healthz")
async def healthz() -> dict:
    """Liveness probe."""
    return {"status": "ok"}


@app.get("/fingerprint")
async def fingerprint() -> dict:
    """
    Returns the sandbox runtime fingerprint for determinism validation.
    The backend calls this during startup to confirm the sidecar is reachable
    and to record the Python/package versions in use.
    Workstream C will extend this with full node execution endpoints.
    """
    import sys
    import numpy as np
    import pandas as pd

    return {
        "status": "ok",
        "python": sys.version,
        "numpy": np.__version__,
        "pandas": pd.__version__,
    }


class ExecuteRequest(BaseModel):
    """
    Stub request model for the node execution endpoint.
    Full schema defined in Workstream C.
    """

    node_type: str
    params: dict
    inputs: dict


class ExecuteResponse(BaseModel):
    outputs: dict
    stdout: str


@app.post("/execute", response_model=ExecuteResponse)
async def execute(request: ExecuteRequest) -> ExecuteResponse:
    """
    Execute a single deterministic node.
    Stub — full implementation in Workstream C.
    """
    return ExecuteResponse(
        outputs={},
        stdout=f"[stub] node_type={request.node_type!r} — not yet implemented",
    )
