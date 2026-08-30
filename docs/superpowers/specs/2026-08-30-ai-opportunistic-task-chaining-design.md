# Opportunistic Task Chaining Design

Date: 2026-08-30  
Status: approved  
Supersedes: `2026-08-30-ai-task-group-reservation-design.md` (task groups — abandoned)

## 1. Intent

Keep **one-task-at-a-time dispatch** (simple board, simple logging), but make **clear-counter staging optional**.

When an AI is about to park an item on a clear counter / ground, the system asks:

> Is there a board task that can consume what I am holding **right now**, and is its facility free / reservable?

- **Yes** → skip staging, go straight to that facility / plate, continue work, **check all chained steps when the continuous run finishes**.
- **No** → do the normal unit-task ending (clear / ground), check only the current task’s step.

Additionally, when assigning PROCESS / ADD, if an agent **holds** the matching ingredient for that order (or just delivered it in the same continuous run), that agent is the **only** allowed assignee while they remain eligible — not a soft sort tip.

This matches the lived sequence:

1. Doing FETCH Tomato, about to put on clear → board empty → divert to cutting board, start cutting.  
2. After cut, both FETCH + PROCESS checked.  
3. About to stage slices → plate ready → divert to plate ADD.  
4. ADD checked.

## 2. Goals / non-goals

**Goals**

1. Unit tasks remain the only dispatch unit (`FETCH`, `PROCESS`, `ADD_TO_PLATE`, …).
2. Staging to clear/ground is a **fallback**, not the default success path when a better sink exists.
3. Realtime facility reservation when claiming a chain continuation or exclusive PROCESS target.
4. **Holder exclusivity** for PROCESS / ADD: while a matching holder (or same-run deliverer) exists and can take the task, nobody else gets it.
5. On failure mid-chain: keep already-achieved world state; check only steps whose postconditions are true; remaining stay on the public board.

**Non-goals**

- Explicit `KitchenTaskGroup` objects / multi-member assign packets.
- Utility score revival.
- Forcing one AI to own an entire recipe.

## 3. Vocabulary

| Term | Meaning |
|------|---------|
| **Unit task** | Normal `KitchenTask` on the board / assigned to an agent. |
| **Staging** | Putting the held item on a free clear counter, else ground. |
| **Chain candidate** | Another unit task (same `orderId`) whose input matches held item / just-produced output, and whose sink is currently usable. |
| **Continuation** | Agent finishes current task’s “obtain/produce” part, then **claims** a chain candidate and keeps executing without going idle. |
| **Chain session** | Soft runtime state on the agent: list of stepIds completed in this continuous run (for batch checkbox / logging). |

No persistent group id in the pool.

## 4. Where decisions live

Two cooperating layers (both allowed; dispatcher is authoritative for claims):

```text
┌─────────────────────┐     claim / reserve      ┌──────────────────────┐
│ KitchenAIManager    │◄────────────────────────►│ AIChefController     │
│ + TaskGenerator     │                          │ “about to stage?”    │
│ Assign prefers      │     TryClaimContinuation │ → AskForContinuation │
│ holders             │◄─────────────────────────│ → Move to sink       │
└─────────────────────┘                          └──────────────────────┘
```

- **Chef** detects the moment: “I would stage now” (end of FETCH carry, end of PROCESS with output in hand, etc.).
- **Dispatcher / blackboard API** atomically: finds candidate, reserves facility, marks task assigned to this agent, returns it.
- Chef never “steals” a task already assigned to someone else.

## 5. Continuation API (core)

```csharp
// Pseudo
bool TryClaimContinuation(
    AgentState agent,
    KitchenObj held,           // or null if item already on reserved facility
    int orderId,
    out KitchenTask next,
    out BaseCounter sink);
```

### 5.1 Candidate matching rules

Two slightly different moments:

**A. Mid-FETCH look-ahead (item in hand, FETCH not yet ✓)**  
Do **not** force-claim PROCESS while its normal preconditions are still false.  
Only ask: “Will PROCESS be the next unit for this order once I put this down, and is a process facility free?”  
If yes → **divert FETCH destination** to that facility (and soft-reserve it). PROCESS stays on the board; others still cannot take it because **FETCH has not completed / input not available**.

**B. After postcondition true (item on facility, or product in hand)**  
Normal assign / `TryClaimContinuation`: PROCESS / ADD whose preconditions are now met.

| Moment | What we do | Sink |
|--------|------------|------|
| Holding raw Tomato during FETCH | Look-ahead next PROCESS for this order; divert FETCH dest | Free/reservable cutting board |
| Tomato just placed on board, FETCH ✓ | Claim/assign PROCESS (prefer this agent as holder/near) | Same board |
| Holding TomatoSlices after PROCESS | Claim ADD if plate ready | Order plate counter |
| Holding Cream after FETCH | Claim ADD if plate ready | Order plate counter |

Priority if multiple match: same `orderId` → stable board order.

### 5.2 Atomic claim

```text
TryClaimContinuation:
  1. Find best candidate C
  2. If C needs exclusive facility F:
       if !TryReserve(F, agent): return false
  3. If C already assigned to other: return false
  4. Assign C to agent (status=assigned/executing), set agent.currentTask=C
  5. Record C.stepId into agent.chainSession
  6. return true
```

Must run on server/host scheduler lock (same flag as assign tick) to avoid double-claim.

### 5.3 When chef calls it

Call **before** staging, at these hooks:

1. `BeginFetchedItemDelivery` / resolve FETCH destination — **first** try continuation sink as destination instead of clear.  
2. After PROCESS output taken into hand — before `ResolveDeliveryDestination` clear.  
3. Optionally: arrived at clear with item still in hand and a candidate appeared while walking (re-check once on arrival).

If claim succeeds → `destFacility = sink`, execute next task logic (may already be at sink).  
If claim fails → staging path; complete **only** current unit task.

## 6. Checkbox / completion semantics

### 6.1 Unit complete without chain

Normal today: set `stepId` completed, release reservations for that task, agent idle.

### 6.2 Chained run — what “打勾” means

Each unit task has its own checkbox. We mark it **as soon as that task’s own success condition is true**, not when the whole continuous streak ends.

Example FETCH → PROCESS → ADD:

| When | Which box |
|------|-----------|
| Tomato placed on the cutting board | FETCH ✓ |
| Cutting finishes / slices ready | PROCESS ✓ |
| Slices put on the order plate | ADD ✓ |

Continuous work only means: **no clear-counter hop and no idle gap** between them. The three boxes still flip **one after another** as each step succeeds (often within seconds, so it feels like “一起打勾”).

We do **not** wait until ADD finishes and then suddenly check FETCH+PROCESS+ADD in one shot.

### 6.3 Mid-chain failure

If PROCESS claimed but board becomes invalid:

1. Stage held item (clear / ground) — always possible.  
2. Complete steps whose postconditions already hold (e.g. FETCH done if tomato already on a counter under our order tag).  
3. Release PROCESS reservation; abandon/requeue PROCESS as unassigned.  
4. Agent idle. Other agents may take PROCESS later (**prefer holder** if someone holds it).

## 7. Assignment policy (holder exclusivity)

When assigning a task `T` that needs item type `I` (PROCESS, ADD_TO_PLATE, …):

```text
eligibleHolders = idle agents who:
  - hold I bound to T.orderId, OR
  - just delivered I onto T’s target facility in the same chainSession
     (hands may be empty; they are still the exclusive next agent)

if eligibleHolders is non-empty:
  assign T only to one of them (never to a non-holder)
else:
  assign with normal panel / queue order
  (also prefer agents standing at the counter that holds I — exclusive
   if exactly one such agent is idle next to that item)
```

This is **hard exclusivity while a holder exists**, not a soft sort weight:

- A holds tomato / just put tomato on Board_X → only A may receive PROCESS for that tomato.  
- B must not be assigned that PROCESS “because A is busy walking” if A is still the holder / deliverer and will take it next.  
- If A goes idle without claiming (chain failed, abandoned), exclusivity ends when they no longer hold / are no longer the deliverer — then others may take it.

FETCH / FETCH_PLATE: no holder rule (or SERVE prefers plate holder the same exclusive way).

## 8. Facility reservation

Keep hard reservation for exclusive facilities:

| Action | Reserve |
|--------|---------|
| Assign solo PROCESS | Target cutter/stove/… |
| ClaimContinuation → PROCESS | Same, atomic with claim |
| Assign/chain ADD | Soft plate lock (order plate), not whole clear counter |
| Complete / abandon / degrade | Release |

Realtime: every scheduler tick + every claim re-check foreign occupancy.

Staging clear counters: **not** exclusively reserved (shared).

## 9. End-to-end: Tomato → Slices → Plate

Board initially has (among others):  
`FETCH Tomato`, `PROCESS Tomato→Slices`, `ADD Slices`, … (only those whose deps/world allow).

### Story A — happy chain

1. Assign `FETCH Tomato` to Chef A.  
2. A picks tomato; about to stage → look-ahead sees upcoming PROCESS + free Board_X → divert FETCH dest (soft-reserve Board_X). PROCESS still not assignable to others.  
3. A places on Board_X → FETCH ✓ → PROCESS now eligible → immediate claim/prefer-holder to A.  
4. A runs PROCESS, gets slices → PROCESS ✓.  
5. About to stage slices → claim ADD if plate ready → ADD ✓.  
6. A idle.

### Story B — no board at FETCH end

1. A has tomato; claim PROCESS fails (all cutters reserved/busy).  
2. A stages to clear; FETCH ✓ only.  
3. Later Board frees; assign PROCESS prefers agent holding tomato (none) or near clear — Chef B takes PROCESS.  

### Story C — board ok, plate not ready after cut

1. A chained FETCH→PROCESS; both ✓.  
2. Claim ADD fails (no plate).  
3. A stages slices; idle.  
4. Plate arrives; ADD assigned preferring whoever holds slices / near slices.

## 10. Relationship to old Bypass

| Old | New |
|-----|-----|
| FETCH task embeds `BypassToProcessFacility` at generate time | FETCH generates as normal; **destination chosen at execute/claim time** |
| Next tick hopes PROCESS appears | PROCESS pre-exists on board when input will exist / deps OK; claim attaches it now |
| Two agents race same board | Reserve at claim/assign |

Generate-time route hints may remain as **soft suggestions** but must not skip claim/reserve.

## 11. Data / code touchpoints

1. `KitchenBlackboard` / manager: `TryClaimContinuation`, holder-aware sort in assign.  
2. `AIChefController`: replace “always FindDropTarget/clear” with “Ask continuation then stage”.  
3. `KitchenTaskGenerator`: still emits unit tasks under **normal dependency / world gates** (no special “early PROCESS only for agent A” lock).  
4. Remove/disable solo FETCH auto-Bypass as success path; replace with look-ahead divert + prefer-holder claim.  
5. Debug panel: show `chainSession` streak on agent.

### 11.1 Closed loop: deps + prefer holder (no special PROCESS lock)

PROCESS can sit on the board whenever the generator’s normal rules allow. Before A finishes FETCH, **PROCESS simply is not assignable** (input not ready / FETCH step not done) — so nobody else gets it. That is ordinary precondition logic, not a private lock.

After A places the tomato (FETCH ✓), PROCESS becomes eligible. Assignment / immediate continuation then gives it **only to A** while A is the holder / same-run deliverer.

Together:

```text
FETCH 未完成 → PROCESS 条件不满足 → 别人拿不到
FETCH 完成 + 持有人排他 → 只派给 A（一定是他做）
即将空台时 look-ahead → 直接送刀板，放上即勾 FETCH，马上认领 PROCESS
```

No extra private lock on the PROCESS row beyond normal deps + this exclusivity.

## 12. Failure matrix

| Risk | Mitigation |
|------|------------|
| Two chefs claim same PROCESS | Atomic claim + reserve |
| PROCESS assigned to B while A still mid-FETCH | Impossible: PROCESS preconditions false until input ready |
| PROCESS assigned to B while A holds tomato on clear | Forbidden: holder exclusivity |
| PROCESS assigned to B while A just delivered to board | Forbidden until A’s exclusivity ends |
| Infinite re-claim / divert loop | One look-ahead or claim per “about to stage” event |
| Checkbox FETCH never fires if die before place | On abandon with tomato in hand → stage then complete FETCH if staged for order |
| Plate counter hard-lock | Soft plate lock only |

## 13. Phased delivery

| Phase | Work | Exit |
|-------|------|------|
| **P0** | Hard reserve on PROCESS assign; holder exclusivity | No dual PROCESS; holder always gets it |
| **P1** | `TryClaimContinuation` FETCH→PROCESS; staging fallback | Tomato story A/B |
| **P2** | Continuation PROCESS→ADD / FETCH→ADD | Cream & slices to plate |
| **P3** | Remove generate-time Bypass; panel chainSession | Clean logs |
| **P4** | Re-check continuation on arrive-at-clear | Late plate appearance |

## 14. Test plan

1. Free board + plate ready: one chef FETCH→cut→ADD without clear hop; three checks in sequence.  
2. No board: FETCH stages; second chef PROCESS later.  
3. Board free, no plate: FETCH→PROCESS chain; slices staged; ADD later.  
4. Two tomatoes two boards: two chefs chain independently.  
5. Force fail reserve after claim: stage + partial checks + public PROCESS.  
6. Holder exclusivity: agent holding slices is the only one who can get that ADD while they hold them.

## 15. Defaults

- Holder exclusivity while a matching holder/deliverer exists; otherwise normal assign.  
- No TaskGroup type.  
- Prefer-holder wording deprecated — use **holder exclusivity**.

---

## Approval

Confirm §5 look-ahead vs claim, §6 per-step checkboxes, §7 holder exclusivity.  
Then implementation plan → code.
