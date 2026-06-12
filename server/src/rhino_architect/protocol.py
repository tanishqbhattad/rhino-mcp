# RhinoAIBridge - TCP Protocol Layer (protocol 5: multiplexed, idempotent, cancellable)
# by tanishqb | https://github.com/tanishqb/rhino-ai-bridge

"""TCP protocol for the Rhino AI Bridge plugin.

Protocol 5 (v4.8):
- MULTIPLEXED CLIENT: a background reader task routes responses to per-request
  futures by request_id. Reads, pings and cancels no longer queue behind a
  long-running script; with a protocol-5 plugin they answer in sub-ms from the
  plugin's TCP thread while the UI thread is busy.
- IDEMPOTENT RETRIES: every command carries a request_id. If the connection
  drops after a mutating command was delivered, the SAME request_id is re-sent;
  a protocol-5 plugin replays the cached result (or joins the still-running op)
  instead of executing twice. Legacy plugins keep the conservative
  "no blind resend after delivery" rule.
- BINARY IMAGE FRAMES (flag 0x02): [4B header len][JSON header][raw image bytes]
  - viewport captures skip base64 inflation on the wire.
- CANCELLATION: cancel(request_id) signals the plugin to stop a running command
  at its next checkpoint.
- LEGACY COMPATIBILITY: plugins that do not echo request_id are served by FIFO
  response matching (they answer strictly in order), with single-flight retry
  semantics preserved.

Wire format
  client -> server: [4 bytes big-endian length][JSON]
  server -> client: [1 byte flag][4 bytes big-endian length][payload]
      flag 0x00 raw UTF-8 JSON
      flag 0x01 gzip JSON (legacy)
      flag 0x02 binary image: payload = [4B header len][JSON header][image bytes]
"""
from __future__ import annotations

import asyncio
import gzip
import logging
import os
import struct
import sys
import uuid
from collections import deque
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any, Optional

import orjson

logger = logging.getLogger("rhino_ai_bridge.protocol")


def _auth_token_path() -> Path:
    """Return the per-user token location shared with the Rhino plugin."""
    if sys.platform == "win32":
        base = os.environ.get("LOCALAPPDATA") or str(Path.home())
    else:
        base = str(Path.home() / ".config")
    return Path(base) / "AIBridge" / "token"


def _read_auth_token() -> Optional[str]:
    try:
        path = _auth_token_path()
        if path.is_file():
            token = path.read_text(encoding="utf-8").strip()
            return token or None
    except Exception as exc:  # pragma: no cover - best effort
        logger.warning("Could not read AIBridge auth token: %s", exc)
    return None

# Honor the same env vars the heartbeat watchdog uses, so a custom port/host
# changes BOTH the liveness probe and the actual command connection.
DEFAULT_HOST = os.environ.get("RHINO_HOST", "127.0.0.1")
try:
    DEFAULT_PORT = int(os.environ.get("RHINO_PORT", "9544"))
except ValueError:
    DEFAULT_PORT = 9544
CONNECT_TIMEOUT = 5.0
READ_TIMEOUT = 190.0      # must exceed the longest C# per-command timeout (180s)
FRAME_BODY_TIMEOUT = 30.0  # once a frame starts arriving, the rest must follow promptly
PING_TOTAL_TIMEOUT = 8.0
HELLO_TIMEOUT = 5.0
MAX_RETRIES = 2
HEADER_SIZE = 4
MAX_FRAME = 50 * 1024 * 1024   # 50MB cap, matches server

CLIENT_FEATURES = ["multiplex", "binary_image", "idempotent_retry", "cancel"]


@dataclass
class RhinoResponse:
    status: str
    result: dict[str, Any] = field(default_factory=dict)
    message: str = ""
    warnings: list[Any] = field(default_factory=list)
    # Etag-style scene version, stamped by the plugin on every response.
    scene_version: int | None = None

    @property
    def ok(self) -> bool:
        return self.status == "ok"


class RhinoConnectionError(Exception):
    pass


class RhinoCommandError(Exception):
    def __init__(self, message: str, details: dict[str, Any] | None = None):
        super().__init__(message)
        self.details = details or {}


_connection: Optional["RhinoProtocol"] = None
_connection_lock: asyncio.Lock | None = None


def _get_conn_lock() -> asyncio.Lock:
    """Lazy-init the connection lock (must be created inside a running event loop)."""
    global _connection_lock
    if _connection_lock is None:
        _connection_lock = asyncio.Lock()
    return _connection_lock


async def get_connection(host: str = DEFAULT_HOST, port: int = DEFAULT_PORT) -> "RhinoProtocol":
    global _connection
    async with _get_conn_lock():
        if _connection is None:
            _connection = RhinoProtocol(host, port)
        await _connection._ensure_connected()
        return _connection


class RhinoProtocol:
    # Commands that are always safe to transparently re-send after a connection
    # drop, even against a legacy (non-idempotent) plugin.
    _IDEMPOTENT_COMMANDS = frozenset({
        "ping", "hello", "cancel", "query_scene", "get_objects", "list_objects", "get_context",
        "get_scene_summary", "get_object_details", "get_object_info", "list_layers",
        "get_selection", "measure_object", "measure_distance", "check_intersection",
        "validate_objects", "get_log", "get_log_stats", "get_rhino_commands",
        "get_scene_diff", "get_change_log", "get_tracker_version", "get_state",
        "list_sections", "list_display_modes", "list_materials", "get_material",
        "list_checkpoints", "get_design_brief", "get_provenance", "search_memory",
        "get_related_objects", "get_group", "get_all_groups", "get_groups",
        "get_trace_layers", "get_section_profile", "get_silhouette",
        "capture_viewport", "capture_inspection_view", "thumbnail", "batch_preview",
        "get_recovery_log",
    })

    def __init__(self, host: str = DEFAULT_HOST, port: int = DEFAULT_PORT):
        self.host = host
        self.port = port
        self._reader: Optional[asyncio.StreamReader] = None
        self._writer: Optional[asyncio.StreamWriter] = None
        # Connection management gate (connect/disconnect).
        self._lock = asyncio.Lock()
        # Serializes frame WRITES only - reads are handled by the reader task,
        # so many requests can be in flight at once (protocol 5 multiplexing).
        self._write_lock = asyncio.Lock()
        self._reader_task: Optional[asyncio.Task] = None
        # request_id -> future, plus a send-ordered FIFO for legacy servers
        # that do not echo request_id (they respond strictly in order).
        self._pending: dict[str, asyncio.Future] = {}
        self._fifo: deque[asyncio.Future] = deque()
        # Negotiated server capabilities (set by the "hello" handshake).
        self._server_multiplex = False
        self._server_binary = False
        self._server_features: set[str] = set()
        # The most recent mutating request id - used by cancel_operation.
        self.last_mutating_request_id: Optional[str] = None

    # ── Connection lifecycle ─────────────────────────────────────────

    async def connect(self) -> None:
        async with self._lock:
            if self._writer is not None:
                return
            try:
                self._reader, self._writer = await asyncio.wait_for(
                    asyncio.open_connection(self.host, self.port), timeout=CONNECT_TIMEOUT
                )
                sock = self._writer.get_extra_info("socket")
                if sock is not None:
                    try:
                        import socket as _socket
                        sock.setsockopt(_socket.IPPROTO_TCP, _socket.TCP_NODELAY, 1)
                    except Exception:
                        pass

                token = _read_auth_token()
                if token:
                    await self._send({"type": "auth", "token": token})
                    try:
                        response = await asyncio.wait_for(self._recv_frame(), timeout=CONNECT_TIMEOUT)
                    except Exception as exc:
                        self._close_writer_nolock()
                        raise RhinoConnectionError(f"Auth handshake failed: {exc}") from exc
                    if response.get("status") == "ok":
                        logger.info("Authenticated to AIBridge")
                        feats = response.get("features") or []
                        if feats:
                            self._apply_features(feats)
                    elif response.get("error_code") == "AUTH_REQUIRED":
                        self._close_writer_nolock()
                        raise RhinoConnectionError(
                            "AIBridge rejected the auth token. Restart AIBridge in Rhino "
                            "to regenerate it, then retry."
                        )
                    else:
                        logger.info("AIBridge did not require authentication (older plugin?)")

                # Start the multiplexing reader BEFORE any pipelined traffic.
                self._reader_task = asyncio.ensure_future(self._reader_loop())

                # Feature handshake (best effort - legacy plugins answer with an
                # "Unknown command" error, which simply leaves us in legacy mode).
                try:
                    hello = await self._roundtrip(
                        {"type": "hello", "protocol": 5, "features": list(CLIENT_FEATURES)},
                        timeout=HELLO_TIMEOUT, sent_flag=[False],
                    )
                    if hello.get("status") == "ok" and hello.get("features"):
                        self._apply_features(hello["features"])
                except Exception:
                    self._server_multiplex = False
                    self._server_binary = False

                logger.info(
                    "Connected to Rhino at %s:%d (%s)", self.host, self.port,
                    "protocol 5" if self._server_multiplex else "legacy mode",
                )
            except (OSError, asyncio.TimeoutError) as exc:
                raise RhinoConnectionError(
                    f"Cannot connect to Rhino at {self.host}:{self.port}. "
                    f"Make sure Rhino is running and AIBridge is started. Error: {exc}"
                ) from exc

    def _apply_features(self, feats: list[str]) -> None:
        self._server_features = set(feats)
        self._server_multiplex = "multiplex" in self._server_features
        self._server_binary = "binary_image" in self._server_features

    def _close_writer_nolock(self) -> None:
        w = self._writer
        self._writer = None
        self._reader = None
        if w is not None:
            try:
                w.close()
            except Exception:
                pass

    async def disconnect(self) -> None:
        async with self._lock:
            task = self._reader_task
            self._reader_task = None
            if task is not None:
                task.cancel()
            self._close_writer_nolock()
            self._fail_all(RhinoConnectionError("Connection closed."))
            self._server_multiplex = False
            self._server_binary = False

    async def _ensure_connected(self) -> None:
        if self._writer is None:
            await self.connect()

    # ── Framing ──────────────────────────────────────────────────────

    async def _send(self, payload: dict[str, Any], sent_flag: list | None = None) -> None:
        body = orjson.dumps(payload)
        assert self._writer is not None
        self._writer.write(struct.pack(">I", len(body)) + body)
        # After write() the frame may already be on the wire - anything past this
        # point must be treated as "possibly delivered" for retry-safety decisions.
        if sent_flag is not None:
            sent_flag[0] = True
        await self._writer.drain()

    async def _recv_frame(self, first_byte_timeout: float | None = None) -> dict[str, Any]:
        assert self._reader is not None
        if first_byte_timeout is None:
            flag_byte = await self._reader.readexactly(1)
        else:
            flag_byte = await asyncio.wait_for(self._reader.readexactly(1), timeout=first_byte_timeout)
        flag = flag_byte[0]
        length_bytes = await asyncio.wait_for(
            self._reader.readexactly(HEADER_SIZE), timeout=FRAME_BODY_TIMEOUT
        )
        (length,) = struct.unpack(">I", length_bytes)
        if length <= 0 or length > MAX_FRAME:
            raise RhinoConnectionError(f"Invalid frame length: {length}")
        data = await asyncio.wait_for(
            self._reader.readexactly(length), timeout=max(FRAME_BODY_TIMEOUT, length / 1_000_000)
        )
        if flag == 0x01:
            data = gzip.decompress(data)
        if flag == 0x02:
            # Binary image frame: [4B header len][JSON header][raw image bytes]
            if len(data) < 4:
                raise RhinoConnectionError("Malformed binary frame")
            (hlen,) = struct.unpack(">I", data[:4])
            if hlen < 0 or hlen + 4 > len(data):
                raise RhinoConnectionError("Malformed binary frame header")
            header = orjson.loads(data[4:4 + hlen])
            if isinstance(header, dict):
                header["_image_raw"] = data[4 + hlen:]
            return header
        return orjson.loads(data)

    # ── Multiplexing reader ──────────────────────────────────────────

    async def _reader_loop(self) -> None:
        try:
            while True:
                raw = await self._recv_frame()  # no idle timeout - quiet is normal
                rid = raw.get("request_id") if isinstance(raw, dict) else None
                fut: asyncio.Future | None = None
                if rid is not None:
                    fut = self._pending.pop(rid, None)
                    if fut is not None:
                        try:
                            self._fifo.remove(fut)
                        except ValueError:
                            pass
                if fut is None and rid is None and self._fifo:
                    # Legacy server: strict in-order responses.
                    fut = self._fifo.popleft()
                    for k, v in list(self._pending.items()):
                        if v is fut:
                            self._pending.pop(k, None)
                            break
                if fut is None or fut.done():
                    # Either a response for a request that timed out client-side
                    # (frame consumed to preserve legacy FIFO alignment) or an
                    # unsolicited frame - drop it.
                    continue
                fut.set_result(raw)
        except asyncio.CancelledError:
            pass
        except Exception as exc:
            self._abort_connection(exc)

    def _abort_connection(self, exc: Exception) -> None:
        """Reader-side teardown (no async lock - we may be inside the reader task)."""
        self._reader_task = None
        self._close_writer_nolock()
        self._fail_all(RhinoConnectionError(f"Connection lost: {exc}"))

    def _fail_all(self, exc: Exception) -> None:
        pending = list(self._pending.values()) + list(self._fifo)
        self._pending.clear()
        self._fifo.clear()
        seen = set()
        for fut in pending:
            if id(fut) in seen:
                continue
            seen.add(id(fut))
            if not fut.done():
                fut.set_exception(exc)

    async def _roundtrip(self, payload: dict[str, Any], timeout: float, sent_flag: list) -> dict[str, Any]:
        rid = payload.get("request_id") or uuid.uuid4().hex
        payload["request_id"] = rid
        loop = asyncio.get_running_loop()
        fut: asyncio.Future = loop.create_future()
        try:
            async with self._write_lock:
                # Register inside the write lock so FIFO order == wire order
                # (legacy servers answer strictly in send order).
                self._pending[rid] = fut
                self._fifo.append(fut)
                await self._send(payload, sent_flag)
            return await asyncio.wait_for(fut, timeout)
        finally:
            # Drop the rid mapping; the FIFO entry stays until the reader consumes
            # the matching frame (keeps legacy alignment even after a timeout).
            self._pending.pop(rid, None)

    # ── Public API ───────────────────────────────────────────────────

    async def send_command(
        self, command_type: str, params: dict[str, Any] | None = None,
        *, timeout: float = READ_TIMEOUT,
    ) -> RhinoResponse:
        payload: dict[str, Any] = {"type": command_type, "request_id": uuid.uuid4().hex}
        if params:
            payload["params"] = params

        retry_safe_always = command_type in self._IDEMPOTENT_COMMANDS
        if not retry_safe_always:
            self.last_mutating_request_id = payload["request_id"]

        last_exc: Exception | None = None
        for attempt in range(MAX_RETRIES + 1):
            sent = [False]
            try:
                await self._ensure_connected()
                raw = await self._roundtrip(payload, timeout, sent)
                return self._parse_response(raw)
            except asyncio.TimeoutError as exc:
                if self._server_multiplex:
                    # Connection is healthy - only this command is slow. Leave the
                    # socket alone (other commands may be in flight). Re-issuing
                    # the same tool call is safe: the plugin replays/joins by id.
                    raise RhinoConnectionError(
                        f"Timed out waiting for '{command_type}' after {timeout:.0f}s. "
                        "It may still be running in Rhino. Re-running this tool is safe "
                        "(idempotent request replay) - or use cancel_operation."
                    ) from exc
                # Legacy: a timed-out frame would desync FIFO matching - reset.
                await self.disconnect()
                last_exc = exc
            except (OSError, asyncio.IncompleteReadError, RhinoConnectionError) as exc:
                await self.disconnect()
                last_exc = exc
                if sent[0] and not retry_safe_always and not self._server_multiplex:
                    # Legacy plugin: no idempotency support, so a delivered mutating
                    # command must not be blindly re-sent.
                    raise RhinoConnectionError(
                        f"Connection lost AFTER '{command_type}' was sent; it may have "
                        "already executed in Rhino. Not retrying automatically - verify "
                        "with query_scene or get_scene_diff before re-issuing it."
                    ) from exc
            if attempt == MAX_RETRIES:
                raise RhinoConnectionError(
                    f"Lost connection after {MAX_RETRIES + 1} attempts: {last_exc}"
                ) from last_exc
            await asyncio.sleep(0.5)
        raise RhinoConnectionError("Unexpected retry exhaustion")

    async def send_batch(
        self,
        commands: list[dict[str, Any]],
        *,
        atomic: bool = False,
        stop_on_error: Optional[bool] = None,
    ) -> RhinoResponse:
        """Send a batch with atomic rollback / stop_on_error semantics."""
        if len(commands) == 1 and not atomic:
            r = await self.send_command(commands[0]["type"], commands[0].get("params"))
            return RhinoResponse(
                status=r.status,
                result={"results": [{"status": r.status, **r.result}], "count": 1, "atomic": False},
                message=r.message,
                scene_version=r.scene_version,
            )

        payload: dict[str, Any] = {
            "type": "batch", "commands": commands, "atomic": atomic,
            "request_id": uuid.uuid4().hex,
        }
        if stop_on_error is not None:
            payload["stop_on_error"] = stop_on_error
        self.last_mutating_request_id = payload["request_id"]

        last_exc: Exception | None = None
        for attempt in range(MAX_RETRIES + 1):
            sent = [False]
            try:
                await self._ensure_connected()
                raw = await self._roundtrip(payload, READ_TIMEOUT, sent)
                return self._parse_response(raw)
            except asyncio.TimeoutError as exc:
                if self._server_multiplex:
                    raise RhinoConnectionError(
                        "Timed out waiting for the batch. It may still be running in "
                        "Rhino. Re-running it is safe (idempotent request replay) - "
                        "or use cancel_operation."
                    ) from exc
                await self.disconnect()
                last_exc = exc
            except (OSError, asyncio.IncompleteReadError, RhinoConnectionError) as exc:
                await self.disconnect()
                last_exc = exc
                if sent[0] and not self._server_multiplex:
                    raise RhinoConnectionError(
                        "Connection lost AFTER the batch was sent; it may have already "
                        "executed in Rhino. Verify with query_scene/get_scene_diff "
                        "before re-issuing."
                    ) from exc
            if attempt == MAX_RETRIES:
                raise RhinoConnectionError(f"Lost connection during batch: {last_exc}") from last_exc
            await asyncio.sleep(0.5)
        raise RhinoConnectionError("Unexpected retry exhaustion")

    async def ping(self) -> dict[str, Any]:
        """Liveness + capability probe.

        Protocol 5: answered from the plugin's TCP thread even while a long
        command runs - never blocks, never tears down the shared connection.
        Legacy plugins answer in order; if a long command is in flight the ping
        times out and we report 'alive but busy' WITHOUT touching the socket
        (the abandoned response frame is safely consumed by FIFO alignment).
        """
        try:
            await asyncio.wait_for(self._ensure_connected(), timeout=CONNECT_TIMEOUT + 1)
        except Exception as exc:
            raise RhinoConnectionError(f"Ping failed: {exc}") from exc

        try:
            return await self._roundtrip({"type": "ping"}, PING_TOTAL_TIMEOUT, [False])
        except asyncio.TimeoutError:
            if not self._server_multiplex and (self._pending or self._fifo):
                return {
                    "status": "ok",
                    "busy": True,
                    "message": "AIBridge connection is busy with an in-flight command; "
                               "the bridge is alive. Retry ping after the command finishes.",
                }
            await self.disconnect()
            raise RhinoConnectionError("Ping failed: timeout")
        except Exception as exc:
            await self.disconnect()
            raise RhinoConnectionError(f"Ping failed: {exc}") from exc

    async def cancel(self, request_id: str) -> dict[str, Any]:
        """Ask the plugin to cancel a running operation (protocol 5)."""
        if not request_id:
            return {"status": "error", "message": "No request_id to cancel."}
        if not self._server_multiplex:
            return {
                "status": "error",
                "error_code": "NOT_SUPPORTED",
                "message": "The connected AIBridge plugin predates protocol 5 and "
                           "does not support cancellation. Update the .rhp plugin.",
            }
        try:
            return await self._roundtrip(
                {"type": "cancel", "params": {"request_id": request_id}},
                timeout=HELLO_TIMEOUT, sent_flag=[False],
            )
        except Exception as exc:
            return {"status": "error", "message": f"Cancel failed: {exc}"}

    @property
    def server_features(self) -> list[str]:
        return sorted(self._server_features)

    @staticmethod
    def _parse_response(raw: dict[str, Any]) -> RhinoResponse:
        # Plugin returns flat dicts: {"status":"ok","object_ids":[...]}.
        # Treat all non-control keys as the result payload.
        if "result" in raw:
            result = raw["result"]
        else:
            result = {k: v for k, v in raw.items()
                      if k not in ("status", "message", "warnings", "scene_version", "request_id")}
        scene_version = raw.get("scene_version")
        if scene_version is None and isinstance(result, dict):
            scene_version = result.get("scene_version")
        return RhinoResponse(
            status=raw.get("status", "error"),
            result=result if isinstance(result, dict) else {},
            message=raw.get("message", ""),
            warnings=raw.get("warnings", []),
            scene_version=scene_version,
        )
