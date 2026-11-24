'use strict';

// End-to-end test for the MCP server using a mock Unity bridge, plus static
// checks on the C# sources (tool list in sync, balanced braces, no stray BOM).
// Run: node test/test.js   (Node 18+, no dependencies)

const assert = require('node:assert/strict');
const fs = require('node:fs');
const http = require('node:http');
const path = require('node:path');
const { spawn } = require('node:child_process');

const root = path.join(__dirname, '..');
const serverDir = path.join(root, 'Server~');
const { TOOLS } = require(path.join(serverDir, 'tools.js'));

let passed = 0;
async function test(name, fn) {
  try {
    await fn();
    passed++;
    console.log(`  ok   ${name}`);
  } catch (err) {
    console.error(`  FAIL ${name}\n       ${err && err.stack ? err.stack : err}`);
    process.exitCode = 1;
  }
}

// ---------- mock Unity bridge (mirrors the rules in ClunityBridge.cs) ----------

function startMockUnity() {
  const calls = [];
  const server = http.createServer((req, res) => {
    const send = (status, body) => {
      res.writeHead(status, { 'Content-Type': 'application/json' });
      res.end(JSON.stringify(body));
    };
    if (req.headers.origin) return send(403, { ok: false, error: 'Forbidden' });
    const host = req.headers.host || '';
    if (!(host.startsWith('127.0.0.1') || host.startsWith('localhost'))) return send(403, { ok: false, error: 'Forbidden' });
    if (req.method === 'GET' && req.url === '/ping') return send(200, { ok: true, name: 'Clunity' });
    if (req.method === 'POST' && req.url === '/call') {
      if (!(req.headers['content-type'] || '').startsWith('application/json')) {
        return send(415, { ok: false, error: 'Content-Type must be application/json' });
      }
      let body = '';
      req.on('data', (c) => (body += c));
      req.on('end', () => {
        const payload = JSON.parse(body);
        calls.push(payload);
        if (payload.tool === 'get_editor_state') {
          return send(200, { ok: true, result: { unityVersion: '2022.3.0f1', isPlaying: false } });
        }
        if (payload.tool === 'get_gameobject' && payload.args.path === 'Nope') {
          return send(200, { ok: false, error: 'GameObject not found at path: Nope' });
        }
        if (payload.tool === 'create_gameobject') {
          return send(200, { ok: true, result: { name: payload.args.name, path: payload.args.name, instanceId: 42 } });
        }
        if (payload.tool === 'clear_console') return send(200, { ok: true, result: { cleared: true } });
        return send(200, { ok: false, error: 'Unknown tool: ' + payload.tool });
      });
      return;
    }
    send(404, { ok: false, error: 'Not found' });
  });
  return new Promise((resolve) => {
    server.listen(0, '127.0.0.1', () => resolve({ server, calls, port: server.address().port }));
  });
}

// ---------- MCP client helper ----------

function startServer(env) {
  const child = spawn(process.execPath, [path.join(serverDir, 'index.js')], {
    env: { ...process.env, ...env },
    stdio: ['pipe', 'pipe', 'pipe'],
  });
  let buffer = '';
  const waiting = new Map();
  const unmatched = [];
  child.stdout.on('data', (chunk) => {
    buffer += chunk.toString('utf8');
    let nl;
    while ((nl = buffer.indexOf('\n')) >= 0) {
      const line = buffer.slice(0, nl);
      buffer = buffer.slice(nl + 1);
      if (!line.trim()) continue;
      const msg = JSON.parse(line); // throws if the server ever prints non-JSON to stdout
      if (waiting.has(msg.id)) {
        waiting.get(msg.id)(msg);
        waiting.delete(msg.id);
      } else unmatched.push(msg);
    }
  });
  let nextId = 1;
  const request = (method, params) =>
    new Promise((resolve, reject) => {
      const id = nextId++;
      const timer = setTimeout(() => reject(new Error(`timeout waiting for ${method}`)), 15000);
      waiting.set(id, (msg) => {
        clearTimeout(timer);
        resolve(msg);
      });
      child.stdin.write(JSON.stringify({ jsonrpc: '2.0', id, method, params }) + '\n');
    });
  const notify = (method, params) => child.stdin.write(JSON.stringify({ jsonrpc: '2.0', method, params }) + '\n');
  const raw = (line) => child.stdin.write(line + '\n');
  const close = () =>
    new Promise((resolve) => {
      child.on('exit', (code) => resolve(code));
      child.stdin.end();
    });
  return { request, notify, raw, close, unmatched, child };
}

(async () => {
  console.log('MCP server');
  const mock = await startMockUnity();
  const client = startServer({ CLUNITY_PORT: String(mock.port) });

  await test('initialize negotiates protocol and reports server info', async () => {
    const res = await client.request('initialize', {
      protocolVersion: '2025-06-18',
      capabilities: {},
      clientInfo: { name: 'test', version: '0' },
    });
    assert.equal(res.result.protocolVersion, '2025-06-18');
    assert.equal(res.result.serverInfo.name, 'clunity');
    assert.ok(res.result.capabilities.tools);
    client.notify('notifications/initialized');
  });

  await test('initialize falls back to a supported version for unknown clients', async () => {
    const res = await client.request('initialize', { protocolVersion: '1999-01-01', capabilities: {}, clientInfo: {} });
    assert.ok(['2025-06-18', '2025-03-26', '2024-11-05'].includes(res.result.protocolVersion));
  });

  await test('ping works', async () => {
    const res = await client.request('ping');
    assert.deepEqual(res.result, {});
  });

  await test('tools/list returns well-formed tools', async () => {
    const res = await client.request('tools/list');
    const tools = res.result.tools;
    assert.ok(tools.length >= 20);
    const names = new Set();
    for (const t of tools) {
      assert.match(t.name, /^[a-z][a-z_]*$/);
      assert.ok(!names.has(t.name), `duplicate tool ${t.name}`);
      names.add(t.name);
      assert.ok(t.description && t.description.length > 10, `${t.name} needs a description`);
      assert.equal(t.inputSchema.type, 'object');
      assert.ok(t.inputSchema.properties, `${t.name} needs properties`);
      for (const r of t.inputSchema.required || []) {
        assert.ok(r in t.inputSchema.properties, `${t.name}: required "${r}" is not a property`);
      }
    }
  });

  await test('tools/call forwards to Unity and returns its result', async () => {
    const res = await client.request('tools/call', { name: 'get_editor_state', arguments: {} });
    assert.equal(res.result.isError, undefined);
    const parsed = JSON.parse(res.result.content[0].text);
    assert.equal(parsed.unityVersion, '2022.3.0f1');
  });

  await test('arguments reach Unity unchanged', async () => {
    const res = await client.request('tools/call', {
      name: 'create_gameobject',
      arguments: { name: 'Hero', primitive: 'Cube', position: [1, 2, 3], components: ['Rigidbody'] },
    });
    assert.equal(JSON.parse(res.result.content[0].text).instanceId, 42);
    const last = mock.calls[mock.calls.length - 1];
    assert.equal(last.tool, 'create_gameobject');
    assert.deepEqual(last.args, { name: 'Hero', primitive: 'Cube', position: [1, 2, 3], components: ['Rigidbody'] });
  });

  await test('errors from Unity come back as isError results', async () => {
    const res = await client.request('tools/call', { name: 'get_gameobject', arguments: { path: 'Nope' } });
    assert.equal(res.result.isError, true);
    assert.match(res.result.content[0].text, /not found/i);
  });

  await test('missing required arguments are rejected before reaching Unity', async () => {
    const before = mock.calls.length;
    const res = await client.request('tools/call', { name: 'set_component_property', arguments: { component: 'Rigidbody' } });
    assert.equal(res.result.isError, true);
    assert.match(res.result.content[0].text, /property, value|value, property|property|value/);
    assert.equal(mock.calls.length, before);
  });

  await test('unknown tools are reported as errors', async () => {
    const res = await client.request('tools/call', { name: 'does_not_exist', arguments: {} });
    assert.equal(res.result.isError, true);
  });

  await test('unknown methods return JSON-RPC errors', async () => {
    const res = await client.request('resources/list');
    assert.equal(res.error.code, -32601);
  });

  await test('malformed JSON gets a parse error and the server keeps running', async () => {
    client.raw('{not json');
    await new Promise((r) => setTimeout(r, 200));
    assert.ok(client.unmatched.some((m) => m.error && m.error.code === -32700));
    const res = await client.request('ping');
    assert.deepEqual(res.result, {});
  });

  await test('stdin closing shuts the server down cleanly', async () => {
    const code = await client.close();
    assert.equal(code, 0);
  });

  await test('reports a clear error when Unity is not running', async () => {
    const closed = await new Promise((resolve) => {
      const s = http.createServer().listen(0, '127.0.0.1', () => {
        const p = s.address().port;
        s.close(() => resolve(p));
      });
    });
    const offline = startServer({ CLUNITY_PORT: String(closed), CLUNITY_RETRIES: '1', CLUNITY_RETRY_DELAY_MS: '50' });
    await offline.request('initialize', { protocolVersion: '2025-06-18', capabilities: {}, clientInfo: {} });
    const res = await offline.request('tools/call', { name: 'get_editor_state', arguments: {} });
    assert.equal(res.result.isError, true);
    assert.match(res.result.content[0].text, /Can't reach Unity/);
    assert.match(res.result.content[0].text, /Window > Clunity/);
    await offline.close();
  });

  await test('the bridge rules reject browser-style requests', async () => {
    const post = (headers) =>
      new Promise((resolve) => {
        const req = http.request(
          { host: '127.0.0.1', port: mock.port, path: '/call', method: 'POST', headers },
          (res) => {
            res.resume();
            resolve(res.statusCode);
          },
        );
        req.end(JSON.stringify({ tool: 'clear_console', args: {} }));
      });
    assert.equal(await post({ 'Content-Type': 'application/json', Origin: 'https://evil.example' }), 403);
    assert.equal(await post({ 'Content-Type': 'text/plain' }), 415);
    assert.equal(await post({ 'Content-Type': 'application/json' }), 200);
  });

  mock.server.close();

  // ---------- static checks on the Unity side ----------
  console.log('Unity sources');

  const editorDir = path.join(root, 'Editor');
  const csFiles = fs.readdirSync(editorDir).filter((f) => f.endsWith('.cs'));

  await test('every server tool has a C# handler and vice versa', () => {
    const src = fs.readFileSync(path.join(editorDir, 'ClunityTools.cs'), 'utf8');
    const block = src.split('// BEGIN HANDLERS')[1].split('// END HANDLERS')[0];
    const handlers = [...block.matchAll(/\["([a-z_]+)"\]\s*=\s*([A-Za-z]+)\s*,/g)];
    const csNames = handlers.map((m) => m[1]).sort();
    const jsNames = TOOLS.map((t) => t.name).sort();
    assert.deepEqual(csNames, jsNames);
    for (const m of handlers) {
      assert.match(src, new RegExp(`static JToken ${m[2]}\\(JObject a\\)`), `missing method ${m[2]}`);
    }
  });

  await test('C# files have balanced braces, parens and brackets', () => {
    for (const file of csFiles) {
      const src = fs.readFileSync(path.join(editorDir, file), 'utf8');
      const stack = [];
      const pairs = { ')': '(', ']': '[', '}': '{' };
      // Single pass so that "//" inside a string is not mistaken for a comment.
      let i = 0;
      while (i < src.length) {
        const ch = src[i];
        const next = src[i + 1];
        if (ch === '/' && next === '/') {
          while (i < src.length && src[i] !== '\n') i++;
        } else if (ch === '/' && next === '*') {
          i = src.indexOf('*/', i + 2);
          assert.ok(i >= 0, `${file}: unterminated block comment`);
          i += 2;
        } else if (ch === '"' || ch === "'") {
          i++;
          while (i < src.length && src[i] !== ch) {
            if (src[i] === '\\') i++;
            assert.notEqual(src[i], '\n', `${file}: unterminated string or char literal`);
            i++;
          }
          i++;
        } else {
          if ('([{'.includes(ch)) stack.push(ch);
          else if (ch in pairs) assert.equal(stack.pop(), pairs[ch], `${file}: unbalanced ${ch} near offset ${i}`);
          i++;
        }
      }
      assert.equal(stack.length, 0, `${file}: unclosed ${stack.join('')}`);
    }
  });

  await test('package.json, asmdef and metas are valid and consistent', () => {
    const pkg = JSON.parse(fs.readFileSync(path.join(root, 'package.json'), 'utf8'));
    assert.equal(pkg.name, 'com.clunity.bridge');
    const serverPkg = JSON.parse(fs.readFileSync(path.join(serverDir, 'package.json'), 'utf8'));
    assert.equal(serverPkg.version, pkg.version, 'server and package versions differ');
    const info = fs.readFileSync(path.join(editorDir, 'ClunityBridge.cs'), 'utf8');
    assert.match(info, new RegExp(`Version = "${pkg.version.replace(/\./g, '\\.')}"`));
    JSON.parse(fs.readFileSync(path.join(editorDir, 'Clunity.Editor.asmdef'), 'utf8'));
    // Unity ignores files in immutable (git) packages that have no .meta file.
    const needMeta = ['Editor', 'package.json', 'README.md', 'LICENSE', ...fs.readdirSync(editorDir).filter((f) => !f.endsWith('.meta')).map((f) => `Editor/${f}`)];
    const guids = new Set();
    for (const f of needMeta) {
      const metaPath = path.join(root, `${f}.meta`);
      assert.ok(fs.existsSync(metaPath), `missing ${f}.meta`);
      const guid = fs.readFileSync(metaPath, 'utf8').match(/^guid: ([0-9a-f]{32})$/m);
      assert.ok(guid, `${f}.meta has no valid guid`);
      assert.ok(!guids.has(guid[1]), `duplicate guid in ${f}.meta`);
      guids.add(guid[1]);
    }
  });

  console.log(process.exitCode ? `\n${passed} passed, some FAILED` : `\nAll ${passed} checks passed`);
})();
