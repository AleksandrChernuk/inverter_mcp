import { z } from "zod";
import { familyRecipeSchema } from "./product-recipe.js";

const shortText = z.string().trim().min(1).max(256);
const expression = z.string().trim().min(1).max(128);
const nonNegative = z.number().finite().nonnegative();
const sheetPointMm = z.tuple([z.number().finite(), z.number().finite()]);

function portableUnder(candidate: string, root: string): boolean {
  const normalizedCandidate = candidate.replace(/\\/g, "/").replace(/\/+$/, "").toLocaleLowerCase();
  const normalizedRoot = root.replace(/\\/g, "/").replace(/\/+$/, "").toLocaleLowerCase();
  return normalizedCandidate === normalizedRoot || normalizedCandidate.startsWith(`${normalizedRoot}/`);
}

function portableEqual(left: string, right: string): boolean {
  const normalize = (value: string) => value.replace(/\\/g, "/").replace(/\/+$/, "").toLocaleLowerCase();
  return normalize(left) === normalize(right);
}

export const absoluteInventorPath = z.string().trim().min(3).max(4096).refine(
  (value) => /^(?:[A-Za-z]:[\\/]|\\\\|\/)/.test(value),
  "Expected an absolute Windows, UNC, or POSIX path",
);

export const inventorDocumentPath = absoluteInventorPath.refine(
  (value) => /\.(?:ipt|iam)$/i.test(value),
  "Expected an Inventor .ipt or .iam document",
);

export const inventorPartPath = absoluteInventorPath.refine(
  (value) => /\.ipt$/i.test(value),
  "Expected an Inventor .ipt part document",
);

export const mutationSchema = z.discriminatedUnion("kind", [
  z.object({
    kind: z.literal("drive_dimension"),
    name: shortText.describe("Inventor parameter or named sketch dimension, for example d0"),
    value: expression.describe("Inventor expression with units, for example 340 mm"),
  }).strict(),
  z.object({
    kind: z.literal("set_component_parameter"),
    occurrence: shortText.describe("Exact assembly occurrence name from inventor_inspect_assembly"),
    name: shortText.describe("Parameter name on the referenced component"),
    value: expression.describe("Inventor expression with units, for example 340 mm"),
  }).strict(),
  z.object({
    kind: z.literal("set_constraint"),
    name: shortText.describe("Exact assembly constraint name from inventor_inspect_assembly"),
    value: expression.describe("Offset or angle expression, for example 5 mm or 30 deg"),
  }).strict(),
  z.object({
    kind: z.literal("set_casing_discharge"),
    angle: z.union([
      z.literal(0), z.literal(45), z.literal(90), z.literal(135),
      z.literal(180), z.literal(225), z.literal(270), z.literal(315),
    ]),
    hand: z.enum(["right", "left"]).optional(),
    param_name: shortText.optional(),
  }).strict(),
  z.object({
    kind: z.literal("set_material"),
    material_name: shortText.describe("Material name that exists in the active Inventor document"),
  }).strict(),
]);

const measureSideSchema = z.object({
  occurrence: shortText,
  ref: shortText.optional(),
}).strict();

const minDistanceCheckSchema = z.object({
  kind: z.literal("min_distance"),
  a: measureSideSchema,
  b: measureSideSchema,
  min_mm: nonNegative.optional(),
  max_mm: nonNegative.optional(),
}).strict().superRefine((value, context) => {
  if (value.min_mm === undefined && value.max_mm === undefined) {
    context.addIssue({ code: z.ZodIssueCode.custom, message: "min_mm or max_mm is required" });
  }
  if (value.min_mm !== undefined && value.max_mm !== undefined && value.min_mm > value.max_mm) {
    context.addIssue({ code: z.ZodIssueCode.custom, message: "min_mm must not exceed max_mm" });
  }
});

const physicalBoundsCheckSchema = z.object({
  kind: z.literal("physical_bounds"),
  min_mass_g: nonNegative.optional(),
  max_mass_g: nonNegative.optional(),
  min_x_mm: nonNegative.optional(),
  max_x_mm: nonNegative.optional(),
  min_y_mm: nonNegative.optional(),
  max_y_mm: nonNegative.optional(),
  min_z_mm: nonNegative.optional(),
  max_z_mm: nonNegative.optional(),
}).strict().superRefine((value, context) => {
  const limits = Object.entries(value).filter(([key, item]) => key !== "kind" && item !== undefined);
  if (limits.length === 0) {
    context.addIssue({ code: z.ZodIssueCode.custom, message: "At least one physical bound is required" });
  }
  for (const axis of ["mass_g", "x_mm", "y_mm", "z_mm"] as const) {
    const min = value[`min_${axis}`];
    const max = value[`max_${axis}`];
    if (min !== undefined && max !== undefined && min > max) {
      context.addIssue({ code: z.ZodIssueCode.custom, message: `min_${axis} must not exceed max_${axis}` });
    }
  }
});

export const acceptanceCheckSchema = z.union([
  z.object({ kind: z.literal("model_health") }).strict(),
  z.object({ kind: z.literal("part_cut_ready") }).strict(),
  z.object({
    kind: z.literal("constraints_healthy"),
    allowed_unhealthy: z.number().int().min(0).max(100).default(0),
  }).strict(),
  z.object({
    kind: z.literal("no_interference"),
    occurrences: z.array(shortText).min(2).max(200).optional(),
    max_pairs: z.number().int().min(0).max(1000).default(0),
  }).strict(),
  minDistanceCheckSchema,
  physicalBoundsCheckSchema,
]);

export const outputSchema = z.discriminatedUnion("kind", [
  z.object({
    kind: z.literal("gabarit"),
    output_path: absoluteInventorPath,
    model_path: inventorDocumentPath.optional(),
    scale: z.number().finite().positive().max(100).optional(),
    export_dxf: z.boolean().default(false),
    template: absoluteInventorPath.optional(),
    retrieve_dimensions: z.boolean().default(true),
    add_overall_dimensions: z.boolean().default(true),
    require_parts_list: z.boolean().default(false),
    require_balloons: z.boolean().default(false),
    minimum_balloons: z.number().int().min(0).max(10000).default(1),
    require_hole_table: z.boolean().default(false),
    minimum_dimensions: z.number().int().min(0).max(10000).default(0),
    technical_notes: z.array(z.string().trim().min(1).max(1000)).max(64).default([]),
    section_views: z.array(z.object({
      name: shortText.optional(), start_mm: sheetPointMm, end_mm: sheetPointMm, position_mm: sheetPointMm,
    }).strict()).max(16).default([]),
    detail_views: z.array(z.object({
      name: shortText.optional(), center_mm: sheetPointMm, position_mm: sheetPointMm,
      radius_mm: z.number().finite().positive(), scale: z.number().finite().positive().max(100).optional(),
    }).strict()).max(32).default([]),
  }).strict(),
  z.object({
    kind: z.literal("flat_dxf"),
    output_dir: absoluteInventorPath,
    parts_dir: absoluteInventorPath.optional(),
    material_aliases: z.record(shortText, shortText).default({}),
    create_missing_flat_patterns: z.boolean().default(false),
  }).strict(),
  z.object({
    kind: z.literal("capture_view"),
    output_path: absoluteInventorPath,
    orientation: z.enum([
      "iso_top_right", "iso_top_left", "iso_bottom_right", "iso_bottom_left",
      "front", "back", "top", "bottom", "left", "right",
    ]).default("iso_top_right"),
    width: z.number().int().min(16).max(4096).default(1280),
    height: z.number().int().min(16).max(4096).default(720),
  }).strict(),
  z.object({
    kind: z.literal("batch_pdf"),
    drawings_dir: absoluteInventorPath,
    output_dir: absoluteInventorPath,
    recursive: z.boolean().default(true),
  }).strict(),
]);

export const engineeringJobV1Schema = z.object({
  version: z.literal("1"),
  objective: z.string().trim().min(10).max(2000),
  product_path: inventorDocumentPath.describe("Absolute path to the target .ipt or .iam"),
  mutations: z.array(mutationSchema).min(1).max(32),
  checks: z.array(acceptanceCheckSchema).min(1).max(32),
  outputs: z.array(outputSchema).max(16).default([]),
  save: z.enum(["never", "after_checks"]).default("never"),
  save_as_path: absoluteInventorPath.optional(),
  context: z.object({
    request_id: shortText.optional(),
    actor: shortText.optional(),
    approval_id: shortText.optional(),
  }).strict().default({}),
}).strict().superRefine((value, context) => {
  if (value.save === "never" && value.save_as_path !== undefined) {
    context.addIssue({
      code: z.ZodIssueCode.custom,
      path: ["save_as_path"],
      message: "save_as_path requires save=after_checks",
    });
  }
});

const mountingCheckSchema = z.object({
  occurrence: shortText.optional(),
  plane: z.enum(["xy", "xz", "yz"]).default("xy"),
  hole_count: z.number().int().min(2).max(128),
  bolt_circle_diameter_mm: z.number().finite().positive(),
  hole_diameter_mm: z.number().finite().positive().optional(),
  center_mm: z.tuple([z.number().finite(), z.number().finite(), z.number().finite()]).optional(),
  tolerance_mm: z.number().finite().positive().max(10).default(0.25),
  angle_tolerance_deg: z.number().finite().positive().max(30).default(1),
}).strict();

const productSourceSchema = z.discriminatedUnion("mode", [
  z.object({
    mode: z.literal("existing"),
    product_path: inventorDocumentPath,
  }).strict(),
  z.object({
    mode: z.literal("clone_recode"),
    source_root: absoluteInventorPath,
    destination_root: absoluteInventorPath,
    top_document: inventorDocumentPath,
    source_code: shortText,
    target_code: shortText,
    update_iproperties: z.boolean().default(true),
  }).strict(),
]);

export const productJobV2Schema = z.object({
  version: z.literal("2"),
  objective: z.string().trim().min(10).max(2000),
  source: productSourceSchema,
  recipe: familyRecipeSchema,
  checks: z.array(acceptanceCheckSchema).min(1).max(32),
  sheet_metal_checks: z.array(inventorPartPath).max(512).default([]),
  mounting_checks: z.array(mountingCheckSchema).max(32).default([]),
  outputs: z.array(outputSchema).max(256).default([]),
  release: z.object({
    product_root: absoluteInventorPath,
    dxf_dir: absoluteInventorPath,
    pdf_dir: absoluteInventorPath.optional(),
    output_dir: absoluteInventorPath,
    product_code: shortText,
    minimum_pdf_count: z.number().int().min(0).max(10000).default(0),
    required_name_pattern: z.string().trim().min(1).max(512).optional(),
    require_clone_manifest: z.boolean().default(false),
    require_dxf_manifest: z.boolean().default(true),
    require_chief_approval: z.literal(true).default(true),
  }).strict(),
  context: z.object({
    request_id: shortText.optional(),
    actor: shortText.optional(),
    approval_id: shortText.optional(),
  }).strict().default({}),
}).strict().superRefine((value, context) => {
  if (value.recipe.bindings.length === 0 && value.recipe.construction_steps.length === 0) {
    context.addIssue({ code: z.ZodIssueCode.custom, path: ["recipe"], message: "recipe must change or construct something" });
  }
  if (value.source.mode === "clone_recode" && !portableUnder(value.source.top_document, value.source.source_root)) {
    context.addIssue({ code: z.ZodIssueCode.custom, path: ["source", "top_document"], message: "top_document must be under source_root" });
  }
  if (value.source.mode === "clone_recode" &&
      value.source.source_code.toLocaleLowerCase() === value.source.target_code.toLocaleLowerCase()) {
    context.addIssue({ code: z.ZodIssueCode.custom, path: ["source", "target_code"], message: "source_code and target_code must differ" });
  }
  if (value.source.mode === "clone_recode" && !portableEqual(value.source.destination_root, value.release.product_root)) {
    context.addIssue({ code: z.ZodIssueCode.custom, path: ["release", "product_root"], message: "product_root must equal clone destination_root" });
  }
  if (value.source.mode === "existing" && !portableUnder(value.source.product_path, value.release.product_root)) {
    context.addIssue({ code: z.ZodIssueCode.custom, path: ["source", "product_path"], message: "existing product_path must be under release.product_root" });
  }
  for (const [field, candidate] of [
    ["dxf_dir", value.release.dxf_dir], ["pdf_dir", value.release.pdf_dir], ["output_dir", value.release.output_dir],
  ] as const) {
    if (candidate && !portableUnder(candidate, value.release.product_root)) {
      context.addIssue({ code: z.ZodIssueCode.custom, path: ["release", field], message: `${field} must be under product_root` });
    }
  }
  if (portableEqual(value.release.output_dir, value.release.product_root)) {
    context.addIssue({ code: z.ZodIssueCode.custom, path: ["release", "output_dir"], message: "output_dir must be a child of product_root" });
  }
  for (const [field, candidate] of [["dxf_dir", value.release.dxf_dir], ["pdf_dir", value.release.pdf_dir]] as const) {
    if (candidate && (portableUnder(candidate, value.release.output_dir) || portableUnder(value.release.output_dir, candidate))) {
      context.addIssue({ code: z.ZodIssueCode.custom, path: ["release", field], message: `${field} and output_dir must not contain one another` });
    }
  }
  value.sheet_metal_checks.forEach((candidate, index) => {
    if (!portableUnder(candidate, value.release.product_root)) context.addIssue({
      code: z.ZodIssueCode.custom, path: ["sheet_metal_checks", index], message: "part must be under product_root",
    });
  });
  value.outputs.forEach((output, index) => {
    const destinations = output.kind === "gabarit" ? [output.output_path]
      : output.kind === "flat_dxf" ? [output.output_dir]
        : output.kind === "capture_view" ? [output.output_path]
          : [output.output_dir];
    for (const candidate of destinations) {
      if (!portableUnder(candidate, value.release.product_root)) context.addIssue({
        code: z.ZodIssueCode.custom, path: ["outputs", index], message: "output destination must be under product_root",
      });
    }
    if (output.kind === "gabarit" && output.model_path && !portableUnder(output.model_path, value.release.product_root)) {
      context.addIssue({ code: z.ZodIssueCode.custom, path: ["outputs", index, "model_path"], message: "model_path must be under product_root" });
    }
    if (output.kind === "flat_dxf" && output.parts_dir && !portableUnder(output.parts_dir, value.release.product_root)) {
      context.addIssue({ code: z.ZodIssueCode.custom, path: ["outputs", index, "parts_dir"], message: "parts_dir must be under product_root" });
    }
    if (output.kind === "batch_pdf" && !portableUnder(output.drawings_dir, value.release.product_root)) {
      context.addIssue({ code: z.ZodIssueCode.custom, path: ["outputs", index, "drawings_dir"], message: "drawings_dir must be under product_root" });
    }
  });
});

export const engineeringJobSchema = z.union([engineeringJobV1Schema, productJobV2Schema]);

export type EngineeringJob = z.infer<typeof engineeringJobSchema>;
export type EngineeringJobV1 = z.infer<typeof engineeringJobV1Schema>;
export type EngineeringMutation = z.infer<typeof mutationSchema>;
export type AcceptanceCheck = z.infer<typeof acceptanceCheckSchema>;
export type EngineeringOutput = z.infer<typeof outputSchema>;
export type ProductJob = z.infer<typeof productJobV2Schema>;

export const jobIdSchema = z.string().uuid();

export type JobStatus =
  | "prepared"
  | "running"
  | "model_committed"
  | "saved"
  | "exporting"
  | "succeeded"
  | "awaiting_release_approval"
  | "released"
  | "failed"
  | "uncertain";

export interface JobEvent {
  at: string;
  stage: string;
  ok: boolean;
  evidence?: unknown;
}

export interface JobRecord {
  id: string;
  digest: string;
  status: JobStatus;
  created_at: string;
  updated_at: string;
  spec: EngineeringJob;
  events: JobEvent[];
  completed_outputs: number;
  release_digest?: string;
  approved_by?: string;
  result?: unknown;
  error?: { code: string; message: string };
}
