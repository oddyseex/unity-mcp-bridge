# Unity MCP Bridge

Drive the **Unity Editor** from any AI assistant over the **Model Context
Protocol**. A C# bridge runs an HTTP server inside the editor; an MCP server
(this repo) exposes it as tools.

```
MCP client (Claude Desktop / Cursor / Muse / ...)
   --stdio-->  server/unity_mcp_server.py
   --HTTP 127.0.0.1:7878-->  Unity Editor (McpBridge.cs)  -->  your scene
```

Part of the bridge family: [`blender-mcp-bridge`](https://github.com/oddyseex/blender-mcp-bridge)
and [`unreal-mcp-bridge`](https://github.com/oddyseex/unreal-mcp-bridge)
share the same tool names and conventions.

## What you can do with it

- `"Create a cube at (0, 1, 0)"` → `create_primitive`
- `"What's in the scene?"` → `list_objects`
- `"Set that light's intensity to 3"` → `set_property`
- `"Show me the game view"` → `take_screenshot` (the AI *sees* it)
- `"Enter play mode"` → `set_play_mode`
- Several things at once → `execute_batch` (one round trip)

All scene mutations support **Undo** (Ctrl+Z).

## Setup

### 1. Install the Unity bridge

1. Copy **`unity/McpBridge.cs`** into your Unity project's **`Assets/Editor/`**
   folder. (One file, zero dependencies, Unity 2021.3 LTS+ / Unity 6.)
2. Unity compiles it; the server **auto-starts** on editor load.
   Manual control: **Tools → MCP Bridge → Start/Stop Server**.
3. Verify: open `http://127.0.0.1:7878/command` in a browser — it should
   answer 404 "use POST /command" (that means it's listening), or POST it:
   ```bash
   curl http://127.0.0.1:7878/command -d '{"id":"1","command":"ping","params":{}}'
   ```

### 2. Install the MCP server

```bash
python -m venv .venv && source .venv/bin/activate   # Windows: .venv\Scripts\activate
pip install -r requirements.txt
```

Windows users: download the release zip and double-click **`run_server.bat`**.

### 3. Point your AI client at it

**Claude Desktop** (`claude_desktop_config.json`):

```json
{
  "mcpServers": {
    "unity": {
      "command": "/absolute/path/to/.venv/bin/python",
      "args": ["/absolute/path/to/server/unity_mcp_server.py"]
    }
  }
}
```

**Cursor**: Settings → MCP → Add Server → same command/args.

**Muse CLI**: `Muse mcp add unity -- /absolute/path/to/.venv/bin/python /absolute/path/to/server/unity_mcp_server.py`

Then ask it to run `ping_unity` — if it reports your Unity version, you're live.

### Shared-server mode (optional)

```bash
python server/unity_mcp_server.py --http --port 8002
# serves MCP at http://127.0.0.1:8002/mcp
```

If the Unity bridge runs on a different host/port: `--unity-host` /
`--unity-port` (or env `UNITY_BRIDGE_HOST` / `UNITY_BRIDGE_PORT`).

## Tools

| Tool | What it does |
|---|---|
| `ping_unity` | Health check; bridge + Unity version |
| `list_objects` | Every GameObject: name, path, active, components |
| `get_object_info` | Transform, tag, layer, components for one object |
| `create_primitive` | Cube / Sphere / Capsule / Cylinder / Plane / Quad |
| `transform_object` | Move / rotate / scale (only what you pass) |
| `delete_object` | Delete a GameObject |
| `get_property` | Read a field/property on a component (reflection) |
| `set_property` | Write one (float/int/bool/string/Vector/Color/enum) |
| `take_screenshot` | Game-view capture as an image |
| `set_play_mode` | Enter/exit Play mode |
| `execute_batch` | Multiple commands in one round trip |

## Unity conventions (the AI must know these)

- **Units are meters, Y-up** (Unity native).
- **Rotation is euler degrees XYZ** (`localEulerAngles`).
- **Objects are addressed by hierarchy path**: `"Player/Arm/Hand"`.
- Works in the **Editor** (edit mode and play mode). Not for builds.

## Troubleshooting

- **"cannot reach Unity bridge at 127.0.0.1:7878"** → editor isn't open,
  `McpBridge.cs` isn't in `Assets/Editor/`, or the server is stopped
  (Tools → MCP Bridge → Start Server).
- **Port in use** → something else holds 7878; change `Port` in McpBridge.cs
  and `--unity-port` to match.
- **Screenshot is black/empty** → the Game view must be open and rendering.
- **`GameObject.Find`-style misses** → the bridge walks the hierarchy itself,
  so inactive objects *are* found. If a path still fails, check spelling —
  paths are case-sensitive.

## Security

The bridge executes scene mutations sent over localhost. That is its job —
but only run it on machines you trust, and never forward port 7878 to a network.

## Project layout

```
unity/McpBridge.cs            # the Unity Editor bridge (HTTP server + commands)
server/unity_http_client.py    # Python HTTP client for the bridge
server/unity_mcp_server.py     # the MCP server (FastMCP tools over stdio)
tests/test_http_client.py      # client tests against a mock bridge
tests/test_mcp_server.py       # tool registration + call-shape tests
```
