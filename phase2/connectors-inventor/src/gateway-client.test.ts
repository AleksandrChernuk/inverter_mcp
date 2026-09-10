import assert from "node:assert/strict";
import test from "node:test";
import { normalizeMcpToolResult } from "./gateway-client.js";

test("normalizer unwraps a successful MCP text payload", () => {
  const result = normalizeMcpToolResult("inventor_health", 200, true, {
    ok: true,
    result: { content: [{ type: "text", text: "{\"inventor_year\":2026}" }] },
  });
  assert.equal(result.ok, true);
  assert.deepEqual(result.payload, { inventor_year: 2026 });
});

test("normalizer treats nested Inventor ok=false as a tool error", () => {
  const result = normalizeMcpToolResult("inventor_set_parameter", 200, true, {
    ok: true,
    result: {
      content: [{
        type: "text",
        text: "{\"ok\":false,\"error\":{\"code\":\"INVALID_ARGUMENT\",\"message\":\"parameter missing\"}}",
      }],
    },
  });
  assert.equal(result.ok, false);
  assert.equal(result.error?.code, "INVENTOR_TOOL_ERROR");
  assert.match(result.error?.message ?? "", /parameter missing/);
});

test("normalizer rejects malformed gateway envelopes", () => {
  const result = normalizeMcpToolResult("inventor_health", 200, true, { hello: "world" });
  assert.equal(result.ok, false);
  assert.equal(result.error?.code, "INVALID_GATEWAY_RESPONSE");
});

test("normalizer preserves an uncertain backend failure", () => {
  const result = normalizeMcpToolResult("inventor_vent_execute_plan", 503, false, {
    ok: false,
    error: { code: "BACKEND_UNAVAILABLE", message: "pipe closed during tool call" },
    uncertain: true,
  });
  assert.equal(result.ok, false);
  assert.equal(result.uncertain, true);
  assert.equal(result.error?.code, "BACKEND_UNAVAILABLE");
});
