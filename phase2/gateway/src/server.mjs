#!/usr/bin/env node
// vent-inventor-gateway — HTTP поверх stdio-MCP .NET-сервера Inventor.
// Ставится на Windows-машине с Inventor. Отдаёт vent-тулзы наружу для коннектора kvz-ai.
//
// ENV:
//   SERVER_EXE      — путь к Bimwright.Ipt.Server.exe (обязателен)
//   SERVER_ARGS     — доп. аргументы через пробел (напр. "--read-only --toolsets meta,query,vent")
//   GATEWAY_TOKEN   — bearer-токен (обязателен в проде; без него /tools/* открыты — только для dev)
//   GATEWAY_HOST    — интерфейс bind (по умолчанию 127.0.0.1; для LAN укажите адрес машины)
//   GATEWAY_PORT    — порт (по умолчанию 8737)
//   TOOL_ALLOWLIST  — список разрешённых тулзов через запятую (по умолчанию — все vent + базовые)
import http from "node:http";
import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { StdioClientTransport } from "@modelcontextprotocol/sdk/client/stdio.js";

const SERVER_EXE = process.env.SERVER_EXE;
const SERVER_ARGS = (process.env.SERVER_ARGS ?? "").trim().length
  ? process.env.SERVER_ARGS.trim().split(/\s+/)
  : [];
const TOKEN = process.env.GATEWAY_TOKEN ?? "";
const HOST = process.env.GATEWAY_HOST ?? "127.0.0.1";
const PORT = Number(process.env.GATEWAY_PORT ?? 8737);
const ALLOW = (process.env.TOOL_ALLOWLIST ?? "")
  .split(",").map(s => s.trim()).filter(Boolean);

if (!SERVER_EXE) { console.error("SERVER_EXE не задан"); process.exit(1); }
if (!TOKEN) console.error("[warn] GATEWAY_TOKEN пуст — /tools/* без авторизации (только dev!)");

const allowed = (name) => ALLOW.length === 0 ? true : ALLOW.includes(name);

// --- MCP client к .NET-серверу (spawn + stdio) ---
let client;
async function connect() {
  const transport = new StdioClientTransport({ command: SERVER_EXE, args: SERVER_ARGS });
  client = new Client({ name: "vent-gateway", version: "0.1.0" }, { capabilities: {} });
  await client.connect(transport);
  const { tools } = await client.listTools();
  console.error(`[gateway] подключён к Server.exe, тулзов: ${tools.length}`);
}

// --- HTTP ---
function send(res, code, obj) {
  const body = JSON.stringify(obj);
  res.writeHead(code, { "content-type": "application/json; charset=utf-8" });
  res.end(body);
}
function authed(req) {
  if (!TOKEN) return true;
  const h = req.headers["authorization"] ?? "";
  return h === `Bearer ${TOKEN}`;
}
async function readJson(req) {
  const chunks = [];
  for await (const c of req) chunks.push(c);
  return chunks.length ? JSON.parse(Buffer.concat(chunks).toString("utf-8")) : {};
}

const server = http.createServer(async (req, res) => {
  try {
    const url = new URL(req.url, `http://${req.headers.host}`);
    if (url.pathname === "/health") {
      return send(res, 200, { ok: true, connected: !!client });
    }
    if (!authed(req)) return send(res, 401, { ok: false, error: "unauthorized" });

    if (req.method === "GET" && url.pathname === "/tools/list") {
      const { tools } = await client.listTools();
      return send(res, 200, { ok: true, tools: tools.filter(t => allowed(t.name)) });
    }
    if (req.method === "POST" && url.pathname === "/tools/call") {
      const { name, arguments: args } = await readJson(req);
      if (!name) return send(res, 400, { ok: false, error: "name required" });
      if (!allowed(name)) return send(res, 403, { ok: false, error: `tool not allowed: ${name}` });
      const result = await client.callTool({ name, arguments: args ?? {} });
      return send(res, 200, { ok: !result.isError, result });
    }
    return send(res, 404, { ok: false, error: "not found" });
  } catch (e) {
    return send(res, 500, { ok: false, error: String(e?.message ?? e) });
  }
});

connect()
  .then(() => server.listen(PORT, HOST, () =>
    console.error(`[gateway] HTTP на http://${HOST}:${PORT} (allowlist: ${ALLOW.length || "all"})`)))
  .catch(e => { console.error("[gateway] не удалось подключиться к Server.exe:", e); process.exit(1); });
