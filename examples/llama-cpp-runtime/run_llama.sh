#!/usr/bin/env bash
# ──────────────────────────────────────────────────────────────────────
# Unswarm Script Runtime: llama.cpp launcher
#
# This script starts a llama-server (llama.cpp) instance that Unswarm
# manages as a Script runtime. The agent starts/stops the process via
# PID control; the backend discovers models via the OpenAI-compatible
# /v1/models endpoint.
#
# The script is self-contained — Unswarm does not inject environment
# variables. Configure PORT to match the containerPort used at
# registration.
#
# Tunable variables (edit directly in this script):
#   PORT           — Port to listen on (must match registration)
#   MODEL          — Path to GGUF model file
#   LLAMA_SERVER   — Path to llama-server binary
#   CTX            — Context size (default: 4096)
#   NGL            — GPU layers (default: 99)
#   BATCH          — Batch size (default: 512)
#   THREADS        — CPU threads (default: auto)
#   EXTRA_FLAGS    — Additional llama-server flags
#
# Usage:
#   1. Place this script in your agent's scripts_dir
#   2. Register it via Fleet UI or API (runtimeKind: "script",
#      containerPort matching PORT below)
#   3. Unswarm will spawn it and manage the process
#
# Prerequisites:
#   - llama.cpp built with llama-server support
#   - llama-server in PATH or set LLAMA_SERVER below
# ──────────────────────────────────────────────────────────────────────
set -euo pipefail

# ── Configuration ───────────────────────────────────────────────────
# Edit these values to match your setup. This script is self-contained —
# Unswarm does not inject any environment variables.

# Model path — first shard; llama.cpp auto-discovers shards in same dir.
MODEL="/models/llama-3.1-70b-Q4_K_M.gguf"

# llama-server binary — leave empty for auto-detect (Vulkan → plain → PATH).
LLAMA_SERVER=""

# Context size — scales KV linearly; keep small on VRAM-constrained setups.
CTX=4096

# GPU offload layers — 99 = all dense/attention layers to GPU.
# Set 0 for CPU-only, or match your layer count for partial offload.
NGL=99

# Batch size — larger = faster prefill, more VRAM.
BATCH=512

# CPU threads — default to nproc; override for MoE or thread-constrained setups.
THREADS=$(nproc)

# Additional llama-server flags (space-separated).
# Examples: "--ctx-size 8192 --n-gpu-layers 99 --flash-attn"
EXTRA_FLAGS="--jinja --fit off"

# Host to bind to
HOST="0.0.0.0"

# Port — must match the containerPort used at registration.
PORT=8080

# ── Binary resolution ───────────────────────────────────────────────
# Prefer Vulkan build (for MI50/gfx906), fallback to plain llama.cpp,
# then to any llama-server in PATH.
BIN_VULKAN="/home/user/llama.cpp-vulkan/build/bin/llama-server"
BIN_FALLBACK="/home/user/llama.cpp/build/bin/llama-server"

if [[ -n "$LLAMA_SERVER" ]]; then
    # Explicit override — use as-is
    :
elif [[ -x "$BIN_VULKAN" ]]; then
    LLAMA_SERVER="$BIN_VULKAN"
elif [[ -x "$BIN_FALLBACK" ]]; then
    LLAMA_SERVER="$BIN_FALLBACK"
elif command -v llama-server &>/dev/null; then
    LLAMA_SERVER="llama-server"
else
    echo "[run_llama.sh] ERROR: No llama-server binary found" >&2
    echo "  Set LLAMA_SERVER=/path/to/llama-server" >&2
    echo "  Expected: $BIN_VULKAN" >&2
    echo "  Fallback: $BIN_FALLBACK" >&2
    exit 1
fi

# ── Logging ─────────────────────────────────────────────────────────
log() {
    echo "[$(date '+%Y-%m-%d %H:%M:%S')] [run_llama.sh] $*"
}

log "══════════════════════════════════════════════════════════════════"
log " Unswarm Script Runtime — llama.cpp launcher"
log "══════════════════════════════════════════════════════════════════"
log " Model:        $MODEL"
log " Binary:       $LLAMA_SERVER"
log " Context:      $CTX"
log " GPU layers:   $NGL"
log " Batch:        $BATCH"
log " Threads:      $THREADS"
log " Host:         $HOST:$PORT"
log " PID:          $$"
log " Extra flags:  $EXTRA_FLAGS"
log "══════════════════════════════════════════════════════════════════"

# ── Pre-flight checks ──────────────────────────────────────────────

# 1) Model file exists
if [[ ! -f "$MODEL" ]]; then
    log "ERROR: Model file not found: $MODEL"
    exit 1
fi

# Check shard count if multi-shard (model name contains "of-000")
SHARD_DIR="$(dirname "$MODEL")"
BASENAME="$(basename "$MODEL")"
if [[ "$BASENAME" == *"-of-"* ]]; then
    # Extract pattern: everything before "-NNNNN-of-"
    SHARD_PATTERN="${BASENAME%%-[0-9]*-of-*}"
    SHARD_COUNT=$(ls -1 "$SHARD_DIR"/"${SHARD_PATTERN}"-*.gguf 2>/dev/null | wc -l)
    log "Shards found: $SHARD_COUNT ($BASENAME)"
    if [[ "$SHARD_COUNT" -lt 2 ]]; then
        log "WARN: Expected multiple shards, found $SHARD_COUNT"
    fi
fi

# 2) System resources
AVAIL_MB=$(free -m 2>/dev/null | awk '/^Mem:/ {print $7}' || echo "")
if [[ -n "$AVAIL_MB" ]]; then
    log "Available RAM: ${AVAIL_MB} MB"
    if [[ "$AVAIL_MB" -lt 8000 ]]; then
        log "WARN: Available RAM <8GB — large models may OOM"
    fi
fi

# 3) Disk space for model
AVAIL_DISK_GB=$(df -BG "$SHARD_DIR" 2>/dev/null | awk 'NR==2 {print $4}' | tr -d 'G' || echo "")
if [[ -n "$AVAIL_DISK_GB" && "$AVAIL_DISK_GB" -lt 10 ]]; then
    log "WARN: Disk free <10GB on model filesystem"
fi

# ── Signal handling ─────────────────────────────────────────────────
# When Unswarm stops this runtime, it sends SIGTERM to the process
# group (negative PID). We catch it and shut down gracefully.
SERVER_PID=""

cleanup() {
    log "Received shutdown signal, stopping llama-server..."
    if [[ -n "$SERVER_PID" ]] && kill -0 "$SERVER_PID" 2>/dev/null; then
        kill -TERM "$SERVER_PID" 2>/dev/null || true
        # Wait up to 10 seconds for graceful shutdown
        for i in $(seq 1 10); do
            if ! kill -0 "$SERVER_PID" 2>/dev/null; then
                break
            fi
            sleep 1
        done
        # Force kill if still running
        if kill -0 "$SERVER_PID" 2>/dev/null; then
            log "Force killing llama-server (PID $SERVER_PID)"
            kill -9 "$SERVER_PID" 2>/dev/null || true
        fi
    fi
    log "Shutdown complete"
    exit 0
}

trap cleanup SIGTERM SIGINT SIGHUP

# ── Health wait loop ────────────────────────────────────────────────
wait_for_health() {
    local url="http://127.0.0.1:${PORT}/v1/models"
    local max_attempts=60
    local attempt=0

    log "Waiting for health check at $url"

    while [[ $attempt -lt $max_attempts ]]; do
        if curl -sf "$url" >/dev/null 2>&1; then
            log "Health check passed on attempt $((attempt + 1))"
            return 0
        fi
        attempt=$((attempt + 1))
        sleep 2
    done

    log "ERROR: Health check failed after $((max_attempts * 2))s"
    return 1
}

# ── Launch llama-server ─────────────────────────────────────────────
log "Launching llama-server..."

# Build command array (proper quoting for paths with spaces)
CMD=(
    "$LLAMA_SERVER"
    -m "$MODEL"
    --host "$HOST"
    --port "$PORT"
    -c "$CTX"
    -ngl "$NGL"
    -b "$BATCH"
    --threads "$THREADS"
)

# Append extra flags (word-split intentionally)
# shellcheck disable=SC2206
CMD+=($EXTRA_FLAGS)

log "Command: ${CMD[*]}"

"${CMD[@]}" &
SERVER_PID=$!

log "llama-server started with PID $SERVER_PID"

# Wait for the server to become healthy before returning.
# Unswarm expects the server to be ready after this script exits.
if ! wait_for_health; then
    log "Server failed to start, cleaning up"
    kill -TERM "$SERVER_PID" 2>/dev/null || true
    exit 1
fi

log "══════════════════════════════════════════════════════════════════"
log " llama-server is healthy and serving on port $PORT"
log " Unswarm will now discover models via /v1/models"
log "══════════════════════════════════════════════════════════════════"

# ── Keep alive ──────────────────────────────────────────────────────
# Block until the server process exits or we receive a signal.
# The shell waits for background jobs by default, but we explicitly
# wait so trap signals are delivered during the wait.
wait "$SERVER_PID" 2>/dev/null
EXIT_CODE=$?

log "llama-server exited with code $EXIT_CODE"
exit "$EXIT_CODE"
