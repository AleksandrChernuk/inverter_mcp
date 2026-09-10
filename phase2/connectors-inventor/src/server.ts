#!/usr/bin/env node
import path from "node:path";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { StdioServerTransport } from "@modelcontextprotocol/sdk/server/stdio.js";
import { z } from "zod";
import { absoluteInventorPath, engineeringJobSchema, jobIdSchema } from "./contracts.js";
import { GatewayClient } from "./gateway-client.js";
import { AuditLog, defaultStateDirectory, JobStore } from "./job-store.js";
import { recipeApprovalCandidate, recipeTemplateDigest, verifyRecipeApproval } from "./product-recipe.js";
import { approveProductRelease, executeEngineeringJob, executeProductJob, summarizePlan } from "./workflow.js";

const GATEWAY_URL = (process.env.GATEWAY_URL ?? "http://127.0.0.1:8737").replace(/\/$/, "");
const GATEWAY_TOKEN = process.env.GATEWAY_TOKEN ?? "";
const WRITE_ENABLED = process.env.WRITE_ENABLED === "1";
const GATEWAY_TIMEOUT_MS = Number(process.env.GATEWAY_TIMEOUT_MS ?? 120_000);
const APPROVED_RECIPE_DIGESTS = new Set(
  (process.env.APPROVED_RECIPE_DIGESTS ?? "").split(",").map((value) => value.trim()).filter(Boolean),
);
const STATE_DIRECTORY = defaultStateDirectory();
const AUDIT_LOG_PATH = process.env.AUDIT_LOG_PATH
  ? path.resolve(process.env.AUDIT_LOG_PATH)
  : path.join(path.dirname(STATE_DIRECTORY), "audit.jsonl");

if (process.env.NODE_ENV === "production" && !GATEWAY_TOKEN) {
  throw new Error("GATEWAY_TOKEN is required when NODE_ENV=production");
}
if (!Number.isFinite(GATEWAY_TIMEOUT_MS) || GATEWAY_TIMEOUT_MS < 1_000 || GATEWAY_TIMEOUT_MS > 900_000) {
  throw new Error("GATEWAY_TIMEOUT_MS must be between 1000 and 900000");
}

const gateway = new GatewayClient({
  baseUrl: GATEWAY_URL,
  token: GATEWAY_TOKEN,
  timeoutMs: GATEWAY_TIMEOUT_MS,
});
const jobs = new JobStore(STATE_DIRECTORY);
const audit = new AuditLog(AUDIT_LOG_PATH);
const server = new McpServer({ name: "kvz-inventor", version: "0.3.0" });
const activeExecutions = new Map<string, Promise<unknown>>();

function json(value: unknown): string {
  return JSON.stringify(value, null, 2);
}

function errorResponse(error: unknown) {
  if (error instanceof z.ZodError) {
    return {
      isError: true,
      content: [{ type: "text" as const, text: json({
        ok: false,
        error: { code: "INVALID_ARGUMENT", message: "Input validation failed", issues: error.issues },
      }) }],
    };
  }
  return {
    isError: true,
    content: [{ type: "text" as const, text: json({
      ok: false,
      error: {
        code: error && typeof error === "object" && "code" in error
          ? String((error as { code: unknown }).code)
          : "CONNECTOR_ERROR",
        message: error instanceof Error ? error.message : String(error),
      },
    }) }],
  };
}

function register(
  name: string,
  description: string,
  shape: z.ZodRawShape,
  handler: (args: Record<string, unknown>) => Promise<unknown>,
) {
  server.tool(name, description, shape, async (args: Record<string, unknown>) => {
    try {
      const value = await handler(args);
      return { content: [{ type: "text" as const, text: json({ ok: true, data: value }) }] };
    } catch (error) {
      return errorResponse(error);
    }
  });
}

register(
  "inventor_connection_status",
  "Check the on-prem Inventor connection and discover the exact gateway tool inventory. Use first when diagnosing availability. Read-only; returns health, write-gate state, and tool names without exposing credentials.",
  {},
  async () => {
    const health = await gateway.callTool("inventor_health");
    const tools = await gateway.listTools();
    return {
      connected: health.ok,
      health: health.ok ? health.payload : health.error,
      write_enabled: WRITE_ENABLED,
      gateway_tools: tools,
      connector_version: "0.3.0",
      approved_recipe_count: APPROVED_RECIPE_DIGESTS.size,
    };
  },
);

register(
  "inventor_list_products",
  "List product folders and their top Inventor assemblies under the configured factory catalog. Use to resolve a product before inspection or job preparation. Read-only and server-side; catalogRoot is optional and must be an absolute path when supplied.",
  { catalogRoot: absoluteInventorPath.optional() },
  async ({ catalogRoot }) => {
    const result = await gateway.callTool("inventor_vent_list_products", { catalogRoot });
    if (!result.ok) throw Object.assign(new Error(result.error?.message), { result });
    return result.payload;
  },
);

register(
  "inventor_open_product",
  "Open an existing .ipt or .iam by absolute path and make it active for subsequent inspection. This changes only the Inventor session focus and does not save model data. Use paths returned by inventor_list_products.",
  { path: absoluteInventorPath },
  async ({ path: productPath }) => {
    const result = await gateway.callTool("inventor_vent_open_product", { path: productPath });
    if (!result.ok) throw new Error(result.error?.message);
    return result.payload;
  },
);

register(
  "inventor_inspect_model",
  "Inspect the active part build tree, sketch dimensions, feature health, and sheet-metal status. Use before preparing any dimension change and again after manual changes. Read-only; unhealthy_feature_count must be zero before release.",
  {},
  async () => {
    const result = await gateway.callTool("inventor_vent_inspect_model");
    if (!result.ok) throw new Error(result.error?.message);
    return result.payload;
  },
);

register(
  "inventor_inspect_sketch",
  "Inspect one named sketch of the active part: points, lines, arcs, circles and geometric-constraint counts. Use only after inventor_inspect_model identifies the sketch; this is evidence for selecting a stable driving dimension. Read-only.",
  { sketch: z.string().trim().min(1).max(256) },
  async ({ sketch }) => {
    const result = await gateway.callTool("inventor_vent_inspect_sketch", { sketch });
    if (!result.ok) throw new Error(result.error?.message);
    return result.payload;
  },
);

register(
  "inventor_inspect_assembly",
  "Run the standard assembly inspection battery on the active .iam: mass/bounds, occurrence tree with DOF, constraint health, and interference pairs. Use occurrence and constraint names from this result when preparing a job. Read-only and deterministic.",
  { max_rows: z.number().int().min(1).max(2000).default(500) },
  async ({ max_rows }) => {
    const calls: Array<[string, Record<string, unknown>]> = [
      ["inventor_get_mass_properties", {}],
      ["inventor_get_assembly_bom", { max_rows }],
      ["inventor_list_constraints", {}],
      ["inventor_check_interference", {}],
    ];
    const evidence: Record<string, unknown> = {};
    for (const [name, args] of calls) {
      const result = await gateway.callTool(name, args);
      if (!result.ok) throw new Error(`${name}: ${result.error?.message}`);
      evidence[name] = result.payload;
    }
    return evidence;
  },
);

register(
  "inventor_check_part",
  "Run the model-side pre-cut gate for the active sheet-metal part: sheet-metal type, flat pattern, thickness and flat bounds. Read-only. A pass here is required before DXF release, followed by deep dxf_tools contour analysis.",
  {},
  async () => {
    const result = await gateway.callTool("inventor_vent_check_part");
    if (!result.ok) throw new Error(result.error?.message);
    return result.payload;
  },
);

register(
  "inventor_recipe_template_digest",
  "Validate a family recipe draft and calculate the SHA-256 of its formulas, variable limits and typed CAD steps. This does not approve it. An administrator/chief engineer must place the digest in APPROVED_RECIPE_DIGESTS before Product Job v2 can run in production.",
  { recipe: z.record(z.unknown()) },
  async ({ recipe }) => {
    const parsed = recipeApprovalCandidate(recipe);
    return {
      family: parsed.family,
      revision: parsed.revision,
      recipe_template_digest: recipeTemplateDigest(parsed),
      approved_in_this_runtime: APPROVED_RECIPE_DIGESTS.has(recipeTemplateDigest(parsed)),
    };
  },
);

register(
  "inventor_prepare_job",
  "Validate and persist JobSpec v1 or full Product Job v2 without changing Inventor. V2 locks clone/recode, family formulas, bounded construction/assembly steps, checks, drawings, DXF/PDF and release rules. Returns an immutable SHA-256 digest; a human/kvz-ai approval must approve that exact digest before execution.",
  { job: engineeringJobSchema },
  async ({ job }) => {
    const parsedJob = engineeringJobSchema.parse(job);
    if (parsedJob.version === "2") {
      const registry = process.env.NODE_ENV === "production" ? APPROVED_RECIPE_DIGESTS : undefined;
      if (registry && registry.size === 0) {
        throw Object.assign(new Error("APPROVED_RECIPE_DIGESTS is required for Product Job v2 in production"), { code: "RECIPE_REGISTRY_MISSING" });
      }
      verifyRecipeApproval(parsedJob.recipe, registry);
    }
    const record = await jobs.prepare(parsedJob);
    await audit.append({
      job_id: record.id,
      digest: record.digest,
      status: record.status,
      stage: "prepared",
      actor: record.spec.context.actor,
      request_id: record.spec.context.request_id,
    });
    return { job_id: record.id, digest: record.digest, status: record.status, plan: summarizePlan(record.spec) };
  },
);

register(
  "inventor_job_status",
  "Read the persistent checkpoint and evidence ledger for a prepared or executed job. For v1 only succeeded is complete; for Product Job v2 only released is complete. Status awaiting_release_approval needs chief review, and uncertain always requires manual Inventor inspection.",
  { job_id: jobIdSchema },
  async ({ job_id }) => jobs.get(job_id as string),
);

if (WRITE_ENABLED) {
  register(
    "inventor_execute_job",
    "Execute one prepared JobSpec v1 or Product Job v2. Requires the approved SHA-256 digest so approval cannot drift. V2 runs clone/recode, typed construction and assembly recipe steps, formula bindings, model/motor checks, save/exports and release packaging. It stops at awaiting_release_approval; it never retries an uncertain write automatically.",
    {
      job_id: jobIdSchema,
      approved_digest: z.string().regex(/^[a-f0-9]{64}$/),
    },
    async ({ job_id, approved_digest }) => {
      const id = job_id as string;
      const existing = activeExecutions.get(id);
      if (existing) return existing;

      const execution = (async () => {
        const record = await jobs.get(id);
        const common = {
          record, approvedDigest: approved_digest as string,
          client: gateway, store: jobs, audit,
        };
        return record.spec.version === "2" ? executeProductJob(common) : executeEngineeringJob(common);
      })();
      activeExecutions.set(id, execution);
      try {
        return await execution;
      } finally {
        if (activeExecutions.get(id) === execution) activeExecutions.delete(id);
      }
    },
  );

  register(
    "inventor_approve_release",
    "Chief-engineer release gate for Product Job v2. Verifies the exact SHA-256 release digest generated from checked files, records approved_by, sends the configured workshop notification webhook, and only then marks the job released. A digest mismatch or unavailable notification changes nothing.",
    {
      job_id: jobIdSchema,
      release_digest: z.string().regex(/^[a-f0-9]{64}$/),
      approved_by: z.string().trim().min(2).max(256),
    },
    async ({ job_id, release_digest, approved_by }) => {
      const record = await jobs.get(job_id as string);
      return approveProductRelease({
        record,
        releaseDigest: release_digest as string,
        approvedBy: approved_by as string,
        client: gateway,
        store: jobs,
        audit,
      });
    },
  );
}

const transport = new StdioServerTransport();
await server.connect(transport);
console.error(`[kvz-inventor] started; write=${WRITE_ENABLED ? "on" : "off"}; gateway=${GATEWAY_URL}; state=${STATE_DIRECTORY}`);
