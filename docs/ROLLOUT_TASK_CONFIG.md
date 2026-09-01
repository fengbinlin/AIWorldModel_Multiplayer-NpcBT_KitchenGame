# Rollout task configuration

Linux builds can load one rollout task from JSON:

```bash
xvfb-run -a ./KitchenGame.x86_64 \
  -batchmode \
  -logFile /data/kitchen-rollouts/logs/worker-0.log \
  --task /data/kitchen-tasks/task-0000.json
```

Create a clean JSON file containing every configurable field and its default value:

```bash
./KitchenGame.x86_64 -batchmode -nographics \
  --Getparam /data/kitchen-tasks/defaults.json
```

`--Getparam=path.json` is also accepted. Calling `--Getparam` without a path
prints the managed JSON to stdout, but Unity itself can emit native startup text
on stdout first; use the path form when the result must be directly parseable.

Build the Linux x86-64 player from macOS or Linux with the configured Unity editor:

```bash
UNITY=/path/to/Unity
"$UNITY" -batchmode -nographics -quit \
  -projectPath /path/to/AIWorldModel_Multiplayer-NpcBT_KitchenGame \
  -executeMethod Kitchen.EditorTools.KitchenLinuxBuilder.Build \
  --buildPath /path/to/Builds/Linux/KitchenGame.x86_64
```

Use a full source checkout when building. If Git sparse checkout is enabled, it
must include both shader packages below; omitting them produces a successful-looking
player whose materials render magenta:

```bash
git sparse-checkout add \
  'Assets/lilToon' \
  'Assets/ImportedResources/JMO Assets/Toony Colors Pro' \
  'Assets/Plugins/TextMesh Pro/Shaders'
```

The Linux build entry point validates the shader GUIDs and now fails early with a
clear error when either package is absent.

Parameter names are emitted with the same category structure used by `--task`;
after output, the process exits with code 0. If both commands are present,
`--Getparam` takes precedence and no scene is started.

Do not pass `-nographics` while image recording is enabled: the recorder renders
the global camera and each agent camera into render textures. Use a working Vulkan
or OpenGL context, with Xvfb when the node has no display server.

Relative `recording.outputDirectory` values are resolved relative to the task JSON.
Each session name contains the task id, worker id, and millisecond timestamp. The
original task JSON is copied into the session directory and `_SUCCESS` is written
after the recorder flushes and the finite episode reaches GameOver.

`Configs/npc_pgc.scene-defaults.json` is a snapshot of the current scene values.
It intentionally retains unlimited episode duration (`0`) and the expensive
60 FPS, 1920x1080, five-camera recording setup. `Configs/rollout.example.json`
is the safer finite, low-resolution starting point for cluster runs.

## Categories

- `run`: task identity, worker identity, scene, deterministic seed, process timing,
  Unity Services policy, and exit behavior.
- `episode`: duration and automatic ready/start behavior.
- `world`: recipe pool and procedural kitchen layout.
- `agents`: agent count, scheduler/navigation behavior, visual timing, wandering,
  avoidance, and display colors.
- `orders`: order arrival timing and active queue limit.
- `recording`: capture rate, resolution, output location, queue pressure, and camera views.
- `network`: loopback transport endpoint and NGO tick rate. Give concurrent processes
  on one host different ports.
- `appearance`: global skin override and character animation.
- `logging`: AI diagnostic enablement and an isolated per-worker log location. An
  empty `outputDirectory` places logs below the recording output directory.

`recipeNames` accepts recipe asset names or their `recipeName` values. An empty list
keeps the scene's 17-recipe candidate pool. Available asset names are:

```text
BeefBurger, BeefSteak, CabbageOrder, CabbageSalad, CabbageShake,
ChickenBurger, ChickenSteak, FishSlices, FishSushi, PizzaBaked,
ShrimpSlices, ShrimpSushi, TomatoCabbageSalad, TomatoCabbageShake,
TomatoOrder, TomatoSalad, TomatoShake
```

The JSON format is strict about value ranges but ignores unknown fields because it
uses Unity `JsonUtility`. JSON comments are not supported.

One process executes one episode. Use a unique `workerId` and network `port` for
each concurrent process. A duration of `0` means unlimited time and therefore does
not reach GameOver or trigger automatic process exit. `agents.radius` reproduces
the Inspector value, but the current controller clamps its effective pathfinding
and RVO radius to the range 0.25–0.55.
