"""Generate a tiny synthetic session for smoke-testing the viewer (no Unity required)."""

from __future__ import annotations

import json
import math
from pathlib import Path

from PIL import Image, ImageDraw


def _make_image(path: Path, color: tuple[int, int, int], label: str, size: int = 128) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    img = Image.new("RGB", (size, size), color)
    draw = ImageDraw.Draw(img)
    draw.rectangle((8, 8, size - 9, size - 9), outline=(255, 255, 255), width=2)
    draw.text((16, size // 2 - 6), label, fill=(255, 255, 255))
    img.save(path)


def _identity_matrix() -> list[float]:
    return [
        1, 0, 0, 0,
        0, 1, 0, 0,
        0, 0, 1, 0,
        0, 0, 0, 1,
    ]


def _camera_info(
    name: str,
    pos: tuple[float, float, float],
    rot: tuple[float, float, float],
    width: int = 128,
    height: int = 128,
    fov_y: float = 60.0,
) -> dict:
    fy = (height * 0.5) / math.tan(math.radians(fov_y) * 0.5)
    return {
        "name": name,
        "int": {
            "fx": fy,
            "fy": fy,
            "cx": width * 0.5,
            "cy": height * 0.5,
            "width": width,
            "height": height,
            "fovY": fov_y,
        },
        "ext": {
            "posX": pos[0],
            "posY": pos[1],
            "posZ": pos[2],
            "rotX": rot[0],
            "rotY": rot[1],
            "rotZ": rot[2],
            "matrix": _identity_matrix(),
        },
    }


def main() -> None:
    root = Path(__file__).resolve().parent.parent / "KitchenTrainingRecordings" / "session_sample_preview"
    root.mkdir(parents=True, exist_ok=True)

    fps = 10.0
    n_frames = 30
    players_meta = [
        {"id": 1, "name": "Player_Red", "color": (180, 60, 60)},
        {"id": 2, "name": "Player_Blue", "color": (60, 90, 180)},
        {"id": 3, "name": "Player_Green", "color": (60, 150, 80)},
        {"id": 4, "name": "Player_Yellow", "color": (180, 160, 50)},
    ]

    manifest = {
        "sessionId": "session_sample_preview",
        "gameName": "Kitchen Chaos",
        "unityTimeStart": 0.0,
        "frameWidth": 128,
        "frameHeight": 128,
        "captureFps": fps,
        "playerCount": len(players_meta),
        "totalFrames": n_frames,
        "task_description": (
            "Kitchen Chaos: multi-agent cooking. Players fetch ingredients, cut/cook at counters, "
            "plate and deliver dishes before orders expire. The AI must assign and execute kitchen "
            "tasks (fetch/process/plate/serve/trash) to complete waiting orders cooperatively."
        ),
    }
    (root / "manifest.json").write_text(json.dumps(manifest, indent=2), encoding="utf-8")

    scene_3d = {
        "facilities": [
            {
                "name": "CuttingCounter",
                "facilityType": "CuttingBoard",
                "posX": 1.0,
                "posY": 0.0,
                "posZ": 2.0,
                "rotY": 90.0,
                "sizeX": 1.2,
                "sizeY": 1.0,
                "sizeZ": 0.8,
            }
        ],
        "spawnPoints": [
            {"posX": 0.0, "posY": 0.0, "posZ": 0.0},
            {"posX": 2.0, "posY": 0.0, "posZ": 0.0},
        ],
    }

    # IDM: action on frame i is the control that takes state_i → state_(i+1).
    # Last frame has zero action (no next state).
    frames_path = root / "frames.jsonl"
    with frames_path.open("w", encoding="utf-8") as fh:
        for i in range(n_frames):
            t = i / fps
            angle = t * math.pi
            global_rel = f"global/frame_{i:06d}.png"
            _make_image(root / global_rel, (45, 90, 70), f"G {i}")

            has_next = i + 1 < n_frames
            player_frames = []
            for ci, player in enumerate(players_meta):
                rel = f"agent_{player['id']}/fp_{i:06d}.png"
                _make_image(root / rel, player["color"], f"{player['name'][:1]}{i}")
                # Action written on Fi drives toward F(i+1); computed as if from F(i+1) look-back.
                moving = ((i + 1) // 3 + ci) % 4 if has_next else -1
                keys = {
                    "keyW": has_next and moving == 0,
                    "keyA": has_next and moving == 1,
                    "keyS": has_next and moving == 2,
                    "keyD": has_next and moving == 3,
                    "keyE": has_next and (i + 1) % 15 == 0,
                }
                local_fwd = math.cos(angle + ci * 0.4) if has_next else 0.0
                local_strafe = math.sin(angle + ci * 0.4) if has_next else 0.0
                yaw = (t * 90 + ci * 40) % 360
                pitch = 10.0 * math.sin(angle + ci)
                next_t = (i + 1) / fps
                next_pitch = 10.0 * math.sin(next_t * math.pi + ci)
                mouse_x = 9.0 if has_next else 0.0
                mouse_y = (next_pitch - pitch) if has_next else 0.0
                substates = ["idle", "moving", "interacting", "working", "waiting"]
                pos = (
                    5.0 + ci * 2 + math.sin(angle),
                    1.6,
                    3.0 + math.cos(angle),
                )
                player_frames.append(
                    {
                        "playerId": player["id"],
                        "playerName": player["name"],
                        "fpImage": rel,
                        **keys,
                        "moveX": local_strafe if has_next else 0.0,
                        "moveZ": local_fwd if has_next else 0.0,
                        "mouseX": mouse_x,
                        "mouseY": mouse_y,
                        "posX": pos[0],
                        "posY": 0.0,
                        "posZ": pos[2],
                        "rotX": pitch,
                        "rotY": yaw,
                        "captureTime": t,
                        "substate": substates[(i + ci) % len(substates)],
                        "taskType": ["FETCH", "PROCESS", "SERVE"][(i // 5 + ci) % 3],
                        "taskLabel": f"demo step {i // 5}",
                        "heldItem": "" if i % 7 else "Tomato",
                        "camera_info": _camera_info("fp", pos, (pitch, yaw, 0.0)),
                    }
                )

            gpos = (10.0 + 0.05 * i, 12.0, -8.0)
            frame = {
                "frame": i,
                "time": t,
                "globalImage": global_rel,
                "camera_info": _camera_info("global", gpos, (45.0, -30.0, 0.0)),
                "scene_3d_info": scene_3d,
                "players": player_frames,
                "world": {
                    "facilities": [
                        {
                            "name": "CuttingCounter",
                            "facilityType": "Cutting",
                            "state": "free" if i % 2 == 0 else "occupied",
                            "posX": 1,
                            "posY": 0,
                            "posZ": 2,
                            "counterItem": "Tomato" if i % 2 else "",
                            "reservedByAgent": 0,
                            "occupiedByAgent": 1 if i % 2 else 0,
                        }
                    ],
                    "items": [
                        {
                            "id": 10,
                            "itemType": "Tomato",
                            "stage": "raw",
                            "posX": 1,
                            "posY": 0,
                            "posZ": 2,
                            "carriedByAgent": 0,
                            "orderId": 1,
                        }
                    ],
                    "orders": [
                        {
                            "orderId": 1,
                            "recipeName": "Salad",
                            "ingredients": ["Tomato", "Cabbage"],
                        }
                    ],
                    "tasks": [
                        {
                            "taskId": 1,
                            "taskType": "FETCH",
                            "label": "get tomato",
                            "status": "executing",
                            "assignedAgentId": 1,
                            "orderId": 1,
                        }
                    ],
                },
            }
            fh.write(json.dumps(frame, ensure_ascii=False) + "\n")

    print(f"Sample session written to:\n  {root}")


if __name__ == "__main__":
    main()
