import assert from "node:assert/strict";
import test from "node:test";
import {
  constantTimeTokenMatch,
  DEFAULT_TOOL_ALLOWLIST,
  FixedWindowRateLimiter,
  normalizeMcpResult,
  parseAllowlist,
} from "./core.mjs";

test("default allowlist is curated and blocks arbitrary code", () => {
  const allow = parseAllowlist("");
  assert.equal(allow.size, DEFAULT_TOOL_ALLOWLIST.length);
  assert.equal(allow.has("inventor_vent_execute_plan"), true);
  assert.equal(allow.has("inventor_vent_save_product"), true);
  assert.equal(allow.has("inventor_send_code"), false);
});

test("configured allowlist replaces defaults", () => {
  assert.deepEqual([...parseAllowlist("inventor_health, inventor_check_interference")], [
    "inventor_health",
    "inventor_check_interference",
  ]);
});

test("bearer token comparison is exact", () => {
  assert.equal(constantTimeTokenMatch("secret", "Bearer secret"), true);
  assert.equal(constantTimeTokenMatch("secret", "Bearer wrong"), false);
  assert.equal(constantTimeTokenMatch("secret", "secret"), false);
  assert.equal(constantTimeTokenMatch("", undefined), true);
});

test("nested tool failures propagate to the HTTP envelope", () => {
  const result = normalizeMcpResult({
    content: [{ type: "text", text: "{\"ok\":false,\"error\":{\"message\":\"bad model\"}}" }],
  });
  assert.equal(result.ok, false);
  assert.equal(result.tool_payload.error.message, "bad model");
});

test("fixed-window limiter resets on the next minute", () => {
  let now = 0;
  const limiter = new FixedWindowRateLimiter(2, () => now);
  assert.equal(limiter.accept("client"), true);
  assert.equal(limiter.accept("client"), true);
  assert.equal(limiter.accept("client"), false);
  now = 60_000;
  assert.equal(limiter.accept("client"), true);
});
