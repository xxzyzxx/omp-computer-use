#!/usr/bin/env node
// Minimal stdio MCP client for smoke-testing the server without a harness.
// Usage: node scripts/mcp-call.mjs <server.exe> [--arg ...] -- '<toolName>' '<json args>' ['<toolName>' '<json args>' ...]
// Prints each result's text blocks; images are saved to %TEMP%\windows-mcp-smoke-<n>.png.
import { spawn } from "node:child_process";
import { writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import readline from "node:readline";

const argv = process.argv.slice(2);
const sep = argv.indexOf("--");
const serverArgs = sep === -1 ? argv : argv.slice(0, sep);
const calls = sep === -1 ? [] : argv.slice(sep + 1);
if (serverArgs.length === 0) {
  console.error("usage: mcp-call.mjs <server.exe> [server args] -- tool '{json}' ...");
  process.exit(2);
}

const child = spawn(serverArgs[0], serverArgs.slice(1), { stdio: ["pipe", "pipe", "inherit"] });
const rl = readline.createInterface({ input: child.stdout });
const pending = new Map();
let nextId = 1;
let imageCount = 0;

rl.on("line", (line) => {
  let msg;
  try { msg = JSON.parse(line); } catch { console.error("non-JSON line:", line); return; }
  if (msg.id !== undefined && pending.has(msg.id)) {
    pending.get(msg.id)(msg);
    pending.delete(msg.id);
  } else if (msg.method) {
    console.log(`<< notification ${msg.method} ${JSON.stringify(msg.params ?? {})}`);
  }
});

function send(method, params) {
  const id = nextId++;
  const p = new Promise((resolve) => pending.set(id, resolve));
  child.stdin.write(JSON.stringify({ jsonrpc: "2.0", id, method, params }) + "\n");
  return p;
}

function notify(method, params) {
  child.stdin.write(JSON.stringify({ jsonrpc: "2.0", method, params }) + "\n");
}

const t0 = Date.now();
const init = await send("initialize", { protocolVersion: "2025-06-18", capabilities: {}, clientInfo: { name: "mcp-call", version: "0" } });
console.log(`initialize -> ${init.result?.serverInfo?.name} ${init.result?.serverInfo?.version} protocol=${init.result?.protocolVersion} instructions=${(init.result?.instructions ?? "").length} chars`);
notify("notifications/initialized", {});

if (calls.length === 0) {
  const list = await send("tools/list", {});
  for (const t of list.result.tools) console.log(`tool ${t.name}: ${t.description.slice(0, 100)}`);
}

for (let i = 0; i + 1 < calls.length; i += 2) {
  const name = calls[i];
  const args = JSON.parse(calls[i + 1] || "{}");
  const started = Date.now();
  const res = await send("tools/call", { name, arguments: args });
  const ms = Date.now() - started;
  if (res.error) {
    console.log(`== ${name} (${ms}ms) RPC ERROR ${res.error.code}: ${res.error.message}`);
    continue;
  }
  const r = res.result;
  console.log(`== ${name} (${ms}ms)${r.isError ? " isError" : ""}`);
  for (const c of r.content ?? []) {
    if (c.type === "text") console.log(c.text);
    else if (c.type === "image") {
      const path = join(tmpdir(), `windows-mcp-smoke-${++imageCount}.png`);
      writeFileSync(path, Buffer.from(c.data, "base64"));
      console.log(`[image ${c.mimeType} ${c.data.length} b64 chars -> ${path}]`);
    }
  }
}

console.log(`total ${Date.now() - t0}ms`);
child.stdin.end();
setTimeout(() => child.kill(), 500);
