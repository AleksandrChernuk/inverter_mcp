import { createHash, randomUUID } from "node:crypto";
import { appendFile, mkdir, readFile, rename, writeFile } from "node:fs/promises";
import { homedir } from "node:os";
import path from "node:path";
import { engineeringJobSchema, jobIdSchema, type EngineeringJob, type JobEvent, type JobRecord, type JobStatus } from "./contracts.js";
import { verifyRecipeApproval } from "./product-recipe.js";

function canonicalize(value: unknown): unknown {
  if (Array.isArray(value)) return value.map(canonicalize);
  if (value && typeof value === "object") {
    return Object.fromEntries(
      Object.entries(value as Record<string, unknown>)
        .sort(([left], [right]) => left.localeCompare(right))
        .map(([key, item]) => [key, canonicalize(item)]),
    );
  }
  return value;
}

export function digestJob(spec: EngineeringJob): string {
  return createHash("sha256").update(JSON.stringify(canonicalize(spec))).digest("hex");
}

export function defaultStateDirectory(environment: NodeJS.ProcessEnv = process.env): string {
  if (environment.JOB_STATE_DIR) return path.resolve(environment.JOB_STATE_DIR);
  if (process.platform === "win32" && environment.LOCALAPPDATA) {
    return path.join(environment.LOCALAPPDATA, "KVZ", "inventor-connector", "jobs");
  }
  if (environment.XDG_STATE_HOME) {
    return path.join(environment.XDG_STATE_HOME, "kvz-inventor", "jobs");
  }
  return path.join(homedir(), ".local", "state", "kvz-inventor", "jobs");
}

export class JobStore {
  constructor(readonly directory: string = defaultStateDirectory()) {}

  async prepare(input: unknown): Promise<JobRecord> {
    const spec = engineeringJobSchema.parse(input);
    if (spec.version === "2") verifyRecipeApproval(spec.recipe);
    const now = new Date().toISOString();
    const record: JobRecord = {
      id: randomUUID(),
      digest: digestJob(spec),
      status: "prepared",
      created_at: now,
      updated_at: now,
      spec,
      events: [{ at: now, stage: "prepared", ok: true }],
      completed_outputs: 0,
    };
    await this.write(record);
    return record;
  }

  async get(id: string): Promise<JobRecord> {
    jobIdSchema.parse(id);
    const file = this.pathFor(id);
    const raw = JSON.parse(await readFile(file, "utf8")) as JobRecord;
    engineeringJobSchema.parse(raw.spec);
    return raw;
  }

  async transition(
    id: string,
    status: JobStatus,
    event: Omit<JobEvent, "at">,
    patch: Partial<Pick<JobRecord, "completed_outputs" | "release_digest" | "approved_by" | "result" | "error">> = {},
  ): Promise<JobRecord> {
    const record = await this.get(id);
    const now = new Date().toISOString();
    const next: JobRecord = {
      ...record,
      ...patch,
      status,
      updated_at: now,
      events: [...record.events, { at: now, ...event }].slice(-256),
    };
    await this.write(next);
    return next;
  }

  private async write(record: JobRecord): Promise<void> {
    await mkdir(this.directory, { recursive: true });
    const destination = this.pathFor(record.id);
    const temporary = path.join(this.directory, `.${record.id}.${process.pid}.tmp`);
    await writeFile(temporary, `${JSON.stringify(record, null, 2)}\n`, { encoding: "utf8", mode: 0o600 });
    await rename(temporary, destination);
  }

  private pathFor(id: string): string {
    return path.join(this.directory, `${jobIdSchema.parse(id)}.json`);
  }
}

export class AuditLog {
  constructor(private readonly filePath: string) {}

  async append(event: Record<string, unknown>): Promise<void> {
    await mkdir(path.dirname(this.filePath), { recursive: true });
    const line = `${JSON.stringify({ at: new Date().toISOString(), ...event })}\n`;
    await appendFile(this.filePath, line, { encoding: "utf8", mode: 0o600 });
  }
}
