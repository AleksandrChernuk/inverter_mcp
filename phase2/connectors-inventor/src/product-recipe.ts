import { createHash } from "node:crypto";
import path from "node:path";
import { z } from "zod";

const name = z.string().trim().min(1).max(256);
const finite = z.number().finite();
const positive = finite.positive();

export const linearFormulaSchema = z.object({
  constant: finite.default(0),
  terms: z.record(name, finite).default({}),
  round_to: positive.optional(),
}).strict().superRefine((formula, context) => {
  if (formula.constant === 0 && Object.keys(formula.terms).length === 0) {
    context.addIssue({ code: z.ZodIssueCode.custom, message: "formula needs a constant or at least one term" });
  }
});

export type LinearFormula = z.infer<typeof linearFormulaSchema>;

export const parameterBindingSchema = z.object({
  target: z.discriminatedUnion("kind", [
    z.object({ kind: z.literal("active_parameter"), name }).strict(),
    z.object({ kind: z.literal("component_parameter"), occurrence: name, name }).strict(),
    z.object({ kind: z.literal("constraint"), name }).strict(),
  ]),
  formula: linearFormulaSchema,
  unit: z.enum(["mm", "deg", ""]),
}).strict();

const stringRefSchema = z.union([
  name,
  z.object({ from_step: name, path: name }).strict(),
]);

const scalarInputSchema = z.union([
  finite,
  z.object({ formula: linearFormulaSchema }).strict(),
]);
const positiveScalarInputSchema = z.union([
  positive,
  z.object({ formula: linearFormulaSchema }).strict(),
]);
const pathInputSchema = z.union([
  z.string().min(3).max(4096),
  z.object({ job_path: z.literal("product_path") }).strict(),
  z.object({
    job_root: z.enum(["destination_root", "product_root"]),
    relative_template: z.string().trim().min(1).max(2048),
  }).strict(),
]);
const point3Schema = z.tuple([scalarInputSchema, scalarInputSchema, scalarInputSchema]);

export const constructionStepSchema = z.union([
  z.object({ id: name, kind: z.literal("new_part"), template: z.string().max(4096).optional() }).strict(),
  z.object({ id: name, kind: z.literal("new_assembly"), template: z.string().max(4096).optional() }).strict(),
  z.object({ id: name, kind: z.literal("open_document"), path: pathInputSchema }).strict(),
  z.object({ id: name, kind: z.literal("save_document"), path: pathInputSchema.optional() }).strict(),
  z.object({ id: name, kind: z.literal("close_document"), save: z.boolean().default(false) }).strict(),
  z.object({ id: name, kind: z.literal("set_material"), material_name: name }).strict(),
  z.object({
    id: name, kind: z.literal("create_parameter"), name,
    value: scalarInputSchema, unit: z.enum(["mm", "deg", "ul"]),
  }).strict(),
  z.object({
    id: name, kind: z.literal("set_parameter"), name,
    value: scalarInputSchema, unit: z.enum(["mm", "deg", "ul"]),
  }).strict(),
  z.object({
    id: name, kind: z.literal("set_iproperty"),
    property_set: z.enum(["Design Tracking Properties", "Summary Information"]),
    property: z.enum(["Part Number", "Stock Number", "Description", "Title"]),
    value_template: z.string().max(2048),
  }).strict(),
  z.object({ id: name, kind: z.literal("create_sketch"), plane: name.default("XY") }).strict(),
  z.object({ id: name, kind: z.literal("project_geometry"), edge_ids: z.array(stringRefSchema).min(1).max(256) }).strict(),
  z.object({ id: name, kind: z.literal("draw_line"), x1: scalarInputSchema, y1: scalarInputSchema, x2: scalarInputSchema, y2: scalarInputSchema }).strict(),
  z.object({ id: name, kind: z.literal("draw_circle"), cx: scalarInputSchema, cy: scalarInputSchema, radius: positiveScalarInputSchema }).strict(),
  z.object({ id: name, kind: z.literal("draw_rectangle"), x1: scalarInputSchema, y1: scalarInputSchema, x2: scalarInputSchema, y2: scalarInputSchema }).strict(),
  z.object({ id: name, kind: z.literal("draw_arc"), cx: scalarInputSchema, cy: scalarInputSchema, radius: positiveScalarInputSchema, start_deg: scalarInputSchema, end_deg: scalarInputSchema }).strict(),
  z.object({
    id: name, kind: z.literal("add_sketch_dimension"),
    entity_id: stringRefSchema, value_mm: positiveScalarInputSchema,
  }).strict(),
  z.object({
    id: name, kind: z.literal("add_sketch_constraint"),
    type: z.enum(["coincident", "parallel", "perpendicular", "horizontal", "vertical", "tangent", "concentric", "equal", "collinear", "symmetric"]),
    entity_ids: z.array(stringRefSchema).min(1).max(16),
  }).strict(),
  z.object({ id: name, kind: z.literal("close_sketch"), sketch_name: stringRefSchema.optional() }).strict(),
  z.object({
    id: name, kind: z.literal("create_work_plane"),
    type: z.enum(["offset", "three_points", "tangent"]), refs: z.array(stringRefSchema).min(1).max(3),
    offset_mm: scalarInputSchema.optional(),
  }).strict(),
  z.object({
    id: name, kind: z.literal("create_work_axis"),
    type: z.enum(["two_points", "edge", "plane_intersection", "normal_to_face_through_point"]),
    refs: z.array(stringRefSchema).min(1).max(2),
  }).strict(),
  z.object({
    id: name, kind: z.literal("extrude"), sketch_name: stringRefSchema,
    distance_mm: positiveScalarInputSchema, operation: z.enum(["join", "cut", "intersect"]).default("join"),
    direction: z.enum(["positive", "negative", "symmetric"]).default("positive"),
  }).strict(),
  z.object({
    id: name, kind: z.literal("revolve"), sketch_name: stringRefSchema, axis_id: stringRefSchema,
    angle_deg: positiveScalarInputSchema, operation: z.enum(["join", "cut", "intersect"]).default("join"),
  }).strict(),
  z.object({ id: name, kind: z.literal("fillet"), edge_ids: z.array(stringRefSchema).min(1).max(128), radius_mm: positiveScalarInputSchema }).strict(),
  z.object({ id: name, kind: z.literal("chamfer"), edge_ids: z.array(stringRefSchema).min(1).max(128), distance_mm: positiveScalarInputSchema }).strict(),
  z.object({
    id: name, kind: z.literal("hole"),
    face: z.object({
      kind: z.literal("planar"), normal: z.enum(["+X", "-X", "+Y", "-Y", "+Z", "-Z"]),
      extreme: z.enum(["max", "min"]), near_mm: point3Schema.optional(),
    }).strict(),
    points_mm: z.array(point3Schema).min(1).max(256), diameter_mm: positiveScalarInputSchema,
    through: z.boolean().default(true), depth_mm: positiveScalarInputSchema.optional(),
  }).strict().superRefine((step, context) => {
    if (step.through === (step.depth_mm !== undefined)) {
      context.addIssue({ code: z.ZodIssueCode.custom, message: "use through=true or depth_mm, exclusively" });
    }
  }),
  z.object({
    id: name, kind: z.literal("circular_pattern"), feature_names: z.array(stringRefSchema).min(1).max(64),
    axis: stringRefSchema, count: z.number().int().min(2).max(1000), angle_deg: positiveScalarInputSchema.default(360),
  }).strict(),
  z.object({
    id: name, kind: z.literal("rectangular_pattern"), feature_names: z.array(stringRefSchema).min(1).max(64),
    dir1: stringRefSchema, count1: z.number().int().min(2).max(1000), spacing_mm1: positiveScalarInputSchema,
    dir2: stringRefSchema.optional(), count2: z.number().int().min(2).max(1000).optional(),
    spacing_mm2: positiveScalarInputSchema.optional(), natural_direction1: z.boolean().default(true),
    natural_direction2: z.boolean().default(true),
  }).strict().superRefine((step, context) => {
    const second = [step.dir2, step.count2, step.spacing_mm2].filter((item) => item !== undefined).length;
    if (second !== 0 && second !== 3) context.addIssue({
      code: z.ZodIssueCode.custom, message: "dir2, count2 and spacing_mm2 must be supplied together",
    });
  }),
  z.object({
    id: name, kind: z.literal("create_imate"), name, type: z.enum(["mate", "flush", "insert"]),
    selector: z.union([
      z.object({
        kind: z.literal("planar"), normal: z.enum(["+X", "-X", "+Y", "-Y", "+Z", "-Z"]),
        extreme: z.enum(["max", "min"]), near_mm: point3Schema.optional(),
        tolerance_deg: positiveScalarInputSchema.default(5),
      }).strict(),
      z.object({
        kind: z.literal("cylindrical"), radius_mm: positiveScalarInputSchema,
        axis: z.enum(["+X", "-X", "+Y", "-Y", "+Z", "-Z"]).optional(), near_mm: point3Schema.optional(),
        radius_tol_mm: positiveScalarInputSchema.default(0.01),
      }).strict(),
    ]),
    offset_mm: scalarInputSchema.default(0), insert_opposed: z.boolean().default(true),
    distance_mm: scalarInputSchema.default(0),
  }).strict(),
  z.object({
    id: name, kind: z.literal("place_occurrence"), path: pathInputSchema, grounded: z.boolean().default(false),
    position_mm: point3Schema.optional(), rotation_deg_xyz: point3Schema.optional(),
  }).strict(),
  z.object({
    id: name, kind: z.literal("add_constraint"), type: z.enum(["mate", "flush", "insert", "angle"]),
    a: z.object({ occurrence: stringRefSchema.optional(), ref: name }).strict(),
    b: z.object({ occurrence: stringRefSchema.optional(), ref: name }).strict(),
    offset_mm: scalarInputSchema.default(0), angle_deg: scalarInputSchema.optional(), insert_opposed: z.boolean().default(true),
  }).strict().superRefine((step, context) => {
    if (step.type === "angle" && step.angle_deg === undefined) {
      context.addIssue({ code: z.ZodIssueCode.custom, message: "angle_deg is required for an angle constraint" });
    }
  }),
]);

export const familyRecipeSchema = z.object({
  family: name,
  revision: name,
  approval_digest: z.string().regex(/^[a-f0-9]{64}$/),
  variables: z.record(name, finite),
  variable_limits: z.record(name, z.object({
    min: finite, max: finite, unit: z.enum(["mm", "deg", "", "count"]).default(""),
  }).strict()).default({}),
  bindings: z.array(parameterBindingSchema).max(256).default([]),
  construction_steps: z.array(constructionStepSchema).max(512).default([]),
}).strict().superRefine((recipe, context) => {
  const ids = new Set<string>();
  for (const [index, step] of recipe.construction_steps.entries()) {
    if (ids.has(step.id)) context.addIssue({
      code: z.ZodIssueCode.custom,
      path: ["construction_steps", index, "id"],
      message: `duplicate construction step id: ${step.id}`,
    });
    ids.add(step.id);
  }
  for (const [variable, value] of Object.entries(recipe.variables)) {
    const limit = recipe.variable_limits[variable];
    if (!limit) {
      context.addIssue({ code: z.ZodIssueCode.custom, path: ["variable_limits", variable], message: "every variable needs an approved limit" });
      continue;
    }
    if (limit.min > limit.max) {
      context.addIssue({ code: z.ZodIssueCode.custom, path: ["variable_limits", variable], message: "min must not exceed max" });
    } else if (value < limit.min || value > limit.max) {
      context.addIssue({ code: z.ZodIssueCode.custom, path: ["variables", variable], message: `value must be within ${limit.min}..${limit.max}` });
    }
  }
  for (const variable of Object.keys(recipe.variable_limits)) {
    if (!(variable in recipe.variables)) {
      context.addIssue({ code: z.ZodIssueCode.custom, path: ["variables", variable], message: "approved variable value is required" });
    }
  }
  for (const [index, binding] of recipe.bindings.entries()) {
    for (const variable of Object.keys(binding.formula.terms)) {
      if (!(variable in recipe.variable_limits)) {
        context.addIssue({ code: z.ZodIssueCode.custom, path: ["bindings", index, "formula", "terms", variable], message: "formula term is not an approved variable" });
      }
    }
  }
  const inspectConstructionFormula = (value: unknown, pathParts: Array<string | number>) => {
    if (Array.isArray(value)) {
      value.forEach((item, index) => inspectConstructionFormula(item, [...pathParts, index]));
      return;
    }
    if (!value || typeof value !== "object") return;
    const object = value as Record<string, unknown>;
    if (object.formula && typeof object.formula === "object" && !Array.isArray(object.formula)) {
      const terms = (object.formula as { terms?: unknown }).terms;
      if (terms && typeof terms === "object" && !Array.isArray(terms)) {
        for (const variable of Object.keys(terms as Record<string, unknown>)) {
          if (!(variable in recipe.variable_limits)) {
            context.addIssue({
              code: z.ZodIssueCode.custom,
              path: [...pathParts, "formula", "terms", variable],
              message: "construction formula term is not an approved variable",
            });
          }
        }
      }
    }
    for (const [key, item] of Object.entries(object)) {
      if (key !== "formula") inspectConstructionFormula(item, [...pathParts, key]);
    }
  };
  recipe.construction_steps.forEach((step, index) =>
    inspectConstructionFormula(step, ["construction_steps", index]));
});

export type FamilyRecipe = z.infer<typeof familyRecipeSchema>;
export type ConstructionStep = z.infer<typeof constructionStepSchema>;

function canonicalize(value: unknown): unknown {
  if (Array.isArray(value)) return value.map(canonicalize);
  if (value && typeof value === "object") {
    return Object.fromEntries(Object.entries(value as Record<string, unknown>)
      .sort(([left], [right]) => left.localeCompare(right))
      .map(([key, item]) => [key, canonicalize(item)]));
  }
  return value;
}

export function recipeTemplateDigest(recipe: Omit<FamilyRecipe, "approval_digest"> | FamilyRecipe): string {
  const basis = {
    family: recipe.family,
    revision: recipe.revision,
    variable_limits: recipe.variable_limits,
    bindings: recipe.bindings,
    construction_steps: recipe.construction_steps,
  };
  return createHash("sha256").update(JSON.stringify(canonicalize(basis))).digest("hex");
}

export function verifyRecipeApproval(recipe: FamilyRecipe, approvedDigests?: ReadonlySet<string>): void {
  const actual = recipeTemplateDigest(recipe);
  if (actual !== recipe.approval_digest) {
    throw Object.assign(new Error("recipe approval_digest does not match formulas/limits/construction template"), {
      code: "RECIPE_DIGEST_MISMATCH",
    });
  }
  if (approvedDigests && !approvedDigests.has(actual)) {
    throw Object.assign(new Error("recipe digest is not present in the approved production registry"), {
      code: "RECIPE_NOT_APPROVED",
    });
  }
}

export function recipeApprovalCandidate(input: unknown): FamilyRecipe {
  if (!input || typeof input !== "object" || Array.isArray(input)) {
    throw new Error("recipe draft must be an object");
  }
  const parsed = familyRecipeSchema.parse({ ...(input as Record<string, unknown>), approval_digest: "0".repeat(64) });
  return familyRecipeSchema.parse({ ...parsed, approval_digest: recipeTemplateDigest(parsed) });
}

export function evaluateLinearFormula(formula: LinearFormula, variables: Record<string, number>): number {
  let value = formula.constant;
  for (const [variable, coefficient] of Object.entries(formula.terms)) {
    if (!(variable in variables)) throw new Error(`recipe variable is missing: ${variable}`);
    value += variables[variable] * coefficient;
  }
  if (formula.round_to !== undefined) value = Math.round(value / formula.round_to) * formula.round_to;
  if (!Number.isFinite(value)) throw new Error("recipe formula produced a non-finite value");
  return Object.is(value, -0) ? 0 : value;
}

export function resolveRecipeBindings(recipe: FamilyRecipe): Array<Record<string, unknown>> {
  return recipe.bindings.map((binding) => {
    const value = evaluateLinearFormula(binding.formula, recipe.variables);
    const expression = binding.unit ? `${value} ${binding.unit}` : String(value);
    if (binding.target.kind === "active_parameter") {
      return { kind: "drive_dimension", name: binding.target.name, value: expression };
    }
    if (binding.target.kind === "component_parameter") {
      return {
        kind: "set_component_parameter",
        occurrence: binding.target.occurrence,
        name: binding.target.name,
        value: expression,
      };
    }
    return { kind: "set_constraint", name: binding.target.name, value: expression };
  });
}

function readPath(value: unknown, path: string): unknown {
  let current = value;
  for (const segment of path.split(".")) {
    if (!current || typeof current !== "object" || !(segment in current)) {
      throw new Error(`step output path not found: ${path}`);
    }
    current = (current as Record<string, unknown>)[segment];
  }
  return current;
}

function resolveString(value: z.infer<typeof stringRefSchema> | undefined, results: Map<string, unknown>): string | undefined {
  if (value === undefined || typeof value === "string") return value;
  if (!results.has(value.from_step)) throw new Error(`step result is unavailable: ${value.from_step}`);
  const resolved = readPath(results.get(value.from_step), value.path);
  if (typeof resolved !== "string" || !resolved.length) {
    throw new Error(`step output is not a non-empty string: ${value.from_step}.${value.path}`);
  }
  return resolved;
}

type ScalarInput = z.infer<typeof scalarInputSchema>;
type PathInput = z.infer<typeof pathInputSchema>;

export interface ConstructionContext {
  variables: Record<string, number>;
  jobPaths: {
    product_path: string;
    product_root: string;
    destination_root?: string;
    target_code?: string;
    product_code: string;
  };
}

function resolveScalar(value: ScalarInput, context: ConstructionContext, label: string, positiveOnly = false, max?: number): number {
  const resolved = typeof value === "number" ? value : evaluateLinearFormula(value.formula, context.variables);
  if (!Number.isFinite(resolved) || (positiveOnly && resolved <= 0) || (max !== undefined && resolved > max)) {
    throw new Error(`${label} resolved outside its permitted range`);
  }
  return resolved;
}

function resolvePoint(point: [ScalarInput, ScalarInput, ScalarInput], context: ConstructionContext, label: string): number[] {
  return point.map((value, index) => resolveScalar(value, context, `${label}[${index}]`));
}

function resolveJobText(template: string, context: ConstructionContext): string {
  const replacements: Record<string, string | undefined> = {
    target_code: context.jobPaths.target_code,
    product_code: context.jobPaths.product_code,
  };
  const value = template.replace(/\{([a-z_]+)\}/g, (_match, token: string) => {
    const replacement = replacements[token];
    if (!replacement) throw new Error(`job text token is unavailable: ${token}`);
    return replacement;
  });
  if (/[{}]/.test(value)) throw new Error("text contains an unsupported job token");
  return value;
}

function resolvePathInput(value: PathInput, context: ConstructionContext): string {
  if (typeof value === "string") return value;
  if ("job_path" in value) return context.jobPaths.product_path;

  const root = context.jobPaths[value.job_root];
  if (!root) throw new Error(`job path is unavailable: ${value.job_root}`);
  const relative = resolveJobText(value.relative_template, context);
  const pathApi = /^(?:[A-Za-z]:[\\/]|\\\\)/.test(root) ? path.win32 : path.posix;
  if (/[{}]/.test(relative) || pathApi.isAbsolute(relative)) {
    throw new Error("relative_template must be relative and contain only supported job tokens");
  }
  const resolved = pathApi.resolve(root, relative);
  const fromRoot = pathApi.relative(pathApi.resolve(root), resolved);
  if (fromRoot === "" || (!fromRoot.startsWith("..") && !pathApi.isAbsolute(fromRoot))) return resolved;
  throw new Error("relative_template escapes its approved job root");
}

export function constructionToolCall(
  step: ConstructionStep,
  results: Map<string, unknown>,
  context: ConstructionContext = {
    variables: {},
    jobPaths: { product_path: "", product_root: "", product_code: "" },
  },
): { tool: string; arguments: Record<string, unknown> } {
  switch (step.kind) {
    case "new_part": return { tool: "inventor_new_part", arguments: { template: step.template } };
    case "new_assembly": return { tool: "inventor_new_assembly", arguments: { template: step.template } };
    case "open_document": return { tool: "inventor_open_document", arguments: { path: resolvePathInput(step.path, context) } };
    case "save_document": return { tool: "inventor_save_document", arguments: { path: step.path === undefined ? undefined : resolvePathInput(step.path, context) } };
    case "close_document": return { tool: "inventor_close_document", arguments: { save: step.save } };
    case "set_material": return { tool: "inventor_set_material", arguments: { materialName: step.material_name } };
    case "create_parameter": return { tool: "inventor_create_parameter", arguments: {
      name: step.name, expression: String(resolveScalar(step.value, context, `create_parameter.${step.name}`)), unit: step.unit,
    } };
    case "set_parameter": {
      const value = resolveScalar(step.value, context, `set_parameter.${step.name}`);
      return { tool: "inventor_set_parameter", arguments: {
        name: step.name, value: step.unit === "ul" ? String(value) : `${value} ${step.unit}`,
      } };
    }
    case "set_iproperty": return { tool: "inventor_set_iproperty", arguments: {
      setName: step.property_set, propName: step.property, value: resolveJobText(step.value_template, context),
    } };
    case "create_sketch": return { tool: "inventor_create_sketch", arguments: { plane: step.plane } };
    case "project_geometry": return { tool: "inventor_project_geometry", arguments: {
      edgeIds: step.edge_ids.map((item) => resolveString(item, results)),
    } };
    case "draw_line": return { tool: "inventor_draw_line", arguments: {
      x1: resolveScalar(step.x1, context, "draw_line.x1"), y1: resolveScalar(step.y1, context, "draw_line.y1"),
      x2: resolveScalar(step.x2, context, "draw_line.x2"), y2: resolveScalar(step.y2, context, "draw_line.y2"),
    } };
    case "draw_circle": return { tool: "inventor_draw_circle", arguments: {
      cx: resolveScalar(step.cx, context, "draw_circle.cx"), cy: resolveScalar(step.cy, context, "draw_circle.cy"),
      radius: resolveScalar(step.radius, context, "draw_circle.radius", true),
    } };
    case "draw_rectangle": return { tool: "inventor_draw_rectangle", arguments: {
      x1: resolveScalar(step.x1, context, "draw_rectangle.x1"), y1: resolveScalar(step.y1, context, "draw_rectangle.y1"),
      x2: resolveScalar(step.x2, context, "draw_rectangle.x2"), y2: resolveScalar(step.y2, context, "draw_rectangle.y2"),
    } };
    case "draw_arc": return { tool: "inventor_draw_arc", arguments: {
      cx: resolveScalar(step.cx, context, "draw_arc.cx"), cy: resolveScalar(step.cy, context, "draw_arc.cy"),
      radius: resolveScalar(step.radius, context, "draw_arc.radius", true),
      startDeg: resolveScalar(step.start_deg, context, "draw_arc.start_deg"),
      endDeg: resolveScalar(step.end_deg, context, "draw_arc.end_deg"),
    } };
    case "add_sketch_dimension": return { tool: "inventor_add_sketch_dimension", arguments: {
      entityId: resolveString(step.entity_id, results),
      value: resolveScalar(step.value_mm, context, "add_sketch_dimension.value_mm", true),
    } };
    case "add_sketch_constraint": return { tool: "inventor_add_sketch_constraint", arguments: {
      type: step.type, entityIds: step.entity_ids.map((item) => resolveString(item, results)),
    } };
    case "close_sketch": return { tool: "inventor_close_sketch", arguments: { sketchName: resolveString(step.sketch_name, results) } };
    case "create_work_plane": return { tool: "inventor_create_work_plane", arguments: {
      type: step.type, refs: step.refs.map((item) => resolveString(item, results)),
      offset: step.offset_mm === undefined ? undefined : resolveScalar(step.offset_mm, context, "create_work_plane.offset_mm"),
    } };
    case "create_work_axis": return { tool: "inventor_create_work_axis", arguments: {
      type: step.type, refs: step.refs.map((item) => resolveString(item, results)),
    } };
    case "extrude": return { tool: "inventor_extrude", arguments: {
      sketchName: resolveString(step.sketch_name, results), distance: resolveScalar(step.distance_mm, context, "extrude.distance_mm", true),
      operation: step.operation, direction: step.direction,
    } };
    case "revolve": return { tool: "inventor_revolve", arguments: {
      sketchName: resolveString(step.sketch_name, results), axisId: resolveString(step.axis_id, results),
      angle: resolveScalar(step.angle_deg, context, "revolve.angle_deg", true, 360), operation: step.operation,
    } };
    case "fillet": return { tool: "inventor_fillet", arguments: {
      edgeIds: step.edge_ids.map((item) => resolveString(item, results)), radius: resolveScalar(step.radius_mm, context, "fillet.radius_mm", true),
    } };
    case "chamfer": return { tool: "inventor_chamfer", arguments: {
      edgeIds: step.edge_ids.map((item) => resolveString(item, results)), distance: resolveScalar(step.distance_mm, context, "chamfer.distance_mm", true),
    } };
    case "hole": return { tool: "inventor_hole", arguments: {
      face: { ...step.face, near_mm: step.face.near_mm ? resolvePoint(step.face.near_mm, context, "hole.face.near_mm") : undefined },
      points_mm: step.points_mm.map((point, index) => resolvePoint(point, context, `hole.points_mm[${index}]`)),
      diameter_mm: resolveScalar(step.diameter_mm, context, "hole.diameter_mm", true),
      through: step.through, depth_mm: step.depth_mm === undefined ? undefined : resolveScalar(step.depth_mm, context, "hole.depth_mm", true),
    } };
    case "circular_pattern": return { tool: "inventor_circular_pattern", arguments: {
      feature_names: step.feature_names.map((item) => resolveString(item, results)),
      axis: resolveString(step.axis, results), count: step.count,
      angle_deg: resolveScalar(step.angle_deg, context, "circular_pattern.angle_deg", true, 360),
    } };
    case "rectangular_pattern": return { tool: "inventor_rectangular_pattern", arguments: {
      feature_names: step.feature_names.map((item) => resolveString(item, results)),
      dir1: resolveString(step.dir1, results), count1: step.count1,
      spacing_mm1: resolveScalar(step.spacing_mm1, context, "rectangular_pattern.spacing_mm1", true),
      dir2: resolveString(step.dir2, results), count2: step.count2,
      spacing_mm2: step.spacing_mm2 === undefined ? undefined : resolveScalar(step.spacing_mm2, context, "rectangular_pattern.spacing_mm2", true),
      natural_direction1: step.natural_direction1, natural_direction2: step.natural_direction2,
    } };
    case "create_imate": {
      const selector = step.selector.kind === "planar" ? {
        kind: step.selector.kind,
        normal: step.selector.normal,
        extreme: step.selector.extreme,
        near_mm: step.selector.near_mm ? resolvePoint(step.selector.near_mm, context, "create_imate.selector.near_mm") : undefined,
        tolerance_deg: resolveScalar(step.selector.tolerance_deg, context, "create_imate.selector.tolerance_deg", true, 90),
      } : {
        kind: step.selector.kind,
        radius_mm: resolveScalar(step.selector.radius_mm, context, "create_imate.selector.radius_mm", true),
        axis: step.selector.axis,
        near_mm: step.selector.near_mm ? resolvePoint(step.selector.near_mm, context, "create_imate.selector.near_mm") : undefined,
        radius_tol_mm: resolveScalar(step.selector.radius_tol_mm, context, "create_imate.selector.radius_tol_mm", true),
      };
      return { tool: "inventor_create_imate", arguments: {
        name: step.name, type: step.type, selector,
        offset_mm: resolveScalar(step.offset_mm, context, "create_imate.offset_mm"),
        insert_opposed: step.insert_opposed,
        distance_mm: resolveScalar(step.distance_mm, context, "create_imate.distance_mm"),
      } };
    }
    case "place_occurrence": return { tool: "inventor_place_occurrence", arguments: {
      path: resolvePathInput(step.path, context), grounded: step.grounded,
      position_mm: step.position_mm ? resolvePoint(step.position_mm, context, "place_occurrence.position_mm") : undefined,
      rotation_deg_xyz: step.rotation_deg_xyz ? resolvePoint(step.rotation_deg_xyz, context, "place_occurrence.rotation_deg_xyz") : undefined,
    } };
    case "add_constraint": return { tool: "inventor_add_constraint", arguments: {
      type: step.type,
      a: { occurrence: resolveString(step.a.occurrence, results), ref: step.a.ref },
      b: { occurrence: resolveString(step.b.occurrence, results), ref: step.b.ref },
      offset_mm: resolveScalar(step.offset_mm, context, "add_constraint.offset_mm"),
      angle_deg: step.angle_deg === undefined ? undefined : resolveScalar(step.angle_deg, context, "add_constraint.angle_deg"),
      insert_opposed: step.insert_opposed,
    } };
  }
}

export function requiredConstructionTools(steps: ConstructionStep[]): string[] {
  const names: Record<ConstructionStep["kind"], string> = {
    new_part: "inventor_new_part",
    new_assembly: "inventor_new_assembly",
    open_document: "inventor_open_document",
    save_document: "inventor_save_document",
    close_document: "inventor_close_document",
    set_material: "inventor_set_material",
    create_parameter: "inventor_create_parameter",
    set_parameter: "inventor_set_parameter",
    set_iproperty: "inventor_set_iproperty",
    create_sketch: "inventor_create_sketch",
    project_geometry: "inventor_project_geometry",
    draw_line: "inventor_draw_line",
    draw_circle: "inventor_draw_circle",
    draw_rectangle: "inventor_draw_rectangle",
    draw_arc: "inventor_draw_arc",
    add_sketch_dimension: "inventor_add_sketch_dimension",
    add_sketch_constraint: "inventor_add_sketch_constraint",
    close_sketch: "inventor_close_sketch",
    create_work_plane: "inventor_create_work_plane",
    create_work_axis: "inventor_create_work_axis",
    extrude: "inventor_extrude",
    revolve: "inventor_revolve",
    fillet: "inventor_fillet",
    chamfer: "inventor_chamfer",
    hole: "inventor_hole",
    circular_pattern: "inventor_circular_pattern",
    rectangular_pattern: "inventor_rectangular_pattern",
    create_imate: "inventor_create_imate",
    place_occurrence: "inventor_place_occurrence",
    add_constraint: "inventor_add_constraint",
  };
  return [...new Set(steps.map((step) => names[step.kind]))].sort();
}
