# Tests for rhino_architect.protocol - the protocol-5 TCP client layer.
#
# A MockPlugin (in-process asyncio TCP server) plays the role of the Rhino
# AIBridge plugin in both protocol-5 (multiplexed) and legacy (strict FIFO)
# modes. No Rhino required.

from __future__ import annotations

import asyncio
import gzip
import json
import struct

import pytest

import rhino_architect.protocol as proto
from rhino_architect.protocol import RhinoConnectionError, RhinoProtocol


# ── Frame helpers (server -> client wire format) ─────────────────────────

def enc_json(obj: dict, flag: int = 0x00) -> bytes:
    body = json.dumps(obj).encode("utf-8")
    if flag == 0x01:
        body = gzip.compress(body)
    return bytes([flag]) + struct.pack(">I", len(body)) + body


def enc_binary(header: dict, image: bytes) -> bytes:
    hb = json.dumps(header).encode("utf-8")
    payload = struct.pack(">I", len(hb)) + hb + image
    return bytes([0x02]) + struct.pack(">I", len(payload)) + payload


async def read_client_frame(reader: asyncio.StreamReader) -> dict:
    length = struct.unpack(">I", await reader.readexactly(4))[0]
    return json.loads(await reader.readexactly(length))


# ── Mock plugin ──────────────────────────────────────────────────────────

class MockPlugin:
    """Scriptable stand-in for the Rhino AIBridge TCP server."""

    # What a protocol-5 plugin advertised before the "progress" feature existed.
    # Kept as the default so every pre-existing test still exercises the old plugin.
    BASE_FEATURES = ["multiplex", "binary_image", "idempotent_retry", "cancel"]

    def __init__(self, multiplex: bool = True, responder=None, features=None):
        self.multiplex = multiplex
        self.responder = responder  # async (writer, payload, conn_index) -> handled: bool
        self.features = list(self.BASE_FEATURES if features is None else features)
        self.connections = 0
        self._server: asyncio.AbstractServer | None = None
        self.port = 0

    async def __aenter__(self):
        self._server = await asyncio.start_server(self._handle, "127.0.0.1", 0)
        self.port = self._server.sockets[0].getsockname()[1]
        return self

    async def __aexit__(self, *exc):
        self._server.close()
        await self._server.wait_closed()

    async def _handle(self, reader: asyncio.StreamReader, writer: asyncio.StreamWriter):
        self.connections += 1
        conn_index = self.connections
        try:
            while True:
                payload = await read_client_frame(reader)
                rid = payload.get("request_id")
                if payload.get("type") == "hello":
                    if self.multiplex:
                        writer.write(enc_json({
                            "status": "ok", "request_id": rid,
                            "features": self.features,
                        }))
                    else:  # legacy plugins answer in order, without echoing request_id
                        writer.write(enc_json({"status": "error", "message": "Unknown command: hello"}))
                    await writer.drain()
                    continue
                if self.responder is not None:
                    handled = await self.responder(writer, payload, conn_index)
                    if handled:
                        continue
                resp = {"status": "ok", "echo": payload.get("type")}
                if self.multiplex:
                    resp["request_id"] = rid
                writer.write(enc_json(resp))
                await writer.drain()
        except (asyncio.IncompleteReadError, ConnectionResetError, OSError):
            pass
        finally:
            try:
                writer.close()
            except Exception:
                pass


@pytest.fixture(autouse=True)
def _no_auth_token(monkeypatch):
    """Never pick up the developer machine's real AIBridge token."""
    monkeypatch.setattr(proto, "_read_auth_token", lambda: None)


async def make_conn(plugin: MockPlugin) -> RhinoProtocol:
    conn = RhinoProtocol("127.0.0.1", plugin.port)
    await conn.connect()
    return conn


# ── Handshake ────────────────────────────────────────────────────────────

@pytest.mark.asyncio
async def test_hello_negotiates_multiplex():
    async with MockPlugin(multiplex=True) as plugin:
        conn = await make_conn(plugin)
        assert conn._server_multiplex is True
        assert conn._server_binary is True
        await conn.disconnect()


@pytest.mark.asyncio
async def test_legacy_plugin_falls_back_to_fifo_mode():
    async with MockPlugin(multiplex=False) as plugin:
        conn = await make_conn(plugin)
        assert conn._server_multiplex is False
        await conn.disconnect()


# ── Routing ──────────────────────────────────────────────────────────────

@pytest.mark.asyncio
async def test_multiplex_routes_out_of_order_responses():
    async def responder(writer, payload, _conn):
        rid = payload["request_id"]
        delay = 0.20 if payload["type"] == "slow" else 0.02

        async def reply():
            await asyncio.sleep(delay)
            writer.write(enc_json({"status": "ok", "which": payload["type"], "request_id": rid}))
            await writer.drain()

        asyncio.ensure_future(reply())
        return True

    async with MockPlugin(multiplex=True, responder=responder) as plugin:
        conn = await make_conn(plugin)
        slow_task = asyncio.ensure_future(conn.send_command("slow"))
        await asyncio.sleep(0.01)  # ensure 'slow' hits the wire first
        fast = await conn.send_command("fast")
        slow = await slow_task
        assert fast.result["which"] == "fast"
        assert slow.result["which"] == "slow"
        await conn.disconnect()


@pytest.mark.asyncio
async def test_legacy_fifo_matches_in_order():
    async with MockPlugin(multiplex=False) as plugin:
        conn = await make_conn(plugin)
        r1 = await conn.send_command("query_scene")
        r2 = await conn.send_command("get_log")
        assert r1.result["echo"] == "query_scene"
        assert r2.result["echo"] == "get_log"
        await conn.disconnect()


# ── Frame formats ────────────────────────────────────────────────────────

@pytest.mark.asyncio
async def test_gzip_frame_decodes():
    async def responder(writer, payload, _conn):
        writer.write(enc_json({"status": "ok", "big": "x" * 500,
                               "request_id": payload["request_id"]}, flag=0x01))
        await writer.drain()
        return True

    async with MockPlugin(multiplex=True, responder=responder) as plugin:
        conn = await make_conn(plugin)
        resp = await conn.send_command("query_scene")
        assert resp.ok and resp.result["big"] == "x" * 500
        await conn.disconnect()


@pytest.mark.asyncio
async def test_binary_image_frame_attaches_raw_bytes():
    img = b"\xff\xd8JPEGDATA" * 8

    async def responder(writer, payload, _conn):
        writer.write(enc_binary(
            {"status": "ok", "format": "jpeg", "request_id": payload["request_id"]}, img))
        await writer.drain()
        return True

    async with MockPlugin(multiplex=True, responder=responder) as plugin:
        conn = await make_conn(plugin)
        resp = await conn.send_command("capture_viewport")
        assert resp.ok and resp.result["_image_raw"] == img
        await conn.disconnect()


@pytest.mark.asyncio
async def test_scene_version_surfaces_as_etag():
    async def responder(writer, payload, _conn):
        writer.write(enc_json({"status": "ok", "scene_version": 42,
                               "request_id": payload["request_id"]}))
        await writer.drain()
        return True

    async with MockPlugin(multiplex=True, responder=responder) as plugin:
        conn = await make_conn(plugin)
        resp = await conn.send_command("query_scene")
        assert resp.scene_version == 42
        await conn.disconnect()


# ── Timeouts & retries ──────────────────────────────────────────────────

@pytest.mark.asyncio
async def test_multiplex_timeout_keeps_socket_and_evicts_future():
    """Regression: timed-out futures must not leak in _fifo (v4.9.3 fix)."""
    async def responder(writer, payload, _conn):
        return True  # swallow the command - never answer

    async with MockPlugin(multiplex=True, responder=responder) as plugin:
        conn = await make_conn(plugin)
        with pytest.raises(RhinoConnectionError) as exc:
            await conn.send_command("boolean_operation", timeout=0.2)
        msg = str(exc.value)
        # The work keeps running in Rhino, so the message must hand back the
        # request_id and point at get_operation_result - otherwise a timeout is
        # indistinguishable from a failure (field report A4).
        assert "STILL RUNNING" in msg
        assert "get_operation_result" in msg
        assert "request_id=" in msg
        assert not conn._pending, "timed-out future leaked in _pending"
        assert not conn._fifo, "timed-out future leaked in _fifo"
        assert conn._writer is not None, "healthy multiplexed socket must stay open"
        await conn.disconnect()


@pytest.mark.asyncio
async def test_abandoned_read_is_auto_cancelled():
    """A read that times out must be cancelled so it stops holding the UI thread.

    One orphaned report_areas blocked four later captures in a real session (A5).
    Reads are recomputable, so cancelling costs nothing; mutations are left running
    because their result is valuable and retrievable.
    """
    cancels: list[str] = []

    async def responder(writer, payload, _conn):
        if payload.get("type") == "cancel":
            cancels.append(payload["params"]["request_id"])
            writer.write(enc_json({"status": "ok", "request_id": payload["request_id"]}))
            await writer.drain()
            return True
        return True  # never answer the actual command

    async with MockPlugin(multiplex=True, responder=responder) as plugin:
        conn = await make_conn(plugin)
        with pytest.raises(RhinoConnectionError):
            await conn.send_command("report_areas", timeout=0.2)
        assert len(cancels) == 1, "an abandoned READ should be cancelled server-side"

        cancels.clear()
        with pytest.raises(RhinoConnectionError):
            await conn.send_command("create_object", timeout=0.2)
        assert cancels == [], "a mutation must NOT be auto-cancelled - its result is wanted"
        await conn.disconnect()


@pytest.mark.asyncio
async def test_legacy_no_blind_resend_of_delivered_mutation():
    """A delivered mutating command must NOT be silently re-sent to a legacy plugin."""
    async def responder(writer, payload, _conn):
        writer.close()  # drop the connection after the mutation was delivered
        return True

    async with MockPlugin(multiplex=False, responder=responder) as plugin:
        conn = await make_conn(plugin)
        with pytest.raises(RhinoConnectionError, match="Not retrying automatically"):
            await conn.send_command("create_object", {"type": "box"})
        await conn.disconnect()


@pytest.mark.asyncio
async def test_idempotent_read_retries_across_reconnect():
    """Read-safe commands transparently retry on a fresh connection."""
    async def responder(writer, payload, conn_index):
        if conn_index == 1:
            writer.close()  # kill the first connection mid-request
            return True
        return False  # second connection: default ok echo

    async with MockPlugin(multiplex=False, responder=responder) as plugin:
        conn = await make_conn(plugin)
        resp = await conn.send_command("query_scene")
        assert resp.ok and resp.result["echo"] == "query_scene"
        assert plugin.connections >= 2
        await conn.disconnect()


# ── Cancellation ─────────────────────────────────────────────────────────

@pytest.mark.asyncio
async def test_cancel_rejected_on_legacy_plugin():
    async with MockPlugin(multiplex=False) as plugin:
        conn = await make_conn(plugin)
        result = await conn.cancel("some-request-id")
        assert result["status"] == "error"
        assert result["error_code"] == "NOT_SUPPORTED"
        await conn.disconnect()


# ── Progress notifications (protocol 5.1) ────────────────────────────────

PROGRESS_FEATURES = MockPlugin.BASE_FEATURES + ["progress"]


def _progress_responder(steps, final=None):
    """Emit `steps` progress frames, then one real response."""
    async def responder(writer, payload, _conn):
        if payload["type"] != "work":
            return False
        rid = payload["request_id"]
        for i in steps:
            writer.write(enc_json({
                "type": "progress", "request_id": rid,
                "percent": i, "message": f"step {i}",
            }))
            await writer.drain()
        writer.write(enc_json({"status": "ok", "request_id": rid,
                               **(final or {"done": True})}))
        await writer.drain()
        return True
    return responder


@pytest.mark.asyncio
async def test_progress_feature_is_negotiated():
    async with MockPlugin(features=PROGRESS_FEATURES) as plugin:
        conn = await make_conn(plugin)
        assert conn.supports_progress is True
        await conn.disconnect()


@pytest.mark.asyncio
async def test_progress_frames_reach_the_callback_and_do_not_resolve_the_request():
    """The whole point: progress arrives DURING the call, the result still lands."""
    seen = []
    async with MockPlugin(responder=_progress_responder([10, 50, 90]),
                          features=PROGRESS_FEATURES) as plugin:
        conn = await make_conn(plugin)
        resp = await conn.send_command("work", on_progress=seen.append)
        assert resp.ok
        assert resp.result == {"done": True}          # real response, not a progress frame
        assert [f["percent"] for f in seen] == [10, 50, 90]
        assert [f["message"] for f in seen] == ["step 10", "step 50", "step 90"]
        await conn.disconnect()


@pytest.mark.asyncio
async def test_progress_is_not_requested_from_a_plugin_that_lacks_the_feature():
    """An older plugin must never be sent want_progress - it would have to ignore it."""
    seen_payloads = []

    async def responder(writer, payload, _conn):
        if payload["type"] != "work":
            return False
        seen_payloads.append(payload)
        writer.write(enc_json({"status": "ok", "request_id": payload["request_id"]}))
        await writer.drain()
        return True

    async with MockPlugin(responder=responder) as plugin:   # BASE_FEATURES: no progress
        conn = await make_conn(plugin)
        assert conn.supports_progress is False
        resp = await conn.send_command("work", on_progress=lambda f: None)
        assert resp.ok                                   # degrades silently, still works
        assert "want_progress" not in seen_payloads[0]
        await conn.disconnect()


@pytest.mark.asyncio
async def test_a_raising_progress_callback_cannot_kill_the_connection():
    """A cosmetic status update must never tear down every in-flight request."""
    def boom(_frame):
        raise RuntimeError("callback is broken")

    async with MockPlugin(responder=_progress_responder([25, 75]),
                          features=PROGRESS_FEATURES) as plugin:
        conn = await make_conn(plugin)
        resp = await conn.send_command("work", on_progress=boom)
        assert resp.ok                                   # survived both raising frames
        # And the connection is still usable afterwards.
        again = await conn.send_command("ping")
        assert again.ok
        await conn.disconnect()


@pytest.mark.asyncio
async def test_progress_frames_do_not_desync_legacy_fifo_matching():
    """Progress must not consume a FIFO slot: legacy matching is strictly positional."""
    async def responder(writer, payload, _conn):
        t = payload["type"]
        if t not in ("a", "b"):
            return False
        if t == "a":
            # Interleave a progress frame with NO request_id echo, legacy style.
            writer.write(enc_json({"type": "progress", "percent": 50, "message": "half"}))
            await writer.drain()
        writer.write(enc_json({"status": "ok", "which": t}))   # legacy: no request_id
        await writer.drain()
        return True

    async with MockPlugin(multiplex=False, responder=responder) as plugin:
        conn = await make_conn(plugin)
        first = await conn.send_command("a")
        second = await conn.send_command("b")
        # If the progress frame had eaten a FIFO slot, "b" would receive "a"'s response.
        assert first.result == {"which": "a"}
        assert second.result == {"which": "b"}
        await conn.disconnect()


@pytest.mark.asyncio
async def test_late_progress_frame_after_completion_is_dropped():
    """A frame arriving after the response must not raise or leak a callback."""
    seen = []

    async def responder(writer, payload, _conn):
        if payload["type"] != "work":
            return False
        rid = payload["request_id"]
        writer.write(enc_json({"status": "ok", "request_id": rid}))
        writer.write(enc_json({"type": "progress", "request_id": rid, "percent": 100}))
        await writer.drain()
        return True

    async with MockPlugin(responder=responder, features=PROGRESS_FEATURES) as plugin:
        conn = await make_conn(plugin)
        resp = await conn.send_command("work", on_progress=seen.append)
        assert resp.ok
        await asyncio.sleep(0.05)          # let the trailing frame be processed
        assert conn._progress_cbs == {}    # unregistered, no leak
        follow = await conn.send_command("ping")
        assert follow.ok                   # connection unharmed
        await conn.disconnect()


# ── The server-side bridge to MCP progress notifications ─────────────────

@pytest.mark.asyncio
async def test_progress_bridge_forwards_frames_as_mcp_notifications():
    """Plugin frame -> ctx.report_progress(percent, 100, message)."""
    from rhino_architect.server import _progress_bridge

    calls = []

    class FakeCtx:
        async def report_progress(self, progress, total, message=None):
            calls.append((progress, total, message))

    bridge = _progress_bridge(FakeCtx())
    bridge({"percent": 40, "message": "op 4/10: create_object"})
    bridge({"percent": 80, "message": "op 8/10: create_object"})
    await asyncio.sleep(0.05)          # report_progress is fired as a task, not awaited

    assert calls == [
        (40.0, 100.0, "op 4/10: create_object"),
        (80.0, 100.0, "op 8/10: create_object"),
    ]


@pytest.mark.asyncio
async def test_progress_bridge_is_inert_without_a_context():
    """Tools that never declare a Context must cost nothing."""
    from rhino_architect.server import _progress_bridge
    assert _progress_bridge(None) is None


@pytest.mark.asyncio
async def test_progress_bridge_ignores_frames_without_a_percent():
    """A malformed frame must not raise inside the reader task."""
    from rhino_architect.server import _progress_bridge

    calls = []

    class FakeCtx:
        async def report_progress(self, progress, total, message=None):
            calls.append(progress)

    bridge = _progress_bridge(FakeCtx())
    bridge({"message": "no percent here"})     # must not raise
    await asyncio.sleep(0.05)
    assert calls == []


# ── Pure functions ───────────────────────────────────────────────────────

def test_parse_response_flattens_non_control_keys():
    raw = {"status": "ok", "message": "", "object_ids": ["a", "b"],
           "scene_version": 7, "request_id": "x"}
    resp = RhinoProtocol._parse_response(raw)
    assert resp.ok
    assert resp.result == {"object_ids": ["a", "b"]}
    assert resp.scene_version == 7


def test_parse_response_explicit_result_key():
    raw = {"status": "error", "message": "boom", "result": {"detail": 1}}
    resp = RhinoProtocol._parse_response(raw)
    assert not resp.ok
    assert resp.message == "boom"
    assert resp.result == {"detail": 1}
