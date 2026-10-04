"""Unity MCP server.

Exposes the Unity Editor to any MCP client (Claude Desktop, Cursor, Muse,
...) as a set of tools. This process speaks MCP over stdio (or Streamable
HTTP with --http); it forwards every tool call to the Unity MCP Bridge
(C# HttpListener inside the editor, default 127.0.0.1:7878).

Setup (Unity side):
    1. Drop unity/McpBridge.cs into your project's Assets/Editor/ folder.
    2. Tools > MCP Bridge > Start Server (it auto-starts on editor load).

Prerequisites:
    pip install -r requirements.txt   (the ``mcp`` Python SDK)

Run:
    python unity_mcp_server.py [--http] [--port 8002]
                               [--unity-host 127.0.0.1] [--unity-port 7878]

Unity conventions the AI client must know (in every relevant docstring):
    - Units are METERS, Y-up (Unity's native convention).
    - Rotations are euler degrees XYZ (localEulerAngles).
    - Objects are addressed by hierarchy path, e.g. "Player/Arm/Hand".
    - Primitives: Cube, Sphere, Capsule, Cylinder, Plane, Quad.
"""

from __future__ import annotations

import base64
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from mcp.server.fastmcp import FastMCP

try:
    from mcp.server.fastmcp.utilities.types import Image as FastMCPImage
except ImportError:  # pragma: no cover - older SDKs
    FastMCPImage = None

from unity_http_client import UnityBridge, UnityBridgeError

mcp = FastMCP("unity-bridge")

# One shared bridge client; tests replace this with a mock.
bridge = UnityBridge()


# ---------------------------------------------------------------------------
# connection / introspection
# ---------------------------------------------------------------------------

@mcp.tool()
def ping_unity() -> dict:
    """Check that the Unity bridge is reachable. Returns bridge + Unity
    version info. Call this first; every other tool needs the editor open
    with the bridge server started (Tools > MCP Bridge > Start Server)."""
    try:
        result = bridge.ping()
        result["reachable"] = True
        return result
    except UnityBridgeError as exc:
        return {"reachable": False, "error": str(exc)}


@mcp.tool()
def list_objects() -> dict:
    """List every GameObject in the active scene: name, hierarchy path,
    active state, and components."""
    return bridge.list_objects()


@mcp.tool()
def get_object_info(path: str) -> dict:
    """Detailed info about one GameObject: transform (meters, euler degrees),
    tag, layer, and components. path looks like "Player/Arm/Hand"."""
    return bridge.get_object_info(path)


# ---------------------------------------------------------------------------
# scene editing
# ---------------------------------------------------------------------------

@mcp.tool()
def create_primitive(primitive_type: str = "Cube", name: str | None = None,
                     position: list[float] | None = None,
                     rotation: list[float] | None = None,
                     scale: list[float] | None = None) -> dict:
    """Create a primitive GameObject. Types: Cube, Sphere, Capsule, Cylinder,
    Plane, Quad. position in METERS [x, y, z] (Y-up); rotation euler degrees
    [x, y, z]; scale multiplier. Undoable (Ctrl+Z). Returns its path."""
    valid = {"cube", "sphere", "capsule", "cylinder", "plane", "quad"}
    if primitive_type.lower() not in valid:
        raise ValueError(f"unknown primitive {primitive_type!r}; choose from {sorted(valid)}")
    return bridge.create_primitive(primitive_type, name, position, rotation, scale)


@mcp.tool()
def transform_object(path: str,
                     position: list[float] | None = None,
                     rotation: list[float] | None = None,
                     scale: list[float] | None = None) -> dict:
    """Move / rotate / scale a GameObject (local transform). Only what you
    pass is changed. Undoable. Returns the new transform."""
    if position is None and rotation is None and scale is None:
        raise ValueError("pass position and/or rotation and/or scale")
    return bridge.transform_object(path, position, rotation, scale)


@mcp.tool()
def delete_object(path: str) -> dict:
    """Delete a GameObject from the scene. Undoable. Returns the deleted path."""
    return bridge.delete_object(path)


# ---------------------------------------------------------------------------
# properties
# ---------------------------------------------------------------------------

@mcp.tool()
def get_property(path: str, component: str = "Transform", property: str = ""):
    """Read a field/property on a component. component examples: "Transform",
    "Light", "Rigidbody", "GameObject". Returns component, property, value."""
    if not property:
        raise ValueError("property is required")
    return bridge.get_property(path, component, property)


@mcp.tool()
def set_property(path: str, component: str, property: str, value) -> dict:
    """Write a field/property on a component (reflection). Supports float,
    int, bool, string, Vector2/3 ({x,y,z}), Color ({r,g,b,a}), and enums.
    Undoable. Returns the written value."""
    return bridge.set_property(path, component, property, value)


# ---------------------------------------------------------------------------
# editor utilities
# ---------------------------------------------------------------------------

@mcp.tool()
def take_screenshot():
    """Capture the Game view as a PNG image. Use it to *see* the scene and
    verify edits did what you intended."""
    result = bridge.take_screenshot()
    if FastMCPImage is not None:
        return FastMCPImage(data=base64.b64decode(result["image_base64"]),
                            format="png")
    return result  # fallback: raw base64 dict


@mcp.tool()
def set_play_mode(playing: bool) -> dict:
    """Enter (true) or exit (false) Play mode in the editor."""
    return bridge.set_play_mode(playing)


@mcp.tool()
def execute_batch(commands: list[dict]) -> dict:
    """Execute multiple bridge commands in ONE round trip to Unity.

    Each item is {"command": str, "params": dict} using the bridge commands
    (ping, list_objects, get_object_info, create_primitive, transform_object,
    delete_object, get_property, set_property, take_screenshot, set_play_mode,
    save_scene). Items run sequentially on Unity's main thread; one item's
    failure is captured per-item and does not abort the batch.
    """
    if not isinstance(commands, list) or not commands:
        raise ValueError("commands must be a non-empty list")
    if len(commands) > 100:
        raise ValueError("batch limited to 100 commands per request")
    return bridge.execute_batch(commands)


if __name__ == "__main__":
    import argparse

    parser = argparse.ArgumentParser(
        description="Unity MCP Bridge server (MCP -> HTTP 127.0.0.1:7878 -> Unity Editor)"
    )
    parser.add_argument("--http", action="store_true",
                        help="serve over Streamable HTTP instead of stdio")
    parser.add_argument("--host", default="127.0.0.1",
                        help="bind address for --http (default: 127.0.0.1)")
    parser.add_argument("--port", type=int, default=8002,
                        help="port for --http (default: 8002)")
    parser.add_argument("--unity-host", default=None,
                        help="Unity bridge host (default: 127.0.0.1 or UNITY_BRIDGE_HOST)")
    parser.add_argument("--unity-port", type=int, default=None,
                        help="Unity bridge port (default: 7878 or UNITY_BRIDGE_PORT)")
    args = parser.parse_args()

    if args.unity_host or args.unity_port:
        from unity_http_client import UNITY_HOST, UNITY_PORT
        bridge = UnityBridge(host=args.unity_host or UNITY_HOST,
                             port=args.unity_port or UNITY_PORT)

    if args.http:
        mcp.settings.host = args.host
        mcp.settings.port = args.port
        print(f"[unity-mcp] serving Streamable HTTP on {args.host}:{args.port}/mcp")
        mcp.run(transport="streamable-http")
    else:
        mcp.run()  # stdio: what Claude Desktop / Cursor / Muse CLI expect
