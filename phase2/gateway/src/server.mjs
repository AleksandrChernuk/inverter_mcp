#!/usr/bin/env node
import { randomUUID } from "node:crypto";
import { execFile } from "node:child_process";
import { existsSync } from "node:fs";
import http from "node:http";
import path from "node:path";
import { promisify } from "node:util";
import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { StdioClientTransport } from "@modelcontextprotocol/sdk/client/stdio.js";
import {
  constantTimeTokenMatch,
  createAuditLogger,
  FixedWindowRateLimiter,
  normalizeMcpResult,
  parseAllowlist,
  readJson,
} from "./core.mjs";

const SERVER_EXE = process.env.SERVER_EXE;
const SERVER_ARGS = (process.env.SERVER_ARGS ?? "").trim().length
  ? process.env.SERVER_ARGS.trim().split(/\s+/)
  : [];
const TOKEN = process.env.GATEWAY_TOKEN ?? "";
const HOST = process.env.GATEWAY_HOST ?? "127.0.0.1";
const PORT = Number(process.env.GATEWAY_PORT ?? 8737);
const REQUEST_BODY_MAX_BYTES = Number(process.env.REQUEST_BODY_MAX_BYTES ?? 1_048_576);
const RATE_LIMIT_PER_MINUTE = Number(process.env.RATE_LIMIT_PER_MINUTE ?? 120);
const ALLOW = parseAllowlist(process.env.TOOL_ALLOWLIST);
const audit = createAuditLogger(process.env.AUDIT_LOG_PATH);
const limiter = new FixedWindowRateLimiter(RATE_LIMIT_PER_MINUTE);
const executeFile = promisify(execFile);
const RELEASE_PYTHON = process.env.RELEASE_PYTHON ?? "";
const RELEASE_SCRIPT = process.env.RELEASE_SCRIPT ?? "";
const RELEASE_WEBHOOK_URL = process.env.RELEASE_WEBHOOK_URL ?? "";
const RELEASE_WEBHOOK_BEARER = process.env.RELEASE_WEBHOOK_BEARER ?? "";
const EXPORT_ROOT = process.env.BIMWRIGHT_INVENTOR_EXPORT_ROOT
  ? path.resolve(process.env.BIMWRIGHT_INVENTOR_EXPORT_ROOT) : "";

if (!SERVER_EXE) throw new Error("SERVER_EXE is required");
if (process.env.NODE_ENV === "production" && !TOKEN) {
  throw new Error("GATEWAY_TOKEN is required when NODE_ENV=production");
}
if (!TOKEN) console.error("[warn] GATEWAY_TOKEN is empty; unauthenticated development mode");
if (!Number.isInteger(PORT) || PORT < 1 || PORT > 65535) throw new Error("GATEWAY_PORT is invalid");
if (!Number.isInteger(REQUEST_BODY_MAX_BYTES) || REQUEST_BODY_MAX_BYTES < 1024) throw new Error("REQUEST_BODY_MAX_BYTES is invalid");
if (!Number.isInteger(RATE_LIMIT_PER_MINUTE) || RATE_LIMIT_PER_MINUTE < 0) throw new Error("RATE_LIMIT_PER_MINUTE is invalid");

const localTools = new Map();
if (RELEASE_PYTHON && RELEASE_SCRIPT && existsSync(RELEASE_SCRIPT) && EXPORT_ROOT) {
  localTools.set("inventor_vent_build_release", {
    name: "inventor_vent_build_release",
    description: "Run deterministic offline DXF/folder/PDF checks and build hashed CSV/XLSX release evidence.",
  });
}
if (RELEASE_WEBHOOK_URL) {
  localTools.set("inventor_vent_publish_release", {
    name: "inventor_vent_publish_release",
    description: "Publish an already approved release digest to the configured workshop webhook.",
  });
}

let client;
let transport;
let connecting;

async function disconnect(reason) {
  const staleClient = client;
  client = undefined;
  transport = undefined;
  if (staleClient) {
    try { await staleClient.close(); } catch { /* process may already be gone */ }
  }
  await audit({ event: "backend_disconnected", reason });
}

async function ensureClient() {
  if (client) return client;
  if (connecting) return connecting;

  connecting = (async () => {
    const nextTransport = new StdioClientTransport({ command: SERVER_EXE, args: SERVER_ARGS });
    const nextClient = new Client({ name: "vent-gateway", version: "0.3.0" }, { capabilities: {} });
    nextTransport.onclose = () => {
      if (client === nextClient) {
        client = undefined;
        transport = undefined;
        void audit({ event: "backend_closed" });
      }
    };
    await nextClient.connect(nextTransport);
    const { tools } = await nextClient.listTools();
    client = nextClient;
    transport = nextTransport;
    await audit({ event: "backend_connected", tool_count: tools.length });
    return nextClient;
  })();

  try {
    return await connecting;
  } finally {
    connecting = undefined;
  }
}

function send(res, code, object) {
  const body = JSON.stringify(object);
  res.writeHead(code, {
    "content-type": "application/json; charset=utf-8",
    "cache-control": "no-store",
    "x-content-type-options": "nosniff",
  });
  res.end(body);
}

function clientKey(req) {
  return req.socket.remoteAddress ?? "unknown";
}

function requireText(args, key) {
  const value = args?.[key];
  if (typeof value !== "string" || !value.trim()) {
    const error = new Error(`${key} is required`);
    error.code = "INVALID_ARGUMENT";
    throw error;
  }
  return value;
}

function allowedPath(args, key, required = true) {
  const value = args?.[key];
  if ((value === undefined || value === null || value === "") && !required) return undefined;
  const resolved = path.resolve(requireText(args, key));
  const relative = path.relative(EXPORT_ROOT, resolved);
  if (!EXPORT_ROOT || relative.startsWith("..") || path.isAbsolute(relative)) {
    const error = new Error(`${key} must be under BIMWRIGHT_INVENTOR_EXPORT_ROOT`);
    error.code = "INVALID_ARGUMENT";
    throw error;
  }
  return resolved;
}

function localMcpResult(payload, isError = false) {
  const result = { content: [{ type: "text", text: JSON.stringify(payload) }], ...(isError ? { isError: true } : {}) };
  return normalizeMcpResult(result);
}

async function runBuildRelease(args) {
  const productRoot = allowedPath(args, "product_root");
  const dxfDir = allowedPath(args, "dxf_dir");
  const outputDir = allowedPath(args, "output_dir");
  const pdfDir = allowedPath(args, "pdf_dir", false);
  const productCode = requireText(args, "product_code");
  const minimumPdfCount = args.minimum_pdf_count ?? 0;
  if (!Number.isInteger(minimumPdfCount) || minimumPdfCount < 0 || minimumPdfCount > 10000) {
    const error = new Error("minimum_pdf_count must be an integer from 0 to 10000");
    error.code = "INVALID_ARGUMENT";
    throw error;
  }
  const commandArgs = [
    RELEASE_SCRIPT,
    "--product-root", productRoot,
    "--dxf-dir", dxfDir,
    "--output-dir", outputDir,
    "--product-code", productCode,
    "--minimum-pdf-count", String(minimumPdfCount),
  ];
  if (pdfDir) commandArgs.push("--pdf-dir", pdfDir);
  if (typeof args.required_name_pattern === "string" && args.required_name_pattern) {
    commandArgs.push("--required-name-pattern", args.required_name_pattern);
  }
  if (args.require_clone_manifest === true) commandArgs.push("--require-clone-manifest");
  if (args.require_dxf_manifest === true) commandArgs.push("--require-dxf-manifest");
  try {
    const { stdout } = await executeFile(RELEASE_PYTHON, commandArgs, {
      timeout: 600_000,
      maxBuffer: 10 * 1024 * 1024,
      windowsHide: true,
    });
    return JSON.parse(stdout.trim());
  } catch (error) {
    const stdout = typeof error?.stdout === "string" ? error.stdout.trim() : "";
    if (stdout) {
      try {
        const parsed = JSON.parse(stdout);
        if (error?.code === 2 || parsed?.state === "blocked") return parsed;
      } catch { /* use process error below */ }
    }
    const wrapped = new Error(`release worker failed: ${String(error?.stderr || error?.message || error)}`);
    wrapped.code = "RELEASE_WORKER_FAILED";
    throw wrapped;
  }
}

async function runPublishRelease(args) {
  const jobId = requireText(args, "job_id");
  const digest = requireText(args, "release_digest");
  const approvedBy = requireText(args, "approved_by");
  const productCode = requireText(args, "product_code");
  if (!/^[a-f0-9]{64}$/.test(digest)) {
    const error = new Error("release_digest must be SHA-256 hex");
    error.code = "INVALID_ARGUMENT";
    throw error;
  }
  const response = await fetch(RELEASE_WEBHOOK_URL, {
    method: "POST",
    headers: {
      "content-type": "application/json",
      ...(RELEASE_WEBHOOK_BEARER ? { authorization: `Bearer ${RELEASE_WEBHOOK_BEARER}` } : {}),
      "idempotency-key": `${jobId}:${digest}`,
    },
    body: JSON.stringify({
      event: "inventor_product_released",
      text: `Виріб ${productCode} перевірено та дозволено у виробництво. Головний конструктор: ${approvedBy}.`,
      job_id: jobId,
      product_code: productCode,
      release_digest: digest,
      approved_by: approvedBy,
      release: args.release,
    }),
    signal: AbortSignal.timeout(15_000),
  });
  if (!response.ok) {
    const error = new Error(`release webhook returned HTTP ${response.status}`);
    error.code = "RELEASE_WEBHOOK_FAILED";
    throw error;
  }
  return { published: true, status: response.status, job_id: jobId, release_digest: digest };
}

async function callLocalTool(name, args) {
  if (name === "inventor_vent_build_release") return runBuildRelease(args);
  if (name === "inventor_vent_publish_release") return runPublishRelease(args);
  throw new Error(`unknown local tool: ${name}`);
}

const server = http.createServer(async (req, res) => {
  const requestId = randomUUID();
  const startedAt = Date.now();
  let toolName;
  try {
    const url = new URL(req.url ?? "/", `http://${req.headers.host ?? "localhost"}`);
    if (url.pathname === "/health") {
      if (!client && !connecting) void ensureClient().catch(() => undefined);
      return send(res, 200, {
        ok: true,
        connected: Boolean(client),
        connecting: Boolean(connecting),
        allowlist_count: ALLOW.size,
        local_tool_count: [...localTools.keys()].filter((name) => ALLOW.has(name)).length,
      });
    }

    if (!limiter.accept(clientKey(req))) {
      await audit({ request_id: requestId, event: "rate_limited", remote: clientKey(req) });
      return send(res, 429, { ok: false, error: { code: "RATE_LIMIT", message: "too many requests" } });
    }
    if (!constantTimeTokenMatch(TOKEN, req.headers.authorization)) {
      await audit({ request_id: requestId, event: "unauthorized", remote: clientKey(req) });
      return send(res, 401, { ok: false, error: { code: "UNAUTHORIZED", message: "unauthorized" } });
    }

    if (req.method === "GET" && url.pathname === "/tools/list") {
      const activeClient = await ensureClient();
      const { tools } = await activeClient.listTools();
      const visible = tools.filter((tool) => ALLOW.has(tool.name) && !localTools.has(tool.name));
      for (const tool of localTools.values()) if (ALLOW.has(tool.name)) visible.push(tool);
      return send(res, 200, { ok: true, tools: visible });
    }

    if (req.method === "POST" && url.pathname === "/tools/call") {
      const body = await readJson(req, REQUEST_BODY_MAX_BYTES);
      toolName = body?.name;
      const args = body?.arguments ?? {};
      if (typeof toolName !== "string" || !toolName.length) {
        return send(res, 400, { ok: false, error: { code: "INVALID_ARGUMENT", message: "name is required" } });
      }
      if (!args || typeof args !== "object" || Array.isArray(args)) {
        return send(res, 400, { ok: false, error: { code: "INVALID_ARGUMENT", message: "arguments must be an object" } });
      }
      if (!ALLOW.has(toolName)) {
        await audit({ request_id: requestId, event: "tool_denied", tool: toolName, remote: clientKey(req) });
        return send(res, 403, { ok: false, error: { code: "TOOL_NOT_ALLOWED", message: `tool not allowed: ${toolName}` } });
      }

      let normalized;
      if (localTools.has(toolName)) {
        normalized = localMcpResult(await callLocalTool(toolName, args));
      } else {
        const activeClient = await ensureClient();
        normalized = normalizeMcpResult(await activeClient.callTool({ name: toolName, arguments: args }));
      }
      await audit({
        request_id: requestId,
        event: "tool_call",
        tool: toolName,
        argument_keys: Object.keys(args).sort(),
        ok: normalized.ok,
        duration_ms: Date.now() - startedAt,
        remote: clientKey(req),
      });
      return send(res, 200, normalized);
    }
    return send(res, 404, { ok: false, error: { code: "NOT_FOUND", message: "not found" } });
  } catch (error) {
    const code = error?.code === "BODY_TOO_LARGE" ? 413
      : ["INVALID_JSON", "INVALID_ARGUMENT"].includes(error?.code) ? 400
        : error?.code === "RELEASE_WEBHOOK_FAILED" ? 502
          : error?.code === "RELEASE_WORKER_FAILED" ? 500
            : 503;
    const errorCode = error?.code ?? "BACKEND_UNAVAILABLE";
    await audit({
      request_id: requestId,
      event: "request_failed",
      tool: toolName,
      code: errorCode,
      message: String(error?.message ?? error),
      duration_ms: Date.now() - startedAt,
      remote: clientKey(req),
    });
    if (code === 503 && !localTools.has(toolName)) await disconnect(String(error?.message ?? error));
    return send(res, code, {
      ok: false,
      error: { code: errorCode, message: String(error?.message ?? error) },
      uncertain: Boolean(toolName) && !["INVALID_ARGUMENT", "INVALID_JSON", "BODY_TOO_LARGE"].includes(errorCode),
    });
  }
});

server.listen(PORT, HOST, () => {
  console.error(`[gateway] HTTP http://${HOST}:${PORT}; allowlist=${ALLOW.size}; rate=${RATE_LIMIT_PER_MINUTE}/min`);
  void ensureClient().catch((error) => audit({ event: "initial_connect_failed", message: String(error?.message ?? error) }));
});

for (const signal of ["SIGINT", "SIGTERM"]) {
  process.on(signal, () => {
    server.close(() => process.exit(0));
    void disconnect(signal);
  });
}
