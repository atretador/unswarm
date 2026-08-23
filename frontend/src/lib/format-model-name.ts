/**
 * Format a model name for display based on user settings.
 *
 * @param modelId - The full model ID (e.g. "cloud/openai/gpt-4o" or "llama-3.1-8b")
 * @param provider - The provider badge text (e.g. "openai", "local", "host")
 * @param hideOriginPrefix - Whether to strip "cloud/" or "managed/" prefix
 * @param agentDisplayNames - Map of agent names to display names
 * @returns Formatted display name
 */
export function formatModelName(
  modelId: string,
  provider: string,
  hideOriginPrefix: boolean,
  agentDisplayNames: Record<string, string>,
): string {
  let name = modelId;

  if (hideOriginPrefix) {
    // Strip "cloud/" prefix: "cloud/openai/gpt-4o" → "openai/gpt-4o"
    if (name.startsWith("cloud/")) {
      name = name.slice("cloud/".length);
    }
    // Strip "managed/" prefix: "managed/host/llama" → "host/llama"
    if (name.startsWith("managed/")) {
      name = name.slice("managed/".length);
    }
  }

  // Apply agent display name overrides for "managed/" paths
  // e.g. "managed/host/llama" → if agentDisplayNames["host"] = "My Workstation",
  // display "managed/My Workstation/llama"
  if (!hideOriginPrefix && name.startsWith("managed/")) {
    const rest = name.slice("managed/".length);
    const slashIdx = rest.indexOf("/");
    if (slashIdx >= 0) {
      const agentName = rest.slice(0, slashIdx);
      const modelName = rest.slice(slashIdx + 1);
      const displayName = agentDisplayNames[agentName];
      if (displayName) {
        name = `managed/${displayName}/${modelName}`;
      }
    }
  }

  // If origin prefix is hidden, also apply agent display names to the path
  if (hideOriginPrefix) {
    // Check for agent name at start of the path (after prefix stripped)
    for (const [agentName, displayName] of Object.entries(agentDisplayNames)) {
      if (name === agentName || name.startsWith(agentName + "/")) {
        name = name.replace(agentName, displayName);
        break;
      }
    }
  }

  return name;
}
