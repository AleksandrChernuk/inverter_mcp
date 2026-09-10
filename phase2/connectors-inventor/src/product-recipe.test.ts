import assert from "node:assert/strict";
import path from "node:path";
import test from "node:test";
import {
  constructionStepSchema,
  constructionToolCall,
  evaluateLinearFormula,
  familyRecipeSchema,
  recipeApprovalCandidate,
  recipeTemplateDigest,
  requiredConstructionTools,
  resolveRecipeBindings,
} from "./product-recipe.js";

test("family recipe resolves explicit linear engineering formulas", () => {
  const recipe = familyRecipeSchema.parse(recipeApprovalCandidate({
    family: "VKR",
    revision: "factory-2026-09-10",
    variables: { wheel_diameter_mm: 710, casing_ratio: 1.18 },
    variable_limits: {
      wheel_diameter_mm: { min: 630, max: 800, unit: "mm" },
      casing_ratio: { min: 1, max: 1.5, unit: "" },
    },
    bindings: [
      {
        target: { kind: "component_parameter", occurrence: "Wheel:1", name: "D" },
        formula: { terms: { wheel_diameter_mm: 1 }, round_to: 0.1 },
        unit: "mm",
      },
      {
        target: { kind: "active_parameter", name: "CasingWidth" },
        formula: { constant: 12, terms: { wheel_diameter_mm: 1.18 }, round_to: 1 },
        unit: "mm",
      },
    ],
  }));

  assert.deepEqual(resolveRecipeBindings(recipe), [
    { kind: "set_component_parameter", occurrence: "Wheel:1", name: "D", value: "710 mm" },
    { kind: "drive_dimension", name: "CasingWidth", value: "850 mm" },
  ]);
});

test("approved recipe digest changes when an engineering coefficient changes", () => {
  const first = recipeApprovalCandidate({
    family: "VKR", revision: "r1", variables: { d: 710 },
    variable_limits: { d: { min: 600, max: 800, unit: "mm" } },
    bindings: [{
      target: { kind: "active_parameter", name: "D" },
      formula: { terms: { d: 1 } }, unit: "mm",
    }],
  });
  const changed = recipeApprovalCandidate({
    ...first,
    bindings: [{
      target: { kind: "active_parameter", name: "D" },
      formula: { terms: { d: 1.01 } }, unit: "mm",
    }],
  });
  assert.notEqual(recipeTemplateDigest(first), recipeTemplateDigest(changed));
});

test("formula evaluation refuses a missing recipe variable", () => {
  assert.throws(
    () => evaluateLinearFormula({ constant: 0, terms: { missing: 1 } }, {}),
    /recipe variable is missing/,
  );
});

test("construction recipe can reference a previous stable tool result", () => {
  const step = constructionStepSchema.parse({
    id: "extrude_body",
    kind: "extrude",
    sketch_name: { from_step: "base_sketch", path: "sketch_name" },
    distance_mm: 4,
  });
  const results = new Map<string, unknown>([["base_sketch", { sketch_name: "Sketch1" }]]);
  assert.deepEqual(constructionToolCall(step, results), {
    tool: "inventor_extrude",
    arguments: { sketchName: "Sketch1", distance: 4, operation: "join", direction: "positive" },
  });
});

test("construction dimensions and recoded paths resolve from approved job inputs", () => {
  const step = constructionStepSchema.parse({
    id: "wheel_profile",
    kind: "draw_circle",
    cx: 0,
    cy: 0,
    radius: { formula: { terms: { wheel_diameter_mm: 0.5 } } },
  });
  const context = {
    variables: { wheel_diameter_mm: 710 },
    jobPaths: {
      product_path: "C:\\Release\\VKR-7.1\\VKR-7.1.iam",
      product_root: "C:\\Release\\VKR-7.1",
      destination_root: "C:\\Release\\VKR-7.1",
      target_code: "VKR-7.1",
      product_code: "VKR-7.1",
    },
  };
  assert.deepEqual(constructionToolCall(step, new Map(), context), {
    tool: "inventor_draw_circle",
    arguments: { cx: 0, cy: 0, radius: 355 },
  });

  const save = constructionStepSchema.parse({
    id: "save_wheel",
    kind: "save_document",
    path: { job_root: "destination_root", relative_template: "parts/{target_code}-wheel.ipt" },
  });
  assert.equal(
    constructionToolCall(save, new Map(), context).arguments.path,
    path.win32.resolve("C:\\Release\\VKR-7.1", "parts/VKR-7.1-wheel.ipt"),
  );
});

test("construction formulas may use only variables with approved limits", () => {
  assert.throws(() => recipeApprovalCandidate({
    family: "VKR", revision: "r1", variables: { d: 710 },
    variable_limits: { d: { min: 630, max: 800, unit: "mm" } },
    construction_steps: [{
      id: "bad", kind: "draw_circle", cx: 0, cy: 0,
      radius: { formula: { terms: { unapproved: 0.5 } } },
    }],
  }), /construction formula term is not an approved variable/);
});

test("typed scratch-build steps expose only the required curated tools", () => {
  const steps = [
    constructionStepSchema.parse({
      id: "dimension", kind: "add_sketch_dimension", entity_id: "line-1",
      value_mm: { formula: { terms: { thickness: 1 } } },
    }),
    constructionStepSchema.parse({
      id: "imate", kind: "create_imate", name: "IF_MOTOR", type: "insert",
      selector: { kind: "cylindrical", radius_mm: 5 },
    }),
    constructionStepSchema.parse({
      id: "code", kind: "set_iproperty", property_set: "Design Tracking Properties",
      property: "Part Number", value_template: "{target_code}.001",
    }),
  ];
  assert.deepEqual(requiredConstructionTools(steps), [
    "inventor_add_sketch_dimension", "inventor_create_imate", "inventor_set_iproperty",
  ]);
});

test("unitless parameter updates do not append a length or angle token", () => {
  const call = constructionToolCall({
    id: "set-count", kind: "set_parameter", name: "BladeCount", value: 12, unit: "ul",
  }, new Map());
  assert.equal(call.tool, "inventor_set_parameter");
  assert.equal(call.arguments.value, "12");
});
