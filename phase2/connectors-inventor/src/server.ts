#!/usr/bin/env node
// kvz-inventor-connector — MCP-коннектор для kvz-ai (worker подключает по stdio).
// Проксирует курированный набор vent/Inventor-тулзов на on-prem шлюз (phase2/gateway) по HTTP.
// read-only по умолчанию; write-тулзы регистрируются только при WRITE_ENABLED=1 (роль/approval в kvz-ai).
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { StdioServerTransport } from "@modelcontextprotocol/sdk/server/stdio.js";
import { z } from "zod";

const GATEWAY_URL = (process.env.GATEWAY_URL ?? "http://127.0.0.1:8737").replace(/\/$/, "");
const GATEWAY_TOKEN = process.env.GATEWAY_TOKEN ?? "";
const WRITE_ENABLED = process.env.WRITE_ENABLED === "1";

async function callGateway(name: string, args: Record<string, unknown>) {
  const r = await fetch(`${GATEWAY_URL}/tools/call`, {
    method: "POST",
    headers: {
      "content-type": "application/json",
      ...(GATEWAY_TOKEN ? { authorization: `Bearer ${GATEWAY_TOKEN}` } : {}),
    },
    body: JSON.stringify({ name, arguments: args }),
  });
  const text = await r.text();
  return { ok: r.ok, status: r.status, text };
}

function tool(server: McpServer, name: string, description: string,
              shape: z.ZodRawShape, wire: string) {
  server.tool(name, description, shape, async (args: Record<string, unknown>) => {
    const res = await callGateway(wire, args);
    return {
      isError: !res.ok,
      content: [{ type: "text" as const, text: res.text }],
    };
  });
}

const server = new McpServer({ name: "kvz-inventor", version: "0.1.0" });

// ---- READ (всегда доступны) ----
tool(server, "inventor_list_products",
  "Список изделий (изделия) в каталоге завода: имя, top-сборка (.iam), детали. Read-only.",
  { catalog_root: z.string().optional() }, "inventor_vent_list_products");

tool(server, "inventor_open_product",
  "Открыть изделие (.iam/.ipt) по абсолютному пути и сделать активным.",
  { path: z.string() }, "inventor_vent_open_product");

tool(server, "inventor_check_part",
  "Проверка активной листовой детали перед резкой: листовая?/flat pattern/толщина/габарит. Read-only.",
  {}, "inventor_vent_check_part");

tool(server, "inventor_bom_report",
  "Спецификация активной сборки: материал/толщина/кол-во/габарит развёртки + итоги по толщине. Read-only.",
  {}, "inventor_vent_bom_report");

// ---- WRITE (только при WRITE_ENABLED — роль конструктора + approval-gate kvz-ai) ----
if (WRITE_ENABLED) {
  tool(server, "inventor_new_product",
    "Создать новое изделие клоном папки-шаблона (Pack-and-Go-подход). Args: template_dir, new_name.",
    { template_dir: z.string(), new_name: z.string(), dest_root: z.string().optional() },
    "inventor_vent_new_product");

  tool(server, "inventor_set_parameter",
    "Задать параметр активной детали (напр. диаметр колеса). Args: name, value ('500 mm').",
    { name: z.string(), value: z.string() }, "inventor_set_parameter");

  tool(server, "inventor_set_casing_discharge",
    "Разворот корпуса: angle (0/45/.../315), hand ('right'/'left'). Пересчёт + новый габарит.",
    { angle: z.number(), hand: z.string().optional(), param_name: z.string().optional() },
    "inventor_vent_set_casing_discharge");

  tool(server, "inventor_make_gabarit",
    "Габаритка активной модели → PDF (и DXF). Args: output_path, scale?, export_dxf?, template?.",
    { output_path: z.string(), scale: z.number().optional(),
      export_dxf: z.boolean().optional(), template: z.string().optional() },
    "inventor_vent_make_gabarit");

  tool(server, "inventor_batch_flat_dxf",
    "Пакетная выгрузка flat-pattern DXF деталей сборки/папки в output_dir.",
    { output_dir: z.string(), parts_dir: z.string().optional() },
    "inventor_vent_batch_flat_dxf");
}

const transport = new StdioServerTransport();
await server.connect(transport);
console.error(`[kvz-inventor] запущен; write=${WRITE_ENABLED ? "on" : "off"}; gateway=${GATEWAY_URL}`);
