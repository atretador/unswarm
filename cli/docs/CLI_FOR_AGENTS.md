# Unswarm CLI Reference for AI Agents

This document teaches AI coding agents how to use the `unswarm` CLI to manage an Unswarm LLM inference control plane.

## Quick Start

```bash
# All commands require a backend URL and API key
export UNSWARM_URL="http://localhost:22301"
export UNSWARM_API_KEY="ck_..."   # control-plane scope key

# Or pass per-command:
unswarm --url http://localhost:22301 --api-key "ck_..." models list

# JSON output for machine parsing:
unswarm --output json models list
```

## Core Concepts

- **Names everywhere**: Commands accept human-readable names, not just UUIDs. Fuzzy matching works.
- **Scope-aware API keys**: Keys have scope — `usk_` (inference), `ak_` (agent), `ck_` (control-plane). Use a control-plane key for management commands, inference key for `/v1` endpoints.
- **Access grants**: Inference keys can be restricted to specific providers/models via access grants.
- **Read-modify-write**: Many config commands read existing state, modify, and write back. Use `--dry-run` to preview.

## Command Structure

```
unswarm <group> <action> [resource] [flags]
```

Groups: `models`, `runtimes`, `containers`, `agents`, `benchmarks`, `prompts`, `scripts`, `queue`, `health`, `stats`, `logs`, `metrics`, `settings`, `users`, `apikeys`, `cloud-providers`, `router-profiles`, `provider-model-catalog`, `config`, `setup`

## Global Flags

| Flag | Env Var | Description |
|------|---------|-------------|
| `--url` | `UNSWARM_URL` | Backend URL |
| `--api-key` | `UNSWARM_API_KEY` | API key for authentication |
| `--output json\|csv\|table\|raw` | — | Output format |
| `-y`, `--yes` | — | Skip confirmation prompts |
| `--dry-run` | — | Preview without executing |
| `--quiet` | — | Non-interactive mode (errors on prompts) |
| `--no-color` | — | Disable colorized output |
| `--insecure` | — | Allow plaintext HTTP to non-loopback |

## Model Operations

```bash
# List all models
unswarm models list
unswarm models list --output json        # JSON for parsing
unswarm models list --status ready       # filter by status

# Get model details
unswarm models get "Qwen 3.6 35B"       # by name
unswarm models get abc123                # by ID

# Create a model
unswarm models create --name "my-model" --family llama --parameter-size 7b --quantization q4_k_m

# Update a model
unswarm models update "my-model" --display-name "My Custom Model"

# Delete a model
unswarm models delete "my-model"

# Test a model with streaming
unswarm models test-chat "my-model" "Hello, what model are you?"
unswarm models test-chat "my-model" "Hello" --system "You are helpful" --temperature 0.7

# Compare models side-by-side
unswarm models compare model-a model-b
```

## Runtime / Container Operations

```bash
# List runtimes
unswarm runtimes list
unswarm runtimes list --output json

# Start/stop/restart
unswarm runtimes start "GPU Runtime"
unswarm runtimes stop "GPU Runtime"
unswarm runtimes restart "GPU Runtime"

# Rediscover models from a runtime
unswarm runtimes rediscover "GPU Runtime"

# List containers
unswarm containers list
unswarm containers list --output json
```

## Agent Operations

```bash
# List connected agents
unswarm agents list

# View agent stats (CPU, RAM, GPU)
unswarm agents stats host              # local agent
unswarm agents stats caf-agent         # remote agent

# List containers on an agent
unswarm agents containers "caf-agent"
```

## API Key Operations

```bash
# List keys
unswarm apikeys list
unswarm apikeys list --output json

# Create inference key
unswarm apikeys create --name my-key

# Create agent key (bound to specific agent)
unswarm apikeys create-agent --name agent-key --bound-agent "caf-agent"

# Rotate key (returns new secret, old invalidated)
unswarm apikeys rotate my-key

# Get/set access grants (restrict which models a key can use)
unswarm apikeys get-access my-key
unswarm apikeys set-access my-key --providers '["OpenAI"]' --models '["cloud/openai/gpt-4o"]'

# Revoke a key
unswarm apikeys revoke my-key
```

## Config Generation for Coding Agents

Generate config files for external coding agents from your Unswarm data:

```bash
# Generate opencode config (~/.config/opencode/opencode.jsonc)
unswarm config generate-opencode --key my-inference-key

# Generate pi.dev config (~/.pi/agent/models.json)
unswarm config generate-pi --key my-inference-key

# Preview without rotating or writing
unswarm config generate-opencode --key my-inference-key --dry-run
unswarm config generate-pi --key my-inference-key --dry-run

# Project-local config
unswarm config generate-opencode --key my-inference-key --target project
```

**How it works:**
1. Resolves API key by name, verifies it's inference-scope
2. Rotates key to capture fresh secret (old invalidated)
3. Fetches access grants to filter which models/providers appear
4. Queries provider model catalog + context windows from `/v1/models`
5. Builds config file, writes with `.bak` backup of existing

**Flags:** `--key` (required), `--target global|project`, `--dry-run`, `-y` (skip confirm)

## Cloud Provider Operations

```bash
# List providers
unswarm cloud-providers list

# Fetch models from a provider
unswarm cloud-providers fetch-models openai

# OAuth flow (interactive)
unswarm cloud-providers oauth openai
unswarm cloud-providers oauth-start openai
unswarm cloud-providers oauth-poll openai
```

## Router Profile Operations

```bash
# List profiles
unswarm router-profiles list

# Add/remove entries
unswarm router-profiles add-entry my-profile --model "Qwen 3.6 35B" --priority 1
unswarm router-profiles add-entry my-profile --model gpt-4o --thinking-effort high
unswarm router-profiles remove-entry my-profile --model "Qwen 3.6 35B"

# Set active model for manual routing
unswarm router-profiles set-active-entry my-profile --model-id "Qwen 3.6 35B"
```

## Benchmarks

```bash
# Run a benchmark
unswarm benchmarks run mistral-7b

# Wait for completion
unswarm benchmarks run mistral-7b --wait

# Compare results
unswarm benchmarks compare llama-7b mistral-7b

# List with filters
unswarm benchmarks list --model llama-7b --status completed
```

## Prompts

```bash
# CRUD operations
unswarm prompts list
unswarm prompts get summarize-code
unswarm prompts create --name my-prompt --file prompt.txt
unswarm prompts update my-prompt --file updated.txt
unswarm prompts delete my-prompt

# Versioning
unswarm prompts versions my-prompt
unswarm prompts diff my-prompt --from 1 --to 3
unswarm prompts rollback my-prompt --version 2
```

## Monitoring

```bash
# Health dashboard
unswarm health
unswarm health --summary               # single line
unswarm health --watch                  # auto-refresh

# Stats
unswarm stats --watch

# Logs
unswarm logs follow                     # SSE streaming
unswarm logs follow --source runtime-1  # filtered
unswarm logs search "connection timeout"
unswarm logs last 50

# Metrics
unswarm metrics today
unswarm metrics last 7d
unswarm metrics models
unswarm metrics providers
```

## Settings

```bash
# Read settings
unswarm settings get
unswarm settings get request-timeout

# Update settings
unswarm settings set request-timeout 30
```

## Inference Queue

```bash
# View queue
unswarm queue list
unswarm queue list --status pending
unswarm queue snapshot

# Cancel/release
unswarm queue cancel <id>
unswarm queue release <id>
```

## Provider Model Catalog

```bash
# List all providers and their servable models
unswarm provider-model-catalog
unswarm provider-model-catalog --output json
```

## Parsing JSON Output

All commands support `--output json`. The JSON structure varies by command:

```bash
# Models list returns an array of model objects
unswarm models list --output json | jq '.[] | .id'

# API keys list returns an array
unswarm apikeys list --output json | jq '.[] | {name: .name, scope: .scope}'

# Health returns a structured object
unswarm health --output json | jq '.runtimes | length'
```

## Common Workflows

### Register a new model from a running container

```bash
unswarm runtimes register          # interactive wizard
unswarm runtimes rediscover "My Runtime"   # discover models
unswarm models list --status ready         # verify
```

### Set up a restricted inference key for a coding agent

```bash
# 1. Create the key
unswarm apikeys create --name agent-key

# 2. Restrict to specific providers/models
unswarm apikeys set-access agent-key \
  --providers '["OpenAI", "Local MI50"]' \
  --models '["cloud/openai/gpt-4o", "ling-3.0-flash"]'

# 3. Generate coding agent config
unswarm config generate-opencode --key agent-key
unswarm config generate-pi --key agent-key
```

### Rotate key and update all downstream configs

```bash
unswarm config generate-opencode --key my-key   # rotates + writes
unswarm config generate-pi --key my-key         # rotates + writes
```

### Monitor a specific model's usage

```bash
unswarm metrics models --output json | jq '.[] | select(.modelId == "my-model")'
unswarm metrics last 1d --models my-model
```

## Error Handling

- Commands exit with code 0 on success, non-zero on error
- `--quiet` mode suppresses prompts and returns errors as JSON
- API errors include `code`, `message`, `status`, and `hint` fields
- Use `--output json` to get machine-parseable error responses

## Tips for Agents

1. **Always use `--output json`** for programmatic consumption
2. **Use `-y` flag** to skip interactive confirmations
3. **Use `--dry-run`** before destructive operations
4. **Names work everywhere** — no need to look up IDs first
5. **The `config generate-*` commands handle key rotation** — you don't need to rotate separately
6. **Context window data** comes from `/v1/models` — cloud models may return 0 (unknown)
7. **Model IDs include container hashes** for local models — use them as-is, they route correctly
