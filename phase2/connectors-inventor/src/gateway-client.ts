import { z } from "zod";

const textContentSchema = z.object({
  type: z.string(),
  text: z.string().optional(),
}).passthrough();

const mcpToolResultSchema = z.object({
  content: z.array(textContentSchema).default([]),
  isError: z.boolean().optional(),
}).passthrough();

const gatewayCallEnvelopeSchema = z.object({
  ok: z.boolean(),
  result: mcpToolResultSchema.optional(),
  error: z.unknown().optional(),
  uncertain: z.boolean().optional(),
}).passthrough();

const gatewayToolsEnvelopeSchema = z.object({
  ok: z.boolean(),
  tools: z.array(z.object({ name: z.string() }).passthrough()).optional(),
  error: z.unknown().optional(),
}).passthrough();

export interface GatewayToolResult {
  ok: boolean;
  status: number;
  tool: string;
  payload?: unknown;
  error?: { code: string; message: string; detail?: unknown };
  uncertain?: boolean;
}

export interface GatewayClientLike {
  listTools(): Promise<string[]>;
  callTool(name: string, args?: Record<string, unknown>): Promise<GatewayToolResult>;
}

function messageFrom(value: unknown): string {
  if (typeof value === "string") return value;
  if (value && typeof value === "object" && "message" in value) {
    const message = (value as { message?: unknown }).message;
    if (typeof message === "string") return message;
  }
  try { return JSON.stringify(value); } catch { return String(value); }
}

function codeFrom(value: unknown): string | undefined {
  if (!value || typeof value !== "object" || !("code" in value)) return undefined;
  const code = (value as { code?: unknown }).code;
  return typeof code === "string" && code.length ? code : undefined;
}

function parseJson(text: string): unknown {
  try { return JSON.parse(text); } catch { return text; }
}

export function normalizeMcpToolResult(
  tool: string,
  status: number,
  httpOk: boolean,
  body: unknown,
): GatewayToolResult {
  const parsed = gatewayCallEnvelopeSchema.safeParse(body);
  if (!parsed.success) {
    return {
      ok: false,
      status,
      tool,
      error: {
        code: "INVALID_GATEWAY_RESPONSE",
        message: "Gateway response does not match the expected MCP envelope",
        detail: parsed.error.flatten(),
      },
    };
  }

  const envelope = parsed.data;
  const firstText = envelope.result?.content.find((item) => typeof item.text === "string")?.text;
  const payload = firstText === undefined ? envelope.result : parseJson(firstText);
  const payloadSaysFailure = Boolean(
    payload && typeof payload === "object" && "ok" in payload
      && (payload as { ok?: unknown }).ok === false,
  );
  const ok = httpOk && envelope.ok && envelope.result?.isError !== true && !payloadSaysFailure;

  if (ok) return { ok: true, status, tool, payload };

  const nestedError = payload && typeof payload === "object" && "error" in payload
    ? (payload as { error?: unknown }).error
    : envelope.error;
  return {
    ok: false,
    status,
    tool,
    payload,
    uncertain: envelope.uncertain === true,
    error: {
      code: payloadSaysFailure
        ? "INVENTOR_TOOL_ERROR"
        : codeFrom(envelope.error) ?? "GATEWAY_TOOL_ERROR",
      message: messageFrom(nestedError ?? `Tool ${tool} failed`),
      detail: nestedError,
    },
  };
}

export class GatewayClient implements GatewayClientLike {
  private readonly baseUrl: string;
  private readonly token: string;
  private readonly timeoutMs: number;
  private readonly fetchImpl: typeof fetch;

  constructor(options: {
    baseUrl: string;
    token?: string;
    timeoutMs?: number;
    fetchImpl?: typeof fetch;
  }) {
    const url = new URL(options.baseUrl);
    if (url.protocol !== "http:" && url.protocol !== "https:") {
      throw new Error("GATEWAY_URL must use http or https");
    }
    this.baseUrl = url.toString().replace(/\/$/, "");
    this.token = options.token ?? "";
    this.timeoutMs = options.timeoutMs ?? 120_000;
    this.fetchImpl = options.fetchImpl ?? fetch;
  }

  async listTools(): Promise<string[]> {
    const { response, body } = await this.request("/tools/list", { method: "GET" });
    const parsed = gatewayToolsEnvelopeSchema.safeParse(body);
    if (!response.ok || !parsed.success || !parsed.data.ok || !parsed.data.tools) {
      const detail = parsed.success ? parsed.data.error : parsed.error.flatten();
      throw new Error(`Gateway tool discovery failed (${response.status}): ${messageFrom(detail)}`);
    }
    return parsed.data.tools.map((item) => item.name);
  }

  async callTool(name: string, args: Record<string, unknown> = {}): Promise<GatewayToolResult> {
    try {
      const { response, body } = await this.request("/tools/call", {
        method: "POST",
        headers: { "content-type": "application/json" },
        body: JSON.stringify({ name, arguments: args }),
      });
      return normalizeMcpToolResult(name, response.status, response.ok, body);
    } catch (error) {
      const code = error instanceof Error && error.name === "AbortError"
        ? "GATEWAY_TIMEOUT"
        : "GATEWAY_UNAVAILABLE";
      return {
        ok: false,
        status: 0,
        tool: name,
        uncertain: true,
        error: { code, message: error instanceof Error ? error.message : String(error) },
      };
    }
  }

  private async request(path: string, init: RequestInit): Promise<{ response: Response; body: unknown }> {
    const controller = new AbortController();
    const timeout = setTimeout(() => controller.abort(), this.timeoutMs);
    try {
      const response = await this.fetchImpl(`${this.baseUrl}${path}`, {
        ...init,
        signal: controller.signal,
        headers: {
          ...(init.headers ?? {}),
          ...(this.token ? { authorization: `Bearer ${this.token}` } : {}),
        },
      });
      const text = await response.text();
      return { response, body: text.length ? parseJson(text) : {} };
    } finally {
      clearTimeout(timeout);
    }
  }
}
