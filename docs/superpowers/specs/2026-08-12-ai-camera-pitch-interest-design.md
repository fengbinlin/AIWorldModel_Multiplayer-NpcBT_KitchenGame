# AI First-Person Pitch Interest Bias

Date: 2026-08-12

## Goal

AI `AICamera` pitch is no longer fixed. It smoothly biases toward a single task interest point on the pitch axis only. Yaw stays body-driven. Recording reverse-engineering already stores `mouseY` / `rotX`; those fields now become non-trivial.

## Behavior

- **Yaw**: unchanged (pathing + `FaceTarget` only). Camera local Y/Z forced to 0.
- **Pitch**: smooth bias around prefab default (~14.8°), clamp ±22.5°.
- **Interest priority** (first match wins):
  1. `GotoItem` → carry target item / its counter
  2. PROCESS `waiting`/`working` → process facility
  3. Active task with `_targetCounter` → that counter
  4. Holding item while traveling, no counter → held item
  5. Else → return to default pitch

## Implementation

| Piece | Role |
|-------|------|
| `AIChefController.TryGetLookInterestWorldPoint` | Interest resolution |
| `ChefCameraPitchController` | Local-X only SmoothDamp; execution order -50 |
| `ChefRecordingAgent.SyncPitchForCapture` | Tick pitch before FP image + angle sample |
| `KitchenSessionRecorder.CaptureSimFrame` | Calls `SyncPitchForCapture` per agent |

## Recording

- `rotX` = signed local pitch after bias
- `mouseY` = `DeltaAngle(prevPitch, pitch)` written back as action on previous frame
- `mouseX` still from body/camera world yaw only
