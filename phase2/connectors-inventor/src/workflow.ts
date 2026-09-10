import type { EngineeringJob, EngineeringJobV1, EngineeringOutput, JobRecord, JobStatus, ProductJob } from "./contracts.js";
import type { GatewayClientLike, GatewayToolResult } from "./gateway-client.js";
import { AuditLog, JobStore } from "./job-store.js";
import { constructionToolCall, requiredConstructionTools, resolveRecipeBindings } from "./product-recipe.js";

const CORE_TOOLS = [
  "inventor_health",
  "inventor_vent_open_product",
  "inventor_get_document_info",
  "inventor_get_mass_properties",
  "inventor_vent_execute_plan",
] as const;

export function requiredTools(spec: EngineeringJob): string[] {
  const tools = new Set<string>(CORE_TOOLS);
  if (spec.version === "1" && spec.save === "after_checks") tools.add("inventor_save_document");
  if (spec.version === "2") {
    tools.add("inventor_vent_save_product");
    tools.add("inventor_vent_build_release");
    if (spec.source.mode === "clone_recode") tools.add("inventor_vent_clone_recode_product");
    if (spec.sheet_metal_checks.length) tools.add("inventor_vent_check_part");
    for (const tool of requiredConstructionTools(spec.recipe.construction_steps)) tools.add(tool);
  }
  for (const output of spec.outputs) {
    if (output.kind === "gabarit") tools.add("inventor_vent_make_gabarit");
    if (output.kind === "flat_dxf") tools.add("inventor_vent_batch_flat_dxf");
    if (output.kind === "capture_view") {
      tools.add("inventor_set_view_orientation");
      tools.add("inventor_capture_view");
    }
    if (output.kind === "batch_pdf") tools.add("inventor_vent_batch_pdf_drawings");
  }
  return [...tools].sort();
}

export function summarizePlan(spec: EngineeringJob): Record<string, unknown> {
  if (spec.version === "2") {
    return {
      version: spec.version,
      objective: spec.objective,
      source: spec.source,
      family: spec.recipe.family,
      recipe_revision: spec.recipe.revision,
      recipe_approval_digest: spec.recipe.approval_digest,
      variable_limits: spec.recipe.variable_limits,
      variables: spec.recipe.variables,
      resolved_mutations: resolveRecipeBindings(spec.recipe),
      construction_steps: spec.recipe.construction_steps,
      checks: spec.checks,
      sheet_metal_checks: spec.sheet_metal_checks,
      mounting_checks: spec.mounting_checks,
      outputs: spec.outputs,
      release: spec.release,
      required_tools: requiredTools(spec),
      guarantees: [
        "the exact recipe, variables, source map and outputs are locked by the approval digest",
        "clone/recode runs through SaveCopyAs and ReplaceReference and never saves source documents",
        "construction uses only the typed CAD step allowlist and has a bounded step count",
        "parameter bindings and motor mounting checks share the same transaction plus referenced-document rollback boundary",
        "the active product and dirty dependencies are saved together only under the approved product root",
        "release artifacts are hashed and require a second chief-engineer approval before notification",
        "transport loss during a non-idempotent stage is uncertain and is never retried automatically",
      ],
    };
  }
  return {
    version: spec.version,
    objective: spec.objective,
    product_path: spec.product_path,
    mutation_count: spec.mutations.length,
    mutations: spec.mutations.map((item, index) => ({ index, ...item })),
    check_count: spec.checks.length,
    checks: spec.checks.map((item, index) => ({ index, ...item })),
    outputs: spec.outputs,
    save: spec.save,
    required_tools: requiredTools(spec),
    guarantees: [
      "all mutations execute inside the vent_execute_plan transaction",
      "Document.Update2(false) must succeed",
      "every acceptance check must pass before the transaction commits",
      "disk save and exports happen only after acceptance",
      "transport loss during mutation/save is marked uncertain and is never retried automatically",
    ],
  };
}

function payloadObject(result: GatewayToolResult): Record<string, unknown> | undefined {
  return result.payload && typeof result.payload === "object" && !Array.isArray(result.payload)
    ? result.payload as Record<string, unknown>
    : undefined;
}

function uncertainTransport(result: GatewayToolResult): boolean {
  return result.uncertain === true
    || result.status === 0
    || result.error?.code === "GATEWAY_TIMEOUT"
    || result.error?.code === "GATEWAY_UNAVAILABLE";
}

function resultError(result: GatewayToolResult): { code: string; message: string } {
  return {
    code: result.error?.code ?? "TOOL_FAILED",
    message: result.error?.message ?? `${result.tool} failed`,
  };
}

async function requireSuccess(result: GatewayToolResult): Promise<unknown> {
  if (!result.ok) throw Object.assign(new Error(resultError(result).message), { toolResult: result });
  return result.payload;
}

async function requirePassingResult(result: GatewayToolResult): Promise<unknown> {
  const payload = await requireSuccess(result);
  const object = payloadObject(result);
  if (object?.pass === false) {
    throw Object.assign(new Error(`${result.tool} returned pass=false`), { toolResult: result });
  }
  return payload;
}

async function runOutput(client: GatewayClientLike, output: EngineeringOutput, defaultProductPath?: string): Promise<unknown> {
  if (output.kind === "gabarit") {
    const modelPath = output.model_path ?? defaultProductPath;
    if (modelPath) await requireSuccess(await client.callTool("inventor_vent_open_product", { path: modelPath }));
    return requirePassingResult(await client.callTool("inventor_vent_make_gabarit", {
      outputPath: output.output_path,
      scale: output.scale,
      exportDxf: output.export_dxf,
      template: output.template,
      retrieveDimensions: output.retrieve_dimensions,
      addOverallDimensions: output.add_overall_dimensions,
      requirePartsList: output.require_parts_list,
      requireBalloons: output.require_balloons,
      minimumBalloons: output.minimum_balloons,
      requireHoleTable: output.require_hole_table,
      minimumDimensions: output.minimum_dimensions,
      technicalNotes: output.technical_notes,
      sectionViews: output.section_views,
      detailViews: output.detail_views,
    }));
  }
  if (output.kind === "flat_dxf") {
    if (!output.parts_dir && defaultProductPath) {
      await requireSuccess(await client.callTool("inventor_vent_open_product", { path: defaultProductPath }));
    }
    return requirePassingResult(await client.callTool("inventor_vent_batch_flat_dxf", {
      outputDir: output.output_dir,
      partsDir: output.parts_dir,
      materialAliases: output.material_aliases,
      createMissingFlatPatterns: output.create_missing_flat_patterns,
    }));
  }
  if (output.kind === "batch_pdf") {
    return requirePassingResult(await client.callTool("inventor_vent_batch_pdf_drawings", {
      drawingsDir: output.drawings_dir,
      outputDir: output.output_dir,
      recursive: output.recursive,
    }));
  }

  if (defaultProductPath) {
    await requireSuccess(await client.callTool("inventor_vent_open_product", { path: defaultProductPath }));
  }
  await requireSuccess(await client.callTool("inventor_set_view_orientation", {
    orientation: output.orientation,
    fit: true,
  }));
  return requireSuccess(await client.callTool("inventor_capture_view", {
    width: output.width,
    height: output.height,
    outputPath: output.output_path,
  }));
}

async function transitionFailure(
  store: JobStore,
  audit: AuditLog,
  record: JobRecord,
  status: Extract<JobStatus, "failed" | "uncertain">,
  stage: string,
  error: { code: string; message: string },
  evidence?: unknown,
): Promise<JobRecord> {
  const next = await store.transition(record.id, status, { stage, ok: false, evidence }, { error });
  await audit.append({
    job_id: record.id,
    digest: record.digest,
    status,
    stage,
    error,
    actor: record.spec.context.actor,
    request_id: record.spec.context.request_id,
  });
  return next;
}

export async function executeEngineeringJob(options: {
  record: JobRecord;
  approvedDigest: string;
  client: GatewayClientLike;
  store: JobStore;
  audit: AuditLog;
}): Promise<JobRecord> {
  const { client, store, audit, approvedDigest } = options;
  let record = options.record;

  if (record.spec.version !== "1") {
    throw Object.assign(new Error("executeEngineeringJob only accepts JobSpec v1"), { code: "WRONG_JOB_VERSION" });
  }
  const spec: EngineeringJobV1 = record.spec;

  if (approvedDigest !== record.digest) {
    await audit.append({
      job_id: record.id,
      digest: record.digest,
      status: record.status,
      stage: "approval_rejected",
      code: "PLAN_DIGEST_MISMATCH",
    });
    throw Object.assign(
      new Error("The approved digest does not match the persisted engineering plan"),
      { code: "PLAN_DIGEST_MISMATCH" },
    );
  }
  if (record.status === "succeeded") return record;
  if (record.status === "running" || record.status === "uncertain") {
    return transitionFailure(store, audit, record, "uncertain", "resume", {
      code: "UNCERTAIN_MODEL_STATE",
      message: "The previous run stopped during a non-idempotent stage; inspect Inventor before retrying",
    });
  }
  if (record.status === "failed") return record;

  const available = new Set(await client.listTools());
  const missing = requiredTools(record.spec).filter((name) => !available.has(name));
  if (missing.length) {
    return transitionFailure(store, audit, record, "failed", "preflight", {
      code: "MISSING_GATEWAY_TOOLS",
      message: `Gateway is missing required tools: ${missing.join(", ")}`,
    }, { missing });
  }

  if (record.status === "prepared") {
    await audit.append({
      job_id: record.id,
      digest: record.digest,
      status: "running",
      stage: "start",
      actor: record.spec.context.actor,
      request_id: record.spec.context.request_id,
      approval_id: record.spec.context.approval_id,
    });
    record = await store.transition(record.id, "running", { stage: "start", ok: true });

    const health = await client.callTool("inventor_health");
    if (!health.ok) {
      return transitionFailure(store, audit, record, "failed", "health", resultError(health), health);
    }
    record = await store.transition(record.id, "running", { stage: "health", ok: true, evidence: health.payload });

    const opened = await client.callTool("inventor_vent_open_product", { path: spec.product_path });
    if (!opened.ok) {
      return transitionFailure(store, audit, record, "failed", "open_product", resultError(opened), opened);
    }
    record = await store.transition(record.id, "running", { stage: "open_product", ok: true, evidence: opened.payload });

    const document = await client.callTool("inventor_get_document_info");
    if (!document.ok) {
      return transitionFailure(store, audit, record, "failed", "document_snapshot", resultError(document), document);
    }
    record = await store.transition(record.id, "running", { stage: "document_snapshot", ok: true, evidence: document.payload });

    const baseline = await client.callTool("inventor_get_mass_properties");
    if (!baseline.ok) {
      return transitionFailure(store, audit, record, "failed", "baseline", resultError(baseline), baseline);
    }
    record = await store.transition(record.id, "running", { stage: "baseline", ok: true, evidence: baseline.payload });

    const executed = await client.callTool("inventor_vent_execute_plan", {
      mutations: spec.mutations,
      checks: spec.checks,
      dryRun: false,
    });
    if (!executed.ok) {
      const status = uncertainTransport(executed) ? "uncertain" : "failed";
      return transitionFailure(store, audit, record, status, "execute_plan", resultError(executed), executed);
    }
    const execution = payloadObject(executed);
    if (execution?.pass !== true || execution.committed !== true) {
      return transitionFailure(store, audit, record, "failed", "acceptance", {
        code: "ACCEPTANCE_FAILED",
        message: "Inventor rolled back the plan because a mutation, rebuild, or acceptance check failed",
      }, executed.payload);
    }
    record = await store.transition(
      record.id,
      "model_committed",
      { stage: "execute_plan", ok: true, evidence: executed.payload },
      { result: { execution: executed.payload } },
    );
  }

  if (spec.save === "after_checks" && record.status === "model_committed") {
    const saved = await client.callTool("inventor_save_document", { path: spec.save_as_path });
    if (!saved.ok) {
      const status = uncertainTransport(saved) ? "uncertain" : "failed";
      return transitionFailure(store, audit, record, status, "save", resultError(saved), saved);
    }
    record = await store.transition(record.id, "saved", { stage: "save", ok: true, evidence: saved.payload });
  }

  if (spec.save === "never" && record.status === "model_committed") {
    record = await store.transition(record.id, "exporting", { stage: "save_skipped", ok: true });
  } else if (record.status === "saved") {
    record = await store.transition(record.id, "exporting", { stage: "exports_start", ok: true });
  }

  for (let index = record.completed_outputs; index < spec.outputs.length; index++) {
    try {
      const evidence = await runOutput(client, spec.outputs[index], spec.product_path);
      record = await store.transition(
        record.id,
        "exporting",
        { stage: `output_${index}`, ok: true, evidence },
        { completed_outputs: index + 1 },
      );
    } catch (error) {
      const toolResult = error && typeof error === "object" && "toolResult" in error
        ? (error as { toolResult: GatewayToolResult }).toolResult
        : undefined;
      return transitionFailure(store, audit, record, toolResult && uncertainTransport(toolResult) ? "uncertain" : "failed", `output_${index}`, {
        code: toolResult?.error?.code ?? "OUTPUT_FAILED",
        message: error instanceof Error ? error.message : String(error),
      }, toolResult);
    }
  }

  const finalSnapshot = await client.callTool("inventor_get_mass_properties");
  if (!finalSnapshot.ok) {
    return transitionFailure(store, audit, record, "uncertain", "final_snapshot", {
      code: "FINAL_EVIDENCE_UNAVAILABLE",
      message: resultError(finalSnapshot).message,
    }, finalSnapshot);
  }
  record = await store.transition(
    record.id,
    "succeeded",
    { stage: "completed", ok: true, evidence: finalSnapshot.payload },
    { result: { ...(record.result as Record<string, unknown> | undefined), final_snapshot: finalSnapshot.payload } },
  );
  await audit.append({
    job_id: record.id,
    digest: record.digest,
    status: "succeeded",
    stage: "completed",
    actor: record.spec.context.actor,
    request_id: record.spec.context.request_id,
    completed_outputs: record.completed_outputs,
  });
  return record;
}

function stringField(payload: unknown, field: string): string | undefined {
  if (!payload || typeof payload !== "object" || Array.isArray(payload)) return undefined;
  const object = payload as Record<string, unknown>;
  if (typeof object[field] === "string") return object[field] as string;
  if (object.data && typeof object.data === "object" && !Array.isArray(object.data)) {
    const nested = object.data as Record<string, unknown>;
    if (typeof nested[field] === "string") return nested[field] as string;
  }
  return undefined;
}

function mergedResult(record: JobRecord, patch: Record<string, unknown>): Record<string, unknown> {
  const current = record.result && typeof record.result === "object" && !Array.isArray(record.result)
    ? record.result as Record<string, unknown>
    : {};
  return { ...current, ...patch };
}

export async function executeProductJob(options: {
  record: JobRecord;
  approvedDigest: string;
  client: GatewayClientLike;
  store: JobStore;
  audit: AuditLog;
}): Promise<JobRecord> {
  const { client, store, audit, approvedDigest } = options;
  let record = options.record;
  if (record.spec.version !== "2") {
    throw Object.assign(new Error("executeProductJob only accepts Product Job v2"), { code: "WRONG_JOB_VERSION" });
  }
  const spec: ProductJob = record.spec;

  if (approvedDigest !== record.digest) {
    await audit.append({ job_id: record.id, digest: record.digest, status: record.status, stage: "approval_rejected", code: "PLAN_DIGEST_MISMATCH" });
    throw Object.assign(new Error("The approved digest does not match the persisted product plan"), { code: "PLAN_DIGEST_MISMATCH" });
  }
  if (record.status === "released" || record.status === "awaiting_release_approval") return record;
  if (record.status === "failed") return record;
  if (record.status !== "prepared") {
    return transitionFailure(store, audit, record, "uncertain", "resume", {
      code: "UNCERTAIN_PRODUCT_STATE",
      message: "The previous product run crossed a filesystem/CAD write boundary; inspect its evidence before retrying",
    });
  }

  const available = new Set(await client.listTools());
  const missing = requiredTools(spec).filter((tool) => !available.has(tool));
  if (missing.length) {
    return transitionFailure(store, audit, record, "failed", "preflight", {
      code: "MISSING_GATEWAY_TOOLS",
      message: `Gateway is missing required tools: ${missing.join(", ")}`,
    }, { missing });
  }

  await audit.append({
    job_id: record.id, digest: record.digest, status: "running", stage: "start_product_job",
    actor: spec.context.actor, request_id: spec.context.request_id, approval_id: spec.context.approval_id,
  });
  record = await store.transition(record.id, "running", { stage: "start_product_job", ok: true });

  const health = await client.callTool("inventor_health");
  if (!health.ok) return transitionFailure(store, audit, record, "failed", "health", resultError(health), health);
  record = await store.transition(record.id, "running", { stage: "health", ok: true, evidence: health.payload });

  let productPath: string;
  if (spec.source.mode === "clone_recode") {
    const cloneArguments = {
      sourceRoot: spec.source.source_root,
      destinationRoot: spec.source.destination_root,
      topDocument: spec.source.top_document,
      sourceCode: spec.source.source_code,
      targetCode: spec.source.target_code,
      updateIproperties: spec.source.update_iproperties,
    };
    const clonePlan = await client.callTool("inventor_vent_clone_recode_product", { ...cloneArguments, dryRun: true });
    if (!clonePlan.ok || payloadObject(clonePlan)?.pass !== true) {
      return transitionFailure(store, audit, record, "failed", "clone_preflight",
        clonePlan.ok ? { code: "CLONE_PREFLIGHT_FAILED", message: "Clone/recode dry-run reported blockers" } : resultError(clonePlan), clonePlan);
    }
    record = await store.transition(record.id, "running", { stage: "clone_preflight", ok: true, evidence: clonePlan.payload });

    const cloned = await client.callTool("inventor_vent_clone_recode_product", { ...cloneArguments, dryRun: false });
    if (!cloned.ok) {
      return transitionFailure(store, audit, record, uncertainTransport(cloned) ? "uncertain" : "failed", "clone_recode", resultError(cloned), cloned);
    }
    productPath = stringField(cloned.payload, "top_document") ?? "";
    if (!productPath) {
      return transitionFailure(store, audit, record, "uncertain", "clone_recode", {
        code: "CLONE_EVIDENCE_INVALID",
        message: "Clone succeeded but did not return the copied top_document path",
      }, cloned.payload);
    }
    record = await store.transition(record.id, "running", { stage: "clone_recode", ok: true, evidence: cloned.payload }, {
      result: mergedResult(record, { clone: cloned.payload, product_path: productPath }),
    });
  } else {
    productPath = spec.source.product_path;
  }

  const stepResults = new Map<string, unknown>();
  const constructionContext = {
    variables: spec.recipe.variables,
    jobPaths: {
      product_path: productPath,
      product_root: spec.release.product_root,
      destination_root: spec.source.mode === "clone_recode" ? spec.source.destination_root : undefined,
      target_code: spec.source.mode === "clone_recode" ? spec.source.target_code : undefined,
      product_code: spec.release.product_code,
    },
  };
  for (const [index, step] of spec.recipe.construction_steps.entries()) {
    let call: { tool: string; arguments: Record<string, unknown> };
    try { call = constructionToolCall(step, stepResults, constructionContext); }
    catch (error) {
      return transitionFailure(store, audit, record, "failed", `construction_${index}`, {
        code: "RECIPE_REFERENCE_ERROR", message: error instanceof Error ? error.message : String(error),
      });
    }
    const result = await client.callTool(call.tool, call.arguments);
    if (!result.ok) {
      return transitionFailure(store, audit, record, uncertainTransport(result) ? "uncertain" : "failed", `construction_${index}`, resultError(result), result);
    }
    stepResults.set(step.id, result.payload);
    record = await store.transition(record.id, "running", {
      stage: `construction_${index}_${step.id}`, ok: true,
      evidence: { kind: step.kind, tool: call.tool, result: result.payload },
    });
  }

  const opened = await client.callTool("inventor_vent_open_product", { path: productPath });
  if (!opened.ok) return transitionFailure(store, audit, record, "failed", "open_product", resultError(opened), opened);
  record = await store.transition(record.id, "running", { stage: "open_product", ok: true, evidence: opened.payload });

  const baseline = await client.callTool("inventor_get_mass_properties");
  if (!baseline.ok) return transitionFailure(store, audit, record, "failed", "baseline", resultError(baseline), baseline);
  record = await store.transition(record.id, "running", { stage: "baseline", ok: true, evidence: baseline.payload });

  const mutations = resolveRecipeBindings(spec.recipe);
  const transactionalChecks = [
    ...spec.checks,
    ...spec.mounting_checks.map((check) => ({
      kind: "mounting_pattern",
      occurrence: check.occurrence,
      plane: check.plane,
      hole_count: check.hole_count,
      bolt_circle_diameter_mm: check.bolt_circle_diameter_mm,
      hole_diameter_mm: check.hole_diameter_mm,
      center_mm: check.center_mm,
      tolerance_mm: check.tolerance_mm,
      angle_tolerance_deg: check.angle_tolerance_deg,
    })),
  ];
  const executed = await client.callTool("inventor_vent_execute_plan", { mutations, checks: transactionalChecks, dryRun: false });
  if (!executed.ok) {
    return transitionFailure(store, audit, record, uncertainTransport(executed) ? "uncertain" : "failed", "execute_recipe", resultError(executed), executed);
  }
  const execution = payloadObject(executed);
  if (execution?.pass !== true || execution.committed !== true) {
    return transitionFailure(store, audit, record, "failed", "recipe_acceptance", {
      code: "ACCEPTANCE_FAILED",
      message: "Recipe bindings were rolled back because rebuild or acceptance failed",
    }, executed.payload);
  }
  record = await store.transition(record.id, "model_committed", { stage: "execute_recipe", ok: true, evidence: executed.payload }, {
    result: mergedResult(record, { execution: executed.payload, product_path: productPath }),
  });

  const saved = await client.callTool("inventor_vent_save_product", { productRoot: spec.release.product_root });
  if (!saved.ok || payloadObject(saved)?.pass !== true) {
    return transitionFailure(store, audit, record, uncertainTransport(saved) ? "uncertain" : "failed", "save_product",
      saved.ok ? { code: "PRODUCT_SAVE_FAILED", message: "Product tree save did not pass verification" } : resultError(saved), saved);
  }
  record = await store.transition(record.id, "saved", { stage: "save", ok: true, evidence: saved.payload });

  for (const [index, partPath] of spec.sheet_metal_checks.entries()) {
    const partOpened = await client.callTool("inventor_vent_open_product", { path: partPath });
    if (!partOpened.ok) return transitionFailure(store, audit, record, "failed", `sheet_metal_open_${index}`, resultError(partOpened), partOpened);
    const checked = await client.callTool("inventor_vent_check_part");
    if (!checked.ok || payloadObject(checked)?.pass !== true) {
      return transitionFailure(store, audit, record, "failed", `sheet_metal_check_${index}`,
        checked.ok ? { code: "SHEET_METAL_CHECK_FAILED", message: "Sheet-metal model gate did not pass" } : resultError(checked), checked);
    }
    record = await store.transition(record.id, "saved", {
      stage: `sheet_metal_check_${index}`, ok: true, evidence: { path: partPath, result: checked.payload },
    });
  }
  record = await store.transition(record.id, "exporting", { stage: "outputs_start", ok: true });

  for (let index = record.completed_outputs; index < spec.outputs.length; index++) {
    try {
      const evidence = await runOutput(client, spec.outputs[index], productPath);
      record = await store.transition(record.id, "exporting", { stage: `output_${index}`, ok: true, evidence }, { completed_outputs: index + 1 });
    } catch (error) {
      const toolResult = error && typeof error === "object" && "toolResult" in error
        ? (error as { toolResult: GatewayToolResult }).toolResult : undefined;
      return transitionFailure(store, audit, record, toolResult && uncertainTransport(toolResult) ? "uncertain" : "failed", `output_${index}`, {
        code: toolResult?.error?.code ?? "OUTPUT_FAILED", message: error instanceof Error ? error.message : String(error),
      }, toolResult);
    }
  }

  const reopened = await client.callTool("inventor_vent_open_product", { path: productPath });
  if (!reopened.ok) return transitionFailure(store, audit, record, "uncertain", "reopen_for_final_snapshot", resultError(reopened), reopened);
  const finalSnapshot = await client.callTool("inventor_get_mass_properties");
  if (!finalSnapshot.ok) return transitionFailure(store, audit, record, "uncertain", "final_snapshot", resultError(finalSnapshot), finalSnapshot);

  const release = await client.callTool("inventor_vent_build_release", {
    product_root: spec.release.product_root,
    dxf_dir: spec.release.dxf_dir,
    pdf_dir: spec.release.pdf_dir,
    output_dir: spec.release.output_dir,
    product_code: spec.release.product_code,
    minimum_pdf_count: spec.release.minimum_pdf_count,
    required_name_pattern: spec.release.required_name_pattern,
    require_clone_manifest: spec.release.require_clone_manifest,
    require_dxf_manifest: spec.release.require_dxf_manifest,
  });
  if (!release.ok || payloadObject(release)?.pass !== true) {
    return transitionFailure(store, audit, record, release.ok ? "failed" : (uncertainTransport(release) ? "uncertain" : "failed"), "build_release",
      release.ok ? { code: "RELEASE_VALIDATION_FAILED", message: "DXF/folder release checks did not pass" } : resultError(release), release);
  }
  const releaseDigest = stringField(release.payload, "release_digest") ?? "";
  if (!/^[a-f0-9]{64}$/.test(releaseDigest)) {
    return transitionFailure(store, audit, record, "uncertain", "build_release", {
      code: "RELEASE_DIGEST_MISSING", message: "Release package passed but returned no valid SHA-256 digest",
    }, release.payload);
  }
  record = await store.transition(record.id, "awaiting_release_approval", {
    stage: "awaiting_release_approval", ok: true,
    evidence: { final_snapshot: finalSnapshot.payload, release: release.payload },
  }, {
    release_digest: releaseDigest,
    result: mergedResult(record, { final_snapshot: finalSnapshot.payload, release: release.payload, product_path: productPath }),
  });
  await audit.append({
    job_id: record.id, digest: record.digest, release_digest: releaseDigest,
    status: record.status, stage: "awaiting_release_approval", actor: spec.context.actor,
  });
  return record;
}

export async function approveProductRelease(options: {
  record: JobRecord;
  releaseDigest: string;
  approvedBy: string;
  client: GatewayClientLike;
  store: JobStore;
  audit: AuditLog;
}): Promise<JobRecord> {
  const { client, store, audit, releaseDigest, approvedBy } = options;
  let record = options.record;
  if (record.spec.version !== "2") throw Object.assign(new Error("Release approval requires Product Job v2"), { code: "WRONG_JOB_VERSION" });
  if (record.status === "released") return record;
  if (record.status !== "awaiting_release_approval") {
    throw Object.assign(new Error(`Job is not awaiting release approval: ${record.status}`), { code: "WRONG_JOB_STATUS" });
  }
  if (releaseDigest !== record.release_digest) {
    await audit.append({ job_id: record.id, status: record.status, stage: "release_approval_rejected", code: "RELEASE_DIGEST_MISMATCH" });
    throw Object.assign(new Error("The approved release digest does not match the generated package"), { code: "RELEASE_DIGEST_MISMATCH" });
  }
  const releaseEvidence = record.result && typeof record.result === "object" && !Array.isArray(record.result)
    ? (record.result as Record<string, unknown>).release : undefined;
  const published = await client.callTool("inventor_vent_publish_release", {
    job_id: record.id,
    release_digest: releaseDigest,
    approved_by: approvedBy,
    product_code: record.spec.release.product_code,
    release: releaseEvidence,
  });
  if (!published.ok) {
    return transitionFailure(store, audit, record, uncertainTransport(published) ? "uncertain" : "failed", "publish_release", resultError(published), published);
  }
  record = await store.transition(record.id, "released", { stage: "released", ok: true, evidence: published.payload }, {
    approved_by: approvedBy,
    result: mergedResult(record, { notification: published.payload }),
  });
  await audit.append({
    job_id: record.id, digest: record.digest, release_digest: releaseDigest,
    status: "released", stage: "released", approved_by: approvedBy,
  });
  return record;
}
