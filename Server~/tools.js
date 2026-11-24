'use strict';

// Tool definitions advertised to Claude. Each name must have a handler in
// Editor/ClunityTools.cs (test/test.js checks that the two lists match).

const vec3 = (description) => ({
  type: 'array',
  items: { type: 'number' },
  minItems: 3,
  maxItems: 3,
  description,
});

// Most tools address a GameObject either by hierarchy path or by instanceId.
const target = {
  path: {
    type: 'string',
    description: 'Hierarchy path of the GameObject, e.g. "Player/Camera". Names are matched from a scene root down.',
  },
  instanceId: {
    type: 'integer',
    description: 'Unity instanceId (returned by other tools). Use instead of path.',
  },
};

const TOOLS = [
  {
    name: 'get_editor_state',
    description:
      'Get Unity version, project path, play/pause/compile state and the active scene. Call this first to check the connection.',
    inputSchema: { type: 'object', properties: {} },
  },
  {
    name: 'list_hierarchy',
    description: 'List GameObjects in the loaded scenes as a tree, with their components. Capped at 500 nodes.',
    inputSchema: {
      type: 'object',
      properties: {
        root: { type: 'string', description: 'Optional path to start from instead of the scene roots.' },
        maxDepth: { type: 'integer', description: 'How many levels of children to include (default 3).' },
      },
    },
  },
  {
    name: 'get_gameobject',
    description: 'Get details of one GameObject: transform, tag, layer, components and children.',
    inputSchema: { type: 'object', properties: { ...target } },
  },
  {
    name: 'create_gameobject',
    description: 'Create a GameObject (empty or a primitive), optionally parented, positioned and with components added.',
    inputSchema: {
      type: 'object',
      properties: {
        name: { type: 'string', description: 'Name of the new object.' },
        primitive: {
          type: 'string',
          enum: ['Cube', 'Sphere', 'Capsule', 'Cylinder', 'Plane', 'Quad'],
          description: 'Create a primitive mesh object instead of an empty one.',
        },
        parent: { type: 'string', description: 'Hierarchy path of the parent.' },
        position: vec3('World position [x, y, z].'),
        rotation: vec3('World rotation in Euler degrees [x, y, z].'),
        scale: vec3('Local scale [x, y, z].'),
        components: {
          type: 'array',
          items: { type: 'string' },
          description: 'Component type names to add, e.g. ["Rigidbody", "AudioSource"].',
        },
      },
    },
  },
  {
    name: 'delete_gameobject',
    description: 'Delete a GameObject (undoable in the editor).',
    inputSchema: { type: 'object', properties: { ...target } },
  },
  {
    name: 'set_transform',
    description: 'Set position, rotation and/or scale of a GameObject.',
    inputSchema: {
      type: 'object',
      properties: {
        ...target,
        position: vec3('Position [x, y, z].'),
        rotation: vec3('Euler rotation in degrees [x, y, z].'),
        scale: vec3('Local scale [x, y, z].'),
        space: { type: 'string', enum: ['world', 'local'], description: 'Space for position/rotation (default world).' },
      },
    },
  },
  {
    name: 'add_component',
    description: 'Add a component to a GameObject by type name (e.g. "Rigidbody", "BoxCollider", or a script class).',
    inputSchema: {
      type: 'object',
      properties: { ...target, type: { type: 'string', description: 'Component type name or full name.' } },
      required: ['type'],
    },
  },
  {
    name: 'get_component_properties',
    description:
      'List the serialized properties of a component with their current values. Use this to find the property names for set_component_property.',
    inputSchema: {
      type: 'object',
      properties: { ...target, component: { type: 'string', description: 'Component type name, e.g. "Rigidbody".' } },
      required: ['component'],
    },
  },
  {
    name: 'set_component_property',
    description:
      'Set a serialized property on a component. Supports numbers, booleans, strings, colors ([r,g,b,a] or "#RRGGBB"), vectors, enums (by name), and object references ("Assets/..." path, or a scene path, optionally "path#ComponentType").',
    inputSchema: {
      type: 'object',
      properties: {
        ...target,
        component: { type: 'string', description: 'Component type name, e.g. "Rigidbody".' },
        property: { type: 'string', description: 'Property name, e.g. "mass" or "m_Mass".' },
        value: { description: 'The new value.' },
      },
      required: ['component', 'property', 'value'],
    },
  },
  {
    name: 'find_assets',
    description: 'Search the project assets with an AssetDatabase filter such as "t:Prefab enemy" or "t:Scene".',
    inputSchema: {
      type: 'object',
      properties: {
        filter: { type: 'string', description: 'AssetDatabase.FindAssets filter string.' },
        folders: { type: 'array', items: { type: 'string' }, description: 'Limit the search to these folders, e.g. ["Assets/Prefabs"].' },
        max: { type: 'integer', description: 'Maximum results (default 100).' },
      },
    },
  },
  {
    name: 'read_file',
    description: 'Read a text file from the project (path must start with Assets/ or Packages/). Limit 400 KB.',
    inputSchema: {
      type: 'object',
      properties: { path: { type: 'string', description: 'Project-relative path, e.g. "Assets/Scripts/Player.cs".' } },
      required: ['path'],
    },
  },
  {
    name: 'write_file',
    description:
      'Create or overwrite a text file under Assets/ (.cs, .shader, .json, .txt, .md, .asmdef, .uss, .uxml and similar). Writing a script makes Unity recompile and reload; check get_console afterwards for compile errors.',
    inputSchema: {
      type: 'object',
      properties: {
        path: { type: 'string', description: 'Project-relative path starting with Assets/.' },
        content: { type: 'string', description: 'Full file contents.' },
        overwrite: { type: 'boolean', description: 'Allow replacing an existing file (default true).' },
      },
      required: ['path', 'content'],
    },
  },
  {
    name: 'get_console',
    description:
      'Read recent Unity console messages (logs, warnings, errors). The buffer is cleared when scripts reload, so read it right after a compile.',
    inputSchema: {
      type: 'object',
      properties: {
        maxEntries: { type: 'integer', description: 'Number of most recent entries to return (default 50).' },
        types: {
          type: 'array',
          items: { type: 'string', enum: ['Log', 'Warning', 'Error'] },
          description: 'Only return these message types.',
        },
      },
    },
  },
  {
    name: 'clear_console',
    description: 'Clear the captured console buffer and the Unity Console window.',
    inputSchema: { type: 'object', properties: {} },
  },
  {
    name: 'set_play_mode',
    description:
      'Enter, leave, pause or unpause play mode. Entering play mode may reload scripts, so the bridge can be briefly unreachable afterwards.',
    inputSchema: {
      type: 'object',
      properties: { state: { type: 'string', enum: ['play', 'stop', 'pause', 'unpause'] } },
      required: ['state'],
    },
  },
  {
    name: 'open_scene',
    description: 'Open a scene asset. Refuses if the current scene has unsaved changes unless discardChanges is true.',
    inputSchema: {
      type: 'object',
      properties: {
        path: { type: 'string', description: 'Scene path, e.g. "Assets/Scenes/Main.unity".' },
        mode: { type: 'string', enum: ['single', 'additive'], description: 'Default single.' },
        discardChanges: { type: 'boolean', description: 'Discard unsaved changes in the current scene (default false).' },
      },
      required: ['path'],
    },
  },
  {
    name: 'save_scene',
    description: 'Save all modified open scenes. A scene that has never been saved needs a path.',
    inputSchema: {
      type: 'object',
      properties: { path: { type: 'string', description: 'Where to save an untitled scene, e.g. "Assets/Scenes/Main.unity".' } },
    },
  },
  {
    name: 'execute_menu_item',
    description:
      'Run a Unity editor menu item by path, e.g. "GameObject/3D Object/Cube" or "Assets/Refresh". Items that open dialogs may block the editor, so prefer the dedicated tools.',
    inputSchema: {
      type: 'object',
      properties: { menuPath: { type: 'string', description: 'Full menu path.' } },
      required: ['menuPath'],
    },
  },
  {
    name: 'get_selection',
    description: 'Get the GameObjects currently selected in the editor.',
    inputSchema: { type: 'object', properties: {} },
  },
  {
    name: 'select_object',
    description: 'Select a GameObject in the editor and ping it in the Hierarchy.',
    inputSchema: { type: 'object', properties: { ...target } },
  },
  {
    name: 'instantiate_prefab',
    description: 'Instantiate a prefab asset into the open scene.',
    inputSchema: {
      type: 'object',
      properties: {
        assetPath: { type: 'string', description: 'Prefab path, e.g. "Assets/Prefabs/Enemy.prefab".' },
        parent: { type: 'string', description: 'Hierarchy path of the parent.' },
        position: vec3('World position [x, y, z].'),
        rotation: vec3('World rotation in Euler degrees [x, y, z].'),
        scale: vec3('Local scale [x, y, z].'),
      },
      required: ['assetPath'],
    },
  },
  {
    name: 'refresh_assets',
    description: 'Ask Unity to re-scan the Assets folder (after files changed on disk). May trigger a recompile.',
    inputSchema: { type: 'object', properties: {} },
  },
];

module.exports = { TOOLS };
