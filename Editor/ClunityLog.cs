using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace Clunity
{
    /// <summary>Keeps a rolling buffer of Unity console messages so Claude can read them.</summary>
    public static class ClunityLog
    {
        const int MaxEntries = 500;
        static readonly List<JObject> entries = new List<JObject>();
        static readonly object gate = new object();
        static bool installed;

        public static void Install()
        {
            if (installed) return;
            installed = true;
            Application.logMessageReceivedThreaded += OnLog;
        }

        static void OnLog(string condition, string stackTrace, LogType type)
        {
            string level = type == LogType.Warning ? "Warning" : (type == LogType.Log ? "Log" : "Error");
            var entry = new JObject
            {
                ["time"] = DateTime.Now.ToString("HH:mm:ss.fff"),
                ["type"] = level,
                ["message"] = condition
            };
            if (level == "Error" && !string.IsNullOrEmpty(stackTrace))
            {
                entry["stackTrace"] = TrimStack(stackTrace);
            }
            lock (gate)
            {
                entries.Add(entry);
                if (entries.Count > MaxEntries) entries.RemoveRange(0, entries.Count - MaxEntries);
            }
        }

        static string TrimStack(string stack)
        {
            string[] lines = stack.Split('\n');
            int n = Math.Min(lines.Length, 6);
            return string.Join("\n", lines, 0, n).TrimEnd();
        }

        /// <summary>Newest <paramref name="max"/> entries (oldest first), optionally filtered by level.</summary>
        public static JArray Get(int max, ICollection<string> levels)
        {
            var result = new JArray();
            lock (gate)
            {
                for (int i = entries.Count - 1; i >= 0 && result.Count < max; i--)
                {
                    JObject e = entries[i];
                    if (levels == null || levels.Count == 0 || levels.Contains((string)e["type"]))
                    {
                        result.Insert(0, e.DeepClone());
                    }
                }
            }
            return result;
        }

        public static void Clear()
        {
            lock (gate) { entries.Clear(); }
            try
            {
                Type t = typeof(Editor).Assembly.GetType("UnityEditor.LogEntries");
                if (t != null)
                {
                    var m = t.GetMethod("Clear");
                    if (m != null) m.Invoke(null, null);
                }
            }
            catch (Exception)
            {
                // Clearing the visible console window is best-effort only.
            }
        }
    }
}
