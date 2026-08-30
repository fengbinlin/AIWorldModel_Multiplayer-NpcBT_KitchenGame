# Task Group + Hard Facility Reservation Design

Date: 2026-08-30  
Status: draft for review  
Approach: **B — KitchenTaskGroup** (planning-time fusion, one agent per group)

## 1. Goals

1. **Stop soft stitching.** Do not rely on `FETCH` bypass + next scheduler tick to invent `PROCESS`. That path caused “arrived at board, then abandoned as busy” loops.
2. **Form task groups when staging can be skipped.** If an ingredient can move directly between two known facilities (storage → cutter, cutter → order plate, storage → order plate), emit one group.
3. **Hard reserve facilities at assign time.** Reserved facilities are unavailable to other agents in realtime (blackboard + assign-time checks).
4. **One group → one AI** until the group finishes or degrades.
5. **Degrade cleanly.** If the group cannot finish later members, complete only member-0 (always completable via clear counter / ground). Remaining recipe steps stay unchecked and re-enter the **normal public pool** — any idle AI may take them. No sticky ownership to the original agent.

## 2. Non-goals

- Utility / distance scoring revival.
- Multi-agent handoff inside one group (A fetches, B cuts).
- Changing recipe step chain generation (`BuildRecipeSteps`) topology (groups sit *on top* of existing steps).
- Skin / catalog work.

## 3. Core definitions

### 3.1 Member vs group

| Concept | Meaning |
|---------|---------|
| **Recipe step** | Existing `RecipeStep` in the order plan (todolist checkbox). |
| **KitchenTask** | Executable unit with a `TaskType` (`FETCH`, `PROCESS`, …). May stand alone or belong to a group. |
| **KitchenTaskGroup** | Ordered list of 2+ members assigned as **one dispatch unit** to one agent. |

### 3.2 Group eligibility (the only fusion rule)

Two consecutive recipe steps `S_a → S_b` may fuse into a group iff:

1. **Direct-facility transfer:** the natural sink of `S_a`’s output is exactly the facility / plate counter required by `S_b`, so a clear-counter hop is unnecessary when that sink is available.
2. **Reservable now:** at *generation or assign* time we can claim that sink (and any other exclusive facility the group needs) for this agent.
3. **Order-bound:** both steps share the same `orderId` and dependencies for `S_b` are otherwise satisfied (or will be satisfied by completing `S_a` inside the same group — see §5.2).

Canonical patterns:

| Pattern | Members | Required reservation |
|---------|---------|----------------------|
| Prep chain | `FETCH` → `PROCESS` | Process facility (cutting / stove / …) |
| Plate chain (processed) | `PROCESS` → `ADD_TO_PLATE` | Process facility (if still needed) + plate’s counter (soft lock, see §6.3) |
| Plate chain (raw) | `FETCH` → `ADD_TO_PLATE` | Plate’s counter (soft) when plate already exists |

If eligibility fails → emit **standalone** `S_a` only (never emit a half-group with Bypass intent).

### 3.3 What “Bypass” becomes

`KitchenDeliveryIntent.BypassTo*` is **no longer a scheduler-level soft link**.  
It survives only as an **intra-group delivery mode** on member-0 → member-1 (internal phase). Standalone FETCH never sets Bypass.

## 4. Data model

```csharp
enum TaskGroupStatus
{
    Pending,      // in pool, not assigned
    Assigned,     // agent claimed, not started
    Executing,    // agent running members
    Completed,    // all members done; all steps checked
    Degraded,     // only prefix completed (usually member 0); rest unchecked
    Abandoned,    // nothing useful done / force-cleared
}

class KitchenTaskGroup
{
    int id;
    int orderId;
    string label;                    // e.g. "番茄包菜沙拉: Tomato→TomatoSlices"
    List<KitchenTask> members;       // length >= 2, ordered
    List<BaseCounter> reservedFacilities;
    int assignedAgentId;             // -1 if unassigned
    TaskGroupStatus status;
    int activeMemberIndex;           // 0..n-1 while executing
}

// KitchenTask additions
int groupId;           // 0 = solo
int memberIndex;       // -1 = solo
bool isGroupMember;    // convenience
```

Invariants:

- A task is either solo (`groupId == 0`) **or** exactly one group’s member.
- While `group.status` is `Assigned`/`Executing`, no other agent may be assigned any member’s `stepId` for that `orderId`.
- Solo tasks and group members never duplicate the same `(orderId, stepId)` in the active set.

## 5. Generation algorithm

### 5.1 Pass order (per order, per scheduler tick)

1. Sync blackboard (items, facilities, plates, step states).
2. Walk recipe steps in chain order.
3. Skip steps with state `completed`.
4. Skip steps already covered by an **active** group or active solo task (`assigned`/`executing`).
5. For each remaining step `S`, try **Lookahead fuse** with the next eligible consumer `S_next` (§5.2).
6. If fuse OK → enqueue one `KitchenTaskGroup` (do **not** also enqueue solo `S` / `S_next`).
7. Else → enqueue solo task for `S` only (if its own preconditions hold).

### 5.2 Lookahead fuse details (FETCH → PROCESS)

Preconditions for fuse:

- `S` is `FETCH` with `outputType = I`.
- Next consumer in chain is `PROCESS` with `inputType = I` (dependency edges or sequential scan as today).
- `PROCESS` output does not already exist for this order.
- No other agent holds an active task for either step.
- `FindBestAvailableFacility(processType, forAgentId)` returns a counter that is:
  - physically empty **or** already holding this order’s matching input; and
  - not reserved by another agent.

On success:

- Build members:  
  - M0: `FETCH` — `targetFacility = storage`, `destFacility = reservedCutter`, delivery = group-internal direct.  
  - M1: `PROCESS` — `targetFacility = reservedCutter`, item type `I`, output slices.
- Attach both `stepId`s on members.
- Group does **not** reserve the storage cabinet (infinite / shared), only the process facility.

### 5.3 Lookahead fuse (→ ADD_TO_PLATE)

Preconditions:

- Order plate exists and sits on a `BaseCounter`.
- Ingredient for ADD exists **or** will be produced by the previous member in the same group.
- Plate counter not reserved exclusively by another agent’s plate work if we use soft locks (§6.3).

If plate missing → **cannot** fuse FETCH/PROCESS with ADD; plate FETCH_PLATE stays independent.

### 5.4 Anti-duplication rules (bug magnets)

| Situation | Rule |
|-----------|------|
| Group pending in pool | Do not generate solo for those stepIds |
| Group executing | Do not generate anything for those stepIds |
| Group degraded after M0 | M0 step `completed`; generate solo for remaining steps next tick |
| Item already on reserved cutter from **this** order | Prefer solo `PROCESS` (or group starting at PROCESS→ADD), **not** a new FETCH |
| Two orders need same cutter type | First assign wins reservation; second order gets solo FETCH to clear/ground |

## 6. Facility reservation (hard, realtime)

### 6.1 Reservation record

Extend `FacilityState`:

```text
state: free | occupied | reserved
reservedByAgent: int      // -1 none
reservedByGroupId: int    // 0 none
reservedUntilStep: string // optional debug
```

Semantics:

- **`reserved`**: other agents’ `FindBestAvailableFacility` / assign checks treat as unavailable.
- Owner agent may still interact with that counter.
- Physical `HasKitchenObj` from **foreign** orders also blocks new reservations.

### 6.2 When to reserve / release

| Event | Action |
|-------|--------|
| Assign group to agent | Atomically reserve all `reservedFacilities`. Fail → skip group this tick (try solo fallback for M0 only if M0 needs no exclusive facility, else skip). |
| Assign solo PROCESS | Reserve target facility |
| Group Completed | Release all |
| Group Degraded | Release process / exclusive facilities **immediately** after M0 staging done |
| Group Abandoned / ForceAbandon | Release all |
| Agent destroyed | Release all owned by agentId |

Atomic assign:

```text
TryReserveAll(list):
  for each f in list:
    if !CanReserve(f, agent): rollback any taken; return false
    Reserve(f, agent, groupId)
  return true
```

### 6.3 Plate counter “soft” lock

Assembly tables hold many orders’ plates. Hard-locking an entire clear counter would serialize plating.

Rule:

- **Hard-reserve** only exclusive process facilities (cutting, stove, oven, blender).
- For ADD / plate destination: reserve by **order plate ownership** (existing order-plate binding) + “no other agent’s active ADD/SERVE targeting the same plate”, not by exclusive counter mutex — unless the counter is empty and used as a private staging spot.

Document this explicitly so implementers do not hard-lock every ClearCounter.

### 6.4 Realtime sync

Each scheduler tick **and** on assign:

1. `SyncFacilities` refresh physical occupancy.
2. Re-validate owner reservations: if reserved cutter somehow holds a **foreign** item, mark group for degrade path (do not steal; degrade).
3. UI / debug: show `reservedByAgent` on facility debug panel.

No network sync beyond existing server-authoritative AI (AI already runs server/host only).

## 7. Assignment

### 7.1 Dispatch unit

`AssignInPanelOrder` consumes a mixed pool:

```text
PoolItem = SoloTask | TaskGroup
```

Ordering:

1. Any item that is `SERVE` (solo) first.
2. Then original generation order (stable).

For each idle agent in queue order:

- Peek next pool item.
- If group: `TryReserveAll` + assign entire group to that agent; agent leaves idle queue.
- If solo: existing reserve rules.
- On reserve failure for group: **do not** assign to this agent; try **degraded solo M0** only when M0 is FETCH with no exclusive facility need; otherwise leave group in pool for a later tick / other agent after facilities free. Do not break the whole queue unless policy says so — prefer `continue` for SERVE-like criticality; for prep groups `continue` to next item so other work proceeds.

### 7.2 One AI ownership

While group is `Assigned`/`Executing`:

- `agent.currentTask` points at **current member** (for FSM compatibility).
- `agent.currentGroup` points at the group.
- No other agent may receive members of this group.

## 8. Execution (single AI)

### 8.1 Phase machine

```text
On AssignGroup:
  status = Executing
  activeMemberIndex = 0
  AssignTask(members[0])   // reuse existing FETCH/PROCESS entry points

On member CompleteTask:
  if group == null: existing solo complete
  else:
    mark member done (local)
    if more members:
      if CanStartNextMember():   // reservation still valid, preconditions OK
        activeMemberIndex++
        AssignTask(members[activeMemberIndex])  // no scheduler round-trip
      else:
        DegradeGroup()
    else:
      CompleteGroup()
```

Critical: **next member starts inside the same agent**, not via “wait for next scheduler cycle”. This removes the old Bypass race.

### 8.2 Intra-group delivery

For FETCH→PROCESS group:

- After FETCH pickup, destination **must** be the reserved process facility (group field), not `FindDropTarget` clear counter.
- On place success, do **not** `CompleteTask` in the solo sense that clears the agent — transition to PROCESS member (may still call internal complete hooks for logging, but group driver owns lifecycle).

Implementation note: refactor `CompleteTask` into:

- `NotifyMemberFinished()` for group-aware path  
- `CompleteSoloTask()` for legacy  

### 8.3 Degrade path (bug-sensitive)

Triggers:

- Reserved facility lost / foreign item appeared.
- `CanPlace` / process reject after retries exhausted.
- Agent force-abandon mid-group after M0 already placed item safely.
- Pathfinding failure to process facility after item in hand → stage via T1 fallback then degrade.

Algorithm:

```text
DegradeGroup():
  1. Ensure held item (if any) is staged: nearest free clear → else ground.
     (This IS completing M0’s postcondition if M0 was FETCH.)
  2. SetStepState(orderId, members[0].stepId, "completed")
     Do NOT complete later members’ steps.
  3. status = Degraded
  4. Release exclusive facility reservations
  5. Clear agent.currentGroup; agent becomes idle
  6. Discard remaining members from active set (they are not “abandoned tasks”
     that stick to the agent — they simply become eligible for regeneration)
```

Next scheduler tick:

- World has ingredient for order (on clear/ground).
- Generator emits **solo PROCESS** (or PROCESS→ADD group if plate ready).
- **Any idle agent** may take it (default assign policy). No `preferredAgentId`.

### 8.4 Full success

```text
CompleteGroup():
  for each member: SetStepState(stepId, completed)
  status = Completed
  Release reservations
  Clear agent.currentGroup; idle
```

## 9. Interaction with existing systems

| System | Change |
|--------|--------|
| `KitchenRouteResolver` | Used only to **decide fuse eligibility + pick facility** at generate/assign; not to soft-link solo FETCH. |
| `AIChefController` | Group driver; remove solo Bypass as default FETCH completion. |
| `KitchenAIManager.OnAgentTaskCompleted` | Branch on group vs solo; degrade vs complete. |
| Deadlock / ForceAbandon | Must call group release + degrade or abandon with reservation cleanup. |
| Debug / Action panel | Show group label + member checklist; on degrade show M0 checked. |
| Recording | Prefer emit one high-level group or sequential members with same `groupId` for offline learning consistency. |

## 10. Failure modes & mitigations

| Bug risk | Mitigation |
|----------|------------|
| Double FETCH while group pending | Active-step set includes pending group members |
| Two groups reserve same cutter | Atomic `TryReserveAll`; second assign fails |
| Degrade without releasing cutter | Single release helper `ReleaseGroupReservations` called from all exits |
| Degrade without staging item | Degrade always runs staging before step complete |
| Sticky agent after degrade | No preferredAgent; regenerate public solo tasks |
| Solo Bypass still races | Delete Bypass on solo FETCH path |
| Plate hard-lock starves kitchen | Soft plate lock only (§6.3) |
| PROCESS member starts but item not on board | Before starting M1, assert item on reserved facility or in hand; else degrade |
| Scheduler assigns solo PROCESS while group still executing same step | `alreadyActive` checks group membership |
| Abandon loop re-assign same doomed group | After degrade, group id retired; only solos regenerate until facilities free; optional cooldown on failed facility |

## 11. Tomato–cabbage salad walkthrough

Steps: FETCH Tomato, PROCESS→Slices, FETCH Cabbage, PROCESS→Slices, FETCH Cream, FETCH_PLATE, ADDs, SERVE.

**Tick with free cutters + idle chefs:**

- Emit `G_tomato = [FETCH Tomato, PROCESS→Slices]` reserve Board_A → Chef1  
- Emit `G_cabbage = [FETCH Cabbage, PROCESS→Slices]` reserve Board_B → Chef2  
- Emit solo FETCH Cream or later `G_cream = [FETCH Cream, ADD]` if plate ready  
- Emit FETCH_PLATE → Chef3  

**Chef1 success:** places tomato on Board_A, cuts, both steps checked, release Board_A.

**Chef1 degrade (extreme):** tomato staged on clear, only FETCH checked, Board_A released; later Chef4 (whoever idle) gets solo PROCESS.

## 12. Implementation phases

| Phase | Deliverable | Exit criteria |
|-------|-------------|----------------|
| **P0** | Hard reservation API + assign-time atomic reserve; debug visualization | Two chefs never assigned same cutter |
| **P1** | `KitchenTaskGroup` + FETCH→PROCESS fuse + in-agent member advance | No solo Bypass; salad tomato path works without abandon loop |
| **P2** | Degrade path + public requeue | Kill cutter mid-group → M0 checked, another chef finishes PROCESS |
| **P3** | FETCH/PROCESS→ADD groups | Cream/slices go plate without clear hop when plate ready |
| **P4** | Remove dead Bypass scheduler paths; logging/panel polish | Grep shows Bypass only as intra-group delivery |

## 13. Test plan (manual / playmode)

1. Single order salad, 4 chefs: two boards used in parallel for tomato/cabbage groups.  
2. One board only: second ingredient FETCH solos to clear; PROCESS waits until board free.  
3. Force degrade (debug button: invalidate reservation mid-carry): item on clear, step1 checked, other chef processes.  
4. Plate not ready: no FETCH→ADD group for cream; cream to clear; ADD after plate.  
5. SERVE still prioritizes over prep groups.  
6. ForceAbandon during group: reservations cleared, no stuck `reserved` facilities.  

## 14. Open decisions (defaults locked unless you object)

1. Group size: **pairwise only** (2 members) in P1–P3; longer chains (FETCH→PROCESS→ADD) as **optional P3.1** later.  
2. Soft plate lock as in §6.3.  
3. On group assign failure due to reserve: **skip group that tick**, optionally emit solo FETCH same tick if storage-only.  

---

## Approval

Please confirm this spec (especially §6 reservation, §8.3 degrade → public pool, pairwise groups first).  
After approval, next step is an implementation plan under `docs/superpowers/plans/`.
