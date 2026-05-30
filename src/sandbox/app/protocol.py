"""
Pydantic models for the fa.foresight sandbox HTTP contract.

Protocol version: 1
"""

from __future__ import annotations

from typing import Any, Dict, List, Literal, Optional, Union

from pydantic import BaseModel, Field


# ---------------------------------------------------------------------------
# Value shapes (struct-of-arrays / columnar)
# ---------------------------------------------------------------------------

TagType = Literal["scalar", "series", "candles", "matrix"]


class ScalarValue(BaseModel):
    tag: Literal["scalar"]
    value: Union[float, int, bool, str]


class SeriesValue(BaseModel):
    tag: Literal["series"]
    value: List[float]


class CandlesValue(BaseModel):
    tag: Literal["candles"]
    value: Dict[str, List[float]]
    """
    Keys: openTime, open, high, low, close, volume
    All lists must have equal length N.
    """


class MatrixValue(BaseModel):
    tag: Literal["matrix"]
    value: Dict[str, Any]
    """
    Keys: columns (List[str]), rows (List[List[float]])
    """


PortValue = Union[ScalarValue, SeriesValue, CandlesValue, MatrixValue]


# ---------------------------------------------------------------------------
# Execution limits
# ---------------------------------------------------------------------------

class Limits(BaseModel):
    timeoutMs: int = Field(default=5000, ge=1, le=60_000)
    memMb: int = Field(default=256, ge=16, le=2048)


# ---------------------------------------------------------------------------
# Request
# ---------------------------------------------------------------------------

class ExecuteRequest(BaseModel):
    protocolVersion: int = Field(default=1)
    mode: Literal["step", "batch"]
    nodeId: str
    code: str
    seed: int = Field(default=0)
    params: Dict[str, Any] = Field(default_factory=dict)
    seriesLength: int = Field(default=0, ge=0)
    inputs: Dict[str, PortValue] = Field(default_factory=dict)
    outputSchema: Dict[str, TagType] = Field(default_factory=dict)
    limits: Limits = Field(default_factory=Limits)


# ---------------------------------------------------------------------------
# Response
# ---------------------------------------------------------------------------

class ErrorDetail(BaseModel):
    kind: Literal[
        "NondeterminismError",
        "Timeout",
        "MemoryLimit",
        "UserException",
        "SchemaMismatch",
    ]
    message: str


class ExecuteResponse(BaseModel):
    ok: bool
    outputs: Optional[Dict[str, PortValue]] = None
    error: Optional[ErrorDetail] = None
    stdout: str = ""
    stderr: str = ""
    durationMs: float = 0.0
    outputHash: Optional[str] = None
