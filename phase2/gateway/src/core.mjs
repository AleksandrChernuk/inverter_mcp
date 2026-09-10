import { timingSafeEqual } from "node:crypto";
import { appendFile, mkdir } from "node:fs/promises";
import path from "node:path";

export const DEFAULT_TOOL_ALLOWLIST = Object.freeze([
  "inventor_health",
  "inventor_get_document_info",
  "inventor_get_mass_properties",
  "inventor_get_assembly_bom",
  "inventor_list_constraints",
  "inventor_check_interference",
  "inventor_measure_min_distance",
  "inventor_save_document",
  "inventor_new_part",
  "inventor_new_assembly",
  "inventor_open_document",
  "inventor_close_document",
  "inventor_set_material",
  "inventor_create_parameter",
  "inventor_set_parameter",
  "inventor_set_iproperty",
  "inventor_create_sketch",
  "inventor_project_geometry",
  "inventor_draw_line",
  "inventor_draw_circle",
  "inventor_draw_rectangle",
  "inventor_draw_arc",
  "inventor_add_sketch_dimension",
  "inventor_add_sketch_constraint",
  "inventor_close_sketch",
  "inventor_create_work_plane",
  "inventor_create_work_axis",
  "inventor_extrude",
  "inventor_revolve",
  "inventor_fillet",
  "inventor_chamfer",
  "inventor_hole",
  "inventor_circular_pattern",
  "inventor_rectangular_pattern",
  "inventor_create_imate",
  "inventor_place_occurrence",
  "inventor_add_constraint",
  "inventor_capture_view",
  "inventor_set_view_orientation",
  "inventor_vent_list_products",
  "inventor_vent_open_product",
  "inventor_vent_save_product",
  "inventor_vent_check_part",
  "inventor_vent_check_mounting_pattern",
  "inventor_vent_bom_report",
  "inventor_vent_inspect_model",
  "inventor_vent_inspect_sketch",
  "inventor_vent_execute_plan",
  "inventor_vent_clone_recode_product",
  "inventor_vent_make_gabarit",
  "inventor_vent_batch_flat_dxf",
  "inventor_vent_batch_pdf_drawings",
  "inventor_vent_build_release",
  "inventor_vent_publish_release",
]);

export function parseAllowlist(raw) {
  const configured = String(raw ?? "").split(",").map((item) => item.trim()).filter(Boolean);
  return new Set(configured.length ? configured : DEFAULT_TOOL_ALLOWLIST);
}

export function constantTimeTokenMatch(expected, authorization) {
  if (!expected) return true;
  const actual = String(authorization ?? "").startsWith("Bearer ")
    ? String(authorization).slice("Bearer ".length)
    : "";
  const left = Buffer.from(expected);
  const right = Buffer.from(actual);
  return left.length === right.length && timingSafeEqual(left, right);
}

function tryJson(text) {
  try { return JSON.parse(text); } catch { return text; }
}

export function normalizeMcpResult(result) {
  const text = result?.content?.find?.((item) => typeof item?.text === "string")?.text;
  const payload = text === undefined ? undefined : tryJson(text);
  const nestedFailure = payload && typeof payload === "object" && payload.ok === false;
  return {
    ok: result?.isError !== true && !nestedFailure,
    result,
    tool_payload: payload,
  };
}

export async function readJson(req, maxBytes = 1_048_576) {
  const chunks = [];
  let size = 0;
  for await (const chunk of req) {
    size += chunk.length;
    if (size > maxBytes) {
      const error = new Error(`request body exceeds ${maxBytes} bytes`);
      error.code = "BODY_TOO_LARGE";
      throw error;
    }
    chunks.push(chunk);
  }
  if (chunks.length === 0) return {};
  try {
    return JSON.parse(Buffer.concat(chunks).toString("utf8"));
  } catch {
    const error = new Error("request body must be valid JSON");
    error.code = "INVALID_JSON";
    throw error;
  }
}

export class FixedWindowRateLimiter {
  constructor(limitPerMinute, now = () => Date.now()) {
    this.limit = limitPerMinute;
    this.now = now;
    this.windows = new Map();
  }

  accept(key) {
    if (this.limit <= 0) return true;
    const minute = Math.floor(this.now() / 60_000);
    const current = this.windows.get(key);
    if (!current || current.minute !== minute) {
      this.windows.set(key, { minute, count: 1 });
      this.prune(minute);
      return true;
    }
    current.count += 1;
    return current.count <= this.limit;
  }

  prune(currentMinute) {
    for (const [key, value] of this.windows) {
      if (value.minute < currentMinute - 1) this.windows.delete(key);
    }
  }
}

export function createAuditLogger(filePath) {
  return async (event) => {
    const line = `${JSON.stringify({ at: new Date().toISOString(), ...event })}\n`;
    console.error(`[gateway-audit] ${line.trim()}`);
    if (!filePath) return;
    await mkdir(path.dirname(filePath), { recursive: true });
    await appendFile(filePath, line, { encoding: "utf8", mode: 0o600 });
  };
}
