# AI Chef Layer-lab Visual + Animancer

Date: 2026-08-12

## Goal

Replace default AIPlayer mesh with Layer lab `Character_*` prefabs and drive animations via Animancer FSM, decoupled from `AIChefController` logic.

## Mapping

| AI substate / signal | Visual mode | Clip (Layer lab) |
|----------------------|-------------|------------------|
| idle / paused / postInteract | Idle | Stand_Idle1 |
| moving (slow) | Move | Action_Walk |
| moving (fast) | Move | Action_Run |
| OnInteractionPerformed + holding | Interact | Interaction_Pickup |
| OnInteractionPerformed + empty | Interact | Interaction_Item_Put |
| working | Work | Interaction_Sickle |
| waiting | Wait | Stand_Idle2 |

## Components

- `ChefVisualAnimSet` — clip references (`Resources/So/AI/ChefVisualAnimSet_LayerLab`)
- `AIChefVisualPresenter` — Animancer `StateMachine<ChefVisualMode, ChefVisualState>`
- `AIChefVisualInstaller.EnsureOn` — called from `KitchenAIManager` after spawn/skin
- Skin catalog `AIPlayer` / `Player` → `Character_1.prefab`

## Decoupling

AI still only exposes `Substate`, `HeldItem`, `OnInteractionPerformed`, velocity via A*. Visual polls/binds; no animation code inside AI task FSM.
