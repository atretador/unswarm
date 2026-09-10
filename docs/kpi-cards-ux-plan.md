# KPI Cards — Implementation Plan (v2)

> **Oracle-reviewed. Changes from v1: No SSE, extend existing polling, fix backend first, extract components, handle zero-GPU and offline states.**

## Overview

Add per-host/model live system metrics KPI cards to the Swarm page expanded view:

1. **Agent-level metrics bar** — GPU utilization + VRAM %, host RAM %, host CPU %
2. **Per-container badges** — CPU % / RAM usage per container

## Architecture Decision: Extend Existing Polling (Not SSE)

The existing agents query (`listAgents`) already polls at 30s (`refetchInterval: 30_000` at Swarm line 1980). The Oracle recommended extending this instead of building a parallel SSE system:

- **Extend `GET /api/agents` response** to include a `telemetry` field with live metrics
- **Reduce poll interval to 10s** when any `AgentSection` is expanded (configurable via Settings)
- **Single data source** — no race conditions between two polling systems
- **Reuse existing React Query cache** — no new hook infrastructure needed

When no section is expanded, poll stays at 30s (or whatever current default is).

## Backend Changes (Must Come First)

### 1. Live Metrics Collection on Agent Side

The agent binary (Go) collects metrics locally. Currently it only reports static hardware info (`GpuInfo`, `TotalMemoryMb`, `CpuCores`). Need to add periodic collection of:

**Host metrics:**
- CPU %: parse `/proc/stat` on Linux, `GetSystemTimes` on Windows
- RAM %: `runtime.MemStats` or OS APIs

**GPU metrics (CLI parsing, no cgo):**
| Vendor | CLI | Utilization | Memory |
|--------|-----|-------------|--------|
| NVIDIA | `nvidia-smi --query-gpu=index,utilization.gpu,memory.used,memory.total --format=csv,noheader,nounits` | `utilization.gpu` | `memory.used/memory.total` |
| AMD | `amd-smi monitor --gfx --mem --json` or sysfs `/sys/class/drm/card*/device/gpu_busy_percent` | `gpu_busy_percent` | `mem_info_vram_used/total` |
| Intel | `intel_gpu_top -J -s 1000` | Per-engine busy (no single %) | No VRAM for iGPU (shared memory) |

Detection: try `nvidia-smi` → `amd-smi` → `intel_gpu_top` → no GPU.

**Container metrics:**
- Use `docker stats --no-stream --format '{{.CPUPerc}}\t{{.MemUsage}}'` per container
- Batching: single `docker stats` call for all containers, parse output
- Fallback: `docker inspect` for memory limit + cgroup v1/v2 for usage

### 2. Backend Contract

Add to `AgentConnection` model:
```csharp
public class AgentTelemetryData
{
    public HostMetrics Host { get; set; }
    public List<GPUMetrics> Gpus { get; set; }
    public Dictionary<string, ContainerMetrics> Containers { get; set; }
    public DateTime CollectedAt { get; set; }
}

public class HostMetrics
{
    public double CpuPercent { get; set; }
    public double RamPercent { get; set; }
    public long RamUsedMb { get; set; }
    public long RamTotalMb { get; set; }
}

public class GPUMetrics
{
    public int Index { get; set; }
    public string Name { get; set; }
    public string Vendor { get; set; }      // "nvidia" | "amd" | "intel"
    public double CorePercent { get; set; }  // -1 if unavailable
    public double MemoryPercent { get; set; } // -1 if unavailable (Intel iGPU)
    public long MemoryUsedMb { get; set; }   // -1 if unavailable
    public long MemoryTotalMb { get; set; }  // -1 if unavailable
}

public class ContainerMetrics
{
    public string ContainerId { get; set; }
    public double CpuPercent { get; set; }
    public double RamPercent { get; set; }
    public long RamUsedMb { get; set; }
    public long RamTotalMb { get; set; }
}
```

### 3. API Changes

**Extend `GET /api/agents` response** to include:
```json
{
  "name": "local",
  "gpuInfo": "NVIDIA GeForce RTX 4090 (24GB)",
  "totalMemoryMb": 16384,
  "cpuCores": 16,
  "telemetry": {
    "host": { "cpuPercent": 45.2, "ramPercent": 68.5, "ramUsedMb": 11040, "ramTotalMb": 16128 },
    "gpus": [{ "index": 0, "name": "RTX 4090", "vendor": "nvidia", "corePercent": 72.0, "memoryPercent": 27.5, "memoryUsedMb": 6656, "memoryTotalMb": 24576 }],
    "containers": { "abc123": { "containerId": "abc123", "cpuPercent": 45.2, "ramPercent": 33.0, "ramUsedMb": 2112, "ramTotalMb": 6400 } },
    "collectedAt": "2025-01-15T10:30:00Z"
  }
}
```

**Backend polling logic:** Agent collects telemetry every `pollInterval` seconds (configurable, default 10). The host includes it in the agents list response. No separate endpoint needed.

### 4. Fix CpuPercent = 0 (Known Bug)

`DockerController.InspectContainerAsync` currently sets `CpuPercent = 0` because Docker.DotNet doesn't expose it directly. Options:
- **Option A (recommended):** Use `docker stats --no-stream` for live CPU % (already planned for container metrics collection)
- **Option B:** Parse `/sys/fs/cgroup/` directly (cgroup v1 and v2)
- This fix unblocks container CPU badges showing real data

## Frontend Changes

### 1. Extend `Settings` Interface

Add to `types.ts`:
```typescript
export interface Settings {
  // ... existing fields
  telemetryPollInterval: number; // seconds, 5-60, default 10
}
```

Add to `GeneralTab` in settings page: numeric input labeled "Metrics poll interval (seconds)" after "Log retention (hours)".

### 2. Extend Agent Query Response

In `types.ts`, add `telemetry` field to `Agent`:
```typescript
export interface Agent {
  // ... existing fields
  telemetry?: {
    host: { cpuPercent: number; ramPercent: number; ramUsedMb: number; ramTotalMb: number };
    gpus: GPUMetrics[];
    containers: Record<string, ContainerMetrics>;
    collectedAt: string;
  };
}
```

### 3. Dynamic Poll Interval

In `Swarm` component (line 1964), the agents query currently has:
```typescript
refetchInterval: 30_000
```

Change to:
```typescript
const hasExpanded = [...expandedSections].length > 0;
const pollInterval = hasExpanded ? (settings?.telemetryPollInterval ?? 10) * 1000 : 30_000;

useQuery({
  queryKey: ["agents"],
  queryFn: () => client.listAgents(),
  refetchInterval: pollInterval,
});
```

This reuses the existing single data channel. No new hook needed.

### 4. Extract Components (Before Adding Features)

The monolith (`index.tsx` at 2095 lines) needs extraction. Extract these into separate files **first**:

| Component | Current Lines | New File |
|-----------|--------------|----------|
| `AgentSection` | 1659-1959 (300 lines) | `AgentSection.tsx` |
| `RegisteredContainerCard` | 1300-1645 (345 lines) | `RegisteredContainerCard.tsx` |
| `ManageRuntimesModal` | 617-1081 | `ManageRuntimesModal.tsx` |

Then add new components:

| Component | New File |
|-----------|----------|
| `AgentMetricsBar` | `AgentMetricsBar.tsx` |
| `ContainerKPIBadges` | `ContainerKPIBadges.tsx` |

### 5. `AgentMetricsBar` Component

**Props:**
```typescript
interface AgentMetricsBarProps {
  gpus: GPUMetrics[];
  host: { cpuPercent: number; ramPercent: number; ramUsedMb: number; ramTotalMb: number };
  collectedAt?: string; // for stale indicator
  isStale?: boolean;    // agent offline or data old
}
```

**Layout Rules:**
- **Single GPU:** One compact line: `[GPU 0  20%⚡ 90%💾]`
- **Multi-GPU:** 2-column grid, fills column-first (0,1,2 left → 3,4,5 right)
- Each GPU = one line. RAM/CPU cards appended at end of grid.
- No vertical growth — always horizontal rows
- **Zero GPUs:** GPU section hidden entirely, show only RAM + CPU

**Insertion Point:**
- Inside the collapsible content, **above** the search input (line 1847)
- Between the `<AnimatePresence>` open (line 1831) and the search input
- This preserves the visual hierarchy — the toggle button still controls the full block

**Visual Design:**
- Compact pills matching existing `text-[10px] text-[var(--color-text-muted)]` style (line 1747)
- Color coding: green < 60%, yellow 60-85%, red > 85% using Badge `success`/`warning`/`error` variants
- Stale data: dimmed opacity + "last updated X ago" tooltip
- Loading: skeleton pills (pulsing gray)

### 6. `ContainerKPIBadges` Component

**Props:**
```typescript
interface ContainerKPIBadgesProps {
  metrics?: ContainerMetrics;
  isStale?: boolean;
}
```

**Insertion Point:**
- Inside `RegisteredContainerCard`, **below** the metrics grid (Port/Models/Slots/Discovered)
- After line 1382 (end of 4-column grid), before discovered models section (line 1385)
- This avoids pushing the existing metrics grid down and keeps cards compact

**Visual Design:**
- Small inline badges: `[CPU: 45%]` `[RAM: 2.1GB]`
- Muted, compact — subordinate to the existing metrics grid
- Color coding: same thresholds as agent bar
- Zero-CPU handling: if `cpuPercent === 0` and data is fresh, show "—" (Docker.DotNet limitation)
- Stale: dimmed with tooltip

### 7. Stale Data Handling

When agent goes offline or telemetry is old (> 2× poll interval):
- Values remain visible but at 50% opacity
- Tooltip shows "Last updated: {relative time}"
- Badge gets a subtle dotted border instead of solid
- If agent disconnects entirely, hide telemetry bar (graceful degradation)

### 8. Zero-GPU Edge Cases

- **No GPUs detected:** `gpus` array is empty → GPU section hidden, only RAM + CPU shown
- **Intel iGPU:** `vendor: "intel"`, `memoryPercent: -1`, `memoryUsedMb: -1`, `memoryTotalMb: -1` → show GPU core % only, no memory badge
- **AMD integrated:** Similar to Intel — if `memoryTotalMb` is suspiciously large (> system RAM), treat as shared memory
- **Mac M-series:** No CLI tool available → `gpus` array empty, show unified memory as RAM

## File Changes Summary

| File | Action | Description |
|------|--------|-------------|
| `frontend/src/features/swarm/AgentSection.tsx` | NEW | Extract from index.tsx |
| `frontend/src/features/swarm/RegisteredContainerCard.tsx` | NEW | Extract from index.tsx |
| `frontend/src/features/swarm/ManageRuntimesModal.tsx` | NEW | Extract from index.tsx |
| `frontend/src/features/swarm/AgentMetricsBar.tsx` | NEW | Agent-level GPU/RAM/CPU bar |
| `frontend/src/features/swarm/ContainerKPIBadges.tsx` | NEW | Per-container CPU/RAM badges |
| `frontend/src/features/swarm/index.tsx` | EDIT | Import extracted + new components, wire telemetry, dynamic poll |
| `frontend/src/lib/api/types.ts` | EDIT | Add telemetry fields to Settings + Agent |
| `frontend/src/features/settings/index.tsx` | EDIT | Add poll interval input to GeneralTab |
| `frontend/src/__tests__/swarm.test.tsx` | EDIT | Update imports, add new component tests |
| `frontend/src/__tests__/settings.test.tsx` | EDIT | Add poll interval setting test |
| `backend/src/Unswarm.Core/Models/AgentConnection.cs` | EDIT | Add telemetry fields |
| `backend/src/Unswarm.Api/Controllers/AgentsController.cs` | EDIT | Include telemetry in agent response |
| `backend/src/Unswarm.Core/Services/DockerController.cs` | EDIT | Fix CpuPercent, add docker stats |

## Implementation Order

```
Phase 0: Extract components (AgentSection, RegisteredContainerCard, ManageRuntimesModal)
Phase 1: Backend — live metrics collection (GPU, CPU, RAM, container stats)
Phase 2: Backend — fix CpuPercent = 0, extend AgentConnection model, include telemetry in API response
Phase 3: Frontend — extend types, add settings, dynamic poll interval
Phase 4: Frontend — AgentMetricsBar + ContainerKPIBadges
Phase 5: Polish — stale data, zero-GPU, loading states, tests
```

## Testing Strategy
- Unit: `AgentMetricsBar` renders with 0, 1, 2+ GPUs
- Unit: `ContainerKPIBadges` renders with/without metrics, stale state
- Integration: Settings poll interval persists and affects refetch rate
- Unit: Stale data detection (timestamp comparison)
- E2E: Telemetry values update in UI when agents are active
