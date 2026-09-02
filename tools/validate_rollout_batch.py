#!/usr/bin/env python3
"""Validate completed Kitchen rollout sessions and emit a node summary."""

import argparse
import json
import os
from datetime import datetime, timezone
from pathlib import Path


def count_pngs(directory: Path) -> int:
    return sum(1 for entry in os.scandir(directory) if entry.is_file() and entry.name.endswith(".png"))


def directory_bytes(directory: Path) -> int:
    total = 0
    for root, _, files in os.walk(directory):
        for name in files:
            total += os.path.getsize(os.path.join(root, name))
    return total


def validate_session(task_path: Path) -> dict:
    task = json.loads(task_path.read_text(encoding="utf-8"))
    recordings = Path(task["recording"]["outputDirectory"])
    sessions = sorted(path for path in recordings.glob("session_*") if path.is_dir())
    if len(sessions) != 1:
        raise ValueError(f"{task_path.name}: expected one session, found {len(sessions)}")

    session = sessions[0]
    if not (session / "_SUCCESS").is_file():
        raise ValueError(f"{session}: missing _SUCCESS")
    manifest = json.loads((session / "manifest.json").read_text(encoding="utf-8"))
    expected_frames = round(
        task["episode"]["durationSeconds"]
        * task["recording"]["captureFps"]
        / task["run"]["timeScale"]
    )
    if manifest["schemaVersion"] != 3 or manifest["status"] != "complete":
        raise ValueError(f"{session}: incomplete or unexpected schema")
    if manifest["totalFrames"] != expected_frames:
        raise ValueError(f"{session}: expected {expected_frames} frames, got {manifest['totalFrames']}")

    map_actors = manifest.get("map_actors") or []
    invalid_map_aabbs = sum(1 for actor in map_actors if not (actor.get("aabb") or {}).get("valid"))
    if not map_actors or invalid_map_aabbs:
        raise ValueError(f"{session}: invalid map actor AABBs")

    frame_count = 0
    interaction_count = 0
    actor_samples = 0
    invalid_actor_aabbs = 0
    first_time = None
    last_time = None
    with (session / manifest["framesFile"]).open(encoding="utf-8") as stream:
        for line in stream:
            frame = json.loads(line)
            if frame.get("frame") != frame_count:
                raise ValueError(f"{session}: non-contiguous frame index at {frame_count}")
            timestamp = frame.get("time")
            first_time = timestamp if first_time is None else first_time
            last_time = timestamp
            interaction_count += len(frame.get("interactions") or [])
            actors = frame.get("actors") or []
            actor_samples += len(actors)
            invalid_actor_aabbs += sum(
                1 for actor in actors if not (actor.get("aabb") or {}).get("valid")
            )
            frame_count += 1
    if frame_count != expected_frames or invalid_actor_aabbs:
        raise ValueError(f"{session}: invalid JSONL frames or actor AABBs")

    camera_frames = {}
    if task["recording"]["captureGlobalCamera"]:
        camera_frames["global"] = count_pngs(session / "global")
    if task["recording"]["captureAgentCameras"]:
        for directory in sorted(session.glob("agent_*")):
            camera_frames[directory.name] = count_pngs(directory)
    expected_camera_count = 1 + manifest["playerCount"]
    if len(camera_frames) != expected_camera_count:
        raise ValueError(f"{session}: expected {expected_camera_count} camera streams")
    if any(count != expected_frames for count in camera_frames.values()):
        raise ValueError(f"{session}: incomplete camera stream")

    return {
        "taskId": manifest["taskId"],
        "workerId": manifest["workerId"],
        "sessionDirectory": str(session),
        "schemaVersion": manifest["schemaVersion"],
        "status": manifest["status"],
        "simulationSeconds": task["episode"]["durationSeconds"],
        "timeScale": task["run"]["timeScale"],
        "stateFrames": frame_count,
        "firstFrameTime": first_time,
        "lastFrameTime": last_time,
        "cameraStreams": camera_frames,
        "imageFrames": sum(camera_frames.values()),
        "mapActors": len(map_actors),
        "actorSamples": actor_samples,
        "interactions": interaction_count,
        "bytes": directory_bytes(session),
    }


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("batch_root", type=Path)
    parser.add_argument("node", choices=("g154", "g155"))
    parser.add_argument("--write-summary", action="store_true")
    args = parser.parse_args()

    task_paths = sorted((args.batch_root / "tasks").glob(f"{args.node}-gpu*.json"))
    if len(task_paths) != 8:
        raise ValueError(f"expected eight {args.node} tasks, found {len(task_paths)}")
    sessions = [validate_session(path) for path in task_paths]
    summary = {
        "batchId": args.batch_root.name,
        "node": args.node.upper(),
        "validatedUtc": datetime.now(timezone.utc).isoformat(),
        "status": "complete",
        "sessionCount": len(sessions),
        "simulationSeconds": sum(item["simulationSeconds"] for item in sessions),
        "stateFrames": sum(item["stateFrames"] for item in sessions),
        "imageFrames": sum(item["imageFrames"] for item in sessions),
        "interactions": sum(item["interactions"] for item in sessions),
        "bytes": sum(item["bytes"] for item in sessions),
        "sessions": sessions,
    }
    rendered = json.dumps(summary, ensure_ascii=False, indent=2) + "\n"
    if args.write_summary:
        output = args.batch_root / args.node / "node_result_manifest.json"
        output.write_text(rendered, encoding="utf-8")
    print(rendered, end="")


if __name__ == "__main__":
    main()
