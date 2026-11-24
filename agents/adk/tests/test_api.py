"""The HTTP contract of contracts/agent-service-api.md, through the real FastAPI app."""

import contextlib
import json

from fastapi.testclient import TestClient

from app.main import create_app

from .conftest import REQUEST, ScriptedModel, assert_well_formed, manager_for, until_memo


def client_for(data_dir) -> TestClient:
    @contextlib.asynccontextmanager
    async def context():
        async with manager_for(data_dir, ScriptedModel(script=until_memo())) as manager:
            yield manager

    return TestClient(create_app(context))


def read_sse(client: TestClient, url: str, headers: dict | None = None) -> list[dict]:
    events = []
    with client.stream("GET", url, headers=headers or {}) as response:
        assert response.status_code == 200
        assert response.headers["content-type"].startswith("text/event-stream")
        frame: dict = {}
        for line in response.iter_lines():
            if line.startswith("id: "):
                frame["id"] = int(line[4:])
            elif line.startswith("event: "):
                frame["event"] = line[7:]
            elif line.startswith("data: "):
                frame["data"] = json.loads(line[6:])
            elif line == "" and frame:
                assert frame["id"] == frame["data"]["sequence"] and frame["event"] == frame["data"]["type"]
                events.append(frame["data"])
                frame = {}
    return events


def test_full_flow_over_http(data_dir):
    with client_for(data_dir) as client:
        assert client.get("/health").json() == {"status": "ok"}

        started = client.post("/runs", json=REQUEST.dump())
        assert started.status_code == 201
        run_id = started.json()["runId"]

        events = read_sse(client, f"/runs/{run_id}/events")
        assert_well_formed(events, terminal=False)

        # Reconnect positions: Last-Event-ID wins over ?after=.
        tail = read_sse(client, f"/runs/{run_id}/events?after=1", {"Last-Event-ID": str(len(events) - 1)})
        assert [e["sequence"] for e in tail] == [len(events)]
        assert read_sse(client, f"/runs/{run_id}/events?after={len(events)}") == []

        approval_id = events[-1]["payload"]["approvalId"]
        stale = client.post(f"/runs/{run_id}/resume", json={"approvalId": "x", "decision": "approve", "decidedBy": "a"})
        assert stale.status_code == 409 and stale.json()["error"]["code"] == "stale_approval"

        resumed = client.post(f"/runs/{run_id}/resume",
                              json={"approvalId": approval_id, "decision": "approve", "decidedBy": "analis"})
        assert resumed.status_code == 202 and resumed.json() == {}

        events += read_sse(client, f"/runs/{run_id}/events?after={events[-1]['sequence']}")
        assert_well_formed(events, terminal=True)
        assert events[-1]["payload"]["outcome"] == "approved"

        assert client.post(f"/runs/{run_id}/cancel").status_code == 202  # idempotent on a finished run


def test_errors_use_the_contract_shape(data_dir):
    with client_for(data_dir) as client:
        bad = client.post("/runs", json={"symbol": "bbca"})
        assert bad.status_code == 400 and bad.json()["error"]["code"] == "invalid_input"

        missing = client.get("/runs/adk-unknown/events")
        assert missing.status_code == 404 and missing.json()["error"]["code"] == "run_not_found"

        assert client.post("/runs/adk-unknown/cancel").status_code == 404
