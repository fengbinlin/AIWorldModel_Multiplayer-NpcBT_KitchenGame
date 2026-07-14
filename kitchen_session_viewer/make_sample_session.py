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


def main() -> None:
    root = Path(__file__).resolve().parent.parent / "KitchenTrainingRecordings" / "session_sample_preview"
    root.mkdir(parents=True, exist_ok=True)

    fps = 10.0
    n_frames = 30
    chefs_meta = [
        {"id": 1, "name": "Chef_Red", "color": (180, 60, 60)},
        {"id": 2, "name": "Chef_Blue", "color": (60, 90, 180)},
        {"id": 3, "name": "Chef_Green", "color": (60, 150, 80)},
        {"id": 4, "name": "Chef_Yellow", "color": (180, 160, 50)},
    ]

    manifest = {
        "sessionId": "session_sample_preview",
        "sceneName": "NPC",
        "unityTimeStart": 0.0,
        "frameWidth": 128,
        "frameHeight": 128,
        "captureFps": fps,
        "chefCount": len(chefs_meta),
    }
    (root / "manifest.json").write_text(json.dumps(manifest, indent=2), encoding="utf-8")

    frames_path = root / "frames.jsonl"
    with frames_path.open("w", encoding="utf-8") as fh:
        for i in range(n_frames):
            t = i / fps
            angle = t * math.pi
            global_rel = f"global/frame_{i:06d}.png"
            _make_image(root / global_rel, (45, 90, 70), f"G {i}")

            chef_frames = []
            for ci, chef in enumerate(chefs_meta):
                rel = f"agent_{chef['id']}/fp_{i:06d}.png"
                _make_image(root / rel, chef["color"], f"{chef['name'][:1]}{i}")
                moving = (i // 3 + ci) % 4
                keys = {
                    "keyW": moving == 0,
                    "keyA": moving == 1,
                    "keyS": moving == 2,
                    "keyD": moving == 3,
                    "keyE": i % 15 == 0,
                }
                # Local-space move axes (relative to yaw) + mouse look deltas
                local_fwd = math.cos(angle + ci * 0.4)
                local_strafe = math.sin(angle + ci * 0.4)
                yaw = (t * 90 + ci * 40) % 360
                pitch = 10.0 * math.sin(angle + ci)
                mouse_x = 9.0 if i > 0 else 0.0  # ~const yaw rate at 10fps
                mouse_y = pitch - (10.0 * math.sin((i - 1) / fps * math.pi + ci) if i > 0 else pitch)
                substates = ["idle", "moving", "interacting", "working", "waiting"]
                chef_frames.append(
                    {
                        "agentId": chef["id"],
                        "chefName": chef["name"],
                        "fpImage": rel,
                        **keys,
                        "moveX": local_strafe,
                        "moveZ": local_fwd,
                        "mouseX": mouse_x,
                        "mouseY": mouse_y,
                        "posX": 5.0 + ci * 2 + math.sin(angle),
                        "posY": 0.0,
                        "posZ": 3.0 + math.cos(angle),
                        "rotX": pitch,
                        "rotY": yaw,
                        "captureTime": t,
                        "substate": substates[(i + ci) % len(substates)],
                        "taskType": ["FETCH", "PROCESS", "SERVE"][(i // 5 + ci) % 3],
                        "taskLabel": f"demo step {i // 5}",
                        "heldItem": "" if i % 7 else "Tomato",
                    }
                )

            frame = {
                "frame": i,
                "time": t,
                "globalImage": global_rel,
                "chefs": chef_frames,
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
