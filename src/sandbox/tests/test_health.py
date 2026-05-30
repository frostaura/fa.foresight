"""
Basic smoke tests for the sandbox sidecar.
Run with: python -m pytest tests/ -v
"""

from fastapi.testclient import TestClient

from app.main import app

client = TestClient(app)


def test_healthz_returns_ok():
    response = client.get("/healthz")
    assert response.status_code == 200
    assert response.json() == {"status": "ok"}


def test_fingerprint_returns_ok():
    response = client.get("/fingerprint")
    assert response.status_code == 200
    data = response.json()
    assert data["status"] == "ok"
    assert "python" in data
    assert "numpy" in data
    assert "pandas" in data


def test_execute_stub_returns_200():
    # Minimal valid ExecuteRequest (see app/protocol.py): mode, nodeId and code are required.
    payload = {
        "mode": "batch",
        "nodeId": "stub",
        "code": 'outputs["x"] = 1.0',
        "seed": 0,
        "params": {},
        "seriesLength": 0,
        "inputs": {},
        "outputSchema": {"x": "scalar"},
    }
    response = client.post("/execute", json=payload)
    assert response.status_code == 200, response.text
    body = response.json()
    assert body["ok"] is True, body.get("error")
    assert "outputs" in body
    assert "stdout" in body
