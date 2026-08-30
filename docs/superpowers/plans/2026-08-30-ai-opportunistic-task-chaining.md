# Opportunistic Task Chaining Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Keep unit-task dispatch, but skip clear-counter staging when a free process/plate sink exists, then immediately give the next eligible unit task exclusively to the holder/deliverer so FETCH→PROCESS→ADD runs as one continuous body of work with per-step checkboxes.

**Architecture:** `KitchenTaskContinuation` owns look-ahead (FETCH divert) and claim (after postcondition). `AssignInPanelOrder` enforces holder exclusivity for PROCESS/ADD. `AIChefController` asks continuation before staging and claims the next task without going idle. Facility hard-reserve stays on PROCESS sinks.

**Tech Stack:** Unity / C#, existing KitchenChaos AI (`KitchenTaskGenerator`, `KitchenAIManager`, `AIChefController`, `KitchenRouteResolver`, `KitchenBlackboard`).

**Spec:** `docs/superpowers/specs/2026-08-30-ai-opportunistic-task-chaining-design.md`

## Global Constraints

- No `KitchenTaskGroup` type or multi-member assign packets.
- Unit tasks only; staging is fallback when look-ahead/claim fails.
- Checkbox: mark each unit task when **its own** postcondition is true (place → FETCH ✓, cut → PROCESS ✓, plate → ADD ✓).
- Holder exclusivity: while a matching holder/same-run deliverer exists, PROCESS/ADD for that input **must not** assign to anyone else.
- One look-ahead or claim attempt per “about to stage” event.
- Do not revive utility scoring (`#if false` GreedyAssign stays dead).

---

## File map

| File | Responsibility |
|------|----------------|
| Create `Assets/Demos/KitchenChaos/_Scipts/AI/Planning/KitchenTaskContinuation.cs` | Pure look-ahead + claim + holder matching helpers |
| Modify `Assets/Demos/KitchenChaos/_Scipts/AI/KitchenBlackboard.cs` | `AgentState.chainSessionId`, `exclusiveDelivererAgentId` on items optional; release helpers |
| Modify `Assets/Demos/KitchenChaos/_Scipts/AI/KitchenTaskGenerator.cs` | Holder exclusivity inside `AssignInPanelOrder`; keep `TryReserveTaskFacilities` |
| Modify `Assets/Demos/KitchenChaos/_Scipts/AI/AIChefController.cs` | Call look-ahead/claim before stage; seamless start of claimed next task; set deliverer on place |
| Modify `Assets/Demos/KitchenChaos/_Scipts/AI/Planning/KitchenRouteResolver.cs` | Prefer continuation look-ahead over generate-time Bypass as source of truth at execute time (keep helpers) |
| Modify `Assets/Demos/KitchenChaos/_Scipts/AI/KitchenAIManager.cs` | Only if assign tick needs to pass exclusivity / clear deliverer tags on abandon |

No new UI required; optional debug log via existing `AIDebugLogger`.

---

### Task 1: Continuation helper API (look-ahead + claim + holder match)

**Files:**
- Create: `Assets/Demos/KitchenChaos/_Scipts/AI/Planning/KitchenTaskContinuation.cs`
- Modify: `Assets/Demos/KitchenChaos/_Scipts/AI/KitchenBlackboard.cs` (`AgentState` + `ItemState` fields)

**Interfaces:**
- Produces:
  - `KitchenTaskContinuation.TryLookAheadProcessSink(...)`
  - `KitchenTaskContinuation.TryLookAheadPlateSink(...)`
  - `KitchenTaskContinuation.TryClaimContinuation(...)`
  - `KitchenTaskContinuation.FindExclusiveHolderAgentId(...)`
  - `AgentState.chainSessionId` (`int`, 0 = none)
  - `ItemState.exclusiveDelivererAgentId` (`int`, -1 = none) — set when FETCH places input for PROCESS; cleared when PROCESS assigned/completed/abandoned or item leaves facility

- [x] **Step 1: Add state fields**
- [x] **Step 2: Create `KitchenTaskContinuation.cs`**
- [x] **Step 3: Sanity compile in Unity**
- [x] **Step 4: Commit** (deferred — user did not request commit)

### Task 2: Holder exclusivity in `AssignInPanelOrder`

Implemented: exclusive holder gate + `TryReserveTaskFacilitiesPublic`.

### Task 3–5: Look-ahead, claim, ADD chain, abandon cleanup

Implemented in `AIChefController` + FETCH generate no longer sets Bypass at spawn time.


```csharp
using System.Collections.Generic;
using System.Linq;
using Kitchen;
using UnityEngine;

namespace Kitchen.AI
{
    /// <summary>
    /// Opportunistic chaining: look-ahead divert during FETCH, claim after postconditions.
    /// Spec: docs/superpowers/specs/2026-08-30-ai-opportunistic-task-chaining-design.md
    /// </summary>
    public static class KitchenTaskContinuation
    {
        static int _nextChainSessionId = 1;

        public static int BeginOrGetChainSession(AgentState agent)
        {
            if (agent == null) return 0;
            if (agent.chainSessionId == 0)
                agent.chainSessionId = _nextChainSessionId++;
            return agent.chainSessionId;
        }

        public static void ClearChainSession(AgentState agent)
        {
            if (agent != null) agent.chainSessionId = 0;
        }

        /// <summary>
        /// Mid-FETCH: if a PROCESS step will consume held type for this order and a
        /// process facility is free/reservable, return that sink. Does NOT claim PROCESS.
        /// </summary>
        public static bool TryLookAheadProcessSink(
            KitchenBlackboard bb,
            AgentState agent,
            KitchenTask fetchTask,
            KitchenObj held,
            out BaseCounter sink,
            out FacilityType facilityType)
        {
            sink = null;
            facilityType = default;
            if (bb == null || agent == null || fetchTask == null || held == null) return false;
            if (fetchTask.type != TaskType.FETCH || fetchTask.orderId == 0) return false;

            var recipe = bb.FindRecipeForOrder(fetchTask.orderId);
            if (recipe == null || !bb.TryGetRecipeSteps(recipe.recipeName, out var steps))
                return false;

            var fetchStep = steps.FirstOrDefault(s => s.id == fetchTask.stepId);
            if (fetchStep == null) return false;

            var processStep = steps.FirstOrDefault(s =>
                s.taskType == TaskType.PROCESS
                && s.inputType == held.objEnum
                && s.dependsOnStepIds != null
                && s.dependsOnStepIds.Contains(fetchStep.id));
            if (processStep == null)
            {
                // Fallback: any PROCESS that consumes this output type for the recipe
                processStep = steps.FirstOrDefault(s =>
                    s.taskType == TaskType.PROCESS && s.inputType == held.objEnum);
            }
            if (processStep == null) return false;

            var facility = bb.FindBestAvailableFacility(
                processStep.requiredFacilityType,
                agent.position,
                agent.agentId);
            if (facility?.counter == null) return false;
            if (facility.counter.HasKitchenObj()) return false;

            sink = facility.counter;
            facilityType = processStep.requiredFacilityType;
            return true;
        }

        /// <summary>
        /// After PROCESS (or FETCH that goes straight to plate): if ADD can take held item.
        /// </summary>
        public static bool TryLookAheadPlateSink(
            KitchenBlackboard bb,
            KitchenTask currentTask,
            KitchenObj held,
            out BaseCounter plateCounter)
        {
            plateCounter = null;
            if (bb == null || currentTask == null || held == null || currentTask.orderId == 0)
                return false;

            var plate = bb.FindPlateForOrder(currentTask.orderId);
            if (plate == null) return false;
            if (plate.GetHolder() is not BaseCounter counter || counter == null) return false;

            // Held must be an ADD input for this order's remaining steps
            var recipe = bb.FindRecipeForOrder(currentTask.orderId);
            if (recipe == null || !bb.TryGetRecipeSteps(recipe.recipeName, out var steps))
                return false;

            bool addWants = steps.Any(s =>
                s.taskType == TaskType.ADD_TO_PLATE && s.inputType == held.objEnum);
            if (!addWants) return false;

            plateCounter = counter;
            return true;
        }

        /// <summary>
        /// After current unit postcondition is true: atomically assign next unassigned
        /// matching task from bb.taskPool to this agent and reserve facilities.
        /// </summary>
        public static bool TryClaimContinuation(
            KitchenBlackboard bb,
            AgentState agent,
            KitchenTask justCompletedOrPlaced,
            out KitchenTask next)
        {
            next = null;
            if (bb == null || agent == null || justCompletedOrPlaced == null) return false;
            if (agent.IsIdle == false && agent.currentTask != null
                && agent.currentTask.status == "executing"
                && agent.currentTask != justCompletedOrPlaced)
                return false;

            // Prefer PROCESS if input now at reserved facility / held by agent
            next = FindClaimableContinuation(bb, agent, justCompletedOrPlaced);
            if (next == null) return false;

            bool skipFacilityReserve = next.type == TaskType.ADD_TO_PLATE
                || next.type == TaskType.FETCH_PLATE
                || next.type == TaskType.TRASH;

            if (!KitchenTaskGenerator.TryReserveTaskFacilitiesPublic(
                    next, agent, bb, skipFacilityReserve))
                return false;

            BeginOrGetChainSession(agent);
            next.status = "assigned";
            next.assignedAgentId = agent.agentId;
            agent.currentTask = next;
            agent.substate = "moving";

            // Clear deliverer tag once claimed
            ClearDelivererForTaskInput(bb, next);

            bb.taskPool.RemoveAll(t => t.id == next.id);
            return true;
        }

        public static int FindExclusiveHolderAgentId(
            KitchenBlackboard bb,
            KitchenTask task)
        {
            if (bb == null || task == null) return -1;
            if (task.type != TaskType.PROCESS && task.type != TaskType.ADD_TO_PLATE)
                return -1;

            var need = task.itemType;
            if (task.type == TaskType.ADD_TO_PLATE)
                need = task.itemType; // ADD uses itemType / input — match existing KitchenTask fields

            foreach (var a in bb.agents)
            {
                if (a.controller == null) continue;
                var held = a.controller.GetHeldKitchenObjOrNull(); // add thin public accessor if missing
                if (held != null
                    && held.objEnum == need
                    && held.BoundOrderId == task.orderId)
                    return a.agentId;
            }

            // Deliverer standing at / owning exclusivity on world item
            foreach (var item in bb.items)
            {
                if (item.itemType != need || item.orderId != task.orderId) continue;
                if (item.exclusiveDelivererAgentId >= 0)
                    return item.exclusiveDelivererAgentId;
            }

            return -1;
        }

        static KitchenTask FindClaimableContinuation(
            KitchenBlackboard bb,
            AgentState agent,
            KitchenTask from)
        {
            var pool = bb.taskPool;
            if (pool == null) return null;

            // PROCESS: input on agent's target facility or held matching from.output/item
            KitchenTask process = pool.FirstOrDefault(t =>
                t.type == TaskType.PROCESS
                && t.orderId == from.orderId
                && t.status != "assigned"
                && t.status != "executing"
                && InputReadyForProcess(bb, agent, t));
            if (process != null) return process;

            KitchenTask add = pool.FirstOrDefault(t =>
                t.type == TaskType.ADD_TO_PLATE
                && t.orderId == from.orderId
                && t.status != "assigned"
                && t.status != "executing"
                && InputReadyForAdd(bb, agent, t));
            return add;
        }

        static bool InputReadyForProcess(KitchenBlackboard bb, AgentState agent, KitchenTask t)
        {
            var held = agent.controller?.GetHeldKitchenObjOrNull();
            if (held != null && held.objEnum == t.itemType && held.BoundOrderId == t.orderId)
                return true;

            if (t.targetFacility != null
                && t.targetFacility.HasKitchenObj()
                && t.targetFacility.GetKitchenObj().objEnum == t.itemType
                && t.targetFacility.GetKitchenObj().BoundOrderId == t.orderId)
                return true;

            return bb.FindItemsOfType(t.itemType, excludeReserved: false, forOrderId: t.orderId)
                .Any(i => i.exclusiveDelivererAgentId == agent.agentId || !i.IsCarried);
        }

        static bool InputReadyForAdd(KitchenBlackboard bb, AgentState agent, KitchenTask t)
        {
            var held = agent.controller?.GetHeldKitchenObjOrNull();
            var need = t.itemType;
            return held != null && held.objEnum == need && held.BoundOrderId == t.orderId;
        }

        static void ClearDelivererForTaskInput(KitchenBlackboard bb, KitchenTask t)
        {
            foreach (var item in bb.items)
            {
                if (item.orderId == t.orderId && item.itemType == t.itemType)
                    item.exclusiveDelivererAgentId = -1;
            }
        }
    }
}
```

Notes for implementer:
- `TryReserveTaskFacilities` is currently `private static` — add a thin public wrapper `TryReserveTaskFacilitiesPublic` that calls it (Task 2), or make the existing method `internal`/`public`.
- `GetHeldKitchenObjOrNull()` — add on `AIChefController` if no public held accessor exists (`_heldItem` is private today).
- Match `KitchenTask` field names actually used for ADD (`itemType` vs `inputType`) by reading `KitchenTask.cs` before coding.
- `FindClaimableContinuation` must only claim tasks whose **generator preconditions are already true** (item on board / in hand). Mid-FETCH look-ahead must **not** call `TryClaimContinuation` for PROCESS.

- [ ] **Step 3: Sanity compile in Unity**

Open project / wait for script compile. Fix any missing accessor / wrong field names.

- [ ] **Step 4: Commit**

```bash
git add Assets/Demos/KitchenChaos/_Scipts/AI/Planning/KitchenTaskContinuation.cs Assets/Demos/KitchenChaos/_Scipts/AI/KitchenBlackboard.cs
git commit -m "$(cat <<'EOF'
feat(ai): add opportunistic task continuation helpers

EOF
)"
```

---

### Task 2: Holder exclusivity in `AssignInPanelOrder`

**Files:**
- Modify: `Assets/Demos/KitchenChaos/_Scipts/AI/KitchenTaskGenerator.cs` (`AssignInPanelOrder`, expose reserve helper)

**Interfaces:**
- Consumes: `KitchenTaskContinuation.FindExclusiveHolderAgentId`
- Produces: assignment that never gives PROCESS/ADD to a non-holder while exclusivity applies; `TryReserveTaskFacilitiesPublic`

- [ ] **Step 1: Expose reserve helper**

Near `TryReserveTaskFacilities`:

```csharp
public static bool TryReserveTaskFacilitiesPublic(
    KitchenTask task, AgentState agent, KitchenBlackboard bb, bool skipFacilityReserve)
    => TryReserveTaskFacilities(task, agent, bb, skipFacilityReserve);
```

- [ ] **Step 2: Change agent selection in `AssignInPanelOrder`**

Replace the peek-first-agent pattern with exclusivity-aware pick. Inside the `foreach (var task in availableTasks)` loop, **before** `remainingAgents.Peek()`:

```csharp
AgentState agent = null;
int exclusiveId = KitchenTaskContinuation.FindExclusiveHolderAgentId(bb, task);
if (exclusiveId >= 0)
{
    agent = remainingAgents.FirstOrDefault(a => a.agentId == exclusiveId);
    if (agent == null)
    {
        // Holder exists but is not idle → do not assign this task to anyone else
        if (bb.agents.Any(a => a.agentId == exclusiveId))
            continue;
    }
    else
    {
        // Rebuild queue without this agent at front: dequeue matching agent
        var list = remainingAgents.ToList();
        list.Remove(agent);
        remainingAgents = new Queue<AgentState>(list);
    }
}
else
{
    agent = remainingAgents.Peek();
}

if (agent == null)
    continue;

// ... existing TryReserveTaskFacilities ...
// On success, if we used Peek path: remainingAgents.Dequeue();
// If exclusivity path already removed agent from queue: do not Dequeue again
```

Important: when exclusivity applies and holder is busy (not in `remainingAgents`), **`continue`** (skip task), do **not** `break` the whole panel (SERVE/FETCH for others must still proceed). Current code `break`s on many failures — for exclusivity miss use `continue` only.

- [ ] **Step 3: PlayMode check (holder exclusivity)**

1. Two AI chefs idle; put tomato for order on clear (or pause after A stages).
2. Ensure PROCESS is on board; A is exclusive deliverer / holder.
3. Confirm B never receives that PROCESS while A is idle and exclusive.
4. If A is mid-other-task and exclusive, PROCESS stays unassigned.

- [ ] **Step 4: Commit**

```bash
git add Assets/Demos/KitchenChaos/_Scipts/AI/KitchenTaskGenerator.cs
git commit -m "$(cat <<'EOF'
feat(ai): exclusive PROCESS/ADD assign to ingredient holder

EOF
)"
```

---

### Task 3: FETCH look-ahead divert + mark deliverer + claim PROCESS after place

**Files:**
- Modify: `Assets/Demos/KitchenChaos/_Scipts/AI/AIChefController.cs` (`BeginFetchedItemDelivery`, place-on-dest success path, `CompleteTask` / seamless handoff)
- Modify: `Assets/Demos/KitchenChaos/_Scipts/AI/Planning/KitchenRouteResolver.cs` only if needed to soft-reserve via blackboard when diverting

**Interfaces:**
- Consumes: `TryLookAheadProcessSink`, `TryLookAheadPlateSink`, `TryClaimContinuation`, `BeginOrGetChainSession`
- Produces: FETCH delivery to cutter/plate when look-ahead succeeds; after place FETCH completes then PROCESS starts without idle

- [ ] **Step 1: Public held accessor**

```csharp
public KitchenObj GetHeldKitchenObjOrNull() => _heldItem;
```

- [ ] **Step 2: Rewrite destination choice in `BeginFetchedItemDelivery`**

After tagging order on held item, **before** `ResolveDeliveryDestination`:

```csharp
var bb = _aiManager?.Blackboard;
var agent = bb?.agents.Find(a => a.agentId == agentId);
BaseCounter dropTarget = null;

if (bb != null && agent != null && _currentTask.type == TaskType.FETCH)
{
    if (KitchenTaskContinuation.TryLookAheadProcessSink(
            bb, agent, _currentTask, _heldItem, out var processSink, out _))
    {
        if (bb.TryReserveFacility(processSink, agentId))
        {
            KitchenTaskContinuation.BeginOrGetChainSession(agent);
            _currentTask.deliveryIntent = KitchenDeliveryIntent.BypassToProcessFacility;
            _currentTask.destFacility = processSink;
            dropTarget = processSink;
            AIDebugLogger.Log(chefName,
                $"LookAhead PROCESS sink → {processSink.name}");
        }
    }
    else if (KitchenTaskContinuation.TryLookAheadPlateSink(
                 bb, _currentTask, _heldItem, out var plateSink))
    {
        KitchenTaskContinuation.BeginOrGetChainSession(agent);
        _currentTask.deliveryIntent = KitchenDeliveryIntent.BypassToPlateAssembly;
        _currentTask.destFacility = plateSink;
        dropTarget = plateSink;
        AIDebugLogger.Log(chefName, $"LookAhead PLATE sink → {plateSink.name}");
    }
}

dropTarget ??= ResolveDeliveryDestination(_currentTask);
// ... existing move/drop logic using dropTarget ...
```

Prefer this look-ahead over generate-time Bypass when both exist (look-ahead runs first).

- [ ] **Step 3: On successful place after FETCH (GotoDest drop success)**

Where FETCH currently calls `CompleteTask()` after putting item on dest:

1. Tag item: find `ItemState` for placed kitchenObj → `exclusiveDelivererAgentId = agentId`.
2. Mark current FETCH completed (existing `CompleteTask` path that sets status + step world state).
3. **Before** clearing agent to idle: call `TryClaimContinuation(bb, agent, completedFetch, out var next)`.
4. If claim succeeds: do **not** leave idle — call existing `StartTask(next)` / assign entry (same path `KitchenAIManager` uses after assign). If no public StartTask, extract `BeginExecuteAssignedTask()` used by manager.
5. If claim fails: normal idle; clear `chainSessionId` only if not claiming.

Implementer: locate the exact FETCH drop-complete branch (search `CompleteTask` near `DropItemAtDestination` / `GotoDest`) and insert claim there. Do not mark FETCH complete before the item is actually on the counter.

- [ ] **Step 4: PlayMode — tomato story A**

1. Free cutting board, tomato salad order.
2. One chef FETCH tomato → must walk to **cutting board**, not clear.
3. On place: FETCH checkbox / step done; chef immediately starts PROCESS without idle wander.
4. After cut: PROCESS done.

- [ ] **Step 5: PlayMode — story B (no board)**

Occupy all cutting boards; FETCH must stage to clear; FETCH ✓ only; later PROCESS when free (exclusivity if deliverer tagged on clear item).

- [ ] **Step 6: Commit**

```bash
git add Assets/Demos/KitchenChaos/_Scipts/AI/AIChefController.cs Assets/Demos/KitchenChaos/_Scipts/AI/Planning/KitchenRouteResolver.cs
git commit -m "$(cat <<'EOF'
feat(ai): divert FETCH to free process sink and claim PROCESS

EOF
)"
```

---

### Task 4: PROCESS → ADD continuation (and FETCH → ADD for cream)

**Files:**
- Modify: `Assets/Demos/KitchenChaos/_Scipts/AI/AIChefController.cs` (PROCESS output delivery / complete path)

- [ ] **Step 1: After PROCESS produces held output**

Where code would `ResolveDeliveryDestination` / stage slices:

1. `TryLookAheadPlateSink` → if plate ready, set dest to plate (BypassToPlateAssembly), soft no exclusive facility.
2. On successful ADD-eligible place **or** when PROCESS completes with slices in hand and ADD claimable: `TryClaimContinuation` for ADD.
3. If plate not ready: stage clear; PROCESS ✓; exclusivity tag on slices item for later ADD.

Typical flow:

```text
PROCESS finishes → take slices into hand → look-ahead plate?
  yes → move to plate, claim ADD (or claim first then execute ADD interact)
  no  → FindDropTarget clear, complete PROCESS, tag exclusiveDeliverer
```

- [ ] **Step 2: PlayMode — story C**

Board free, no plate yet: FETCH→PROCESS chain, slices stage to clear, ADD later exclusive to deliverer/holder.

- [ ] **Step 3: PlayMode — cream**

FETCH cream with plate ready: divert to plate, claim ADD, no clear hop.

- [ ] **Step 4: Commit**

```bash
git add Assets/Demos/KitchenChaos/_Scipts/AI/AIChefController.cs
git commit -m "$(cat <<'EOF'
feat(ai): chain PROCESS/FETCH into ADD when plate is ready

EOF
)"
```

---

### Task 5: Cleanup Bypass reliance + abandon / session clear

**Files:**
- Modify: `AIChefController.cs`, `KitchenAIManager.cs`, optionally stop writing Bypass at generate time in `KitchenTaskGenerator` FETCH creation (look-ahead becomes source of truth)

- [ ] **Step 1: On `AbandonTask` / `ForceAbandonTask` / idle cleanup**

```csharp
var agent = _aiManager?.Blackboard?.agents.Find(a => a.agentId == agentId);
KitchenTaskContinuation.ClearChainSession(agent);
// Clear exclusiveDelivererAgentId on items pointing at this agent if abandoning without handoff
```

- [ ] **Step 2: Optional — stop setting `deliveryIntent` at FETCH generate time**

In `GenerateFetchTasks` (or equivalent), leave `deliveryIntent = Default` so execute-time look-ahead owns divert. Keep `KitchenRouteResolver` methods for look-ahead/claim reuse.

- [ ] **Step 3: Two-chef stress**

Two tomatoes, two boards: both can chain independently; no cross-claim of the other’s PROCESS.

- [ ] **Step 4: Commit**

```bash
git add Assets/Demos/KitchenChaos/_Scipts/AI/
git commit -m "$(cat <<'EOF'
fix(ai): clear chain session on abandon; prefer execute-time divert

EOF
)"
```

---

### Task 6: Spec/status + manual regression checklist

**Files:**
- Modify: `docs/superpowers/specs/2026-08-30-ai-opportunistic-task-chaining-design.md` (status already approved)
- This plan’s checklist completion

- [ ] **Step 1: Run full PlayMode checklist**

| # | Scenario | Expect |
|---|----------|--------|
| 1 | Free board + plate | FETCH→cut→ADD, no clear hop; three sequential checks |
| 2 | No board | FETCH→clear only; PROCESS later |
| 3 | Board, no plate | FETCH→PROCESS; slices clear; ADD later exclusive |
| 4 | Two chefs two boards | Independent chains |
| 5 | Holder busy | PROCESS waits; not given to other |
| 6 | Cream + plate | FETCH→ADD divert |

- [ ] **Step 2: Commit docs if any wording tweaks**

```bash
git add docs/superpowers/
git commit -m "$(cat <<'EOF'
docs(ai): opportunistic chaining plan checklist complete

EOF
)"
```

---

## Spec coverage (self-review)

| Spec section | Task |
|--------------|------|
| §5 look-ahead vs claim | Task 1, 3 |
| §6 per-step checkbox | Task 3–4 (complete on postcondition; no batch defer) |
| §7 holder exclusivity | Task 2 |
| §8 facility reserve | Task 1 claim + existing `TryReserveTaskFacilities` |
| §9 stories A/B/C | Task 3–4 PlayMode |
| §10 replace Bypass as truth | Task 3, 5 |
| §11.1 deps + exclusivity closed loop | Task 2–3 |
| Mid-chain failure / abandon | Task 5 |

## Placeholder scan

No TBD steps; code blocks are starting points — implementer must align field names (`KitchenTask` ADD input, `ItemState` members) with the live files when applying.

## Type consistency

- `TryReserveTaskFacilitiesPublic` defined in Task 2, used in Task 1’s claim — **implement Task 2 reserve wrapper before or with Task 1 claim compile**, or temporarily inline reserve in Task 1 then switch. Recommended order: Task 1 fields + look-ahead methods first (no claim compile), Task 2 exclusivity + public reserve, then finish claim method / Task 3 wiring. If compiling Task 1 claim early, add the public wrapper in the same commit as Task 1.
