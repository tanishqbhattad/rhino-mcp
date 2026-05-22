# RhinoAIBridge — TCP Protocol Layer
# by tanishqb | https://github.com/tanishqb/rhino-ai-bridge

"""TCP protocol for the Rhino AI Bridge plugin.

Phase 1 changes vs v3:
- orjson instead of stdlib json (~3x faster, no GIL release on encode)
- single-shot connect: no asyncio.Lock release between framing + payload writes
- ping support, both as a fast-path command and as a connection liveness check
- the global connection accessor stays (keeps API stable for Phase 4 pool migration)

Tier 1 wire protocol (server → client):
  [1 byte: flags] [4 bytes: big-endian payload length] [N bytes: payload]
  flag 0x00 = raw UTF-8 JSON
  flag 0x01 = gzip-compressed UTF-8 JSON  (kicks in at > 10 KB responses)

Client → server direction stays at the old 4-byte format (requests are always small).
Compression yields 5-8x on large object lists, ~2x on base64 image payloads.
"""
from __future__ import annotations

import asyncio
import gzip
import logging
import struct
from dataclasses import dataclass, field
from typing import Any, Optional

import orjson

logger = logging.getLogger("rhino_ai_bridge.protocol")

DEFAULT_HOST = "127.0.0.1"
DEFAULT_PORT = 9544
CONNECT_TIMEOUT = 5.0
READ_TIMEOUT = 190.0      # Phase 7: must exceed the longest C# per-command timeout (180s for batch/scripts)
PING_TIMEOUT = 1.0
MAX_RETRIES = 2
HEADER_SIZE = 4
MAX_FRAME = 50 * 1024 * 1024   # 50MB cap, matches server


@dataclass
class RhinoResponse:
    status: str
    result: dict[str, Any] = field(default_factory=dict)
    message: str = ""
    warnings: list[Any] = field(default_factory=list)
    # Phase 2: Etag-style scene version. Stamped by the plugin on every response.
    # Read tools see the version after pending events have flushed; mutating tools
    # see the version reflecting their own changes. None if the snapshot wasn't ready.
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


async def get_connection(host: str = DEFAULT_HOST, port: int = DEFAULT_PORT) -> "RhinoProtocol":
    global _connection
    if _connection is None:
        _connection = RhinoProtocol(host, port)
    await _connection._ensure_connected()
    return _connection


class RhinoProtocol:
    def __init__(self, host: str = DEFAULT_HOST, port: int = DEFAULT_PORT):
        self.host = host
        self.port = port
        self._reader: Optional[asyncio.StreamReader] = None
        self._writer: Optional[asyncio.StreamWriter] = None
        # NOTE: this lock single-flights all commands. Phase 4 replaces it with request IDs +
        # a futures table so calls multiplex over the wire. For Phase 1 we keep the contract.
        self._lock = asyncio.Lock()

    async def connect(self) -> None:
        async with self._lock:
            if self._writer is not None:
                return
            try:
                self._reader, self._writer = await asyncio.wait_for(
                    asyncio.open_connection(self.host, self.port), timeout=CONNECT_TIMEOUT
                )
                # Disable Nagle — we send small framed messages and want them out the door now.
                sock = self._writer.get_extra_info("socket")
                if sock is not None:
                    try:
                        import socket as _socket
                        sock.setsockopt(_socket.IPPROTO_TCP, _socket.TCP_NODELAY, 1)
                    except Exception:
                        pass
                logger.info("Connected to Rhino at %s:%d", self.host, self.port)
            except (OSError, asyncio.TimeoutError) as exc:
                raise RhinoConnectionError(
                    f"Cannot connect to Rhino at {self.host}:{self.port}. "
                    f"Make sure Rhino is running and AIBridge is started. Error: {exc}"
                ) from exc

    async def disconnect(self) -> None:
        async with self._lock:
            if self._writer is not None:
                try:
                    self._writer.close()
                    await self._writer.wait_closed()
                except Exception:
                    pass
                self._writer = None
                self._reader = None

    async def _ensure_connected(self) -> None:
        if self._writer is None:
            await self.connect()

    async def _send(self, payload: dict[str, Any]) -> None:
        # orjson.dumps is C, releases no GIL but ~3x faster than json.dumps.
        # Single bytes() concatenation = single writer.write = single TCP frame in NODELAY mode.
        body = orjson.dumps(payload)
        assert self._writer is not None
        self._writer.write(struct.pack(">I", len(body)) + body)
        await self._writer.drain()

    async def _recv(self) -> dict[str, Any]:
        assert self._reader is not None
        # Tier 1 protocol: [1-byte flags][4-byte length][payload]
        # flag 0x00 = raw JSON, 0x01 = gzip-compressed JSON
        flag_byte = await asyncio.wait_for(
            self._reader.readexactly(1), timeout=READ_TIMEOUT
        )
        flag = flag_byte[0]
        length_bytes = await asyncio.wait_for(
            self._reader.readexactly(HEADER_SIZE), timeout=READ_TIMEOUT
        )
        (length,) = struct.unpack(">I", length_bytes)
        if length <= 0 or length > MAX_FRAME:
            raise RhinoConnectionError(f"Invalid frame length: {length}")
        data = await asyncio.wait_for(
            self._reader.readexactly(length), timeout=READ_TIMEOUT
        )
        if flag == 0x01:
            data = gzip.decompress(data)
        # orjson.loads ~2-3x faster than json.loads, especially on large floor-stack responses.
        return orjson.loads(data)

    async def send_command(
        self, command_type: str, params: dict[str, Any] | None = None
    ) -> RhinoResponse:
        payload: dict[str, Any] = {"type": command_type}
        if params:
            payload["params"] = params

        for attempt in range(MAX_RETRIES + 1):
            try:
                await self._ensure_connected()
                async with self._lock:
                    await self._send(payload)
                    raw = await self._recv()
                return self._parse_response(raw)
            except (OSError, asyncio.TimeoutError, asyncio.IncompleteReadError) as exc:
                logger.warning("Connection error attempt %d: %s", attempt + 1, exc)
                await self.disconnect()
                if attempt == MAX_RETRIES:
                    raise RhinoConnectionError(
                        f"Lost connection after {MAX_RETRIES + 1} attempts."
                    ) from exc
                await asyncio.sleep(0.5)
        raise RhinoConnectionError("Unexpected retry exhaustion")

    async def send_batch(
        self,
        commands: list[dict[str, Any]],
        *,
        atomic: bool = False,
        stop_on_error: Optional[bool] = None,
    ) -> RhinoResponse:
        """Send a batch. Phase 3 — supports atomic rollback and stop_on_error.

        Returns the whole batch response as a single RhinoResponse. The result dict carries:
            results: list of per-op responses (each with index, op_index, status, ...)
            atomic: bool
            rolled_back: bool (if atomic and failed)
            failed_index, failed_indices: present on failure
        """
        if len(commands) == 1 and not atomic:
            # Fast path: single non-atomic op — use send_command and wrap.
            r = await self.send_command(commands[0]["type"], commands[0].get("params"))
            return RhinoResponse(
                status=r.status,
                result={"results": [{"status": r.status, **r.result}], "count": 1, "atomic": False},
                message=r.message,
                scene_version=r.scene_version,
            )

        payload: dict[str, Any] = {"type": "batch", "commands": commands, "atomic": atomic}
        if stop_on_error is not None:
            payload["stop_on_error"] = stop_on_error

        for attempt in range(MAX_RETRIES + 1):
            try:
                await self._ensure_connected()
                async with self._lock:
                    await self._send(payload)
                    raw = await self._recv()
                # The plugin returns the full batch envelope; parse it as a normal response.
                # Top-level scene_version is stamped by the server.
                return self._parse_response(raw)
            except (OSError, asyncio.TimeoutError, asyncio.IncompleteReadError) as exc:
                await self.disconnect()
                if attempt == MAX_RETRIES:
                    raise RhinoConnectionError("Lost connection during batch.") from exc
                await asyncio.sleep(0.5)
        raise RhinoConnectionError("Unexpected retry exhaustion")

    async def ping(self) -> dict[str, Any]:
        """Liveness + capability probe. Sub-millisecond on the server side (no UI thread hop)."""
        try:
            await self._ensure_connected()
            async with self._lock:
                await self._send({"type": "ping"})
                raw = await asyncio.wait_for(self._recv(), timeout=PING_TIMEOUT)
            return raw
        except Exception as exc:
            await self.disconnect()
            raise RhinoConnectionError(f"Ping failed: {exc}") from exc

    @staticmethod
    def _parse_response(raw: dict[str, Any]) -> RhinoResponse:
        # Plugin returns flat dicts: {"status":"ok","object_ids":[...]}.
        # Treat all non-control keys as the result payload.
        if "result" in raw:
            result = raw["result"]
        else:
            result = {k: v for k, v in raw.items() if k not in ("status", "message", "warnings", "scene_version")}
        # Pull scene_version out either from the top level (mutating tools) or the result (read tools).
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
