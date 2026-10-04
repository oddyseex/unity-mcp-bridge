"""HTTP client for the Unity MCP Bridge (Unity Editor C# side).

The bridge runs an HttpListener inside the Unity Editor
(default http://127.0.0.1:7878/command). Drop unity/McpBridge.cs into
Assets/Editor/ and start it via Tools > MCP Bridge > Start Server
(it auto-starts on editor load).

Wire protocol: POST /command with
    {"id": str, "command": str, "params": dict}
and the bridge answers
    {"id": str, "ok": bool, "result": any, "error": str | null}
"""

from __future__ import annotations

import json
import os
import urllib.error
import urllib.request
import uuid

UNITY_HOST = os.environ.get("UNITY_BRIDGE_HOST", "127.0.0.1")
UNITY_PORT = int(os.environ.get("UNITY_BRIDGE_PORT", "7878"))
DEFAULT_TIMEOUT = 120.0


class UnityBridgeError(Exception):
    """Raised when Unity is unreachable or the bridge reports an error."""


class UnityBridge:
    """Thin client over the Unity MCP Bridge HTTP API (stdlib only)."""

    def __init__(self, host: str = UNITY_HOST, port: int = UNITY_PORT,
                 timeout: float = DEFAULT_TIMEOUT):
        self.host, self.port = host, port
        self.timeout = timeout
        self.url = f"http://{host}:{port}/command"

    def send(self, command: str, params: dict | None = None):
        """Send one command; return its ``result`` or raise UnityBridgeError."""
        payload = json.dumps({
            "id": uuid.uuid4().hex,
            "command": command,
            "params": params or {},
        }).encode("utf-8")
        req = urllib.request.Request(self.url, data=payload, method="POST")
        req.add_header("Content-Type", "application/json")
        req.add_header("User-Agent", "unity-mcp-bridge")
        try:
            with urllib.request.urlopen(req, timeout=self.timeout) as resp:
                raw = resp.read()
        except urllib.error.HTTPError as exc:
            raise UnityBridgeError(
                f"Unity bridge HTTP {exc.code}: {exc.reason}") from exc
        except urllib.error.URLError as exc:
            raise UnityBridgeError(
                f"cannot reach Unity bridge at {self.host}:{self.port} "
                f"(is the Unity Editor open with McpBridge.cs in Assets/Editor/ "
                f"and the server started via Tools > MCP Bridge? {exc.reason})"
            ) from exc
        try:
            data = json.loads(raw.decode("utf-8"))
        except (ValueError, UnicodeDecodeError) as exc:
            raise UnityBridgeError(f"invalid JSON from Unity: {exc}") from exc
        if not data.get("ok", False):
            raise UnityBridgeError(data.get("error") or "Unity reported an unknown error")
        return data.get("result")

    # -- convenience wrappers (1:1 with bridge commands) -----------------

    def ping(self):
        return self.send("ping")

    def execute_batch(self, commands: list[dict]):
        return self.send("execute_batch", {"commands": commands})

    def list_objects(self):
        return self.send("list_objects")

    def get_object_info(self, path: str):
        return self.send("get_object_info", {"path": path})

    def create_primitive(self, primitive_type: str = "Cube", name: str | None = None,
                         position=None, rotation=None, scale=None):
        params: dict = {"type": primitive_type}
        if name: params["name"] = name
        if position is not None: params["position"] = list(position)
        if rotation is not None: params["rotation"] = list(rotation)
        if scale is not None: params["scale"] = list(scale)
        return self.send("create_primitive", params)

    def transform_object(self, path: str, position=None, rotation=None, scale=None):
        params: dict = {"path": path}
        if position is not None: params["position"] = list(position)
        if rotation is not None: params["rotation"] = list(rotation)
        if scale is not None: params["scale"] = list(scale)
        return self.send("transform_object", params)

    def delete_object(self, path: str):
        return self.send("delete_object", {"path": path})

    def get_property(self, path: str, component: str = "Transform", property: str = ""):
        return self.send("get_property",
                         {"path": path, "component": component, "property": property})

    def set_property(self, path: str, component: str, property: str, value):
        return self.send("set_property",
                         {"path": path, "component": component,
                          "property": property, "value": value})

    def take_screenshot(self):
        return self.send("take_screenshot")

    def set_play_mode(self, playing: bool):
        return self.send("set_play_mode", {"playing": bool(playing)})

    def save_scene(self):
        return self.send("save_scene")
