import asyncio

import pytest
from google.genai import errors as genai_errors

from app.contract import DISCLAIMER, ApprovalDecision
from app.runs import ServiceError

from .conftest import (REQUEST, ScriptedModel, assert_well_formed, call, drain, finished, function_responses,
                       manager_for, memo_args, text, until_memo)

pytestmark = pytest.mark.asyncio


def decision(events, kind: str, comment: str | None = None) -> ApprovalDecision:
    return ApprovalDecision(approval_id=events[-1]["payload"]["approvalId"], decision=kind,
                            decided_by="analis", comment=comment)


async def test_run_pauses_at_submit_memo_and_approve_ends_with_done(data_dir):
    model = ScriptedModel(script=until_memo())
    async with manager_for(data_dir, model) as m:
        run_id = await m.start(REQUEST)
        events = await drain(m, run_id)
        assert_well_formed(events, terminal=False)

        summaries = {p["toolName"]: p for p in finished(events)}
        assert summaries["idx_quote"]["summary"].startswith("Harga 5.800 (+2,2%)")
        assert "66 sesi" in summaries["idx_history"]["summary"]
        approval = events[-1]["payload"]
        assert approval["revision"] == 0 and approval["memo"]["disclaimer"] == DISCLAIMER
        assert any(e["type"] == "token" for e in events)

        # The model saw the call id it must cite.
        seen = [r.response.get("result", "") for r in function_responses(model.requests[2]) if r.name == "idx_quote"]
        assert seen and seen[0].startswith("[toolCallId: c2]")

        requests_before = len(model.requests)
        await m.resume(run_id, decision(events, "approve"))
        events += await drain(m, run_id, events[-1]["sequence"])

        assert_well_formed(events, terminal=True)
        done = events[-1]["payload"]
        assert done["outcome"] == "approved" and done["toolCallsUsed"] == 3
        assert next(p for p in finished(events) if p["toolCallId"] == "m1")["summary"] == "Memo disetujui analis."
        assert len(model.requests) == requests_before  # no closing reply after approval


async def test_reject_ends_without_calling_the_model_again(data_dir):
    model = ScriptedModel(script=until_memo())
    async with manager_for(data_dir, model) as m:
        run_id = await m.start(REQUEST)
        events = await drain(m, run_id)
        before = len(model.requests)

        await m.resume(run_id, decision(events, "reject", "Bukti belum cukup."))
        events += await drain(m, run_id, events[-1]["sequence"])

        assert_well_formed(events, terminal=True)
        assert events[-1]["payload"]["outcome"] == "rejected"
        assert len(model.requests) == before


async def test_revise_sends_comment_and_produces_new_draft(data_dir):
    model = ScriptedModel(script=[*until_memo(), [call("m2", "submit_memo", memo_args("c2", "c3"))]])
    async with manager_for(data_dir, model) as m:
        run_id = await m.start(REQUEST)
        events = await drain(m, run_id)

        await m.resume(run_id, decision(events, "revise", "Tambahkan riwayat."))
        events += await drain(m, run_id, events[-1]["sequence"])

        assert_well_formed(events, terminal=False)
        assert events[-1]["payload"]["revision"] == 1
        reply = [r for r in function_responses(model.requests[-1]) if r.name == "submit_memo"][-1]
        assert reply.response["result"] == "Analis meminta revisi: Tambahkan riwayat."


async def test_third_revision_ends_with_revision_limit(data_dir):
    resubmit = [[call(f"m{i}", "submit_memo", memo_args("c2"))] for i in range(2, 4)]
    model = ScriptedModel(script=[*until_memo(), *resubmit])
    async with manager_for(data_dir, model) as m:
        run_id = await m.start(REQUEST)
        events = await drain(m, run_id)
        for _ in range(3):
            await m.resume(run_id, decision(events, "revise", "Lagi."))
            events += await drain(m, run_id, events[-1]["sequence"])

        assert_well_formed(events, terminal=True)
        assert events[-1]["payload"]["code"] == "revision_limit_exceeded"


async def test_invalid_memo_is_returned_to_the_model_and_corrected(data_dir):
    model = ScriptedModel(script=[
        [call("c2", "idx_quote")],
        [call("m1", "submit_memo", memo_args("call_tidak_ada"))],
        [call("m2", "submit_memo", memo_args("c2"))],
    ])
    async with manager_for(data_dir, model) as m:
        events = await drain(m, await m.start(REQUEST))

        assert_well_formed(events, terminal=False)
        rejected = next(p for p in finished(events) if p["toolCallId"] == "m1")
        assert rejected["error"]["code"] == "invalid_memo" and "call_tidak_ada" in rejected["error"]["message"]


async def test_failed_tool_is_reported_and_run_continues(data_dir):
    model = ScriptedModel(script=[
        [call("c2", "idx_quote"), call("c9", "idx_bandar", {"symbols": "BBCA"})],
        [call("m1", "submit_memo", memo_args("c2"))],
    ])
    async with manager_for(data_dir, model) as m:
        events = await drain(m, await m.start(REQUEST))

        assert_well_formed(events, terminal=False)
        bandar = next(p for p in finished(events) if p["toolName"] == "idx_bandar")
        assert bandar["status"] == "error" and bandar["error"]["code"] == "provider"


async def test_parallel_calls_past_limit_get_warning_and_model_can_submit(data_dir):
    model = ScriptedModel(script=[
        [call("c1", "idx_quote"), call("c2", "idx_history"), call("c3", "idx_stockbit_status"), call("c4", "idx_quote")],
        [call("m1", "submit_memo", memo_args("c1"))],
    ])
    async with manager_for(data_dir, model, max_tool_calls=2) as m:
        events = await drain(m, await m.start(REQUEST))

        assert_well_formed(events, terminal=False)
        codes = [p.get("error", {}).get("code") for p in finished(events) if p["toolName"] != "submit_memo"]
        assert codes.count(None) == 2 and codes.count("step_limit") == 2


async def test_step_limit_warns_once_then_ends_with_error(data_dir):
    counter = iter(range(1, 100))
    model = ScriptedModel(fallback=lambda _: [call(f"q{next(counter)}", "idx_quote")])
    async with manager_for(data_dir, model, max_tool_calls=3) as m:
        events = await drain(m, await m.start(REQUEST))

        assert_well_formed(events, terminal=True)
        assert events[-1]["payload"]["code"] == "step_limit_exceeded"
        results = finished(events)
        assert sum(p["status"] == "ok" for p in results) == 3
        assert all(p["error"]["code"] == "step_limit" for p in results[3:])


async def test_narration_in_separate_turns_continues_the_run(data_dir):
    model = ScriptedModel(script=[
        [text("Memeriksa sesi Stockbit.")],
        [call("c1", "idx_stockbit_status")],
        [text("Mengukur harga.")],
        [call("c2", "idx_quote")],
        [call("m1", "submit_memo", memo_args("c2"))],
    ])
    async with manager_for(data_dir, model) as m:
        events = await drain(m, await m.start(REQUEST))

    assert_well_formed(events, terminal=False)
    assert [e["type"] for e in events].count("token") >= 2


async def test_too_many_text_only_turns_ends_with_model_error(data_dir):
    model = ScriptedModel(fallback=lambda _: [text("Masih berpikir.")])
    async with manager_for(data_dir, model) as m:
        events = await drain(m, await m.start(REQUEST))

    assert_well_formed(events, terminal=True)
    assert events[-1]["payload"]["code"] == "model_error"
    assert len(model.requests) == 3


async def test_cancel_during_model_call_ends_cancelled(data_dir):
    model = ScriptedModel(script=until_memo(), delay=2)
    async with manager_for(data_dir, model) as m:
        run_id = await m.start(REQUEST)
        await asyncio.sleep(0.3)
        await m.cancel(run_id)
        events = await drain(m, run_id)

        assert_well_formed(events, terminal=True)
        assert events[-1]["payload"]["outcome"] == "cancelled"


async def test_cancel_while_paused_closes_submit_memo(data_dir):
    async with manager_for(data_dir, ScriptedModel(script=until_memo())) as m:
        run_id = await m.start(REQUEST)
        events = await drain(m, run_id)

        await m.cancel(run_id)
        await m.cancel(run_id)  # idempotent
        events += await drain(m, run_id, events[-1]["sequence"])

        assert_well_formed(events, terminal=True)
        assert events[-1]["payload"]["outcome"] == "cancelled"
        assert events[-2]["payload"]["error"]["code"] == "cancelled"


async def test_paused_run_survives_a_service_restart(data_dir):
    async with manager_for(data_dir, ScriptedModel(script=until_memo())) as first:
        run_id = await first.start(REQUEST)
        events = await drain(first, run_id)

    # New runner, new DatabaseSessionService, same files: as after a restart.
    second_model = ScriptedModel(script=[[call("m2", "submit_memo", memo_args("c2"))]])
    async with manager_for(data_dir, second_model) as second:
        assert (await second.get(run_id)).status == "paused"
        assert await drain(second, run_id) == events  # replay from storage
        await second.resume(run_id, decision(events, "revise", "Perjelas."))
        events += await drain(second, run_id, events[-1]["sequence"])
        assert events[-1]["type"] == "approval_required" and events[-1]["payload"]["revision"] == 1

        await second.resume(run_id, decision(events, "approve"))
        events += await drain(second, run_id, events[-1]["sequence"])

    assert_well_formed(events, terminal=True)
    assert events[-1]["payload"]["outcome"] == "approved"


async def test_run_interrupted_mid_stream_is_closed_on_restart(data_dir):
    async with manager_for(data_dir, ScriptedModel(script=until_memo(), delay=5)) as first:
        run_id = await first.start(REQUEST)
        await asyncio.sleep(0.2)
        first._tasks.clear()  # simulate a crash: the task is abandoned, not cancelled
        await first._store.close()

    async with manager_for(data_dir, ScriptedModel(script=[])) as second:
        events = await drain(second, run_id)

    assert_well_formed(events, terminal=True)
    assert events[-1]["payload"]["code"] == "internal" and events[-1]["payload"]["retryable"]


async def test_resume_errors_follow_the_contract(data_dir):
    async with manager_for(data_dir, ScriptedModel(script=until_memo())) as m:
        run_id = await m.start(REQUEST)
        events = await drain(m, run_id)

        with pytest.raises(ServiceError) as stale:
            await m.resume(run_id, ApprovalDecision(approval_id="approval_9", decision="approve", decided_by="a"))
        with pytest.raises(ServiceError) as no_comment:
            await m.resume(run_id, decision(events, "reject"))
        with pytest.raises(ServiceError) as unknown:
            await m.resume("adk-unknown", decision(events, "approve"))

        assert (stale.value.status, stale.value.code) == (409, "stale_approval")
        assert (no_comment.value.status, no_comment.value.code) == (400, "invalid_input")
        assert (unknown.value.status, unknown.value.code) == (404, "run_not_found")


async def test_mcp_unavailable_ends_with_retryable_error(data_dir):
    async with manager_for(data_dir, ScriptedModel(), tools_available=False) as m:
        events = await drain(m, await m.start(REQUEST))

    assert_well_formed(events, terminal=True)
    assert events[-1]["payload"]["code"] == "mcp_unavailable" and events[-1]["payload"]["retryable"]


async def test_model_failure_ends_with_model_error(data_dir):
    failure = genai_errors.ServerError(503, {"error": {"code": 503, "message": "overloaded", "status": "UNAVAILABLE"}})
    async with manager_for(data_dir, ScriptedModel(error=failure)) as m:
        events = await drain(m, await m.start(REQUEST))

    assert_well_formed(events, terminal=True)
    assert events[-1]["payload"]["code"] == "model_error"
