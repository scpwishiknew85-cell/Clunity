# Clunity

Let Claude drive the Unity Editor. Clunity is two pieces that ship together in one repo:

- **Unity plugin** (`Editor/`): a Unity package that runs a small local bridge inside the editor and exposes editor actions (scene, GameObjects, components, assets, scripts, console, play mode).
- **MCP connector** (`Server~/`): a dependency-free Node.js MCP server that Claude launches. It forwards Claude's tool calls to the bridge in Unity.

```
Claude  <--MCP over stdio-->  Server~/index.js  <--HTTP 127.0.0.1:8765-->  Unity Editor (Clunity bridge)
```

## Install

Requirements: Unity 2021.3 or newer, Node.js 18 or newer.

1. In Unity open **Window > Package Manager > + > Add package from git URL** and enter:
   ```
   https://github.com/scpwishiknew85-cell/Clunity.git
   ```
2. Open **Window > Clunity**. It should say `Running on http://127.0.0.1:8765`.
3. In that window click **Copy Claude Code command** (or **Copy Claude Desktop config**) and use it as shown below.

### Claude Code

Paste the copied command, which looks like:

```
claude mcp add clunity -e CLUNITY_PORT=8765 -- node "/path/to/Library/PackageCache/com.clunity.bridge@.../Server~/index.js"
```

### Claude Desktop

Add the copied JSON to `claude_desktop_config.json` (Settings > Developer > Edit Config) and restart Claude:

```json
{
  "mcpServers": {
    "clunity": {
      "command": "node",
      "args": ["/path/to/Server~/index.js"],
      "env": { "CLUNITY_PORT": "8765" }
    }
  }
}
```

The path inside `Library/PackageCache` changes when the package updates. If you prefer a stable path, clone this repo and point `args` at `Server~/index.js` in your clone.

### Try it

Keep the Unity project open, then ask Claude something like:

> Use Clunity to create a red cube called "Crate" at (0, 1, 0) with a Rigidbody, then press play and tell me if the console shows any errors.

## Tools

| Tool | What it does |
| --- | --- |
| `get_editor_state` | Unity version, project path, play/compile state, active scene |
| `list_hierarchy` | Scene tree with components (capped at 500 nodes) |
| `get_gameobject` | Transform, tag, layer, components, children of one object |
| `create_gameobject` | Empty object or primitive, with parent, transform and components |
| `delete_gameobject` | Delete an object (undoable) |
| `set_transform` | Position, rotation, scale (world or local) |
| `add_component` | Add a component by type name |
| `get_component_properties` | List serialized properties and current values |
| `set_component_property` | Set numbers, bools, strings, colors, vectors, enums, object references |
| `find_assets` | Search assets with an AssetDatabase filter |
| `read_file` / `write_file` | Read text files, write scripts and other text assets under `Assets/` |
| `get_console` / `clear_console` | Read and clear captured console messages |
| `set_play_mode` | Play, stop, pause, unpause |
| `open_scene` / `save_scene` | Open and save scenes (never shows blocking dialogs) |
| `execute_menu_item` | Run an editor menu item |
| `get_selection` / `select_object` | Read or change the editor selection |
| `instantiate_prefab` | Place a prefab in the scene |
| `refresh_assets` | Re-scan the Assets folder |

Notes:

- Writing a `.cs` file makes Unity recompile and reload. The bridge restarts after the reload and the MCP server retries automatically; check `get_console` for compile errors afterwards. The console buffer resets on reload.
- Scene edits are registered with Undo, so you can revert anything Claude did with Ctrl/Cmd+Z.

## Security

The bridge listens on `127.0.0.1` only and rejects requests that carry an `Origin` header, a non-local `Host` header, or a non-JSON content type, so web pages in your browser can't drive the editor. File writes are limited to text asset types under `Assets/`. Even so, `write_file` can create scripts that run in your editor, so only connect Claude to projects you are comfortable letting it modify, and use version control.

## Configuration

| Setting | Where | Default |
| --- | --- | --- |
| Bridge on/off, port | Window > Clunity | on, 8765 |
| `CLUNITY_PORT` | env var for the MCP server | 8765 |
| `CLUNITY_URL` | env var, overrides host and port | `http://127.0.0.1:$CLUNITY_PORT` |
| `CLUNITY_RETRIES`, `CLUNITY_RETRY_DELAY_MS` | env vars | 3, 1500 |

If you change the port in Unity, set the same `CLUNITY_PORT` for the server.

## Troubleshooting

- **"Can't reach Unity"**: the editor isn't open, is reloading scripts, or the port differs. Check Window > Clunity.
- **Port already in use**: another app uses 8765. Pick another port in Window > Clunity and update `CLUNITY_PORT`.
- **Tools time out**: Unity may be showing a modal dialog or importing assets. Dismiss it and retry.
- **Unity can't find Newtonsoft.Json**: Clunity depends on `com.unity.nuget.newtonsoft-json`, which Package Manager installs automatically; check the Console for package resolution errors.

## Development

```
node test/test.js
```

The test starts the MCP server against a mock Unity bridge and checks the MCP handshake, tool listing, argument forwarding, error handling and the offline case. It also verifies that every tool in `Server~/tools.js` has a handler in `Editor/ClunityTools.cs`, that the C# files are structurally balanced, and that every package file has a `.meta` file (Unity ignores files without one in git packages).

To add a tool: add a handler in `Editor/ClunityTools.cs` (and its entry in the `Handlers` table), then describe it in `Server~/tools.js`.

## License

MIT
