using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Clunity
{
    /// <summary>Raised for problems the caller should see as a plain error message.</summary>
    public class ToolException : Exception
    {
        public ToolException(string message) : base(message) { }
    }

    /// <summary>All tools Claude can call. Every method here runs on the Unity main thread.</summary>
    public static class ClunityTools
    {
        // BEGIN HANDLERS (keep in sync with Server~/tools.js)
        static readonly Dictionary<string, Func<JObject, JToken>> Handlers = new Dictionary<string, Func<JObject, JToken>>
        {
            ["get_editor_state"] = GetEditorState,
            ["list_hierarchy"] = ListHierarchy,
            ["get_gameobject"] = GetGameObject,
            ["create_gameobject"] = CreateGameObject,
            ["delete_gameobject"] = DeleteGameObject,
            ["set_transform"] = SetTransform,
            ["add_component"] = AddComponent,
            ["get_component_properties"] = GetComponentProperties,
            ["set_component_property"] = SetComponentProperty,
            ["find_assets"] = FindAssets,
            ["read_file"] = ReadFile,
            ["write_file"] = WriteFile,
            ["get_console"] = GetConsole,
            ["clear_console"] = ClearConsole,
            ["set_play_mode"] = SetPlayMode,
            ["open_scene"] = OpenScene,
            ["save_scene"] = SaveScene,
            ["execute_menu_item"] = ExecuteMenuItem,
            ["get_selection"] = GetSelection,
            ["select_object"] = SelectObject,
            ["instantiate_prefab"] = InstantiatePrefab,
            ["refresh_assets"] = RefreshAssets,
        };
        // END HANDLERS

        static readonly HashSet<string> WritableExtensions = new HashSet<string>
        {
            ".cs", ".shader", ".cginc", ".hlsl", ".compute", ".json", ".txt", ".md",
            ".asmdef", ".asmref", ".uss", ".uxml", ".xml", ".csv", ".yaml", ".yml"
        };

        const int MaxReadBytes = 400 * 1024;

        // ---------- entry points ----------

        public static JObject Fail(string message)
        {
            return new JObject { ["ok"] = false, ["error"] = message };
        }

        public static JObject Execute(string tool, JObject args)
        {
            Func<JObject, JToken> handler;
            if (!Handlers.TryGetValue(tool, out handler)) return Fail("Unknown tool: " + tool);
            try
            {
                JToken result = handler(args);
                if (result == null) result = JValue.CreateNull();
                return new JObject { ["ok"] = true, ["result"] = result };
            }
            catch (ToolException e)
            {
                return Fail(e.Message);
            }
            catch (Exception e)
            {
                return Fail(e.GetType().Name + ": " + e.Message);
            }
        }

        // ---------- argument helpers ----------

        static bool Has(JObject a, string key)
        {
            JToken t = a[key];
            return t != null && t.Type != JTokenType.Null;
        }

        static string Str(JObject a, string key)
        {
            return Has(a, key) ? (string)a[key] : null;
        }

        static int Int(JObject a, string key, int fallback)
        {
            return Has(a, key) ? (int)a[key] : fallback;
        }

        static bool Bool(JObject a, string key, bool fallback)
        {
            return Has(a, key) ? (bool)a[key] : fallback;
        }

        static string Require(JObject a, string key)
        {
            string s = Str(a, key);
            if (string.IsNullOrEmpty(s)) throw new ToolException("Missing required argument '" + key + "'.");
            return s;
        }

        static float Num(JToken t)
        {
            return (t == null || t.Type == JTokenType.Null) ? 0f : (float)t;
        }

        static Vector3 ToVec3(JToken t)
        {
            JArray arr = t as JArray;
            if (arr != null && arr.Count >= 3) return new Vector3(Num(arr[0]), Num(arr[1]), Num(arr[2]));
            JObject obj = t as JObject;
            if (obj != null) return new Vector3(Num(obj["x"]), Num(obj["y"]), Num(obj["z"]));
            throw new ToolException("Expected a vector like [x, y, z].");
        }

        static JArray FromVec3(Vector3 v)
        {
            return new JArray(v.x, v.y, v.z);
        }

        static Color ToColor(JToken t)
        {
            JArray arr = t as JArray;
            if (arr != null && arr.Count >= 3)
            {
                return new Color(Num(arr[0]), Num(arr[1]), Num(arr[2]), arr.Count > 3 ? Num(arr[3]) : 1f);
            }
            if (t != null && t.Type == JTokenType.String)
            {
                Color c;
                if (ColorUtility.TryParseHtmlString((string)t, out c)) return c;
            }
            throw new ToolException("Expected a color like [r, g, b, a] (0-1) or \"#RRGGBB\".");
        }

        static string ProjectPath()
        {
            return Directory.GetParent(Application.dataPath).FullName;
        }

        // ---------- scene lookup helpers ----------

        static string PathOf(GameObject go)
        {
            string path = go.name;
            Transform t = go.transform.parent;
            while (t != null)
            {
                path = t.name + "/" + path;
                t = t.parent;
            }
            return path;
        }

        static GameObject FindByPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            string[] parts = path.Trim('/').Split('/');
            for (int s = 0; s < SceneManager.sceneCount; s++)
            {
                Scene scene = SceneManager.GetSceneAt(s);
                if (!scene.isLoaded) continue;
                foreach (GameObject root in scene.GetRootGameObjects())
                {
                    if (root.name != parts[0]) continue;
                    Transform t = root.transform;
                    bool ok = true;
                    for (int i = 1; i < parts.Length; i++)
                    {
                        t = t.Find(parts[i]);
                        if (t == null) { ok = false; break; }
                    }
                    if (ok) return t.gameObject;
                }
            }
            return null;
        }

        static GameObject ResolveGameObject(JObject a)
        {
            if (Has(a, "instanceId"))
            {
                int id = (int)a["instanceId"];
                Object obj = EditorUtility.InstanceIDToObject(id);
                GameObject go = obj as GameObject;
                if (go == null)
                {
                    Component c = obj as Component;
                    if (c != null) go = c.gameObject;
                }
                if (go == null) throw new ToolException("No GameObject with instanceId " + id + ".");
                return go;
            }
            string path = Str(a, "path");
            if (string.IsNullOrEmpty(path)) throw new ToolException("Provide 'path' or 'instanceId'.");
            GameObject found = FindByPath(path);
            if (found == null) throw new ToolException("GameObject not found at path: " + path);
            return found;
        }

        static JObject Brief(GameObject go)
        {
            return new JObject
            {
                ["name"] = go.name,
                ["path"] = PathOf(go),
                ["instanceId"] = go.GetInstanceID()
            };
        }

        static void MarkDirty(GameObject go)
        {
            if (go != null && go.scene.IsValid() && !Application.isPlaying)
            {
                EditorSceneManager.MarkSceneDirty(go.scene);
            }
        }

        static Type ResolveComponentType(string name)
        {
            if (string.IsNullOrEmpty(name)) throw new ToolException("Missing component type name.");
            Type match = null;
            foreach (Type t in TypeCache.GetTypesDerivedFrom<Component>())
            {
                if (t.IsAbstract) continue;
                if (t.FullName == name) return t;
                if (!string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
                bool isUnity = t.Namespace != null && t.Namespace.StartsWith("UnityEngine");
                bool matchIsUnity = match != null && match.Namespace != null && match.Namespace.StartsWith("UnityEngine");
                if (match == null || (isUnity && !matchIsUnity)) match = t;
            }
            if (match == null) throw new ToolException("Component type not found: " + name);
            return match;
        }

        static Component ResolveComponentOn(GameObject go, string typeName)
        {
            Type type = ResolveComponentType(typeName);
            Component c = go.GetComponent(type);
            if (c == null) throw new ToolException(go.name + " has no " + type.Name + " component.");
            return c;
        }

        // ---------- editor / scene tools ----------

        static JToken GetEditorState(JObject a)
        {
            Scene scene = SceneManager.GetActiveScene();
            return new JObject
            {
                ["unityVersion"] = Application.unityVersion,
                ["projectPath"] = ProjectPath(),
                ["isPlaying"] = EditorApplication.isPlaying,
                ["isPaused"] = EditorApplication.isPaused,
                ["isCompiling"] = EditorApplication.isCompiling,
                ["buildTarget"] = EditorUserBuildSettings.activeBuildTarget.ToString(),
                ["activeScene"] = new JObject
                {
                    ["name"] = scene.name,
                    ["path"] = scene.path,
                    ["isDirty"] = scene.isDirty
                },
                ["bridgeVersion"] = ClunityInfo.Version
            };
        }

        static JToken ListHierarchy(JObject a)
        {
            int maxDepth = Int(a, "maxDepth", 3);
            int budget = 500;
            var nodes = new JArray();
            string rootPath = Str(a, "root");

            if (!string.IsNullOrEmpty(rootPath))
            {
                GameObject root = FindByPath(rootPath);
                if (root == null) throw new ToolException("GameObject not found: " + rootPath);
                nodes.Add(HierarchyNode(root, 0, maxDepth, ref budget));
            }
            else
            {
                for (int s = 0; s < SceneManager.sceneCount; s++)
                {
                    Scene scene = SceneManager.GetSceneAt(s);
                    if (!scene.isLoaded) continue;
                    foreach (GameObject go in scene.GetRootGameObjects())
                    {
                        if (budget <= 0) break;
                        nodes.Add(HierarchyNode(go, 0, maxDepth, ref budget));
                    }
                }
            }

            return new JObject { ["nodes"] = nodes, ["truncated"] = budget <= 0 };
        }

        static JObject HierarchyNode(GameObject go, int depth, int maxDepth, ref int budget)
        {
            budget--;
            var node = new JObject
            {
                ["name"] = go.name,
                ["path"] = PathOf(go),
                ["instanceId"] = go.GetInstanceID(),
                ["active"] = go.activeSelf
            };
            var comps = new JArray();
            foreach (Component c in go.GetComponents<Component>())
            {
                comps.Add(c == null ? "(Missing Script)" : c.GetType().Name);
            }
            node["components"] = comps;

            int childCount = go.transform.childCount;
            if (childCount > 0)
            {
                if (depth < maxDepth)
                {
                    var kids = new JArray();
                    foreach (Transform child in go.transform)
                    {
                        if (budget <= 0) break;
                        kids.Add(HierarchyNode(child.gameObject, depth + 1, maxDepth, ref budget));
                    }
                    node["children"] = kids;
                }
                else
                {
                    node["childCount"] = childCount;
                }
            }
            return node;
        }

        static JToken GetGameObject(JObject a)
        {
            GameObject go = ResolveGameObject(a);
            Transform t = go.transform;

            var comps = new JArray();
            foreach (Component c in go.GetComponents<Component>())
            {
                if (c == null)
                {
                    comps.Add(new JObject { ["type"] = "(Missing Script)" });
                    continue;
                }
                var entry = new JObject
                {
                    ["type"] = c.GetType().Name,
                    ["fullType"] = c.GetType().FullName
                };
                Behaviour b = c as Behaviour;
                if (b != null) entry["enabled"] = b.enabled;
                comps.Add(entry);
            }

            var children = new JArray();
            foreach (Transform child in t) children.Add(child.name);

            return new JObject
            {
                ["name"] = go.name,
                ["path"] = PathOf(go),
                ["instanceId"] = go.GetInstanceID(),
                ["scene"] = go.scene.name,
                ["activeSelf"] = go.activeSelf,
                ["activeInHierarchy"] = go.activeInHierarchy,
                ["tag"] = go.tag,
                ["layer"] = LayerMask.LayerToName(go.layer),
                ["isStatic"] = go.isStatic,
                ["transform"] = new JObject
                {
                    ["position"] = FromVec3(t.position),
                    ["localPosition"] = FromVec3(t.localPosition),
                    ["rotation"] = FromVec3(t.eulerAngles),
                    ["localRotation"] = FromVec3(t.localEulerAngles),
                    ["localScale"] = FromVec3(t.localScale)
                },
                ["components"] = comps,
                ["children"] = children
            };
        }

        static JToken CreateGameObject(JObject a)
        {
            string name = Str(a, "name") ?? "GameObject";
            string primitive = Str(a, "primitive");
            GameObject go;

            if (!string.IsNullOrEmpty(primitive))
            {
                PrimitiveType pt;
                if (!Enum.TryParse(primitive, true, out pt))
                {
                    throw new ToolException("Unknown primitive '" + primitive + "'. Use Cube, Sphere, Capsule, Cylinder, Plane or Quad.");
                }
                go = GameObject.CreatePrimitive(pt);
                go.name = name;
            }
            else
            {
                go = new GameObject(name);
            }
            Undo.RegisterCreatedObjectUndo(go, "Clunity: Create " + name);

            string parentPath = Str(a, "parent");
            if (!string.IsNullOrEmpty(parentPath))
            {
                GameObject parent = FindByPath(parentPath);
                if (parent == null)
                {
                    Undo.DestroyObjectImmediate(go);
                    throw new ToolException("Parent not found: " + parentPath);
                }
                go.transform.SetParent(parent.transform, false);
            }

            if (Has(a, "position")) go.transform.position = ToVec3(a["position"]);
            if (Has(a, "rotation")) go.transform.eulerAngles = ToVec3(a["rotation"]);
            if (Has(a, "scale")) go.transform.localScale = ToVec3(a["scale"]);

            JArray comps = a["components"] as JArray;
            if (comps != null)
            {
                foreach (JToken c in comps)
                {
                    Type type = ResolveComponentType((string)c);
                    if (Undo.AddComponent(go, type) == null)
                    {
                        throw new ToolException("Could not add component " + type.Name + " to " + name + ".");
                    }
                }
            }

            MarkDirty(go);
            return Brief(go);
        }

        static JToken DeleteGameObject(JObject a)
        {
            GameObject go = ResolveGameObject(a);
            JObject info = Brief(go);
            Scene scene = go.scene;
            Undo.DestroyObjectImmediate(go);
            if (scene.IsValid() && !Application.isPlaying) EditorSceneManager.MarkSceneDirty(scene);
            info["deleted"] = true;
            return info;
        }

        static JToken SetTransform(JObject a)
        {
            GameObject go = ResolveGameObject(a);
            Transform t = go.transform;
            bool local = string.Equals(Str(a, "space"), "local", StringComparison.OrdinalIgnoreCase);
            if (!Has(a, "position") && !Has(a, "rotation") && !Has(a, "scale"))
            {
                throw new ToolException("Provide at least one of 'position', 'rotation', 'scale'.");
            }

            Undo.RecordObject(t, "Clunity: Set Transform");
            if (Has(a, "position"))
            {
                Vector3 p = ToVec3(a["position"]);
                if (local) t.localPosition = p; else t.position = p;
            }
            if (Has(a, "rotation"))
            {
                Vector3 r = ToVec3(a["rotation"]);
                if (local) t.localEulerAngles = r; else t.eulerAngles = r;
            }
            if (Has(a, "scale")) t.localScale = ToVec3(a["scale"]);
            MarkDirty(go);

            return new JObject
            {
                ["path"] = PathOf(go),
                ["position"] = FromVec3(t.position),
                ["rotation"] = FromVec3(t.eulerAngles),
                ["localScale"] = FromVec3(t.localScale)
            };
        }

        static JToken AddComponent(JObject a)
        {
            GameObject go = ResolveGameObject(a);
            Type type = ResolveComponentType(Require(a, "type"));
            Component c = Undo.AddComponent(go, type);
            if (c == null) throw new ToolException("Unity refused to add " + type.Name + " to " + go.name + " (conflicting or disallowed component).");
            MarkDirty(go);
            return new JObject { ["path"] = PathOf(go), ["added"] = type.FullName };
        }

        // ---------- component property tools ----------

        static JToken GetComponentProperties(JObject a)
        {
            GameObject go = ResolveGameObject(a);
            Component comp = ResolveComponentOn(go, Require(a, "component"));
            var so = new SerializedObject(comp);
            var props = new JArray();
            SerializedProperty it = so.GetIterator();
            bool enter = true;
            while (it.NextVisible(enter))
            {
                enter = false;
                var p = new JObject
                {
                    ["name"] = it.propertyPath,
                    ["type"] = it.propertyType.ToString()
                };
                JToken v = ReadProperty(it);
                if (v != null) p["value"] = v;
                props.Add(p);
                if (props.Count >= 200) break;
            }
            return new JObject { ["component"] = comp.GetType().FullName, ["properties"] = props };
        }

        static JToken SetComponentProperty(JObject a)
        {
            GameObject go = ResolveGameObject(a);
            Component comp = ResolveComponentOn(go, Require(a, "component"));
            string prop = Require(a, "property");
            JToken value = a["value"];
            if (value == null) throw new ToolException("Missing required argument 'value'.");

            var so = new SerializedObject(comp);
            SerializedProperty p = so.FindProperty(prop);
            if (p == null && prop.Length > 0)
            {
                // Friendly fallback: "mass" -> "m_Mass".
                p = so.FindProperty("m_" + char.ToUpperInvariant(prop[0]) + prop.Substring(1));
            }
            if (p == null)
            {
                throw new ToolException("Property '" + prop + "' not found on " + comp.GetType().Name +
                                        ". Call get_component_properties to see the serialized property names.");
            }

            WriteProperty(p, value);
            so.ApplyModifiedProperties();
            EditorUtility.SetDirty(comp);
            MarkDirty(go);

            JToken now = ReadProperty(p);
            return new JObject
            {
                ["property"] = p.propertyPath,
                ["value"] = now ?? JValue.CreateNull()
            };
        }

        static JToken ReadProperty(SerializedProperty p)
        {
            switch (p.propertyType)
            {
                case SerializedPropertyType.Integer: return p.intValue;
                case SerializedPropertyType.Boolean: return p.boolValue;
                case SerializedPropertyType.Float: return p.floatValue;
                case SerializedPropertyType.String: return p.stringValue;
                case SerializedPropertyType.LayerMask: return p.intValue;
                case SerializedPropertyType.Color:
                {
                    Color c = p.colorValue;
                    return new JArray(c.r, c.g, c.b, c.a);
                }
                case SerializedPropertyType.Vector2:
                {
                    Vector2 v = p.vector2Value;
                    return new JArray(v.x, v.y);
                }
                case SerializedPropertyType.Vector3: return FromVec3(p.vector3Value);
                case SerializedPropertyType.Vector4:
                {
                    Vector4 v = p.vector4Value;
                    return new JArray(v.x, v.y, v.z, v.w);
                }
                case SerializedPropertyType.Quaternion: return FromVec3(p.quaternionValue.eulerAngles);
                case SerializedPropertyType.Enum:
                {
                    int i = p.enumValueIndex;
                    if (i >= 0 && i < p.enumNames.Length) return p.enumNames[i];
                    return i;
                }
                case SerializedPropertyType.ObjectReference:
                {
                    Object o = p.objectReferenceValue;
                    if (o == null) return JValue.CreateNull();
                    string assetPath = AssetDatabase.GetAssetPath(o);
                    if (!string.IsNullOrEmpty(assetPath)) return assetPath;
                    return o.name;
                }
                default:
                    return null;
            }
        }

        static void WriteProperty(SerializedProperty p, JToken v)
        {
            switch (p.propertyType)
            {
                case SerializedPropertyType.Integer:
                case SerializedPropertyType.LayerMask:
                    p.intValue = (int)v;
                    break;
                case SerializedPropertyType.Boolean:
                    p.boolValue = (bool)v;
                    break;
                case SerializedPropertyType.Float:
                    p.floatValue = (float)v;
                    break;
                case SerializedPropertyType.String:
                    p.stringValue = (string)v;
                    break;
                case SerializedPropertyType.Color:
                    p.colorValue = ToColor(v);
                    break;
                case SerializedPropertyType.Vector2:
                {
                    JArray arr = v as JArray;
                    if (arr == null || arr.Count < 2) throw new ToolException("Expected [x, y].");
                    p.vector2Value = new Vector2(Num(arr[0]), Num(arr[1]));
                    break;
                }
                case SerializedPropertyType.Vector3:
                    p.vector3Value = ToVec3(v);
                    break;
                case SerializedPropertyType.Vector4:
                {
                    JArray arr = v as JArray;
                    if (arr == null || arr.Count < 4) throw new ToolException("Expected [x, y, z, w].");
                    p.vector4Value = new Vector4(Num(arr[0]), Num(arr[1]), Num(arr[2]), Num(arr[3]));
                    break;
                }
                case SerializedPropertyType.Quaternion:
                    p.quaternionValue = Quaternion.Euler(ToVec3(v));
                    break;
                case SerializedPropertyType.Enum:
                {
                    if (v.Type == JTokenType.String)
                    {
                        string wanted = (string)v;
                        int idx = Array.FindIndex(p.enumNames, n => string.Equals(n, wanted, StringComparison.OrdinalIgnoreCase));
                        if (idx < 0)
                        {
                            idx = Array.FindIndex(p.enumDisplayNames, n => string.Equals(n, wanted, StringComparison.OrdinalIgnoreCase));
                        }
                        if (idx < 0)
                        {
                            throw new ToolException("Unknown enum value '" + wanted + "'. Options: " + string.Join(", ", p.enumNames));
                        }
                        p.enumValueIndex = idx;
                    }
                    else
                    {
                        p.enumValueIndex = (int)v;
                    }
                    break;
                }
                case SerializedPropertyType.ObjectReference:
                    p.objectReferenceValue = ResolveObjectReference(v);
                    break;
                default:
                    throw new ToolException("Setting properties of type " + p.propertyType + " is not supported yet.");
            }
        }

        // "Assets/Mat.mat", "Scene/Object/Path" or "Scene/Object/Path#ComponentType"; null clears the reference.
        static Object ResolveObjectReference(JToken v)
        {
            if (v.Type == JTokenType.Null) return null;
            string s = (string)v;
            if (string.IsNullOrEmpty(s)) return null;

            if (s.StartsWith("Assets/") || s.StartsWith("Packages/"))
            {
                Object asset = AssetDatabase.LoadAssetAtPath<Object>(s);
                if (asset == null) throw new ToolException("No asset at " + s);
                return asset;
            }

            string componentName = null;
            int hash = s.IndexOf('#');
            if (hash >= 0)
            {
                componentName = s.Substring(hash + 1);
                s = s.Substring(0, hash);
            }
            GameObject go = FindByPath(s);
            if (go == null) throw new ToolException("No GameObject at path " + s);
            if (componentName == null) return go;
            return ResolveComponentOn(go, componentName);
        }

        // ---------- assets and files ----------

        static JToken FindAssets(JObject a)
        {
            string filter = Str(a, "filter") ?? "";
            int max = Int(a, "max", 100);
            JArray folderArray = a["folders"] as JArray;
            string[] guids;
            if (folderArray != null && folderArray.Count > 0)
            {
                string[] folders = folderArray.Select(x => (string)x).ToArray();
                guids = AssetDatabase.FindAssets(filter, folders);
            }
            else
            {
                guids = AssetDatabase.FindAssets(filter);
            }

            var results = new JArray();
            foreach (string guid in guids)
            {
                if (results.Count >= max) break;
                string path = AssetDatabase.GUIDToAssetPath(guid);
                Type type = AssetDatabase.GetMainAssetTypeAtPath(path);
                results.Add(new JObject
                {
                    ["path"] = path,
                    ["type"] = type != null ? type.Name : null
                });
            }
            return new JObject { ["total"] = guids.Length, ["assets"] = results };
        }

        // Resolves a project-relative path to a full path and refuses anything that escapes Assets/ (or Packages/ for reads).
        static string SafeProjectPath(string path, bool forWrite)
        {
            path = path.Replace('\\', '/');
            bool inAssets = path.StartsWith("Assets/");
            bool inPackages = !forWrite && path.StartsWith("Packages/");
            if (!inAssets && !inPackages)
            {
                throw new ToolException(forWrite
                    ? "Path must start with 'Assets/'."
                    : "Path must start with 'Assets/' or 'Packages/'.");
            }
            string rootName = inAssets ? "Assets" : "Packages";
            string full = Path.GetFullPath(Path.Combine(ProjectPath(), path));
            string root = Path.GetFullPath(Path.Combine(ProjectPath(), rootName));
            if (!full.StartsWith(root + Path.DirectorySeparatorChar))
            {
                throw new ToolException("Path escapes the " + rootName + " folder.");
            }
            return full;
        }

        static JToken ReadFile(JObject a)
        {
            string path = Require(a, "path");
            string full = SafeProjectPath(path, false);
            if (!File.Exists(full)) throw new ToolException("File not found: " + path);
            var info = new FileInfo(full);
            if (info.Length > MaxReadBytes)
            {
                throw new ToolException("File is too large (" + info.Length + " bytes, limit " + MaxReadBytes + ").");
            }
            return new JObject
            {
                ["path"] = path,
                ["content"] = File.ReadAllText(full, Encoding.UTF8)
            };
        }

        static JToken WriteFile(JObject a)
        {
            string path = Require(a, "path");
            if (!Has(a, "content")) throw new ToolException("Missing required argument 'content'.");
            string content = (string)a["content"];

            string ext = Path.GetExtension(path).ToLowerInvariant();
            if (!WritableExtensions.Contains(ext))
            {
                throw new ToolException("Files with extension '" + ext + "' can't be written. Allowed: " +
                                        string.Join(", ", WritableExtensions.OrderBy(x => x).ToArray()));
            }

            string full = SafeProjectPath(path, true);
            bool existed = File.Exists(full);
            if (existed && !Bool(a, "overwrite", true)) throw new ToolException("File already exists: " + path);

            Directory.CreateDirectory(Path.GetDirectoryName(full));
            File.WriteAllText(full, content, new UTF8Encoding(false));
            AssetDatabase.Refresh();

            var result = new JObject { ["path"] = path, ["created"] = !existed };
            if (ext == ".cs" || ext == ".asmdef" || ext == ".asmref")
            {
                result["note"] = "Unity will now recompile. The bridge restarts after the domain reload; call get_editor_state until isCompiling is false, then check get_console for compile errors.";
            }
            return result;
        }

        static JToken RefreshAssets(JObject a)
        {
            AssetDatabase.Refresh();
            return new JObject { ["isCompiling"] = EditorApplication.isCompiling };
        }

        static JToken InstantiatePrefab(JObject a)
        {
            string assetPath = Require(a, "assetPath");
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
            if (prefab == null) throw new ToolException("No prefab at " + assetPath);

            var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            Undo.RegisterCreatedObjectUndo(instance, "Clunity: Instantiate " + prefab.name);

            string parentPath = Str(a, "parent");
            if (!string.IsNullOrEmpty(parentPath))
            {
                GameObject parent = FindByPath(parentPath);
                if (parent == null)
                {
                    Undo.DestroyObjectImmediate(instance);
                    throw new ToolException("Parent not found: " + parentPath);
                }
                instance.transform.SetParent(parent.transform, false);
            }
            if (Has(a, "position")) instance.transform.position = ToVec3(a["position"]);
            if (Has(a, "rotation")) instance.transform.eulerAngles = ToVec3(a["rotation"]);
            if (Has(a, "scale")) instance.transform.localScale = ToVec3(a["scale"]);

            MarkDirty(instance);
            return Brief(instance);
        }

        // ---------- console ----------

        static JToken GetConsole(JObject a)
        {
            int max = Int(a, "maxEntries", 50);
            JArray typeArray = a["types"] as JArray;
            List<string> levels = typeArray == null
                ? null
                : typeArray.Select(x => (string)x).ToList();
            JArray entries = ClunityLog.Get(max, levels);
            return new JObject { ["count"] = entries.Count, ["entries"] = entries };
        }

        static JToken ClearConsole(JObject a)
        {
            ClunityLog.Clear();
            return new JObject { ["cleared"] = true };
        }

        // ---------- play mode, scenes, menu ----------

        static JToken SetPlayMode(JObject a)
        {
            string state = Require(a, "state").ToLowerInvariant();
            switch (state)
            {
                case "play": EditorApplication.isPlaying = true; break;
                case "stop": EditorApplication.isPlaying = false; break;
                case "pause": EditorApplication.isPaused = true; break;
                case "unpause": EditorApplication.isPaused = false; break;
                default: throw new ToolException("state must be one of: play, stop, pause, unpause.");
            }
            return new JObject
            {
                ["requested"] = state,
                ["note"] = "Entering or leaving play mode can reload scripts; the bridge may be unreachable for a few seconds."
            };
        }

        static JToken OpenScene(JObject a)
        {
            string path = Require(a, "path");
            if (Application.isPlaying) throw new ToolException("Stop play mode before opening a scene.");
            if (!path.StartsWith("Assets/") || !File.Exists(SafeProjectPath(path, true)))
            {
                throw new ToolException("Scene file not found: " + path);
            }

            bool additive = string.Equals(Str(a, "mode"), "additive", StringComparison.OrdinalIgnoreCase);
            if (!additive && !Bool(a, "discardChanges", false))
            {
                for (int i = 0; i < SceneManager.sceneCount; i++)
                {
                    Scene open = SceneManager.GetSceneAt(i);
                    if (open.isDirty)
                    {
                        throw new ToolException("Scene '" + open.name + "' has unsaved changes. Call save_scene first, or pass discardChanges: true.");
                    }
                }
            }

            Scene scene = EditorSceneManager.OpenScene(path, additive ? OpenSceneMode.Additive : OpenSceneMode.Single);
            return new JObject { ["name"] = scene.name, ["path"] = scene.path };
        }

        static JToken SaveScene(JObject a)
        {
            if (Application.isPlaying) throw new ToolException("Can't save scenes while in play mode.");
            string newPath = Str(a, "path");
            var saved = new JArray();
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                Scene scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded || !scene.isDirty) continue;

                if (string.IsNullOrEmpty(scene.path))
                {
                    if (string.IsNullOrEmpty(newPath))
                    {
                        throw new ToolException("Scene '" + scene.name + "' has never been saved. Pass 'path' (e.g. Assets/Scenes/Main.unity).");
                    }
                    SafeProjectPath(newPath, true);
                    string dir = Path.GetDirectoryName(Path.Combine(ProjectPath(), newPath));
                    Directory.CreateDirectory(dir);
                    if (!EditorSceneManager.SaveScene(scene, newPath)) throw new ToolException("Failed to save scene to " + newPath);
                    saved.Add(newPath);
                }
                else
                {
                    if (!EditorSceneManager.SaveScene(scene)) throw new ToolException("Failed to save scene " + scene.path);
                    saved.Add(scene.path);
                }
            }
            return new JObject { ["saved"] = saved };
        }

        static JToken ExecuteMenuItem(JObject a)
        {
            string menu = Require(a, "menuPath");
            string lower = menu.ToLowerInvariant();
            if (lower.Contains("quit") || lower.Contains("exit"))
            {
                throw new ToolException("Refusing to run a menu item that quits the editor.");
            }
            bool ok = EditorApplication.ExecuteMenuItem(menu);
            if (!ok) throw new ToolException("Menu item not found or unavailable: " + menu);
            return new JObject { ["executed"] = menu };
        }

        static JToken GetSelection(JObject a)
        {
            var list = new JArray();
            foreach (GameObject go in Selection.gameObjects) list.Add(Brief(go));
            return new JObject { ["selection"] = list };
        }

        static JToken SelectObject(JObject a)
        {
            GameObject go = ResolveGameObject(a);
            Selection.activeGameObject = go;
            EditorGUIUtility.PingObject(go);
            return Brief(go);
        }
    }
}
