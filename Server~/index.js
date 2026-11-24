#!/usr/bin/env node
'use strict';

// Clunity MCP server: speaks MCP over stdio to Claude and forwards tool calls
// to the Clunity bridge running inside the Unity Editor (HTTP on 127.0.0.1).
// No dependencies; needs Node.js 18+ (for built-in fetch).

const readline = require('node:readline');
const { TOOLS } = require('./tools');
const pkg = require('./package.json');

const PORT = process.env.CLUNITY_PORT || '8765';
const BASE_URL = (process.env.CLUNITY_URL || `http://127.0.0.1:${PORT}`).replace(/\/+$/, '');
const RETRIES = Number.parseInt(process.env.CLUNITY_RETRIES || '3', 10);
const RETRY_DELAY_MS = Number.parseInt(process.env.CLUNITY_RETRY_DELAY_MS || '1500', 10);
const CALL_TIMEOUT_MS = 70000;

const SUPPORTED_PROTOCOLS = ['2025-06-18', '2025-03-26', '2024-11-05'];
const INSTRUCTIONS =
  'Clunity controls a running Unity Editor. Call get_editor_state first to confirm the connection. ' +
  'After write_file on a .cs file Unity recompiles and reloads scripts: wait until get_editor_state reports isCompiling=false, then read get_console for errors. ' +
  'Use get_component_properties to discover property names before set_component_property.';

const toolsByName = new Map(TOOLS.map((t) => [t.name, t]));

function log(...args) {
  // stdout is reserved for the MCP protocol, so diagnostics go to stderr.
  console.error('[clunity]', ...args);
}

function send(message) {
  process.stdout.write(JSON.stringify({ jsonrpc: '2.0', ...message }) + '\n');
}

const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

function toolText(text, isError = false) {
  const result = { content: [{ type: 'text', text }] };
  if (isError) result.isError = true;
  return result;
}

function validateArgs(tool, args) {
  const required = tool.inputSchema.required || [];
  const missing = required.filter((key) => args[key] === undefined || args[key] === null);
  if (missing.length) return `Missing required argument(s): ${missing.join(', ')}.`;
  return null;
}

function unreachableMessage(err) {
  const reason = err && err.cause && err.cause.code ? err.cause.code : err && err.message ? err.message : String(err);
  return (
    `Can't reach Unity at ${BASE_URL} (${reason}). ` +
    'Make sure the Unity Editor is open with the Clunity package installed and not in the middle of a script reload. ' +
    'Window > Clunity shows the bridge status and port; set CLUNITY_PORT if you changed it.'
  );
}

async function postToUnity(name, args) {
  let lastError;
  for (let attempt = 0; attempt <= RETRIES; attempt++) {
    if (attempt > 0) await sleep(RETRY_DELAY_MS);
    try {
      const res = await fetch(`${BASE_URL}/call`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ tool: name, args }),
        signal: AbortSignal.timeout(CALL_TIMEOUT_MS),
      });
      const text = await res.text();
      try {
        return JSON.parse(text);
      } catch {
        return { ok: false, error: `Unity returned an unexpected response (HTTP ${res.status}): ${text.slice(0, 200)}` };
      }
    } catch (err) {
      if (err && err.name === 'TimeoutError') {
        return { ok: false, error: 'Unity did not answer in time. The editor may be busy or showing a dialog.' };
      }
      lastError = err; // connection refused/reset: Unity may be reloading, so retry
    }
  }
  return { ok: false, error: unreachableMessage(lastError) };
}

async function callTool(params) {
  const name = params && params.name;
  const args = (params && params.arguments) || {};
  const tool = toolsByName.get(name);
  if (!tool) return toolText(`Unknown tool: ${name}`, true);

  const problem = validateArgs(tool, args);
  if (problem) return toolText(problem, true);

  const response = await postToUnity(name, args);
  if (response && response.ok) {
    return toolText(JSON.stringify(response.result === undefined ? null : response.result, null, 2));
  }
  return toolText((response && response.error) || 'Unknown error from Unity.', true);
}

async function handle(msg) {
  const { id, method, params } = msg;
  const isRequest = id !== undefined && id !== null;

  try {
    switch (method) {
      case 'initialize': {
        const wanted = params && params.protocolVersion;
        send({
          id,
          result: {
            protocolVersion: SUPPORTED_PROTOCOLS.includes(wanted) ? wanted : SUPPORTED_PROTOCOLS[0],
            capabilities: { tools: { listChanged: false } },
            serverInfo: { name: 'clunity', version: pkg.version },
            instructions: INSTRUCTIONS,
          },
        });
        return;
      }
      case 'ping':
        if (isRequest) send({ id, result: {} });
        return;
      case 'tools/list':
        send({ id, result: { tools: TOOLS } });
        return;
      case 'tools/call':
        send({ id, result: await callTool(params) });
        return;
      default:
        if (!isRequest) return; // notifications (e.g. notifications/initialized) need no reply
        send({ id, error: { code: -32601, message: `Method not found: ${method}` } });
    }
  } catch (err) {
    log('handler error:', err);
    if (isRequest) send({ id, error: { code: -32603, message: String(err && err.message ? err.message : err) } });
  }
}

// When Claude closes stdin the readline interface closes; in-flight calls keep the
// event loop alive until they finish, then the process exits on its own.
const rl = readline.createInterface({ input: process.stdin, crlfDelay: Infinity });

rl.on('line', (line) => {
  if (!line.trim()) return;
  let msg;
  try {
    msg = JSON.parse(line);
  } catch {
    send({ id: null, error: { code: -32700, message: 'Parse error' } });
    return;
  }
  handle(msg);
});

log(`ready (Unity bridge at ${BASE_URL})`);
