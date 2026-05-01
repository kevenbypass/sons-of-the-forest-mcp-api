import json
import os
import subprocess
import sys
import threading
import time
import uuid
from contextlib import asynccontextmanager
from io import TextIOWrapper
from typing import Any

if sys.platform != "win32":
    raise SystemExit("unityexplorer-mcp only runs on Windows")

import msvcrt

msvcrt.setmode(sys.stdin.fileno(), os.O_BINARY)
msvcrt.setmode(sys.stdout.fileno(), os.O_BINARY)

import anyio
import anyio.lowlevel
import mcp.server.fastmcp.server as fastmcp_server
import mcp.server.stdio as mcp_stdio
import mcp.types as types
from mcp.server.fastmcp import FastMCP
from mcp.shared.message import SessionMessage

SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
HELPER_PATH = os.path.join(SCRIPT_DIR, "ue_pipe_helper.ps1")
GAME_DIR = r"C:\Program Files (x86)\Steam\steamapps\common\Sons Of The Forest"
GAME_EXE_NAME = "SonsOfTheForest.exe"
BRIDGE_PIPE_NAME = "unityexplorer_bridge"
BRIDGE_PIPE_PATH = rf"\\.\pipe\{BRIDGE_PIPE_NAME}"
DEFAULT_TIMEOUT_MS = 5000
REDLOADER_LOG = os.path.join(GAME_DIR, "_Redloader", "Latest.log")
UNITYEXPLORER_MANIFEST = os.path.join(GAME_DIR, "Mods", "UnityExplorer", "manifest.json")
BRIDGE_MANIFEST = os.path.join(GAME_DIR, "Mods", "UnityExplorerBridge", "manifest.json")
TRACE_PATH = os.path.join(
    os.environ.get("TEMP", os.environ.get("TMP", SCRIPT_DIR)),
    "unityexplorer_mcp_status_trace.log",
)

_mcp_stdout = sys.stdout
sys.stdout = sys.stderr
_pipe_lock = threading.Lock()
_pipe_file: Any | None = None
_bridge_failure_count = 0
_bridge_circuit_open_until = 0.0


@asynccontextmanager
async def _patched_stdio_server(
    stdin: "anyio.AsyncFile[str] | None" = None,
    stdout: "anyio.AsyncFile[str] | None" = None,
):
    if not stdin:
        stdin = anyio.wrap_file(TextIOWrapper(sys.stdin.buffer, encoding="utf-8", newline="\n"))
    if not stdout:
        stdout = anyio.wrap_file(TextIOWrapper(_mcp_stdout.buffer, encoding="utf-8", newline="\n"))

    read_stream_writer, read_stream = anyio.create_memory_object_stream(0)
    write_stream, write_stream_reader = anyio.create_memory_object_stream(0)

    async def stdin_reader():
        try:
            async with read_stream_writer:
                async for line in stdin:
                    try:
                        message = types.JSONRPCMessage.model_validate_json(line)
                    except Exception as exc:
                        await read_stream_writer.send(exc)
                        continue
                    await read_stream_writer.send(SessionMessage(message))
        except anyio.ClosedResourceError:
            await anyio.lowlevel.checkpoint()

    async def stdout_writer():
        try:
            async with write_stream_reader:
                async for session_message in write_stream_reader:
                    payload = session_message.message.model_dump_json(by_alias=True, exclude_none=True)
                    await stdout.write(payload + "\n")
                    await stdout.flush()
        except anyio.ClosedResourceError:
            await anyio.lowlevel.checkpoint()

    async with anyio.create_task_group() as tg:
        tg.start_soon(stdin_reader)
        tg.start_soon(stdout_writer)
        yield read_stream, write_stream


mcp_stdio.stdio_server = _patched_stdio_server
fastmcp_server.stdio_server = _patched_stdio_server


def debug_log(message: str) -> None:
    print(f"[unityexplorer-mcp] {message}", file=sys.stderr, flush=True)


def _mark_bridge_success() -> None:
    global _bridge_failure_count, _bridge_circuit_open_until
    _bridge_failure_count = 0
    _bridge_circuit_open_until = 0.0


def _mark_bridge_failure() -> None:
    global _bridge_failure_count, _bridge_circuit_open_until
    _bridge_failure_count += 1
    if _bridge_failure_count >= 4:
        _bridge_circuit_open_until = time.time() + 3.0


def _assert_circuit_closed() -> None:
    if _bridge_circuit_open_until > time.time():
        remaining = round(_bridge_circuit_open_until - time.time(), 2)
        raise ConnectionError(f"Bridge circuit temporarily open after repeated failures. Retry in {remaining}s.")


def _tasklist_contains(process_name: str) -> bool:
    completed = subprocess.run(
        ["tasklist", "/FI", f"IMAGENAME eq {process_name}", "/FO", "CSV", "/NH"],
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="replace",
        timeout=5,
    )
    stdout = completed.stdout.strip()
    stderr = completed.stderr.strip()

    if completed.returncode == 0 and stdout and "No tasks are running" not in stdout:
        return process_name.lower() in stdout.lower()

    # Some environments return access denied for tasklist even when the game is open.
    powershell_completed = subprocess.run(
        [
            "powershell",
            "-NoProfile",
            "-Command",
            (
                "$p = Get-Process -Name "
                f"'{os.path.splitext(process_name)[0]}' "
                "-ErrorAction SilentlyContinue | Select-Object -First 1 -ExpandProperty ProcessName; "
                "if ($p) { $p }"
            ),
        ],
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="replace",
        timeout=5,
    )
    fallback_stdout = powershell_completed.stdout.strip()
    if powershell_completed.returncode == 0 and fallback_stdout:
        return process_name.lower().startswith(fallback_stdout.lower())

    if stderr:
        debug_log(f"Process probe failed for {process_name}: {stderr}")

    return False


def _read_log_tail(path: str, max_lines: int = 80) -> list[str]:
    if not os.path.exists(path):
        return []
    with open(path, "r", encoding="utf-8", errors="replace") as handle:
        lines = handle.readlines()
    return [line.rstrip("\r\n") for line in lines[-max_lines:]]


def _safe_status() -> dict[str, Any]:
    log_tail = _read_log_tail(REDLOADER_LOG, max_lines=120)
    try:
        with open(TRACE_PATH, "a", encoding="utf-8", errors="replace") as handle:
            handle.write(f"{time.strftime('%Y-%m-%d %H:%M:%S')} safe_status\n")
    except OSError:
        pass

    return {
        "safe": True,
        "note": "Este status nao toca no UnityExplorer antigo; ele so olha processo, arquivos e o bridge novo.",
        "gameRunning": _tasklist_contains(GAME_EXE_NAME),
        "unityExplorerInstalled": os.path.exists(UNITYEXPLORER_MANIFEST),
        "bridgeInstalled": os.path.exists(BRIDGE_MANIFEST),
        "redloaderLogFound": os.path.exists(REDLOADER_LOG),
        "latestLogTail": log_tail[-10:],
    }


def _close_pipe_file() -> None:
    global _pipe_file
    if _pipe_file is not None:
        try:
            _pipe_file.close()
        except Exception:
            pass
        _pipe_file = None


def _pipe_request_via_helper(command: str, args: dict[str, Any] | None = None, timeout_ms: int = DEFAULT_TIMEOUT_MS) -> dict[str, Any]:
    _assert_circuit_closed()
    payload = {
        "id": str(uuid.uuid4()),
        "command": command,
        "args": args or {},
        "timeoutMs": timeout_ms,
    }

    helper_cmd = [
        "powershell",
        "-NoProfile",
        "-ExecutionPolicy",
        "Bypass",
        "-File",
        HELPER_PATH,
        "-PipeName",
        BRIDGE_PIPE_NAME,
        "-TimeoutMs",
        str(timeout_ms),
    ]
    process_timeout_s = max(8, int(timeout_ms / 1000) + 12)

    for attempt in range(2):
        completed = subprocess.run(
            helper_cmd,
            input=json.dumps(payload, ensure_ascii=False),
            capture_output=True,
            text=True,
            encoding="utf-8",
            errors="replace",
            timeout=process_timeout_s,
        )

        output = completed.stdout.strip()
        if not output:
            stderr = completed.stderr.strip()
            raise ConnectionError(f"Bridge helper returned no output. stderr={stderr}")

        response = json.loads(output.splitlines()[-1])
        if completed.returncode == 0:
            _mark_bridge_success()
            return response

        details = str(response.get("details", "")).lower()
        if "acesso ao caminho foi negado" in details or "access is denied" in details:
            raise PermissionError(
                "Bridge pipe access denied. Run MCP host with the same privilege/integrity level as the game process."
            )

        transient = (
            "tempo limite do semaforo expirou" in details
            or "semaphore timeout period has expired" in details
            or "all pipe instances are busy" in details
            or "pipe is being closed" in details
        )
        if attempt == 0 and transient:
            time.sleep(0.2)
            continue

        _mark_bridge_failure()
        raise ConnectionError(json.dumps(response, ensure_ascii=False))

    _mark_bridge_failure()
    raise ConnectionError("Bridge helper failed after retries.")


def _pipe_request(command: str, args: dict[str, Any] | None = None, timeout_ms: int = DEFAULT_TIMEOUT_MS) -> dict[str, Any]:
    _assert_circuit_closed()
    payload = {
        "id": str(uuid.uuid4()),
        "command": command,
        "args": args or {},
        "timeoutMs": timeout_ms,
    }
    encoded = (json.dumps(payload, ensure_ascii=False) + "\n").encode("utf-8")

    # Fast path: keep a direct named pipe handle open across calls.
    with _pipe_lock:
        global _pipe_file
        try:
            if _pipe_file is None:
                _pipe_file = open(BRIDGE_PIPE_PATH, "r+b", buffering=0)

            _pipe_file.write(encoded)
            line = _pipe_file.readline()
            if not line:
                raise BrokenPipeError("Bridge pipe returned empty response.")

            response = json.loads(line.decode("utf-8", errors="replace").strip())
            _mark_bridge_success()
            return response
        except PermissionError:
            _close_pipe_file()
            _mark_bridge_failure()
            raise PermissionError(
                "Bridge pipe access denied. Run MCP host with the same privilege/integrity level as the game process."
            )
        except Exception:
            # If direct pipe fails, reset it and fallback to helper path.
            _close_pipe_file()
            _mark_bridge_failure()

    return _pipe_request_via_helper(command, args=args, timeout_ms=timeout_ms)


def _bridge_call(command: str, args: dict[str, Any] | None = None, timeout_ms: int = DEFAULT_TIMEOUT_MS) -> dict[str, Any]:
    response = _pipe_request(command, args=args, timeout_ms=timeout_ms)
    if not response.get("ok"):
        raise RuntimeError(json.dumps(response, ensure_ascii=False))
    return response["data"]


def _optional_bridge_status(timeout_ms: int) -> dict[str, Any]:
    try:
        bridge_data = _bridge_call("status", timeout_ms=timeout_ms)
        return {
            "bridgeReachable": True,
            "bridge": bridge_data,
        }
    except Exception as exc:
        return {
            "bridgeReachable": False,
            "bridgeError": str(exc),
        }


mcp = FastMCP("unityexplorer")


@mcp.tool()
def status(timeout_ms: int = 1200) -> dict[str, Any]:
    """Passive health check plus an optional probe to the safe RedLoader bridge."""
    result = _safe_status()
    result.update(_optional_bridge_status(timeout_ms=timeout_ms))
    return result


@mcp.tool()
def ping(timeout_ms: int = 3000) -> dict[str, Any]:
    """Ping the in-game RedLoader bridge."""
    return _bridge_call("ping", timeout_ms=timeout_ms)


@mcp.tool()
def list_scenes(timeout_ms: int = DEFAULT_TIMEOUT_MS) -> dict[str, Any]:
    """List loaded Unity scenes from the running game."""
    return _bridge_call("list_scenes", timeout_ms=timeout_ms)


@mcp.tool()
def network_status(timeout_ms: int = DEFAULT_TIMEOUT_MS) -> dict[str, Any]:
    """Get current multiplayer/Bolt state from the running game."""
    return _bridge_call("network_status", timeout_ms=timeout_ms)


@mcp.tool()
def host_client_state(timeout_ms: int = DEFAULT_TIMEOUT_MS) -> dict[str, Any]:
    """Summarize whether the current session is host, server, client, or offline."""
    return _bridge_call("host_client_state", timeout_ms=timeout_ms)


@mcp.tool()
def current_lobby_info(timeout_ms: int = DEFAULT_TIMEOUT_MS) -> dict[str, Any]:
    """Return lobby and Steam-related static state snapshots when available."""
    return _bridge_call("current_lobby_info", timeout_ms=timeout_ms)


@mcp.tool()
def show_message(message: str, duration: float = 4.0, timeout_ms: int = DEFAULT_TIMEOUT_MS) -> dict[str, Any]:
    """Show an in-game toast message."""
    return _bridge_call(
        "show_message",
        args={"message": message, "duration": duration},
        timeout_ms=timeout_ms,
    )


@mcp.tool()
def run_game_command(command: str, argument: str = "", timeout_ms: int = 15000) -> dict[str, Any]:
    """Execute a registered SonsSdk game command (for example: finishblueprints, noforest, clearpickups)."""
    return _bridge_call(
        "run_game_command",
        args={"command": command, "argument": argument},
        timeout_ms=timeout_ms,
    )


@mcp.tool()
def get_type_layout(
    type_name: str,
    include_non_public: bool = True,
    include_properties: bool = True,
    timeout_ms: int = 15000,
) -> dict[str, Any]:
    """Inspect a loaded IL2CPP/managed type and return fields/properties plus native field pointers and offsets when available."""
    return _bridge_call(
        "get_type_layout",
        args={
            "typeName": type_name,
            "includeNonPublic": include_non_public,
            "includeProperties": include_properties,
        },
        timeout_ms=timeout_ms,
    )


@mcp.tool()
def get_field_offset(
    type_name: str,
    field_name: str,
    timeout_ms: int = 15000,
) -> dict[str, Any]:
    """Return native field pointer and IL2CPP offset for a specific field when available."""
    return _bridge_call(
        "get_field_offset",
        args={"typeName": type_name, "fieldName": field_name},
        timeout_ms=timeout_ms,
    )


@mcp.tool()
def send_chat_message(message: str, color: str = "#ffffff", timeout_ms: int = DEFAULT_TIMEOUT_MS) -> dict[str, Any]:
    """Broadcast a chat message via SonsSdk networking helpers."""
    return _bridge_call(
        "send_chat_message",
        args={"message": message, "color": color},
        timeout_ms=timeout_ms,
    )


@mcp.tool()
def execute_many(
    commands: list[dict[str, Any]],
    timeout_ms: int = 20000,
) -> dict[str, Any]:
    """Execute multiple bridge commands in one roundtrip. Each item supports {'command': str, 'args': {...}}."""
    if not commands:
        return {"count": 0, "results": []}
    if len(commands) > 64:
        raise ValueError("execute_many supports up to 64 commands per request.")
    return _bridge_call(
        "execute_many",
        args={"commands": commands},
        timeout_ms=timeout_ms,
    )


@mcp.tool()
def find_gameobjects(
    name: str | None = None,
    contains: str | None = None,
    include_inactive: bool = True,
    limit: int = 25,
    timeout_ms: int = DEFAULT_TIMEOUT_MS,
) -> dict[str, Any]:
    """Find GameObjects by exact name or substring match."""
    return _bridge_call(
        "find_gameobjects",
        args={
            "name": name,
            "contains": contains,
            "includeInactive": include_inactive,
            "limit": limit,
        },
        timeout_ms=timeout_ms,
    )


@mcp.tool()
def list_players(
    include_inactive: bool = True,
    limit: int = 25,
    max_objects: int = 8000,
    timeout_ms: int = 20000,
) -> dict[str, Any]:
    """Find likely player objects using name/component heuristics."""
    return _bridge_call(
        "list_players",
        args={"includeInactive": include_inactive, "limit": limit, "maxObjects": max_objects},
        timeout_ms=timeout_ms,
    )


@mcp.tool()
def players_scan_start(
    include_inactive: bool = True,
    max_objects: int = 8000,
    timeout_ms: int = 30000,
) -> dict[str, Any]:
    """Start paged player scan session. Use players_scan_next() to pull pages without freezing the game thread."""
    return _bridge_call(
        "players_scan_start",
        args={"includeInactive": include_inactive, "maxObjects": max_objects},
        timeout_ms=timeout_ms,
    )


@mcp.tool()
def players_scan_next(
    scan_id: str,
    batch_size: int = 25,
    timeout_ms: int = 20000,
) -> dict[str, Any]:
    """Fetch next page from a players scan session."""
    return _bridge_call(
        "players_scan_next",
        args={"scanId": scan_id, "batchSize": batch_size},
        timeout_ms=timeout_ms,
    )


@mcp.tool()
def players_scan_stop(
    scan_id: str,
    timeout_ms: int = 10000,
) -> dict[str, Any]:
    """Stop and cleanup a players scan session."""
    return _bridge_call(
        "players_scan_stop",
        args={"scanId": scan_id},
        timeout_ms=timeout_ms,
    )


@mcp.tool()
def get_local_player(
    include_components: bool = False,
    include_children: bool = False,
    child_depth: int = 1,
    child_limit: int = 50,
    max_objects: int = 8000,
    timeout_ms: int = 20000,
) -> dict[str, Any]:
    """Return the best LocalPlayer match instead of a broad player heuristic list."""
    return _bridge_call(
        "get_local_player",
        args={
            "includeComponents": include_components,
            "includeChildren": include_children,
            "childDepth": child_depth,
            "childLimit": child_limit,
            "maxObjects": max_objects,
        },
        timeout_ms=timeout_ms,
    )


@mcp.tool()
def get_local_player_items(
    category: str = "all",
    include_inactive: bool = True,
    limit: int = 50,
    max_objects: int = 8000,
    timeout_ms: int = 20000,
) -> dict[str, Any]:
    """List LocalPlayer-related held items, inventory roots, or equipment."""
    return _bridge_call(
        "get_local_player_items",
        args={
            "category": category,
            "includeInactive": include_inactive,
            "limit": limit,
            "maxObjects": max_objects,
        },
        timeout_ms=timeout_ms,
    )


@mcp.tool()
def list_bolt_entities(
    include_inactive: bool = True,
    limit: int = 50,
    max_objects: int = 8000,
    timeout_ms: int = DEFAULT_TIMEOUT_MS,
) -> dict[str, Any]:
    """List BoltEntity-like components currently loaded in the game."""
    return _bridge_call(
        "list_bolt_entities",
        args={"includeInactive": include_inactive, "limit": limit, "maxObjects": max_objects},
        timeout_ms=timeout_ms,
    )


@mcp.tool()
def get_gameobject(
    instance_id: int,
    include_components: bool = True,
    include_children: bool = False,
    child_depth: int = 1,
    child_limit: int = 50,
    timeout_ms: int = DEFAULT_TIMEOUT_MS,
) -> dict[str, Any]:
    """Get a full snapshot for a GameObject instance id."""
    return _bridge_call(
        "get_gameobject",
        args={
            "instanceId": instance_id,
            "includeComponents": include_components,
            "includeChildren": include_children,
            "childDepth": child_depth,
            "childLimit": child_limit,
        },
        timeout_ms=timeout_ms,
    )


@mcp.tool()
def inspect_gameobject(
    instance_id: int | None = None,
    object_name: str | None = None,
    contains: str | None = None,
    show_explorer: bool = False,
    include_inactive: bool = True,
    timeout_ms: int = DEFAULT_TIMEOUT_MS,
) -> dict[str, Any]:
    """Inspect a GameObject and optionally focus UnityExplorer on it."""
    return _bridge_call(
        "inspect_gameobject",
        args={
            "instanceId": instance_id,
            "name": object_name,
            "contains": contains,
            "showExplorer": show_explorer,
            "includeInactive": include_inactive,
        },
        timeout_ms=timeout_ms,
    )


@mcp.tool()
def list_components(instance_id: int, timeout_ms: int = DEFAULT_TIMEOUT_MS) -> dict[str, Any]:
    """List components attached to a GameObject."""
    return _bridge_call(
        "list_components",
        args={"instanceId": instance_id},
        timeout_ms=timeout_ms,
    )


@mcp.tool()
def list_children(
    instance_id: int,
    recursive: bool = False,
    max_depth: int = 1,
    limit: int = 100,
    contains: str | None = None,
    include_inactive: bool = True,
    timeout_ms: int = 20000,
) -> dict[str, Any]:
    """List direct children or descendants of a GameObject."""
    return _bridge_call(
        "list_children",
        args={
            "instanceId": instance_id,
            "recursive": recursive,
            "maxDepth": max_depth,
            "limit": limit,
            "contains": contains,
            "includeInactive": include_inactive,
        },
        timeout_ms=timeout_ms,
    )


@mcp.tool()
def get_component_members(
    instance_id: int,
    component_type: str,
    include_non_public: bool = False,
    timeout_ms: int = DEFAULT_TIMEOUT_MS,
) -> dict[str, Any]:
    """Read component fields and properties. Set include_non_public=true for protected/private members."""
    return _bridge_call(
        "get_component_members",
        args={
            "instanceId": instance_id,
            "componentType": component_type,
            "includeNonPublic": include_non_public,
        },
        timeout_ms=timeout_ms,
    )


@mcp.tool()
def set_component_member(
    instance_id: int,
    component_type: str,
    member_name: str,
    value: Any,
    timeout_ms: int = DEFAULT_TIMEOUT_MS,
) -> dict[str, Any]:
    """Set a writable component field or property."""
    return _bridge_call(
        "set_component_member",
        args={
            "instanceId": instance_id,
            "componentType": component_type,
            "memberName": member_name,
            "value": value,
        },
        timeout_ms=timeout_ms,
    )


if __name__ == "__main__":
    debug_log("Starting UnityExplorer FastMCP server")
    mcp.run()
