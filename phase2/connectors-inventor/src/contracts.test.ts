import assert from "node:assert/strict";
import test from "node:test";
import { engineeringJobSchema } from "./contracts.js";
import { digestJob } from "./job-store.js";
import { recipeApprovalCandidate } from "./product-recipe.js";

test("engineering job accepts bounded typed work and applies defaults", () => {
  const job = engineeringJobSchema.parse({
    version: "1",
    objective: "Set the wheel diameter and verify model health",
    product_path: "C:\\Catalog\\VKR-7.1\\fan.iam",
    mutations: [{ kind: "drive_dimension", name: "wheel_diameter", value: "500 mm" }],
    checks: [{ kind: "model_health" }],
  });

  assert.equal(job.version, "1");
  if (job.version !== "1") throw new Error("unexpected job version");
  assert.equal(job.save, "never");
  assert.deepEqual(job.outputs, []);
  assert.deepEqual(job.context, {});
});

test("engineering job rejects a write plan without acceptance checks", () => {
  const result = engineeringJobSchema.safeParse({
    version: "1",
    objective: "Change a dimension without checking the resulting model",
    product_path: "C:\\Catalog\\fan.ipt",
    mutations: [{ kind: "drive_dimension", name: "d0", value: "500 mm" }],
    checks: [],
  });
  assert.equal(result.success, false);
});

test("job digest is stable across object key order", () => {
  const first = engineeringJobSchema.parse({
    version: "1",
    objective: "Configure casing discharge and verify model health",
    product_path: "C:\\Catalog\\fan.ipt",
    mutations: [{ kind: "set_casing_discharge", angle: 90, hand: "right" }],
    checks: [{ kind: "model_health" }],
  });
  const second = engineeringJobSchema.parse({
    checks: [{ kind: "model_health" }],
    mutations: [{ hand: "right", angle: 90, kind: "set_casing_discharge" }],
    product_path: "C:\\Catalog\\fan.ipt",
    objective: "Configure casing discharge and verify model health",
    version: "1",
  });
  assert.equal(digestJob(first), digestJob(second));
});

test("Product Job v2 locks family formulas, release checks and chief approval", () => {
  const job = engineeringJobSchema.parse({
    version: "2",
    objective: "Create a new VKR size and build its checked production release",
    source: { mode: "existing", product_path: "C:\\Catalog\\VKR-7.1\\fan.iam" },
    recipe: recipeApprovalCandidate({
      family: "VKR",
      revision: "r1",
      variables: { wheel_diameter_mm: 710 },
      variable_limits: { wheel_diameter_mm: { min: 630, max: 800, unit: "mm" } },
      bindings: [{
        target: { kind: "component_parameter", occurrence: "Wheel:1", name: "D" },
        formula: { terms: { wheel_diameter_mm: 1 } },
        unit: "mm",
      }],
    }),
    checks: [{ kind: "model_health" }],
    release: {
      product_root: "C:\\Catalog\\VKR-7.1",
      dxf_dir: "C:\\Catalog\\VKR-7.1\\DXF",
      output_dir: "C:\\Catalog\\VKR-7.1\\release",
      product_code: "VKR-7.1",
    },
  });
  assert.equal(job.version, "2");
  if (job.version !== "2") throw new Error("unexpected job version");
  assert.equal(job.release.require_chief_approval, true);
  assert.equal(job.recipe.variables.wheel_diameter_mm, 710);
});
