using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace Clunity
{
    public static class ClunityInfo
    {
        public const string Version = "0.1.0";
    }

    /// <summary>
    /// Tiny HTTP server inside the Unity Editor. The Clunity MCP server (Server~/index.js)
    /// forwards Claude's tool calls here. Everything runs on the Unity main thread.
    /// </summary>
    [InitializeOnLoad]
    public static class ClunityBridge
    {
        public const int DefaultPort = 8765;
        const string PortPref = "Clunity.Port";
        const string EnabledPref = "Clunity.Enabled";
        const int MainThreadTimeoutMs = 60000;

        static HttpListener listener;
        static Thread thread;
        static volatile bool running;
        static readonly ConcurrentQueue<Action> mainQueue = new ConcurrentQueue<Action>();

        public static int Port { get; private set; }
        public static bool IsRunning { get { return running; } }
        public static string LastError { get; private set; }

        static ClunityBridge()
        {
            ClunityLog.Install();
            EditorApplication.update += Pump;
            AssemblyReloadEvents.beforeAssemblyReload += Stop;
            EditorApplication.quitting += Stop;
            if (IsEnabled()) Start();
        }

        // ---------- settings ----------

        public static bool IsEnabled() { return EditorPrefs.GetBool(EnabledPref, true); }

        public static int ConfiguredPort() { return EditorPrefs.GetInt(PortPref, DefaultPort); }

        public static void SetEnabled(bool enabled)
        {
            EditorPrefs.SetBool(EnabledPref, enabled);
            if (enabled) Start(); else Stop();
        }

        public static void SetPort(int port)
        {
            EditorPrefs.SetInt(PortPref, port);
            if (running)
            {
                Stop();
                Start();
            }
        }

        // ---------- lifecycle ----------

        public static void Start()
        {
            if (running) return;
            Port = ConfiguredPort();
            try
            {
                listener = new HttpListener();
                listener.Prefixes.Add("http://127.0.0.1:" + Port + "/");
                listener.Start();
                running = true;
                LastError = null;
                thread = new Thread(Loop) { IsBackground = true, Name = "Clunity Bridge" };
                thread.Start();
            }
            catch (Exception e)
            {
                running = false;
                LastError = e.Message;
                Debug.LogWarning("[Clunity] Could not start the bridge on port " + Port + ": " + e.Message);
            }
        }

        public static void Stop()
        {
            running = false;
            try
            {
                if (listener != null) listener.Close();
            }
            catch (Exception)
            {
                // Already closed.
            }
            listener = null;
        }

        static void Loop()
        {
            HttpListener l = listener;
            while (running && l != null && l.IsListening)
            {
                HttpListenerContext ctx;
                try
                {
                    ctx = l.GetContext();
                }
                catch (Exception)
                {
                    break; // listener was closed
                }
                ThreadPool.QueueUserWorkItem(delegate { Handle(ctx); });
            }
        }

        static void Pump()
        {
            int n = 0;
            Action action;
            while (n++ < 16 && mainQueue.TryDequeue(out action))
            {
                try { action(); }
                catch (Exception e) { Debug.LogException(e); }
            }
        }

        // ---------- request handling (runs on a worker thread) ----------

        static void Handle(HttpListenerContext ctx)
        {
            try
            {
                HttpListenerRequest req = ctx.Request;

                // Block browsers: a random web page must never be able to drive the editor.
                string host = req.Headers["Host"] ?? "";
                bool hostOk = host.StartsWith("127.0.0.1") || host.StartsWith("localhost");
                if (!hostOk || !string.IsNullOrEmpty(req.Headers["Origin"]))
                {
                    Respond(ctx, 403, ClunityTools.Fail("Forbidden"));
                    return;
                }

                string path = req.Url.AbsolutePath;

                if (req.HttpMethod == "GET" && path == "/ping")
                {
                    Respond(ctx, 200, new JObject
                    {
                        ["ok"] = true,
                        ["name"] = "Clunity",
                        ["version"] = ClunityInfo.Version
                    });
                    return;
                }

                if (req.HttpMethod == "POST" && path == "/call")
                {
                    string contentType = req.ContentType ?? "";
                    if (!contentType.StartsWith("application/json"))
                    {
                        Respond(ctx, 415, ClunityTools.Fail("Content-Type must be application/json"));
                        return;
                    }

                    string body;
                    using (var reader = new StreamReader(req.InputStream, Encoding.UTF8))
                    {
                        body = reader.ReadToEnd();
                    }

                    JObject payload;
                    try
                    {
                        payload = JObject.Parse(body);
                    }
                    catch (Exception e)
                    {
                        Respond(ctx, 400, ClunityTools.Fail("Invalid JSON: " + e.Message));
                        return;
                    }

                    JToken toolToken = payload["tool"];
                    string tool = toolToken == null ? null : (string)toolToken;
                    JObject args = payload["args"] as JObject ?? new JObject();
                    if (string.IsNullOrEmpty(tool))
                    {
                        Respond(ctx, 400, ClunityTools.Fail("Missing 'tool'"));
                        return;
                    }

                    JObject result = RunOnMainThread(delegate { return ClunityTools.Execute(tool, args); });
                    Respond(ctx, 200, result);
                    return;
                }

                Respond(ctx, 404, ClunityTools.Fail("Not found"));
            }
            catch (Exception e)
            {
                try { Respond(ctx, 500, ClunityTools.Fail(e.Message)); }
                catch (Exception) { /* client went away */ }
            }
        }

        static JObject RunOnMainThread(Func<JObject> fn)
        {
            JObject result = null;
            Exception error = null;
            var done = new ManualResetEventSlim(false);

            mainQueue.Enqueue(delegate
            {
                try { result = fn(); }
                catch (Exception e) { error = e; }
                finally { done.Set(); }
            });

            if (!done.Wait(MainThreadTimeoutMs))
            {
                return ClunityTools.Fail(
                    "Timed out waiting for the Unity main thread. The editor may be busy, compiling, or showing a modal dialog.");
            }
            if (error != null) return ClunityTools.Fail(error.GetType().Name + ": " + error.Message);
            return result ?? ClunityTools.Fail("Tool returned nothing");
        }

        static void Respond(HttpListenerContext ctx, int status, JObject body)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(body.ToString(Formatting.None));
            ctx.Response.StatusCode = status;
            ctx.Response.ContentType = "application/json";
            ctx.Response.ContentLength64 = bytes.Length;
            ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
            ctx.Response.OutputStream.Close();
        }
    }
}
