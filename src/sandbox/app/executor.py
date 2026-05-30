"""
Subprocess executor for sandbox code nodes.

Architecture
------------
Each /execute call spawns a fresh child process via multiprocessing.Process.
The child runs the user code inside a fully guarded environment (see guards.py)
and communicates results back through a multiprocessing.Queue.  The parent
waits up to limits.timeoutMs ms; if the child does not finish, it is killed
and a Timeout error is returned.

KEY INVARIANT
-------------
A "step" call (mode=step, N=1) is identical to a "batch" call with N=1.
The same user code must handle both modes — series are always numpy arrays;
in step mode they are length-1 arrays.  The user accesses `inputs`, `params`,
`seed`, and writes to `outputs`.

User code contract
------------------
The following names are pre-bound in the user code's global namespace:

    inputs  : dict[str, scalar | np.ndarray | dict]
                  scalar  → float/int/bool/str
                  series  → np.ndarray shape (N,) float64
                  candles → dict with keys openTime/open/high/low/close/volume,
                            each a np.ndarray shape (N,) float64
                  matrix  → {"columns": list[str], "rows": np.ndarray (M,N)}
    params  : dict[str, Any]   (from request.params)
    seed    : int
    outputs : dict             (user writes results here)

After user code runs, the executor validates that every key in outputSchema is
present, has the correct tag, and (for numeric outputs) contains no NaN/Inf.
"""

from __future__ import annotations

import hashlib
import io
import json
import math
import multiprocessing
import os
import sys
import textwrap
import time
import traceback
from contextlib import redirect_stderr, redirect_stdout
from typing import Any, Dict, Optional, Tuple

# ---------------------------------------------------------------------------
# Public API
# ---------------------------------------------------------------------------

def run_node(
    code: str,
    seed: int,
    params: Dict[str, Any],
    inputs_raw: Dict[str, Any],       # already dicts with tag/value keys
    output_schema: Dict[str, str],    # portName -> tag
    series_length: int,
    timeout_ms: int,
    mem_mb: int,
) -> Dict[str, Any]:
    """
    Execute user code in a fresh subprocess and return a result dict:

        {
            "ok": bool,
            "outputs": dict | None,   # tag/value shaped, or None on failure
            "error": {"kind": str, "message": str} | None,
            "stdout": str,
            "stderr": str,
            "durationMs": float,
            "outputHash": str | None,
        }
    """
    ctx = multiprocessing.get_context("spawn")
    queue = ctx.Queue()

    worker_args = (
        queue,
        code,
        seed,
        params,
        inputs_raw,
        output_schema,
        series_length,
        mem_mb,
    )

    proc = ctx.Process(target=_worker_main, args=worker_args, daemon=True)
    t0 = time.monotonic()
    proc.start()

    timeout_s = timeout_ms / 1000.0
    proc.join(timeout_s)
    elapsed_ms = (time.monotonic() - t0) * 1000.0

    if proc.is_alive():
        proc.kill()
        proc.join()
        return {
            "ok": False,
            "outputs": None,
            "error": {"kind": "Timeout", "message": f"Node exceeded {timeout_ms} ms time limit."},
            "stdout": "",
            "stderr": "",
            "durationMs": elapsed_ms,
            "outputHash": None,
        }

    if queue.empty():
        # Worker crashed hard (OOM kill, signal, etc.) without posting a result.
        exit_code = proc.exitcode
        kind = "MemoryLimit" if exit_code == -9 else "UserException"
        return {
            "ok": False,
            "outputs": None,
            "error": {"kind": kind, "message": f"Worker process terminated unexpectedly (exit {exit_code})."},
            "stdout": "",
            "stderr": "",
            "durationMs": elapsed_ms,
            "outputHash": None,
        }

    result = queue.get_nowait()
    result["durationMs"] = elapsed_ms
    return result


# ---------------------------------------------------------------------------
# Worker process
# ---------------------------------------------------------------------------

def _worker_main(
    queue,
    code: str,
    seed: int,
    params: Dict[str, Any],
    inputs_raw: Dict[str, Any],
    output_schema: Dict[str, str],
    series_length: int,
    mem_mb: int,
):
    """Entry point for the worker subprocess.

    Runs entirely inside the child process.  Imports guards here so that
    BLAS env vars are set before numpy is imported.
    """
    # Import guards first — this sets BLAS env and applies rlimits/monkeypatches
    # The import itself triggers set_blas_env() via the module-level call below.
    from app.guards import bootstrap_worker, NondeterminismError
    bootstrap_worker(seed=seed, mem_mb=mem_mb)

    # Now safe to import numpy
    import numpy as np

    stdout_buf = io.StringIO()
    stderr_buf = io.StringIO()

    try:
        with redirect_stdout(stdout_buf), redirect_stderr(stderr_buf):
            decoded_inputs = _decode_inputs(inputs_raw, np)
            user_outputs = _execute_user_code(
                code=code,
                seed=seed,
                params=params,
                decoded_inputs=decoded_inputs,
                NondeterminismError=NondeterminismError,
            )
            encoded_outputs, output_hash = _validate_and_encode_outputs(
                user_outputs=user_outputs,
                output_schema=output_schema,
                series_length=series_length,
                np=np,
            )

        result = {
            "ok": True,
            "outputs": encoded_outputs,
            "error": None,
            "stdout": stdout_buf.getvalue(),
            "stderr": stderr_buf.getvalue(),
            "outputHash": output_hash,
        }

    except _SchemaMismatch as exc:
        result = {
            "ok": False,
            "outputs": None,
            "error": {"kind": "SchemaMismatch", "message": str(exc)},
            "stdout": stdout_buf.getvalue(),
            "stderr": stderr_buf.getvalue(),
            "outputHash": None,
        }

    except NondeterminismError as exc:
        result = {
            "ok": False,
            "outputs": None,
            "error": {"kind": "NondeterminismError", "message": str(exc)},
            "stdout": stdout_buf.getvalue(),
            "stderr": stderr_buf.getvalue(),
            "outputHash": None,
        }

    except Exception as exc:
        tb = traceback.format_exc()
        result = {
            "ok": False,
            "outputs": None,
            "error": {"kind": "UserException", "message": f"{type(exc).__name__}: {exc}\n{tb}"},
            "stdout": stdout_buf.getvalue(),
            "stderr": stderr_buf.getvalue(),
            "outputHash": None,
        }

    queue.put(result)


# ---------------------------------------------------------------------------
# Input decoding: JSON wire format → Python / numpy
# ---------------------------------------------------------------------------

def _decode_inputs(inputs_raw: Dict[str, Any], np) -> Dict[str, Any]:
    """Convert tag/value port dicts into native Python/NumPy values."""
    decoded = {}
    for port_name, port in inputs_raw.items():
        tag = port["tag"]
        value = port["value"]

        if tag == "scalar":
            decoded[port_name] = value

        elif tag == "series":
            decoded[port_name] = np.array(value, dtype=np.float64)

        elif tag == "candles":
            decoded[port_name] = {
                col: np.array(arr, dtype=np.float64)
                for col, arr in value.items()
            }

        elif tag == "matrix":
            cols = value["columns"]
            rows = np.array(value["rows"], dtype=np.float64)
            decoded[port_name] = {"columns": cols, "rows": rows}

        else:
            raise _SchemaMismatch(f"Unknown tag {tag!r} on input port {port_name!r}.")

    return decoded


# ---------------------------------------------------------------------------
# Code execution
# ---------------------------------------------------------------------------

_PREAMBLE_TEMPLATE = textwrap.dedent("""\
    # --- sandbox preamble (non-user-writable) ---
    import random as _random
    import numpy as _numpy
    _random.seed({seed})
    _numpy.random.seed({seed_u32})
    # NondeterminismError is already active via monkeypatches applied at process start.
    # --- end preamble ---

""")


def _execute_user_code(
    code: str,
    seed: int,
    params: Dict[str, Any],
    decoded_inputs: Dict[str, Any],
    NondeterminismError,
) -> Dict[str, Any]:
    """Compile and exec user code, return the outputs dict."""
    preamble = _PREAMBLE_TEMPLATE.format(
        seed=seed,
        seed_u32=seed & 0xFFFFFFFF,
    )
    full_code = preamble + code

    outputs: Dict[str, Any] = {}
    namespace = {
        "inputs": decoded_inputs,
        "params": params,
        "seed": seed,
        "outputs": outputs,
        # Expose NondeterminismError so user code can catch it if needed
        # (unusual but does not break anything)
        "NondeterminismError": NondeterminismError,
    }

    compiled = compile(full_code, "<code_node>", "exec")
    exec(compiled, namespace)  # noqa: S102

    # The user may have rebound `outputs` to a new dict; retrieve it.
    return namespace.get("outputs", outputs)


# ---------------------------------------------------------------------------
# Output validation and encoding
# ---------------------------------------------------------------------------

class _SchemaMismatch(Exception):
    pass


def _validate_and_encode_outputs(
    user_outputs: Dict[str, Any],
    output_schema: Dict[str, str],
    series_length: int,
    np,
) -> Tuple[Dict[str, Any], str]:
    """
    Validate that every declared output is present with the correct tag and
    length.  Encodes to JSON-serialisable tag/value dicts and returns
    (encoded_outputs, sha256_hash).
    """
    encoded: Dict[str, Any] = {}

    # --- Check for missing outputs ---
    missing = set(output_schema) - set(user_outputs)
    if missing:
        raise _SchemaMismatch(
            f"Outputs declared in outputSchema but not produced by user code: {sorted(missing)}"
        )

    # --- Check for extra outputs (allowed — we just ignore them? No, spec says SchemaMismatch) ---
    extra = set(user_outputs) - set(output_schema)
    if extra:
        raise _SchemaMismatch(
            f"Outputs produced by user code but not declared in outputSchema: {sorted(extra)}"
        )

    for port_name, expected_tag in output_schema.items():
        raw = user_outputs[port_name]
        encoded[port_name] = _encode_single_output(
            port_name, raw, expected_tag, series_length, np
        )

    output_hash = _hash_outputs(encoded)
    return encoded, output_hash


def _encode_single_output(
    port_name: str,
    raw: Any,
    expected_tag: str,
    series_length: int,
    np,
) -> Dict[str, Any]:
    """Encode a single output value and validate tag / length / finiteness."""

    if expected_tag == "scalar":
        if isinstance(raw, (np.floating, np.integer)):
            raw = raw.item()
        if not isinstance(raw, (float, int, bool, str)):
            raise _SchemaMismatch(
                f"Output port {port_name!r}: expected scalar, got {type(raw).__name__}"
            )
        if isinstance(raw, float) and not math.isfinite(raw):
            raise _SchemaMismatch(
                f"Output port {port_name!r}: scalar value is not finite ({raw})"
            )
        return {"tag": "scalar", "value": raw}

    elif expected_tag == "series":
        arr = np.asarray(raw, dtype=np.float64)
        if arr.ndim != 1:
            raise _SchemaMismatch(
                f"Output port {port_name!r}: series must be 1-D, got shape {arr.shape}"
            )
        if series_length > 0 and len(arr) != series_length:
            raise _SchemaMismatch(
                f"Output port {port_name!r}: series length {len(arr)} != expected {series_length}"
            )
        if not np.all(np.isfinite(arr)):
            bad = np.where(~np.isfinite(arr))[0][:5].tolist()
            raise _SchemaMismatch(
                f"Output port {port_name!r}: series contains NaN/Inf at indices {bad}"
            )
        return {"tag": "series", "value": arr.tolist()}

    elif expected_tag == "candles":
        if not isinstance(raw, dict):
            raise _SchemaMismatch(
                f"Output port {port_name!r}: candles must be a dict, got {type(raw).__name__}"
            )
        required_keys = {"openTime", "open", "high", "low", "close", "volume"}
        missing_keys = required_keys - set(raw.keys())
        if missing_keys:
            raise _SchemaMismatch(
                f"Output port {port_name!r}: candles missing keys {sorted(missing_keys)}"
            )
        encoded_value = {}
        for col, col_data in raw.items():
            col_arr = np.asarray(col_data, dtype=np.float64)
            if series_length > 0 and len(col_arr) != series_length:
                raise _SchemaMismatch(
                    f"Output port {port_name!r}: candles column {col!r} length {len(col_arr)} != {series_length}"
                )
            if not np.all(np.isfinite(col_arr)):
                raise _SchemaMismatch(
                    f"Output port {port_name!r}: candles column {col!r} contains NaN/Inf"
                )
            encoded_value[col] = col_arr.tolist()
        return {"tag": "candles", "value": encoded_value}

    elif expected_tag == "matrix":
        if not isinstance(raw, dict):
            raise _SchemaMismatch(
                f"Output port {port_name!r}: matrix must be a dict, got {type(raw).__name__}"
            )
        cols = raw.get("columns", [])
        rows_raw = raw.get("rows")
        rows_arr = np.asarray(rows_raw, dtype=np.float64)
        if not np.all(np.isfinite(rows_arr)):
            raise _SchemaMismatch(
                f"Output port {port_name!r}: matrix contains NaN/Inf"
            )
        return {
            "tag": "matrix",
            "value": {"columns": list(cols), "rows": rows_arr.tolist()},
        }

    else:
        raise _SchemaMismatch(f"Unknown output tag {expected_tag!r} for port {port_name!r}.")


# ---------------------------------------------------------------------------
# Output hashing (canonical, byte-stable)
# ---------------------------------------------------------------------------

def _canonical_repr(value: Any) -> str:
    """Produce a deterministic string representation for hashing."""
    if isinstance(value, float):
        return repr(round(value, 12))
    if isinstance(value, list):
        return "[" + ",".join(_canonical_repr(v) for v in value) + "]"
    if isinstance(value, dict):
        return "{" + ",".join(
            f"{repr(k)}:{_canonical_repr(v)}" for k, v in sorted(value.items())
        ) + "}"
    return repr(value)


def _hash_outputs(encoded_outputs: Dict[str, Any]) -> str:
    """SHA-256 over the canonicalized, sorted-key output dict."""
    canonical = _canonical_repr(encoded_outputs)
    return hashlib.sha256(canonical.encode("utf-8")).hexdigest()


# ---------------------------------------------------------------------------
# Fingerprint helper (used by main.py for /fingerprint endpoint)
# ---------------------------------------------------------------------------

_FINGERPRINT_CODE = textwrap.dedent("""\
    import numpy as _np
    data = _np.arange(1, 11, dtype=_np.float64)
    cumsum = _np.cumsum(data)
    rolling_mean = _np.array([
        _np.mean(data[max(0, i-2):i+1]) for i in range(len(data))
    ])
    outputs["cumsum"] = cumsum
    outputs["rolling_mean"] = rolling_mean
""")


def compute_fingerprint_hash() -> str:
    """Run the canonical determinism snippet and return the output hash.

    The .NET backend can call /fingerprint on startup and after upgrades to
    detect silent float drift caused by a numpy/BLAS version change.
    """
    result = run_node(
        code=_FINGERPRINT_CODE,
        seed=42,
        params={},
        inputs_raw={},
        output_schema={"cumsum": "series", "rolling_mean": "series"},
        series_length=10,
        timeout_ms=10_000,
        mem_mb=64,
    )
    if not result["ok"]:
        raise RuntimeError(
            f"Fingerprint computation failed: {result.get('error')}"
        )
    return result["outputHash"]
