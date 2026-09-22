# Tests for elicitation: confirming destructive operations with the human.
#
# These drive the server through a REAL MCP client over the SDK's in-memory
# transport, so capability negotiation and the elicitation/create round trip are
# the genuine protocol. Only Rhino is faked (_exec_simple), so no Rhino is needed.

from __future__ import annotations

import json

import mcp.types as t
import pytest
from mcp.shared.memory import create_connected_server_and_client_session

import rhino_architect.server as S


class FakeRhino:
    """Stands in for the plugin: counts objects per selector, records real deletes."""

    def __init__(self, count: int, layers=("Walls",), total: int = 137):
        self.count = count
        self.layers = layers
        self.total = total
        self.calls: list[tuple[str, dict]] = []

    async def __call__(self, command: str, params: dict, ctx=None) -> dict:
        self.calls.append((command, dict(params)))
        if command == "delete_objects" and params.get("dry_run"):
            would = [{"id": str(i), "layer": self.layers[i % len(self.layers)]}
                     for i in range(self.count)]
            return {"status": "ok", "dry_run": True, "count": self.count, "would_delete": would}
        if command == "delete_objects":
            return {"status": "ok", "deleted_count": self.count}
        if command == "query_scene":
            return {"status": "ok", "total_objects": self.total}
        if command == "restore_checkpoint":
            return {"status": "ok", "restored": params["name"]}
        return {"status": "ok"}

    def really_deleted(self) -> bool:
        return any(c == "delete_objects" and not p.get("dry_run") for c, p in self.calls)

    def really_restored(self) -> bool:
        return any(c == "restore_checkpoint" for c, _ in self.calls)


def responder(action: str, confirm: bool | None = True, prompts: list | None = None):
    async def cb(_context, params: t.ElicitRequestParams):
        if prompts is not None:
            prompts.append(params.message)
        if action == "accept":
            return t.ElicitResult(action="accept", content={"confirm": confirm})
        return t.ElicitResult(action=action)
    return cb


def payload(res: t.CallToolResult) -> dict:
    if res.structuredContent:
        sc = res.structuredContent
        return sc.get("result", sc) if isinstance(sc, dict) else sc
    return json.loads(res.content[0].text)


async def call(tool: str, args: dict, callback=None) -> dict:
    kw = {"elicitation_callback": callback} if callback else {}
    async with create_connected_server_and_client_session(S.mcp, **kw) as client:
        return payload(await client.call_tool(tool, args))


@pytest.fixture
def rhino(monkeypatch):
    def install(count, **kw):
        fake = FakeRhino(count, **kw)
        monkeypatch.setattr(S, "_exec_simple", fake)
        monkeypatch.setattr(S, "_CONFIRM_DELETE_OVER", 25)
        return fake
    return install


# ── delete_objects ───────────────────────────────────────────────────────


async def test_large_delete_asks_and_proceeds_when_accepted(rhino):
    fake = rhino(842, layers=("NotreDame::Piers", "NotreDame::Vaults"))
    prompts = []
    r = await call("delete_objects", {"params": {"object_ids": ["by_layer:NotreDame"]}},
                   responder("accept", True, prompts))
    assert r["status"] == "ok" and r["confirmation"] == "accepted"
    assert fake.really_deleted()
    # The user sees the real count and where it lives - the facts the model lacked.
    assert "842" in prompts[0]
    assert "NotreDame::Piers" in prompts[0] and "NotreDame::Vaults" in prompts[0]


@pytest.mark.parametrize("action, confirm", [
    ("decline", None), ("cancel", None), ("accept", False),
])
async def test_large_delete_does_not_run_unless_confirmed(rhino, action, confirm):
    fake = rhino(842)
    r = await call("delete_objects", {"params": {"object_ids": ["all"]}},
                   responder(action, confirm))
    assert r["status"] == "cancelled"
    assert r["error_code"] == "USER_DECLINED"
    assert not fake.really_deleted(), "a declined delete must never reach Rhino"


async def test_client_without_elicitation_behaves_exactly_as_before(rhino):
    """No capability -> no dry run, no prompt, delete runs. Nothing regresses."""
    fake = rhino(842)
    r = await call("delete_objects", {"params": {"object_ids": ["all"]}})   # no callback
    assert r["status"] == "ok"
    assert "confirmation" not in r
    assert fake.calls == [("delete_objects", {"object_ids": ["all"]})]


async def test_small_delete_is_not_interrupted(rhino):
    fake = rhino(3)
    prompts = []
    r = await call("delete_objects", {"params": {"object_ids": ["by_layer:Probe"]}},
                   responder("decline", prompts=prompts))
    assert r["status"] == "ok" and prompts == []
    assert fake.really_deleted()


async def test_deleting_all_always_asks_even_when_small(rhino):
    """'all' wipes the scene: 5 objects can still be an hour of work."""
    fake = rhino(5)
    prompts = []
    r = await call("delete_objects", {"params": {"object_ids": ["all"]}},
                   responder("decline", prompts=prompts))
    assert r["status"] == "cancelled" and len(prompts) == 1
    assert "ENTIRE scene" in prompts[0]
    assert not fake.really_deleted()


async def test_a_client_that_claims_elicitation_but_fails_is_refused(rhino):
    """It said it could ask the human. When asking breaks, do not guess - fail closed."""
    fake = rhino(842)

    async def broken(_context, _params):
        return t.ErrorData(code=t.INTERNAL_ERROR, message="dialog crashed")

    r = await call("delete_objects", {"params": {"object_ids": ["all"]}}, broken)
    assert r["status"] == "cancelled"
    assert r["error_code"] == "CONFIRMATION_FAILED"
    assert not fake.really_deleted()


async def test_threshold_zero_disables_confirmation(rhino, monkeypatch):
    fake = rhino(842)
    monkeypatch.setattr(S, "_CONFIRM_DELETE_OVER", 0)
    prompts = []
    r = await call("delete_objects", {"params": {"object_ids": ["all"]}},
                   responder("decline", prompts=prompts))
    assert r["status"] == "ok" and prompts == [] and fake.really_deleted()


# ── restore_checkpoint ───────────────────────────────────────────────────


async def test_restore_quotes_what_will_be_replaced_and_runs_when_accepted(rhino):
    fake = rhino(0, total=137)
    prompts = []
    r = await call("restore_checkpoint", {"name": "phase2"}, responder("accept", True, prompts))
    assert r["status"] == "ok" and r["confirmation"] == "accepted"
    assert fake.really_restored()
    assert "phase2" in prompts[0] and "137 objects" in prompts[0]


async def test_restore_does_not_run_when_declined(rhino):
    fake = rhino(0)
    r = await call("restore_checkpoint", {"name": "phase2"}, responder("decline"))
    assert r["status"] == "cancelled"
    assert not fake.really_restored()


async def test_restore_without_elicitation_behaves_as_before(rhino):
    fake = rhino(0)
    r = await call("restore_checkpoint", {"name": "phase2"})
    assert r["status"] == "ok"
    assert fake.calls == [("restore_checkpoint", {"name": "phase2"})]


# ── the declined response steers the model ───────────────────────────────


async def test_declined_response_tells_the_model_not_to_retry(rhino):
    rhino(842)
    r = await call("delete_objects", {"params": {"object_ids": ["all"]}}, responder("decline"))
    assert "Do not retry" in r["retry_hint"]
