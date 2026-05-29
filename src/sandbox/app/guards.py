"""
Determinism guards for the sandbox worker process.

Called from executor.py INSIDE the worker subprocess (not the FastAPI process).
Responsibilities:
  1. Set BLAS / thread-count environment variables BEFORE any numpy import.
  2. Seed Python and NumPy RNGs.
  3. Monkeypatch non-deterministic builtins to raise NondeterminismError.
  4. Apply rlimits (memory + file-write) where the OS supports them.
"""

from __future__ import annotations

import os
import sys


# ---------------------------------------------------------------------------
# Step 1: force single-thread BLAS (must run before numpy is imported)
# ---------------------------------------------------------------------------

def set_blas_env():
    """Force all BLAS/thread libraries to single-thread mode.

    Must be called as early as possible in the worker process, before any
    import of numpy / scipy / pandas so that the library picks up the env
    variables on its first initialisation.
    """
    for var in (
        "OMP_NUM_THREADS",
        "MKL_NUM_THREADS",
        "OPENBLAS_NUM_THREADS",
        "NUMEXPR_NUM_THREADS",
        "VECLIB_MAXIMUM_THREADS",
        "BLIS_NUM_THREADS",
    ):
        os.environ[var] = "1"


# ---------------------------------------------------------------------------
# Step 2: seed RNGs
# ---------------------------------------------------------------------------

def seed_rngs(seed: int):
    """Seed Python random and NumPy RNG deterministically."""
    import random
    random.seed(seed)

    try:
        import numpy as np
        np.random.seed(seed & 0xFFFFFFFF)  # numpy seed must be uint32
    except ImportError:
        pass


# ---------------------------------------------------------------------------
# Step 3: monkeypatch non-deterministic builtins
# ---------------------------------------------------------------------------

class NondeterminismError(RuntimeError):
    """Raised when user code attempts a non-deterministic operation."""


def _raise(name: str):
    def _blocked(*_args, **_kwargs):
        raise NondeterminismError(
            f"Non-deterministic call blocked: {name}(). "
            "Code nodes must be pure functions of their declared inputs."
        )
    _blocked.__name__ = name
    return _blocked


def apply_monkeypatches():
    """Replace non-deterministic stdlib calls with hard errors.

    Patched:
    - time.time / time.monotonic / time.perf_counter / time.time_ns /
      time.monotonic_ns / time.perf_counter_ns / time.process_time
    - datetime.datetime.now / utcnow
    - os.urandom
    - secrets module (all public functions)
    - uuid.uuid1 / uuid4
    - socket.socket (constructor)
    - builtins.open in write modes
    """
    import time
    import datetime as _dt
    import uuid as _uuid

    # --- time ---
    time.time = _raise("time.time")
    time.monotonic = _raise("time.monotonic")
    time.perf_counter = _raise("time.perf_counter")
    time.time_ns = _raise("time.time_ns")
    time.monotonic_ns = _raise("time.monotonic_ns")
    time.perf_counter_ns = _raise("time.perf_counter_ns")
    time.process_time = _raise("time.process_time")

    # --- datetime ---
    _dt.datetime.now = classmethod(lambda cls, *a, **kw: (_ for _ in ()).throw(
        NondeterminismError("Non-deterministic call blocked: datetime.datetime.now()")
    ))
    _dt.datetime.utcnow = classmethod(lambda cls, *a, **kw: (_ for _ in ()).throw(
        NondeterminismError("Non-deterministic call blocked: datetime.datetime.utcnow()")
    ))

    # Simpler classmethod patching via a thin class replacement
    class _BlockedDatetime(_dt.datetime):
        @classmethod
        def now(cls, tz=None):
            raise NondeterminismError(
                "Non-deterministic call blocked: datetime.datetime.now()"
            )

        @classmethod
        def utcnow(cls):
            raise NondeterminismError(
                "Non-deterministic call blocked: datetime.datetime.utcnow()"
            )

    _dt.datetime = _BlockedDatetime

    # --- os.urandom ---
    os.urandom = _raise("os.urandom")

    # --- secrets ---
    try:
        import secrets
        secrets.token_bytes = _raise("secrets.token_bytes")
        secrets.token_hex = _raise("secrets.token_hex")
        secrets.token_urlsafe = _raise("secrets.token_urlsafe")
        secrets.choice = _raise("secrets.choice")
        secrets.randbelow = _raise("secrets.randbelow")
        secrets.randbits = _raise("secrets.randbits")
        secrets.SystemRandom = _raise("secrets.SystemRandom")
    except ImportError:
        pass

    # --- uuid ---
    _uuid.uuid1 = _raise("uuid.uuid1")
    _uuid.uuid4 = _raise("uuid.uuid4")

    # --- socket ---
    try:
        import socket
        _OrigSocket = socket.socket

        class _BlockedSocket(_OrigSocket):
            def __init__(self, *args, **kwargs):
                raise NondeterminismError(
                    "Non-deterministic call blocked: socket.socket(). "
                    "Network access is forbidden in code nodes."
                )

        socket.socket = _BlockedSocket
        # Also block create_connection, etc.
        socket.create_connection = _raise("socket.create_connection")
        socket.getaddrinfo = _raise("socket.getaddrinfo")
        socket.gethostbyname = _raise("socket.gethostbyname")
    except ImportError:
        pass

    # --- open() write modes ---
    import builtins
    _orig_open = builtins.open

    def _guarded_open(file, mode="r", *args, **kwargs):
        _write_modes = set("wxa") | {"wb", "xb", "ab", "w+", "r+", "a+"}
        mode_str = mode if isinstance(mode, str) else "r"
        if any(m in mode_str for m in ("w", "x", "a")) or "+" in mode_str:
            raise NondeterminismError(
                f"Non-deterministic call blocked: open({file!r}, {mode!r}). "
                "Write-mode file access is forbidden in code nodes."
            )
        return _orig_open(file, mode, *args, **kwargs)

    builtins.open = _guarded_open


# ---------------------------------------------------------------------------
# Step 4: rlimits
# ---------------------------------------------------------------------------

def apply_rlimits(mem_mb: int):
    """Apply OS-level resource limits.

    - RLIMIT_AS: virtual address space cap (mem_mb MiB).
    - RLIMIT_FSIZE: file size cap = 0 (any write syscall fails at OS level).

    Both are best-effort: macOS does not honour RLIMIT_AS for malloc, and some
    limits are not available on all kernels.  We wrap each in try/except so
    local dev always runs; the Docker container (Linux + seccomp) enforces them
    fully.
    """
    try:
        import resource  # Unix only
    except ImportError:
        return  # Windows — skip silently

    mem_bytes = mem_mb * 1024 * 1024

    try:
        resource.setrlimit(resource.RLIMIT_AS, (mem_bytes, mem_bytes))
    except (ValueError, resource.error, OSError):
        # macOS: RLIMIT_AS is accepted but not enforced by the kernel for heap;
        # Docker/Linux honours it.  Failure here is acceptable.
        pass

    try:
        resource.setrlimit(resource.RLIMIT_FSIZE, (0, 0))
    except (ValueError, resource.error, OSError):
        pass


# ---------------------------------------------------------------------------
# Convenience: apply everything
# ---------------------------------------------------------------------------

def bootstrap_worker(seed: int, mem_mb: int):
    """Full determinism bootstrap for a sandbox worker process.

    Call this as early as possible in the worker, before any user or library
    code runs.  Order matters:
      1. BLAS env (before numpy import)
      2. rlimits
      3. RNG seeds (numpy must already be importable here)
      4. monkeypatches
    """
    set_blas_env()
    apply_rlimits(mem_mb)
    seed_rngs(seed)
    apply_monkeypatches()
