"""Verify the Unity MCP server: tool registration + bridge call shapes.

srv.bridge is replaced with a recording mock; no Unity needed.

Run with a venv that has the mcp SDK:
    python tests/test_mcp_server.py
"""

import asyncio
import base64
import sys

sys.path.insert(0, "/home/hatch/workspace/unity-mcp-bridge/server")

import unity_mcp_server as srv  # noqa: E402


EXPECTED_TOOLS = sorted([
    "ping_unity",
    "list_objects",
    "get_object_info",
    "create_primitive",
    "transform_object",
    "delete_object",
    "get_property",
    "set_property",
    "take_screenshot",
    "set_play_mode",
    "execute_batch",
])


class MockBridge:
    def __init__(self):
        self.calls = []

    def __getattr__(self, name):
        def rec(*args, **kwargs):
            self.calls.append((name, args, kwargs))
            if name == "take_screenshot":
                return {"mime": "image/png", "width": 8, "height": 8,
                        "image_base64": base64.b64encode(b"fakepng").decode()}
            return {"mock": name}
        return rec


def call_tool(fn, *args, **kwargs):
    target = getattr(fn, "fn", fn)
    return target(*args, **kwargs)


def run_tests():
    try:
        tools = asyncio.run(srv.mcp.list_tools())
        names = sorted(t.name for t in tools)
    except AttributeError:
        names = sorted(srv.mcp._tool_manager._tools.keys())
    assert names == EXPECTED_TOOLS, f"\n got: {names}\n want: {EXPECTED_TOOLS}"
    print(f"PASS: {len(names)} tools registered")

    mock = MockBridge()
    srv.bridge = mock

    # create_primitive forwards to bridge with validated type
    call_tool(srv.create_primitive, primitive_type="Sphere",
              position=[1, 2, 3], rotation=[0, 90, 0])
    name, args, kwargs = mock.calls[-1]
    assert name == "create_primitive", name
    assert args[0] == "Sphere" and args[2] == [1, 2, 3] and args[3] == [0, 90, 0], args
    print("PASS: create_primitive forwards validated type")

    # unknown primitive rejected before any bridge call
    mock.calls.clear()
    try:
        call_tool(srv.create_primitive, primitive_type="Teapot")
    except ValueError as e:
        assert "unknown primitive" in str(e) and mock.calls == []
        print("PASS: create_primitive rejects bad type")
    else:
        raise AssertionError("expected ValueError")

    # transform_object requires at least one transform
    try:
        call_tool(srv.transform_object, path="A")
    except ValueError:
        print("PASS: transform_object rejects empty call")
    else:
        raise AssertionError("expected ValueError")

    # get_property requires a property name
    try:
        call_tool(srv.get_property, path="A", component="Light")
    except ValueError:
        print("PASS: get_property requires property")
    else:
        raise AssertionError("expected ValueError")

    # take_screenshot returns an image (or raw dict fallback)
    mock.calls.clear()
    img = call_tool(srv.take_screenshot)
    assert mock.calls[-1][0] == "take_screenshot"
    print(f"PASS: take_screenshot -> {type(img).__name__}")

    # set_play_mode forwards bool
    call_tool(srv.set_play_mode, playing=True)
    assert mock.calls[-1] == ("set_play_mode", (True,), {}), mock.calls[-1]
    print("PASS: set_play_mode forwards bool")

    # execute_batch validation
    for bad in ([], "nope"):
        try:
            call_tool(srv.execute_batch, commands=bad)
        except ValueError:
            pass
        else:
            raise AssertionError(f"expected ValueError for {bad!r}")
    print("PASS: execute_batch validates input")

    print("\nAll MCP server tests passed.")


if __name__ == "__main__":
    run_tests()
