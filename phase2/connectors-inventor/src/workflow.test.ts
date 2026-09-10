import assert from "node:assert/strict";
import { mkdtemp } from "node:fs/promises";
import { tmpdir } from "node:os";
import path from "node:path";
import test from "node:test";
import type { GatewayClientLike, GatewayToolResult } from "./gateway-client.js";
import { AuditLog, JobStore } from "./job-store.js";
import { recipeApprovalCandidate } from "./product-recipe.js";
import { approveProductRelease, executeEngineeringJob, executeProductJob, requiredTools } from "./workflow.js";

class MockGateway implements GatewayClientLike {
  readonly calls: string[] = [];
  readonly callArguments: Array<{ name: string; arguments: Record<string, unknown> }> = [];
  constructor(
    private readonly tools: string[],
    private readonly overrides: Record<string, GatewayToolResult> = {},
  ) {}

  async listTools(): Promise<string[]> { return this.tools; }

  async callTool(name: string, args: Record<string, unknown> = {}): Promise<GatewayToolResult> {
    this.calls.push(name);
    this.callArguments.push({ name, arguments: args });
    if (this.overrides[name]) return this.overrides[name];
    if (name === "inventor_vent_execute_plan") {
      return { ok: true, status: 200, tool: name, payload: { pass: true, committed: true, checks: [] } };
    }
    if (name === "inventor_vent_build_release") {
      return { ok: true, status: 200, tool: name, payload: { pass: true, release_digest: "a".repeat(64), artifacts: {} } };
    }
    if (name === "inventor_vent_save_product") {
      return { ok: true, status: 200, tool: name, payload: { pass: true, saved: true, saved_document_count: 2 } };
    }
    return { ok: true, status: 200, tool: name, payload: { tool: name, ok: true } };
  }
}

async function fixture(save: "never" | "after_checks" = "after_checks") {
  const root = await mkdtemp(path.join(tmpdir(), "kvz-inventor-test-"));
  const store = new JobStore(path.join(root, "jobs"));
  const audit = new AuditLog(path.join(root, "audit.jsonl"));
  const record = await store.prepare({
    version: "1",
    objective: "Set the wheel diameter and verify the resulting model",
    product_path: "C:\\Catalog\\fan.iam",
    mutations: [{ kind: "set_component_parameter", occurrence: "Wheel:1", name: "d0", value: "500 mm" }],
    checks: [{ kind: "constraints_healthy" }, { kind: "no_interference" }],
    save,
  });
  return { store, audit, record };
}

test("successful job follows preflight, atomic execution, save and final evidence", async () => {
  const { store, audit, record } = await fixture();
  const client = new MockGateway(requiredTools(record.spec));
  const result = await executeEngineeringJob({
    record,
    approvedDigest: record.digest,
    client,
    store,
    audit,
  });

  assert.equal(result.status, "succeeded");
  assert.deepEqual(client.calls, [
    "inventor_health",
    "inventor_vent_open_product",
    "inventor_get_document_info",
    "inventor_get_mass_properties",
    "inventor_vent_execute_plan",
    "inventor_save_document",
    "inventor_get_mass_properties",
  ]);
});

test("failed acceptance never saves", async () => {
  const { store, audit, record } = await fixture();
  const client = new MockGateway(requiredTools(record.spec), {
    inventor_vent_execute_plan: {
      ok: true,
      status: 200,
      tool: "inventor_vent_execute_plan",
      payload: { pass: false, committed: false, rolled_back: true },
    },
  });
  const result = await executeEngineeringJob({
    record,
    approvedDigest: record.digest,
    client,
    store,
    audit,
  });

  assert.equal(result.status, "failed");
  assert.equal(client.calls.includes("inventor_save_document"), false);
});

test("transport loss during atomic mutation is uncertain and not retried", async () => {
  const { store, audit, record } = await fixture();
  const client = new MockGateway(requiredTools(record.spec), {
    inventor_vent_execute_plan: {
      ok: false,
      status: 503,
      tool: "inventor_vent_execute_plan",
      uncertain: true,
      error: { code: "BACKEND_UNAVAILABLE", message: "pipe closed during tool call" },
    },
  });
  const result = await executeEngineeringJob({
    record,
    approvedDigest: record.digest,
    client,
    store,
    audit,
  });

  assert.equal(result.status, "uncertain");
  assert.equal(client.calls.filter((name) => name === "inventor_vent_execute_plan").length, 1);
});

test("wrong approval digest is rejected without poisoning the prepared job", async () => {
  const { store, audit, record } = await fixture();
  const client = new MockGateway(requiredTools(record.spec));
  await assert.rejects(
    executeEngineeringJob({
      record,
      approvedDigest: "0".repeat(64),
      client,
      store,
      audit,
    }),
    /approved digest does not match/,
  );
  assert.equal((await store.get(record.id)).status, "prepared");
  assert.deepEqual(client.calls, []);
});

test("Product Job v2 stops at hashed chief-engineer approval and publishes only after approval", async () => {
  const root = await mkdtemp(path.join(tmpdir(), "kvz-product-test-"));
  const store = new JobStore(path.join(root, "jobs"));
  const audit = new AuditLog(path.join(root, "audit.jsonl"));
  const record = await store.prepare({
    version: "2",
    objective: "Configure a complete fan product and create release evidence",
    source: { mode: "existing", product_path: "C:\\Catalog\\fan.iam" },
    recipe: recipeApprovalCandidate({
      family: "VKR", revision: "r1", variables: { diameter: 710 },
      variable_limits: { diameter: { min: 630, max: 800, unit: "mm" } },
      bindings: [{
        target: { kind: "component_parameter", occurrence: "Wheel:1", name: "D" },
        formula: { terms: { diameter: 1 } }, unit: "mm",
      }],
    }),
    checks: [{ kind: "model_health" }, { kind: "no_interference" }],
    mounting_checks: [{
      occurrence: "MotorPlate:1", plane: "xy", hole_count: 4,
      bolt_circle_diameter_mm: 200, hole_diameter_mm: 14, center_mm: [0, 0, 0],
    }],
    release: {
      product_root: "C:\\Catalog", dxf_dir: "C:\\Catalog\\DXF",
      output_dir: "C:\\Catalog\\release", product_code: "VKR-7.1",
    },
  });
  const tools = [...requiredTools(record.spec), "inventor_vent_publish_release"];
  const client = new MockGateway(tools);
  const generated = await executeProductJob({ record, approvedDigest: record.digest, client, store, audit });
  assert.equal(generated.status, "awaiting_release_approval");
  assert.equal(generated.release_digest, "a".repeat(64));
  assert.equal(client.calls.includes("inventor_vent_publish_release"), false);
  assert.equal(client.calls.includes("inventor_vent_check_mounting_pattern"), false);
  assert.equal(client.calls.includes("inventor_vent_save_product"), true);
  const executeCall = client.callArguments.find((call) => call.name === "inventor_vent_execute_plan");
  const executeChecks = executeCall?.arguments.checks as Array<Record<string, unknown>>;
  assert.equal(executeChecks.at(-1)?.kind, "mounting_pattern");
  assert.equal(executeChecks.at(-1)?.occurrence, "MotorPlate:1");
  assert.equal(executeChecks.at(-1)?.bolt_circle_diameter_mm, 200);

  const released = await approveProductRelease({
    record: generated, releaseDigest: "a".repeat(64), approvedBy: "Chief Engineer",
    client, store, audit,
  });
  assert.equal(released.status, "released");
  assert.equal(released.approved_by, "Chief Engineer");
  assert.equal(client.calls.filter((name) => name === "inventor_vent_publish_release").length, 1);
});

test("Product Job v2 never starts clone/recode when its dry-run reports blockers", async () => {
  const root = await mkdtemp(path.join(tmpdir(), "kvz-clone-test-"));
  const store = new JobStore(path.join(root, "jobs"));
  const audit = new AuditLog(path.join(root, "audit.jsonl"));
  const record = await store.prepare({
    version: "2",
    objective: "Clone a fan product only after a clean deterministic preflight",
    source: {
      mode: "clone_recode", source_root: "C:\\Templates\\VKR-6.3",
      destination_root: "D:\\Release\\VKR-7.1", top_document: "C:\\Templates\\VKR-6.3\\VKR-6.3.iam",
      source_code: "VKR-6.3", target_code: "VKR-7.1",
    },
    recipe: recipeApprovalCandidate({
      family: "VKR", revision: "r1", variables: { d: 710 },
      variable_limits: { d: { min: 630, max: 800, unit: "mm" } },
      bindings: [{
        target: { kind: "component_parameter", occurrence: "Wheel:1", name: "D" },
        formula: { terms: { d: 1 } }, unit: "mm",
      }],
    }),
    checks: [{ kind: "model_health" }],
    release: {
      product_root: "D:\\Release\\VKR-7.1", dxf_dir: "D:\\Release\\VKR-7.1\\DXF",
      output_dir: "D:\\Release\\VKR-7.1\\release", product_code: "VKR-7.1",
    },
  });
  const client = new MockGateway(requiredTools(record.spec), {
    inventor_vent_clone_recode_product: {
      ok: true, status: 200, tool: "inventor_vent_clone_recode_product",
      payload: { pass: false, source_dirty_documents: ["C:\\Templates\\VKR-6.3\\VKR-6.3.iam"] },
    },
  });
  const result = await executeProductJob({ record, approvedDigest: record.digest, client, store, audit });
  assert.equal(result.status, "failed");
  assert.equal(client.calls.filter((name) => name === "inventor_vent_clone_recode_product").length, 1);
});

test("Product Job v2 stops before export when dependent-document save verification fails", async () => {
  const root = await mkdtemp(path.join(tmpdir(), "kvz-save-product-test-"));
  const store = new JobStore(path.join(root, "jobs"));
  const audit = new AuditLog(path.join(root, "audit.jsonl"));
  const record = await store.prepare({
    version: "2",
    objective: "Save every changed referenced part before producing release artifacts",
    source: { mode: "existing", product_path: "C:\\Catalog\\fan.iam" },
    recipe: recipeApprovalCandidate({
      family: "VKR", revision: "r-save", variables: { d: 710 },
      variable_limits: { d: { min: 630, max: 800, unit: "mm" } },
      bindings: [{
        target: { kind: "component_parameter", occurrence: "Wheel:1", name: "D" },
        formula: { terms: { d: 1 } }, unit: "mm",
      }],
    }),
    checks: [{ kind: "model_health" }],
    release: {
      product_root: "C:\\Catalog", dxf_dir: "C:\\Catalog\\DXF",
      output_dir: "C:\\Catalog\\release", product_code: "VKR-7.1",
    },
  });
  const client = new MockGateway(requiredTools(record.spec), {
    inventor_vent_save_product: {
      ok: true, status: 200, tool: "inventor_vent_save_product",
      payload: { pass: false, saved: false, blocked_documents: ["C:\\Library\\bolt.ipt"] },
    },
  });
  const result = await executeProductJob({ record, approvedDigest: record.digest, client, store, audit });
  assert.equal(result.status, "failed");
  assert.equal(client.calls.includes("inventor_vent_build_release"), false);
});
