import { describe, it, expect } from "vitest";
import { formatModelName } from "../lib/format-model-name";

describe("formatModelName", () => {
  it("returns the model id unchanged by default", () => {
    expect(formatModelName("llama-3.1-8b", "swarm", false, {})).toBe(
      "llama-3.1-8b",
    );
  });

  it("prefers an explicit display name over the model id", () => {
    expect(
      formatModelName("cloud/openai/gpt-4o", "cloud", false, {}, undefined, "My GPT"),
    ).toBe("My GPT");
  });

  it("strips the cloud/ origin prefix when hideOriginPrefix is set", () => {
    expect(formatModelName("cloud/openai/gpt-4o", "cloud", true, {})).toBe(
      "openai/gpt-4o",
    );
  });

  it("strips the managed/ origin prefix when hideOriginPrefix is set", () => {
    expect(formatModelName("managed/host/llama", "swarm", true, {})).toBe(
      "host/llama",
    );
  });

  it("maps a managed agent segment to its display name when prefixes are shown", () => {
    expect(
      formatModelName("managed/host/llama", "swarm", false, {
        host: "My Workstation",
      }),
    ).toBe("managed/My Workstation/llama");
  });

  it("leaves managed ids without a slash untouched", () => {
    expect(formatModelName("managed/llama", "swarm", false, { host: "WS" })).toBe(
      "managed/llama",
    );
  });

  it("leaves managed ids untouched when the agent has no display name", () => {
    expect(formatModelName("managed/host/llama", "swarm", false, {})).toBe(
      "managed/host/llama",
    );
  });

  it("applies agent display names to the path when the prefix is hidden", () => {
    expect(
      formatModelName("managed/host/llama", "swarm", true, { host: "My Workstation" }),
    ).toBe("My Workstation/llama");
  });

  it("replaces a bare agent name when the prefix is hidden", () => {
    expect(formatModelName("host", "swarm", true, { host: "WS" })).toBe("WS");
  });

  it("does not replace a non-matching leading path segment", () => {
    expect(formatModelName("other/llama", "swarm", true, { host: "WS" })).toBe(
      "other/llama",
    );
  });

  it("prepends the source runtime display name", () => {
    expect(
      formatModelName("llama-3.1-8b", "swarm", false, {}, "RT Name"),
    ).toBe("RT Name / llama-3.1-8b");
  });

  it("combines prefix stripping and runtime name", () => {
    expect(
      formatModelName("cloud/openai/gpt-4o", "cloud", true, {}, "Runtime"),
    ).toBe("Runtime / openai/gpt-4o");
  });
});
