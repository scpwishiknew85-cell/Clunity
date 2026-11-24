using System.IO;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEngine;

namespace Clunity
{
    /// <summary>Window > Clunity: bridge status, port, and ready-to-copy Claude config.</summary>
    public class ClunityWindow : EditorWindow
    {
        int port;

        [MenuItem("Window/Clunity")]
        public static void Open()
        {
            GetWindow<ClunityWindow>("Clunity");
        }

        void OnEnable()
        {
            port = ClunityBridge.ConfiguredPort();
        }

        void OnInspectorUpdate()
        {
            Repaint();
        }

        static string ServerScriptPath()
        {
            PackageInfo info = PackageInfo.FindForAssembly(typeof(ClunityBridge).Assembly);
            if (info == null) return null;
            return Path.Combine(info.resolvedPath, "Server~", "index.js").Replace('\\', '/');
        }

        void OnGUI()
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Clunity " + ClunityInfo.Version, EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Lets Claude control this editor over MCP.", EditorStyles.miniLabel);
            EditorGUILayout.Space();

            bool enabled = ClunityBridge.IsEnabled();
            bool newEnabled = EditorGUILayout.Toggle("Bridge enabled", enabled);
            if (newEnabled != enabled) ClunityBridge.SetEnabled(newEnabled);

            string status;
            if (ClunityBridge.IsRunning) status = "Running on http://127.0.0.1:" + ClunityBridge.Port;
            else if (!string.IsNullOrEmpty(ClunityBridge.LastError)) status = "Not running: " + ClunityBridge.LastError;
            else status = "Stopped";
            EditorGUILayout.LabelField("Status", status);

            EditorGUILayout.BeginHorizontal();
            port = EditorGUILayout.IntField("Port", port);
            if (GUILayout.Button("Apply", GUILayout.Width(60)))
            {
                port = Mathf.Clamp(port, 1024, 65535);
                ClunityBridge.SetPort(port);
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Connect Claude", EditorStyles.boldLabel);

            string script = ServerScriptPath();
            if (script == null)
            {
                EditorGUILayout.HelpBox("Could not locate the Clunity package folder.", MessageType.Warning);
                return;
            }

            EditorGUILayout.SelectableLabel(script, EditorStyles.textField, GUILayout.Height(18));

            if (GUILayout.Button("Copy Claude Code command"))
            {
                EditorGUIUtility.systemCopyBuffer = "claude mcp add clunity -e CLUNITY_PORT=" + ClunityBridge.ConfiguredPort() +
                                                    " -- node \"" + script + "\"";
            }
            if (GUILayout.Button("Copy Claude Desktop config (JSON)"))
            {
                EditorGUIUtility.systemCopyBuffer =
                    "{\n  \"mcpServers\": {\n    \"clunity\": {\n      \"command\": \"node\",\n      \"args\": [\"" + script +
                    "\"],\n      \"env\": { \"CLUNITY_PORT\": \"" + ClunityBridge.ConfiguredPort() + "\" }\n    }\n  }\n}";
            }
            EditorGUILayout.HelpBox("Requires Node.js 18 or newer. See the README for details.", MessageType.Info);
        }
    }
}
