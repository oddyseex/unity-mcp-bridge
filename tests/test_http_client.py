"""Tests for unity_http_client against a mock Unity bridge.

A local http.server emulates the C# HttpListener side (POST /command with
the {"id","command","params"} envelope), so framing, parsing, and error
mapping are exercised for real. No Unity needed.

Run:  python3 tests/test_http_client.py
"""

import json
import sys
import threading
from http.server import BaseHTTPRequestHandler, HTTPServer

sys.path.insert(0, "/home/hatch/workspace/unity-mcp-bridge/server")

from unity_http_client import UnityBridge, UnityBridgeError  # noqa: E402

MOCK_PORT = 17878
SEEN = []


class MockBridge(BaseHTTPRequestHandler):
    def do_POST(self):
        length = int(self.headers.get("Content-Length", 0))
        req = json.loads(self.rfile.read(length) or b"{}")
        SEEN.append(req)
        cmd = req.get("command")
        if cmd == "boom":
            body = {"id": req.get("id"), "ok": False, "result": None,
                    "error": "InvalidOperationException: editor says no"}
        elif cmd == "ping":
            body = {"id": req.get("id"), "ok": True,
                    "result": {"pong": True, "unity": "6000.0.0f1"}, "error": None}
        else:
            body = {"id": req.get("id"), "ok": True,
                    "result": {"echo_params": req.get("params")}, "error": None}
        data = json.dumps(body).encode()
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def log_message(self, *args):
        pass


def run_tests():
    server = HTTPServer(("127.0.0.1", MOCK_PORT), MockBridge)
    threading.Thread(target=server.serve_forever, daemon=True).start()
    b = UnityBridge(port=MOCK_PORT, timeout=5)

    # 1. ping round trip, id echoed
    r = b.ping()
    assert r["pong"] is True, r
    assert SEEN[-1]["id"], "request id missing"
    assert SEEN[-1]["command"] == "ping" and SEEN[-1]["params"] == {}
    print("PASS: ping round trip with id echo")

    # 2. params passed through
    r = b.create_primitive("Cube", name="TestCube", position=[1, 2, 3])
    p = SEEN[-1]["params"]
    assert p == {"type": "Cube", "name": "TestCube", "position": [1, 2, 3]}, p
    print("PASS: create_primitive param building")

    # 3. transform only sends what was passed
    b.transform_object("A/B", rotation=[0, 90, 0])
    p = SEEN[-1]["params"]
    assert p == {"path": "A/B", "rotation": [0, 90, 0]}, p
    print("PASS: transform_object selective params")

    # 4. bridge error -> UnityBridgeError with bridge message
    try:
        b.send("boom")
    except UnityBridgeError as e:
        assert "editor says no" in str(e), e
        print(f"PASS: bridge error mapped ({e})")
    else:
        raise AssertionError("expected UnityBridgeError")

    # 5. connection refused -> helpful message
    bad = UnityBridge(port=19999, timeout=2)
    try:
        bad.ping()
    except UnityBridgeError as e:
        assert "19999" in str(e) and "MCP Bridge" in str(e), e
        print("PASS: connection-refused guidance")
    else:
        raise AssertionError("expected UnityBridgeError")

    # 6. batch passthrough
    r = b.execute_batch([{"command": "ping", "params": {}}])
    assert SEEN[-1]["command"] == "execute_batch"
    print("PASS: execute_batch passthrough")

    server.shutdown()
    print("\nAll HTTP client tests passed.")


if __name__ == "__main__":
    run_tests()
