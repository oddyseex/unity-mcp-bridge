// McpBridge.cs — Unity MCP Bridge (v1.0.0)
//
// Drop this file into any Unity project's Assets/Editor/ folder.
// It starts an HTTP server on 127.0.0.1:7878 inside the Unity Editor.
// An MCP server (see ../server/) POSTs JSON commands; they execute on
// Unity's main thread and JSON results come back.
//
//   POST /command  {"id": "...", "command": "...", "params": {...}}
//   ->             {"id": "...", "ok": true|false, "result": ..., "error": ...}
//
// Threading: HttpListener runs on background threads; requests are queued
// and executed by an EditorApplication.update pump (Unity API is
// main-thread-only). Single file, zero dependencies.
//
// Unity 2021.3 LTS+ / Unity 6.  Menu: Tools > MCP Bridge > Start/Stop Server.

#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Reflection;
using System.Text;
using System.Threading;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace McpBridge
{
    // ------------------------------------------------------------------
    // Minimal JSON parser/serializer (no dependencies).
    // Parses into Dictionary<string,object> / List<object> / string /
    // double / bool / null. Serializes the same shapes back.
    // ------------------------------------------------------------------
    static class MiniJson
    {
        public static object Parse(string json)
        {
            var p = new Parser(json);
            var v = p.ParseValue();
            p.SkipWhite();
            if (!p.AtEnd) throw new Exception("trailing characters after JSON value");
            return v;
        }

        public static string Stringify(object v)
        {
            var sb = new StringBuilder();
            Write(v, sb);
            return sb.ToString();
        }

        static void Write(object v, StringBuilder sb)
        {
            if (v == null) { sb.Append("null"); return; }
            if (v is string s) { WriteString(s, sb); return; }
            if (v is bool b) { sb.Append(b ? "true" : "false"); return; }
            if (v is double d)
            {
                sb.Append(d.ToString("R", CultureInfo.InvariantCulture));
                return;
            }
            if (v is float f) { sb.Append(((double)f).ToString("R", CultureInfo.InvariantCulture)); return; }
            if (v is int i) { sb.Append(i.ToString(CultureInfo.InvariantCulture)); return; }
            if (v is long l) { sb.Append(l.ToString(CultureInfo.InvariantCulture)); return; }
            if (v is IDictionary<string, object> dict)
            {
                sb.Append('{');
                bool first = true;
                foreach (var kv in dict)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    WriteString(kv.Key, sb);
                    sb.Append(':');
                    Write(kv.Value, sb);
                }
                sb.Append('}');
                return;
            }
            if (v is System.Collections.IEnumerable e)
            {
                sb.Append('[');
                bool first = true;
                foreach (var item in e)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    Write(item, sb);
                }
                sb.Append(']');
                return;
            }
            WriteString(v.ToString(), sb);
        }

        static void WriteString(string s, StringBuilder sb)
        {
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        class Parser
        {
            readonly string _s;
            int _i;
            public Parser(string s) { _s = s; }
            public bool AtEnd { get { SkipWhite(); return _i >= _s.Length; } }

            public void SkipWhite()
            {
                while (_i < _s.Length && char.IsWhiteSpace(_s[_i])) _i++;
            }

            public object ParseValue()
            {
                SkipWhite();
                if (_i >= _s.Length) throw new Exception("unexpected end of JSON");
                char c = _s[_i];
                if (c == '{') return ParseObject();
                if (c == '[') return ParseArray();
                if (c == '"') return ParseString();
                if (c == 't') { Expect("true"); return true; }
                if (c == 'f') { Expect("false"); return false; }
                if (c == 'n') { Expect("null"); return null; }
                return ParseNumber();
            }

            void Expect(string lit)
            {
                if (_s.Substring(_i, Math.Min(lit.Length, _s.Length - _i)) != lit)
                    throw new Exception("invalid literal at " + _i);
                _i += lit.Length;
            }

            Dictionary<string, object> ParseObject()
            {
                var d = new Dictionary<string, object>();
                _i++; // {
                SkipWhite();
                if (_i < _s.Length && _s[_i] == '}') { _i++; return d; }
                while (true)
                {
                    SkipWhite();
                    string key = ParseString();
                    SkipWhite();
                    if (_i >= _s.Length || _s[_i] != ':') throw new Exception("expected ':' at " + _i);
                    _i++;
                    d[key] = ParseValue();
                    SkipWhite();
                    if (_i < _s.Length && _s[_i] == ',') { _i++; continue; }
                    if (_i < _s.Length && _s[_i] == '}') { _i++; break; }
                    throw new Exception("expected ',' or '}' at " + _i);
                }
                return d;
            }

            List<object> ParseArray()
            {
                var l = new List<object>();
                _i++; // [
                SkipWhite();
                if (_i < _s.Length && _s[_i] == ']') { _i++; return l; }
                while (true)
                {
                    l.Add(ParseValue());
                    SkipWhite();
                    if (_i < _s.Length && _s[_i] == ',') { _i++; continue; }
                    if (_i < _s.Length && _s[_i] == ']') { _i++; break; }
                    throw new Exception("expected ',' or ']' at " + _i);
                }
                return l;
            }

            string ParseString()
            {
                _i++; // "
                var sb = new StringBuilder();
                while (true)
                {
                    if (_i >= _s.Length) throw new Exception("unterminated string");
                    char c = _s[_i++];
                    if (c == '"') break;
                    if (c == '\\')
                    {
                        if (_i >= _s.Length) throw new Exception("bad escape");
                        char e = _s[_i++];
                        switch (e)
                        {
                            case '"': sb.Append('"'); break;
                            case '\\': sb.Append('\\'); break;
                            case '/': sb.Append('/'); break;
                            case 'n': sb.Append('\n'); break;
                            case 'r': sb.Append('\r'); break;
                            case 't': sb.Append('\t'); break;
                            case 'u':
                                sb.Append((char)Convert.ToInt32(_s.Substring(_i, 4), 16));
                                _i += 4;
                                break;
                            default: throw new Exception("bad escape \\" + e);
                        }
                    }
                    else sb.Append(c);
                }
                return sb.ToString();
            }

            object ParseNumber()
            {
                int start = _i;
                if (_i < _s.Length && _s[_i] == '-') _i++;
                while (_i < _s.Length && (char.IsDigit(_s[_i]) || _s[_i] == '.' || _s[_i] == 'e' || _s[_i] == 'E' || _s[_i] == '+' || _s[_i] == '-')) _i++;
                string tok = _s.Substring(start, _i - start);
                double d;
                if (!double.TryParse(tok, NumberStyles.Float, CultureInfo.InvariantCulture, out d))
                    throw new Exception("bad number: " + tok);
                return d;
            }
        }
    }

    // ------------------------------------------------------------------
    // HTTP server: background threads receive, main thread executes.
    // ------------------------------------------------------------------
    [InitializeOnLoad]
    public static class McpBridgeServer
    {
        const string Host = "127.0.0.1";
        const int Port = 7878;
        const string BridgeVersion = "1.0.0";

        static HttpListener _listener;
        static Thread _acceptThread;
        static volatile bool _running;
        static readonly object _queueLock = new object();
        static readonly Queue<PendingRequest> _queue = new Queue<PendingRequest>();

        class PendingRequest
        {
            public string Id;
            public string Command;
            public Dictionary<string, object> Params;
            public readonly ManualResetEventSlim Done = new ManualResetEventSlim(false);
            public string ResponseJson;
        }

        static McpBridgeServer()
        {
            // Re-subscribe cleanly across domain reloads.
            AssemblyReloadEvents.beforeAssemblyReload -= StopServer;
            AssemblyReloadEvents.beforeAssemblyReload += StopServer;
            EditorApplication.update -= Pump;
            EditorApplication.update += Pump;
            StartServer();
        }

        [MenuItem("Tools/MCP Bridge/Start Server")]
        public static void StartServer()
        {
            if (_running) return;
            try
            {
                _listener = new HttpListener();
                _listener.Prefixes.Add("http://" + Host + ":" + Port + "/");
                _listener.Start();
            }
            catch (Exception e)
            {
                Debug.LogError("[MCP Bridge] Could not start HTTP server on " + Host + ":" + Port + ": " + e.Message);
                return;
            }
            _running = true;
            _acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "mcp-bridge-accept" };
            _acceptThread.Start();
            Debug.Log("[MCP Bridge] Listening on http://" + Host + ":" + Port + "/command");
        }

        [MenuItem("Tools/MCP Bridge/Stop Server")]
        public static void StopServer()
        {
            if (!_running && _listener == null) return;
            _running = false;
            try { if (_listener != null) { _listener.Stop(); _listener.Close(); } }
            catch { /* already stopped */ }
            _listener = null;
            lock (_queueLock) _queue.Clear();
            Debug.Log("[MCP Bridge] Stopped");
        }

        public static bool IsRunning() { return _running; }

        static void AcceptLoop()
        {
            while (_running)
            {
                HttpListenerContext ctx = null;
                try { ctx = _listener.GetContext(); }
                catch { break; } // listener stopped
                // Hand to the thread pool so one slow request never blocks accepts.
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    try { HandleContext((HttpListenerContext)_); }
                    catch (Exception e) { Debug.LogError("[MCP Bridge] Handler error: " + e.Message); }
                }, ctx);
            }
        }

        static void HandleContext(HttpListenerContext ctx)
        {
            string responseJson;
            int status = 200;
            try
            {
                if (ctx.Request.HttpMethod != "POST" || !ctx.Request.Url.AbsolutePath.StartsWith("/command"))
                {
                    status = 404;
                    responseJson = MiniJson.Stringify(Err(null, "use POST /command"));
                }
                else
                {
                    string body;
                    using (var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8))
                        body = reader.ReadToEnd();
                    var req = (Dictionary<string, object>)MiniJson.Parse(body);
                    var pending = new PendingRequest
                    {
                        Id = req.ContainsKey("id") ? req["id"] as string : null,
                        Command = req.ContainsKey("command") ? req["command"] as string : null,
                        Params = (req.ContainsKey("params") ? req["params"] as Dictionary<string, object> : null)
                                 ?? new Dictionary<string, object>(),
                    };
                    if (string.IsNullOrEmpty(pending.Command))
                        throw new Exception("missing 'command'");
                    lock (_queueLock) _queue.Enqueue(pending);
                    if (!pending.Done.Wait(TimeSpan.FromSeconds(120)))
                        responseJson = MiniJson.Stringify(Err(pending.Id, "timed out waiting for Unity main thread"));
                    else
                        responseJson = pending.ResponseJson;
                }
            }
            catch (Exception e)
            {
                status = 400;
                responseJson = MiniJson.Stringify(Err(null, "bad request: " + e.Message));
            }
            try
            {
                byte[] bytes = Encoding.UTF8.GetBytes(responseJson);
                ctx.Response.StatusCode = status;
                ctx.Response.ContentType = "application/json";
                ctx.Response.ContentLength64 = bytes.Length;
                ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
            }
            catch { /* client went away */ }
            finally { try { ctx.Response.Close(); } catch { } }
        }

        // Runs on Unity's main thread via EditorApplication.update.
        static void Pump()
        {
            while (true)
            {
                PendingRequest req = null;
                lock (_queueLock) { if (_queue.Count > 0) req = _queue.Dequeue(); }
                if (req == null) break;
                try
                {
                    object result = Dispatch(req.Command, req.Params);
                    req.ResponseJson = MiniJson.Stringify(Ok(req.Id, result));
                }
                catch (Exception e)
                {
                    // One bad command must never kill the pump.
                    req.ResponseJson = MiniJson.Stringify(Err(req.Id, e.GetType().Name + ": " + e.Message));
                }
                finally { req.Done.Set(); }
            }
        }

        static Dictionary<string, object> Ok(string id, object result)
        {
            return new Dictionary<string, object>
            {
                { "id", id }, { "ok", true }, { "result", result }, { "error", null },
            };
        }

        static Dictionary<string, object> Err(string id, string error)
        {
            return new Dictionary<string, object>
            {
                { "id", id }, { "ok", false }, { "result", null }, { "error", error },
            };
        }

        // ------------------------------------------------------------------
        // Command dispatch
        // ------------------------------------------------------------------
        static readonly Dictionary<string, Func<Dictionary<string, object>, object>> Commands =
            new Dictionary<string, Func<Dictionary<string, object>, object>>
            {
                { "ping", Ping },
                { "execute_batch", ExecuteBatch },
                { "list_objects", ListObjects },
                { "get_object_info", GetObjectInfo },
                { "create_primitive", CreatePrimitive },
                { "transform_object", TransformObject },
                { "delete_object", DeleteObject },
                { "get_property", GetProperty },
                { "set_property", SetProperty },
                { "take_screenshot", TakeScreenshot },
                { "set_play_mode", SetPlayMode },
                { "save_scene", SaveScene },
            };

        static object Dispatch(string command, Dictionary<string, object> p)
        {
            Func<Dictionary<string, object>, object> handler;
            if (!Commands.TryGetValue(command, out handler))
                throw new Exception("unknown command '" + command + "'; known: " +
                                    string.Join(", ", new List<string>(Commands.Keys).ToArray()));
            return handler(p);
        }

        // ------------------------------------------------------------------
        // Param helpers (MiniJson gives double / string / bool /
        // Dictionary<string,object> / List<object>)
        // ------------------------------------------------------------------
        static string Str(Dictionary<string, object> p, string key, string def = null)
        {
            object v;
            return p.TryGetValue(key, out v) && v is string ? (string)v : def;
        }

        static bool Bool(Dictionary<string, object> p, string key, bool def = false)
        {
            object v;
            if (!p.TryGetValue(key, out v)) return def;
            if (v is bool) return (bool)v;
            return def;
        }

        static List<object> Lst(Dictionary<string, object> p, string key)
        {
            object v;
            return p.TryGetValue(key, out v) && v is List<object> ? (List<object>)v : null;
        }

        static double[] Vec(Dictionary<string, object> p, string key)
        {
            var l = Lst(p, key);
            if (l == null || l.Count != 3) return null;
            return new[] { Convert.ToDouble(l[0]), Convert.ToDouble(l[1]), Convert.ToDouble(l[2]) };
        }

        static Dictionary<string, object> Dbl3(double x, double y, double z)
        {
            return new Dictionary<string, object> { { "x", x }, { "y", y }, { "z", z } };
        }

        // ------------------------------------------------------------------
        // Object lookup (GameObject.Find misses inactive objects, so walk
        // the hierarchy manually; path looks like "Parent/Child").
        // ------------------------------------------------------------------
        static GameObject FindByPath(string path)
        {
            if (string.IsNullOrEmpty(path))
                throw new Exception("params.path is required");
            string[] parts = path.Split('/');
            foreach (var root in SceneManager.GetActiveScene().GetRootGameObjects())
            {
                if (root.name != parts[0]) continue;
                GameObject cur = root;
                bool ok = true;
                for (int i = 1; i < parts.Length; i++)
                {
                    Transform child = cur.transform.Find(parts[i]);
                    if (child == null) { ok = false; break; }
                    cur = child.gameObject;
                }
                if (ok) return cur;
            }
            throw new Exception("object not found: '" + path + "'");
        }

        static string PathOf(GameObject go)
        {
            var parts = new List<string>();
            for (Transform t = go.transform; t != null; t = t.parent)
                parts.Add(t.name);
            parts.Reverse();
            return string.Join("/", parts.ToArray());
        }

        // ------------------------------------------------------------------
        // Commands
        // ------------------------------------------------------------------
        static object Ping(Dictionary<string, object> p)
        {
            return new Dictionary<string, object>
            {
                { "pong", true },
                { "bridge", BridgeVersion },
                { "unity", Application.unityVersion },
                { "scene", SceneManager.GetActiveScene().name },
            };
        }

        static object ExecuteBatch(Dictionary<string, object> p)
        {
            var commands = Lst(p, "commands");
            if (commands == null || commands.Count == 0)
                throw new Exception("params.commands must be a non-empty list");
            if (commands.Count > 100)
                throw new Exception("batch limited to 100 commands");
            var results = new List<object>();
            foreach (var item in commands)
            {
                var entry = item as Dictionary<string, object>;
                if (entry == null)
                {
                    results.Add(new Dictionary<string, object>
                        { { "command", null }, { "ok", false }, { "result", null }, { "error", "batch item must be an object" } });
                    continue;
                }
                string cmd = entry.ContainsKey("command") ? entry["command"] as string : null;
                var sub = entry.ContainsKey("params") ? entry["params"] as Dictionary<string, object>
                                                      : new Dictionary<string, object>();
                if (cmd == "execute_batch")
                {
                    results.Add(new Dictionary<string, object>
                        { { "command", cmd }, { "ok", false }, { "result", null }, { "error", "nested batches are not allowed" } });
                    continue;
                }
                try
                {
                    results.Add(new Dictionary<string, object>
                        { { "command", cmd }, { "ok", true }, { "result", Dispatch(cmd, sub) }, { "error", null } });
                }
                catch (Exception e)
                {
                    results.Add(new Dictionary<string, object>
                        { { "command", cmd }, { "ok", false }, { "result", null }, { "error", e.GetType().Name + ": " + e.Message } });
                }
            }
            return new Dictionary<string, object> { { "results", results }, { "count", (double)results.Count } };
        }

        static void CollectObjects(GameObject go, List<object> outList)
        {
            var comps = new List<object>();
            foreach (var c in go.GetComponents<Component>())
                comps.Add(c.GetType().Name);
            outList.Add(new Dictionary<string, object>
            {
                { "name", go.name },
                { "path", PathOf(go) },
                { "active", go.activeSelf },
                { "components", comps },
            });
            foreach (Transform child in go.transform)
                CollectObjects(child.gameObject, outList);
        }

        static object ListObjects(Dictionary<string, object> p)
        {
            var list = new List<object>();
            foreach (var root in SceneManager.GetActiveScene().GetRootGameObjects())
                CollectObjects(root, list);
            return new Dictionary<string, object>
            {
                { "scene", SceneManager.GetActiveScene().name },
                { "count", (double)list.Count },
                { "objects", list },
            };
        }

        static object GetObjectInfo(Dictionary<string, object> p)
        {
            var go = FindByPath(Str(p, "path"));
            var t = go.transform;
            var comps = new List<object>();
            foreach (var c in go.GetComponents<Component>())
                comps.Add(c.GetType().FullName);
            return new Dictionary<string, object>
            {
                { "name", go.name },
                { "path", PathOf(go) },
                { "active", go.activeSelf },
                { "tag", go.tag },
                { "layer", LayerMask.LayerToName(go.layer) },
                { "position", Dbl3(t.localPosition.x, t.localPosition.y, t.localPosition.z) },
                { "rotation", Dbl3(t.localEulerAngles.x, t.localEulerAngles.y, t.localEulerAngles.z) },
                { "scale", Dbl3(t.localScale.x, t.localScale.y, t.localScale.z) },
                { "components", comps },
            };
        }

        static object CreatePrimitive(Dictionary<string, object> p)
        {
            string typeName = Str(p, "type", "Cube");
            PrimitiveType type;
            try { type = (PrimitiveType)Enum.Parse(typeof(PrimitiveType), typeName, true); }
            catch { throw new Exception("unknown primitive '" + typeName + "'; choose Cube, Sphere, Capsule, Cylinder, Plane, Quad"); }

            var go = GameObject.CreatePrimitive(type);
            Undo.RegisterCreatedObjectUndo(go, "MCP Create " + typeName);
            string name = Str(p, "name");
            if (!string.IsNullOrEmpty(name)) go.name = name;
            ApplyTransform(go, p);
            return new Dictionary<string, object> { { "path", PathOf(go) }, { "name", go.name } };
        }

        static void ApplyTransform(GameObject go, Dictionary<string, object> p)
        {
            var t = go.transform;
            Undo.RecordObject(t, "MCP Transform");
            var pos = Vec(p, "position");
            if (pos != null) t.localPosition = new Vector3((float)pos[0], (float)pos[1], (float)pos[2]);
            var rot = Vec(p, "rotation");
            if (rot != null) t.localEulerAngles = new Vector3((float)rot[0], (float)rot[1], (float)rot[2]);
            var scl = Vec(p, "scale");
            if (scl != null) t.localScale = new Vector3((float)scl[0], (float)scl[1], (float)scl[2]);
        }

        static object TransformObject(Dictionary<string, object> p)
        {
            var go = FindByPath(Str(p, "path"));
            ApplyTransform(go, p);
            var t = go.transform;
            return new Dictionary<string, object>
            {
                { "path", PathOf(go) },
                { "position", Dbl3(t.localPosition.x, t.localPosition.y, t.localPosition.z) },
                { "rotation", Dbl3(t.localEulerAngles.x, t.localEulerAngles.y, t.localEulerAngles.z) },
                { "scale", Dbl3(t.localScale.x, t.localScale.y, t.localScale.z) },
            };
        }

        static object DeleteObject(Dictionary<string, object> p)
        {
            var go = FindByPath(Str(p, "path"));
            string path = PathOf(go);
            Undo.DestroyObjectImmediate(go);
            return new Dictionary<string, object> { { "deleted", path } };
        }

        // ------------------------------------------------------------------
        // Property access via reflection
        // ------------------------------------------------------------------
        static object ResolveTarget(GameObject go, string component)
        {
            if (string.IsNullOrEmpty(component) || component == "GameObject") return go;
            if (component == "Transform") return go.transform;
            Type type = FindComponentType(component);
            if (type == null)
                throw new Exception("component type not found: '" + component + "'");
            var comp = go.GetComponent(type);
            if (comp == null)
                throw new Exception("'" + go.name + "' has no " + component);
            return comp;
        }

        static Type FindComponentType(string name)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type t = asm.GetType(name);
                if (t != null && typeof(Component).IsAssignableFrom(t)) return t;
                t = asm.GetType("UnityEngine." + name);
                if (t != null && typeof(Component).IsAssignableFrom(t)) return t;
            }
            return null;
        }

        static MemberInfo FindMember(Type type, string name)
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            var f = type.GetField(name, flags);
            if (f != null) return f;
            var pr = type.GetProperty(name, flags);
            if (pr != null) return pr;
            return null;
        }

        static object SerializeValue(object v)
        {
            if (v == null) return null;
            if (v is Vector3 v3) return Dbl3(v3.x, v3.y, v3.z);
            if (v is Vector2 v2) return new Dictionary<string, object> { { "x", (double)v2.x }, { "y", (double)v2.y } };
            if (v is Quaternion q) return new Dictionary<string, object> { { "x", (double)q.x }, { "y", (double)q.y }, { "z", (double)q.z }, { "w", (double)q.w } };
            if (v is Color c) return new Dictionary<string, object> { { "r", (double)c.r }, { "g", (double)c.g }, { "b", (double)c.b }, { "a", (double)c.a } };
            if (v is Enum) return v.ToString();
            if (v is UnityEngine.Object o) return o.name;
            if (v is float || v is double || v is int || v is long || v is bool || v is string) return v;
            return v.ToString();
        }

        static float F(Dictionary<string, object> d, string key)
        {
            object v;
            if (!d.TryGetValue(key, out v)) throw new Exception("vector missing '" + key + "'");
            return Convert.ToSingle(v);
        }

        static object ConvertJson(object v, Type target)
        {
            if (target == typeof(float) || target == typeof(double)) return Convert.ToDouble(v);
            if (target == typeof(int) || target == typeof(long)) return Convert.ToInt32(v);
            if (target == typeof(bool)) return Convert.ToBoolean(v);
            if (target == typeof(string)) return v == null ? null : v.ToString();
            if (target == typeof(Vector3))
            {
                var d = v as Dictionary<string, object>;
                if (d == null) throw new Exception("expected {x,y,z}");
                return new Vector3(F(d, "x"), F(d, "y"), F(d, "z"));
            }
            if (target == typeof(Vector2))
            {
                var d = v as Dictionary<string, object>;
                if (d == null) throw new Exception("expected {x,y}");
                return new Vector2(F(d, "x"), F(d, "y"));
            }
            if (target == typeof(Color))
            {
                var d = v as Dictionary<string, object>;
                if (d == null) throw new Exception("expected {r,g,b[,a]}");
                object a;
                return new Color(F(d, "r"), F(d, "g"), F(d, "b"),
                                 d.TryGetValue("a", out a) ? Convert.ToSingle(a) : 1f);
            }
            if (target.IsEnum)
            {
                if (v is string) return Enum.Parse(target, (string)v, true);
                return Enum.ToObject(target, Convert.ToInt32(v));
            }
            throw new Exception("unsupported property type: " + target.Name +
                                " (supported: float, int, bool, string, Vector2/3, Color, enums)");
        }

        static object GetProperty(Dictionary<string, object> p)
        {
            var go = FindByPath(Str(p, "path"));
            var target = ResolveTarget(go, Str(p, "component", "Transform"));
            string name = Str(p, "property");
            if (string.IsNullOrEmpty(name)) throw new Exception("params.property is required");
            var member = FindMember(target.GetType(), name);
            if (member == null)
                throw new Exception("no field/property '" + name + "' on " + target.GetType().Name);
            object value = member is FieldInfo
                ? ((FieldInfo)member).GetValue(target)
                : ((PropertyInfo)member).GetValue(target, null);
            return new Dictionary<string, object>
            {
                { "component", target.GetType().Name },
                { "property", name },
                { "value", SerializeValue(value) },
            };
        }

        static object SetProperty(Dictionary<string, object> p)
        {
            var go = FindByPath(Str(p, "path"));
            var target = ResolveTarget(go, Str(p, "component", "Transform"));
            string name = Str(p, "property");
            if (string.IsNullOrEmpty(name)) throw new Exception("params.property is required");
            if (!p.ContainsKey("value")) throw new Exception("params.value is required");
            var member = FindMember(target.GetType(), name);
            if (member == null)
                throw new Exception("no field/property '" + name + "' on " + target.GetType().Name);
            Type targetType = member is FieldInfo
                ? ((FieldInfo)member).FieldType
                : ((PropertyInfo)member).PropertyType;
            object converted = ConvertJson(p["value"], targetType);
            var undoTarget = target as UnityEngine.Object;
            if (undoTarget != null) Undo.RecordObject(undoTarget, "MCP Set Property");
            if (member is FieldInfo) ((FieldInfo)member).SetValue(target, converted);
            else ((PropertyInfo)member).SetValue(target, converted, null);
            return new Dictionary<string, object>
            {
                { "component", target.GetType().Name },
                { "property", name },
                { "value", SerializeValue(converted) },
            };
        }

        // ------------------------------------------------------------------
        // Editor utilities
        // ------------------------------------------------------------------
        static object TakeScreenshot(Dictionary<string, object> p)
        {
            // Captures the Game view. Must run on the main thread (it does,
            // via the pump).
            Texture2D tex = ScreenCapture.CaptureScreenshotAsTexture();
            if (tex == null)
                throw new Exception("screenshot failed — is the Game view open?");
            try
            {
                byte[] png = tex.EncodeToPNG();
                return new Dictionary<string, object>
                {
                    { "mime", "image/png" },
                    { "width", (double)tex.width },
                    { "height", (double)tex.height },
                    { "image_base64", Convert.ToBase64String(png) },
                };
            }
            finally { UnityEngine.Object.DestroyImmediate(tex); }
        }

        static object SetPlayMode(Dictionary<string, object> p)
        {
            bool playing = Bool(p, "playing");
            EditorApplication.isPlaying = playing;
            return new Dictionary<string, object> { { "playing", playing } };
        }

        static object SaveScene(Dictionary<string, object> p)
        {
            bool saved = EditorSceneManager.SaveOpenScenes();
            return new Dictionary<string, object> { "saved", saved };
        }
    }
}
#endif
