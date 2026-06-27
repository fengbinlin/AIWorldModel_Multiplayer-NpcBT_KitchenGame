# Ground Drop & Server-Authoritative Physics — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Allow players to drop held KitchenObj on the ground with server-authoritative physics, and pick up free objects from the ground.

**Architecture:** Extend KitchenObj with Rigidbody + NetworkTransform (server-authoritative). Server runs physics; clients receive transform state. When held, Rigidbody is disabled and TransformFollower drives position. When free, Rigidbody runs on server and NetworkTransform syncs to clients. A new physics layer `KitchenObj` isolates item-item collisions from player colliders.

**Tech Stack:** Unity Netcode for GameObjects (NGO) v1.11.0, Unity Physics, UniTask

## Global Constraints

- All game-logic state changes must go through ServerRpc → ClientRpc pattern (server authority)
- Physics simulation runs only on the server; clients are visual-only
- KitchenObj physics layer must NOT collide with Player layer
- Drop/pickup uses the existing Interact key (E / Gamepad South button)
- Existing counter interactions take priority — ground logic only fires when `SelectedCounter == null`

---

## File Structure

```
Modify:
  Assets/Demos/KitchenChaos/_Scipts/KitchenObjects/KitchenObj.cs
    — Add Rigidbody ref, NetworkTransform ref, SetFree(), SetHeld(), IsFree prop
  Assets/Demos/KitchenChaos/_Scipts/KitchenObjects/KitchenObjFactory.cs
    — Add DropObjServerRpc, PickupObjServerRpc + client callbacks
  Assets/Demos/KitchenChaos/_Scipts/Player/Core/Player.Interact.cs
    — Add ground drop/pickup branch when SelectedCounter is null
  Assets/Demos/KitchenChaos/_Scipts/Player/Core/Player.cs
    — Add TryFindNearbyFreeKitchenObj() helper method
  Assets/Demos/KitchenChaos/_Scipts/Player/PlayerData.cs
    — Add dropForce, pickupRange fields
  ProjectSettings/TagManager.asset
    — Add KitchenObj layer (manual step documented)
  KitchenObj Prefab (.prefab)
    — Add Rigidbody component (disabled, kinematic), NetworkTransform component
```

---

### Task 1: Add dropForce and pickupRange to PlayerData

**Files:**
- Modify: `Assets/Demos/KitchenChaos/_Scipts/Player/PlayerData.cs`

**Interfaces:**
- Produces: `PlayerData.dropForce: float = 5f`, `PlayerData.pickupRange: float = 1.5f`

- [ ] **Step 1: Add fields to PlayerData**

```csharp
// Assets/Demos/KitchenChaos/_Scipts/Player/PlayerData.cs
// After the existing `collisionLayer` field, add:

public float dropForce = 5f;
public float pickupRange = 1.5f;
```

- [ ] **Step 2: Commit**

```bash
git add Assets/Demos/KitchenChaos/_Scipts/Player/PlayerData.cs
git commit -m "feat: add dropForce and pickupRange to PlayerData"
```

---

### Task 2: Extend KitchenObj with Free/Held state and physics management

**Files:**
- Modify: `Assets/Demos/KitchenChaos/_Scipts/KitchenObjects/KitchenObj.cs`

**Interfaces:**
- Consumes: (none from prior tasks — uses existing `TransformFollower`, `NetworkObject`)
- Produces:
  - `KitchenObj.IsFree: bool` — true when on ground, false when held
  - `KitchenObj.SetFree(Vector3 dropPosition, Vector3 dropDirection, float dropForce): void` — server -> all clients transition
  - `KitchenObj.SetHeld(ICanHoldKitchenObj holder): void` — server -> all clients transition

- [ ] **Step 1: Add component references and IsFree property**

```csharp
// Assets/Demos/KitchenChaos/_Scipts/KitchenObjects/KitchenObj.cs
// Replace the entire file content:

using System;
using Kitchen;
using Nico.Components;
using Unity.Netcode;
using Unity.Netcode.Components;
using UnityEngine;

namespace Kitchen
{
    public class KitchenObj : NetworkBehaviour
    {
        public KitchenObjEnum objEnum;

        protected ICanHoldKitchenObj holder;

        [field: SerializeField] public TransformFollower follower { get; private set; }

        private Rigidbody _rigidbody;
        private NetworkTransform _networkTransform;

        public bool IsFree { get; private set; }

        private void Awake()
        {
            follower = GetComponent<TransformFollower>();
            _rigidbody = GetComponent<Rigidbody>();
            _networkTransform = GetComponent<NetworkTransform>();

            // Rigidbody starts disabled — only enabled when free (on server)
            if (_rigidbody != null)
            {
                _rigidbody.isKinematic = true;
                _rigidbody.useGravity = false;
            }
        }

        /// <summary>
        /// Called by ServerRpc handler on server, then broadcast to all clients via ClientRpc.
        /// Enables physics on server, clears holder on all clients.
        /// </summary>
        public void SetFree(Vector3 dropPosition, Vector3 dropDirection, float dropForce)
        {
            IsFree = true;
            holder = null;
            follower.SetFollowTarget(null);

            if (IsServer)
            {
                // Server runs physics
                transform.position = dropPosition;
                if (_rigidbody != null)
                {
                    _rigidbody.isKinematic = false;
                    _rigidbody.useGravity = true;
                    _rigidbody.AddForce(dropDirection.normalized * dropForce, ForceMode.Impulse);
                }
            }
            else
            {
                // Clients receive position via NetworkTransform; no physics
                if (_rigidbody != null)
                {
                    _rigidbody.isKinematic = true;
                    _rigidbody.useGravity = false;
                }
            }
        }

        /// <summary>
        /// Called on all clients when an object is picked up.
        /// Disables physics, sets holder.
        /// </summary>
        public void SetHeld(ICanHoldKitchenObj newHolder)
        {
            IsFree = false;

            if (_rigidbody != null)
            {
                _rigidbody.isKinematic = true;
                _rigidbody.useGravity = false;
                _rigidbody.velocity = Vector3.zero;
            }

            SetHolder(newHolder);
        }

        public void SetHolder(ICanHoldKitchenObj iholder)
        {
            follower.SetFollowTarget(iholder.GetHoldTransform());
            holder = iholder;
        }

        public ICanHoldKitchenObj GetHolder()
        {
            return holder;
        }

        public void ClearHolder()
        {
            holder.ClearKitchenObj();
            holder = null;
        }
    }
}
```

- [ ] **Step 2: Commit**

```bash
git add Assets/Demos/KitchenChaos/_Scipts/KitchenObjects/KitchenObj.cs
git commit -m "feat: add Free/Held state management to KitchenObj with physics"
```

---

### Task 3: Add DropObjServerRpc and PickupObjServerRpc to KitchenObjFactory

**Files:**
- Modify: `Assets/Demos/KitchenChaos/_Scipts/KitchenObjects/KitchenObjFactory.cs`

**Interfaces:**
- Consumes: `KitchenObj.SetFree()`, `KitchenObj.SetHeld()`, `KitchenObj.IsFree`
- Produces:
  - `KitchenObjFactory.DropObjServerRpc(NetworkObjectReference, Vector3, Vector3, float): void`
  - `KitchenObjFactory.PickupObjServerRpc(NetworkObjectReference, NetworkObjectReference): void`

- [ ] **Step 1: Add DropObjServerRpc method**

```csharp
// Append to KitchenObjFactory class, before the final closing brace:

[ServerRpc(RequireOwnership = false)]
public void DropObjServerRpc(NetworkObjectReference objRef, Vector3 dropPosition, Vector3 dropDirection, float dropForce)
{
    _DropObjClientRpc(objRef, dropPosition, dropDirection, dropForce);
}

[ClientRpc]
private void _DropObjClientRpc(NetworkObjectReference objRef, Vector3 dropPosition, Vector3 dropDirection, float dropForce)
{
    objRef.TryGet(out NetworkObject obj);
    if (obj == null) return;
    var kitchenObj = obj.GetComponent<KitchenObj>();
    kitchenObj.SetFree(dropPosition, dropDirection, dropForce);
}
```

- [ ] **Step 2: Add PickupObjServerRpc method**

```csharp
// Append to KitchenObjFactory class:

[ServerRpc(RequireOwnership = false)]
public void PickupObjServerRpc(NetworkObjectReference objRef, NetworkObjectReference holderRef)
{
    _PickupObjClientRpc(objRef, holderRef);
}

[ClientRpc]
private void _PickupObjClientRpc(NetworkObjectReference objRef, NetworkObjectReference holderRef)
{
    objRef.TryGet(out NetworkObject obj);
    holderRef.TryGet(out NetworkObject holderObj);
    if (obj == null || holderObj == null) return;

    var kitchenObj = obj.GetComponent<KitchenObj>();
    var holder = holderObj.GetComponent<ICanHoldKitchenObj>();
    kitchenObj.SetHeld(holder);
    holder.SetKitchenObj(kitchenObj);
}
```

- [ ] **Step 3: Commit**

```bash
git add Assets/Demos/KitchenChaos/_Scipts/KitchenObjects/KitchenObjFactory.cs
git commit -m "feat: add DropObjServerRpc and PickupObjServerRpc"
```

---

### Task 4: Add ground pickup helper to Player

**Files:**
- Modify: `Assets/Demos/KitchenChaos/_Scipts/Player/Core/Player.cs`

**Interfaces:**
- Consumes: `PlayerData.pickupRange`, `KitchenObj.IsFree`
- Produces: `Player.TryFindNearbyFreeKitchenObj(): KitchenObj` — returns nearest free KitchenObj within pickup range, or null

- [ ] **Step 1: Add TryFindNearbyFreeKitchenObj method**

```csharp
// Add to the Player partial class in Player.cs, inside the class body:

/// <summary>
/// Finds the nearest free (on-ground) KitchenObj within pickup range.
/// Returns null if none found.
/// </summary>
public KitchenObj TryFindNearbyFreeKitchenObj()
{
    var hits = Physics.OverlapSphere(transform.position, data.pickupRange);
    KitchenObj nearest = null;
    float nearestDist = float.MaxValue;

    foreach (var hit in hits)
    {
        if (hit.TryGetComponent(out KitchenObj kitchenObj) && kitchenObj.IsFree)
        {
            float dist = Vector3.Distance(transform.position, hit.transform.position);
            if (dist < nearestDist)
            {
                nearestDist = dist;
                nearest = kitchenObj;
            }
        }
    }

    return nearest;
}
```

- [ ] **Step 2: Commit**

```bash
git add Assets/Demos/KitchenChaos/_Scipts/Player/Core/Player.cs
git commit -m "feat: add TryFindNearbyFreeKitchenObj to Player"
```

---

### Task 5: Add ground drop/pickup logic to Player.Interact

**Files:**
- Modify: `Assets/Demos/KitchenChaos/_Scipts/Player/Core/Player.Interact.cs`

**Interfaces:**
- Consumes: `Player.TryFindNearbyFreeKitchenObj()`, `KitchenObjFactory.DropObjServerRpc()`, `KitchenObjFactory.PickupObjServerRpc()`, `PlayerData.dropForce`
- Produces: (new behavior — no new public API)

- [ ] **Step 1: Replace OnPerformInteract with drop/pickup logic**

```csharp
// Assets/Demos/KitchenChaos/_Scipts/Player/Core/Player.Interact.cs
// Replace the ENTIRE file:

using System;
using UnityEngine;

namespace Kitchen.Player
{
    public partial class Player
    {
        public event Action pause;

        private void OnPerformInteract()
        {
            if (!GameManager.Instance.IsPlaying()) return;

            // Priority 1: Interact with selected counter
            if (SelectedCounter != null)
            {
                Debug.Log("按下交互键 - 与柜台交互");
                SelectedCounter.Interact(this);
                return;
            }

            // Priority 2: Drop held item on ground
            if (HasKitchenObj())
            {
                DropOnGround();
                return;
            }

            // Priority 3: Pickup free item from ground
            var nearbyObj = TryFindNearbyFreeKitchenObj();
            if (nearbyObj != null)
            {
                PickupFromGround(nearbyObj);
            }
        }

        private void DropOnGround()
        {
            var dropPos = transform.position + transform.forward * 1f + Vector3.up * 0.5f;
            var dropDir = transform.forward;
            var obj = GetKitchenObj();
            ClearKitchenObj();
            KitchenObjFactory.Instance.DropObjServerRpc(
                obj.NetworkObject, dropPos, dropDir, data.dropForce);
        }

        private void PickupFromGround(KitchenObj kitchenObj)
        {
            KitchenObjFactory.Instance.PickupObjServerRpc(
                kitchenObj.NetworkObject, this.NetworkObject);
        }

        private void OnPerformInteractAlternate()
        {
            if (!GameManager.Instance.IsPlaying()) return;

            if (SelectedCounter == null) return;
            if (SelectedCounter.TryGetComponent(out IInteractAlternate interactAlternate))
            {
                interactAlternate.InteractAlternate(this);
            }
        }
    }
}
```

- [ ] **Step 2: Commit**

```bash
git add Assets/Demos/KitchenChaos/_Scipts/Player/Core/Player.Interact.cs
git commit -m "feat: add ground drop and pickup to player interact"
```

---

### Task 6: Add KitchenObj physics layer and configure prefab

**This task requires Unity Editor — document the manual steps.**

**Files:**
- Modify: KitchenObj Prefab (in Unity Editor)
- Modify: `ProjectSettings/TagManager.asset` (via Unity Editor)

**Interfaces:**
- Consumes: (Task 2 code — Rigidbody and NetworkTransform references)
- Produces: Working physics setup

- [ ] **Step 1: Create "KitchenObj" physics layer**

1. Open Unity Editor
2. Edit → Project Settings → Tags and Layers
3. Add a new layer: User Layer 8 → name it `KitchenObj`

- [ ] **Step 2: Configure collision matrix**

1. Edit → Project Settings → Physics
2. In the Layer Collision Matrix, find the `KitchenObj` row:
   - `KitchenObj` × `KitchenObj` = ☑ (checked — items collide with each other)
   - `KitchenObj` × `Default` = ☑ (checked — collide with walls/floor)
   - `KitchenObj` × `Player` = ☐ (unchecked — items don't collide with player)
   - All other pairs = ☐ (unchecked)

- [ ] **Step 3: Update KitchenObj Prefab**

1. Open the KitchenObj Prefab (all variants: MeatPatty, Plate, etc.)
2. Set the root GameObject's layer to `KitchenObj`
3. Add component → `Rigidbody`:
   - Mass: 1
   - Drag: 2
   - Angular Drag: 1
   - Use Gravity: ☑
   - Is Kinematic: ☑ (starts kinematic, enabled by code)
   - Collision Detection: Continuous Dynamic
4. Add component → `Network Rigidbody` (optional, for cleaner sync; if not available use `NetworkTransform`):
   - **If using NetworkTransform instead:** Add `Network Transform` component
   - Must be server-authoritative (default, do NOT use ClientNetworkTransform)
5. Apply changes to all KitchenObj prefab variants

- [ ] **Step 4: Commit**

```bash
git add Assets/Demos/KitchenChaos/_Scipts/KitchenObjects/  # any .prefab changes
git add ProjectSettings/TagManager.asset
git commit -m "feat: configure KitchenObj physics layer and prefab Rigidbody"
```

---

### Task 7: Manual verification test

**No code changes — verify end-to-end in Unity Editor with ParrelSync.**

- [ ] **Step 1: Test drop**

1. Start Host in original project
2. Start Client in ParrelSync clone
3. Host player: approach ContainerCounter, press Interact to pick up a MeatPatty
4. Walk away from any counter, press Interact
5. **Expected:** MeatPatty drops in front of player, falls with gravity (on server), both Host and Client see it on the ground
6. Client player: repeat steps 3-4
7. **Expected:** Same behavior — client can also drop items and both see the result

- [ ] **Step 2: Test pickup from ground**

1. Host player: walk up to dropped MeatPatty (no counter nearby), press Interact
2. **Expected:** MeatPatty is picked up, no longer on ground
3. Client should see the MeatPatty disappear from ground and appear in Host player's hands
4. Client player: repeat steps 1-2
5. **Expected:** Same behavior

- [ ] **Step 3: Test physics collisions**

1. Host: drop two MeatPatties near each other on the ground
2. **Expected:** They should collide with each other and the environment (not pass through)
3. Player can walk through them (no collision with Player)

- [ ] **Step 4: Test counter priority**

1. Stand near a ClearCounter with a MeatPatty in hand, press Interact
2. **Expected:** Places on counter (not on ground) — counter interaction takes priority

---
