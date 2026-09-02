#!/usr/bin/env python3
"""Generate the reproducible 16-worker, 10-simulation-hour rollout subset."""

import json
from pathlib import Path


BATCH_ID = "kitchen-subset-10h-v3-20260902"
OUTPUT_DIR = Path(__file__).resolve().parents[1] / "Configs" / BATCH_ID
REMOTE_ROOT = f"/data/mayanwen/kitchen-rollouts/{BATCH_ID}"
EPISODE_SECONDS = 2250.0


SPECS = [
    # node, gpu, difficulty, recipes, agents, map(w,h), order interval/max, seed
    ("G154", 0, "easy", ["TomatoOrder", "CabbageOrder"], 2, (8, 8), (14.0, 2), 54100),
    ("G154", 1, "easy", ["FishSlices", "ShrimpSlices"], 2, (9, 8), (13.0, 2), 54101),
    ("G154", 2, "medium", ["BeefSteak", "ChickenSteak", "TomatoSalad"], 3, (12, 9), (9.0, 4), 54102),
    ("G154", 3, "medium", ["TomatoShake", "CabbageShake", "FishSushi"], 3, (12, 9), (9.0, 4), 54103),
    ("G154", 4, "medium", ["ShrimpSushi", "BeefBurger", "ChickenBurger"], 3, (13, 10), (8.0, 4), 54104),
    ("G154", 5, "medium", ["TomatoCabbageSalad", "TomatoCabbageShake", "PizzaBaked"], 3, (13, 10), (8.0, 4), 54105),
    ("G154", 6, "hard", ["BeefBurger", "ChickenBurger", "FishSushi", "ShrimpSushi", "PizzaBaked"], 4, (16, 10), (5.0, 6), 54106),
    ("G154", 7, "hard", ["TomatoCabbageSalad", "TomatoCabbageShake", "BeefBurger", "ChickenBurger", "PizzaBaked"], 4, (18, 12), (5.0, 6), 54107),
    ("G155", 0, "easy", ["TomatoOrder", "FishSlices"], 2, (8, 8), (14.0, 2), 55100),
    ("G155", 1, "easy", ["CabbageOrder", "ShrimpSlices"], 2, (9, 8), (13.0, 2), 55101),
    ("G155", 2, "medium", ["BeefSteak", "ChickenSteak", "CabbageSalad"], 3, (12, 9), (9.0, 4), 55102),
    ("G155", 3, "medium", ["TomatoShake", "CabbageShake", "ShrimpSushi"], 3, (12, 9), (9.0, 4), 55103),
    ("G155", 4, "medium", ["FishSushi", "BeefBurger", "TomatoCabbageSalad"], 3, (13, 10), (8.0, 4), 55104),
    ("G155", 5, "medium", ["ChickenBurger", "PizzaBaked", "TomatoCabbageShake"], 3, (13, 10), (8.0, 4), 55105),
    ("G155", 6, "hard", ["BeefBurger", "ChickenBurger", "TomatoCabbageSalad", "FishSushi", "PizzaBaked"], 4, (16, 10), (5.0, 6), 55106),
    ("G155", 7, "hard", ["BeefBurger", "ChickenBurger", "TomatoCabbageShake", "ShrimpSushi", "PizzaBaked"], 4, (18, 12), (5.0, 6), 55107),
]


def make_task(node, gpu, difficulty, recipes, agents, dimensions, order, seed):
    worker_id = f"{node.lower()}-gpu{gpu}"
    worker_root = f"{REMOTE_ROOT}/{node.lower()}/gpu_{gpu}"
    width, height = dimensions
    order_interval, max_active = order
    return {
        "version": 1,
        "run": {
            "taskId": f"{BATCH_ID}-{difficulty}-{worker_id}",
            "workerId": worker_id,
            "scene": "NPC_PGC",
            "seed": seed,
            "timeScale": 2.0,
            "targetFrameRate": -1,
            "quitOnGameOver": True,
            "initializeUnityServices": False,
        },
        "episode": {
            "durationSeconds": EPISODE_SECONDS,
            "readyCountdownSeconds": 0,
            "maxPlayers": agents,
            "autoReady": True,
            "autoReadyDelaySeconds": 0.0,
        },
        "world": {
            "useRandomRecipes": False,
            "randomRecipeCount": len(recipes),
            "recipeNames": recipes,
            "width": width,
            "height": height,
            "dilateKernel": 0,
            "pathRandomness": 1.1 if difficulty == "easy" else 1.4 if difficulty == "medium" else 1.8,
            "extraEdges": 0 if difficulty == "easy" else 1 if difficulty == "medium" else 2,
            "cellSize": 1.5,
            "layoutSeed": seed,
            "randomizeLayoutSeed": False,
            "destroyExistingCounters": True,
            "networkSpawnCounters": True,
        },
        "agents": {
            "enabled": True,
            "count": agents,
            "scheduleIntervalSeconds": 0.25 if difficulty == "easy" else 0.2 if difficulty == "medium" else 0.15,
            "moveSpeed": 4.0 if difficulty == "easy" else 4.2 if difficulty == "medium" else 4.5,
            "interactionRange": 2.5,
            "arrivalThreshold": 0.4,
            "stuckTimeoutSeconds": 8.0,
            "radius": 0.55,
            "rvoBasePriority": 0.5,
            "approachOffset": 1.2,
            "colors": ["#ff453a", "#0a84ff", "#30d158", "#ffd60a"][:agents],
            "interactAnimationHoldSeconds": 1.35,
            "postInteractAnimationHoldSeconds": 0.85,
            "minimumWorkAnimationSeconds": 2.8,
            "minimumWaitAnimationSeconds": 1.6,
            "enableWander": True,
            "idleWanderPointCount": 12,
            "wanderRadius": 5.0,
            "wanderIntervalSeconds": 3.0,
            "wanderNavmeshTolerance": 0.5,
            "detourRadius": 2.5,
            "detourAngleStep": 60.0,
        },
        "orders": {
            "spawnIntervalSeconds": order_interval,
            "spawnJitterSeconds": order_interval * 0.2,
            "maxActive": max_active,
        },
        "recording": {
            "enabled": True,
            "autoStart": True,
            "captureFps": 10.0,
            "width": 320,
            "height": 180,
            "outputDirectory": f"{worker_root}/recordings",
            "maxPendingWrites": 16,
            "captureGlobalCamera": True,
            "captureAgentCameras": True,
        },
        "network": {
            "address": "127.0.0.1",
            "listenAddress": "0.0.0.0",
            "port": 19000 + (0 if node == "G154" else 100) + gpu,
            "tickRate": 30,
        },
        "appearance": {
            "overrideGlobalSkin": False,
            "globalSkinId": 0,
            "enableCharacterAnimation": True,
        },
        "logging": {
            "enabled": True,
            "outputDirectory": f"{worker_root}/logs",
            "fileName": "ai_debug.log",
        },
    }


def main():
    OUTPUT_DIR.mkdir(parents=True, exist_ok=True)
    workers = []
    for spec in SPECS:
        node, gpu, difficulty, recipes, agents, dimensions, order, seed = spec
        task = make_task(*spec)
        filename = f"{node.lower()}-gpu{gpu}-{difficulty}.json"
        (OUTPUT_DIR / filename).write_text(
            json.dumps(task, indent=2, ensure_ascii=False) + "\n", encoding="utf-8"
        )
        workers.append({
            "node": node,
            "gpu": gpu,
            "difficulty": difficulty,
            "taskFile": filename,
            "taskId": task["run"]["taskId"],
            "seed": seed,
            "episodeSeconds": EPISODE_SECONDS,
            "timeScale": 2.0,
            "agents": agents,
            "map": {"width": dimensions[0], "height": dimensions[1]},
            "recipes": recipes,
            "orderIntervalSeconds": order[0],
            "maxActiveOrders": order[1],
            "outputDirectory": task["recording"]["outputDirectory"],
        })

    manifest = {
        "batchId": BATCH_ID,
        "schemaVersion": 1,
        "workerCount": len(workers),
        "totalSimulationSeconds": sum(w["episodeSeconds"] for w in workers),
        "totalSimulationHours": sum(w["episodeSeconds"] for w in workers) / 3600.0,
        "timeScale": 2.0,
        "captureFps": 10.0,
        "resolution": {"width": 320, "height": 180},
        "captureGlobalCamera": True,
        "captureAgentCameras": True,
        "workers": workers,
    }
    (OUTPUT_DIR / "task_set_manifest.json").write_text(
        json.dumps(manifest, indent=2, ensure_ascii=False) + "\n", encoding="utf-8"
    )


if __name__ == "__main__":
    main()
