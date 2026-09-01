# MulPlayer
 `a demo for learning mulplayer game`

一个学习多人游戏的练习项目,使用`NetCode`进行网络同步

参考`codemonkey`的教程，按照自己的代码风格做的实现

## NPC rollout task configuration

The `NPC_PGC` scene parameters can be supplied to a standalone player with:

```bash
./KitchenGame.x86_64 --task /absolute/path/to/task.json
```

Write the complete default configuration with
`./KitchenGame.x86_64 --Getparam /path/to/defaults.json`.

See [`docs/ROLLOUT_TASK_CONFIG.md`](docs/ROLLOUT_TASK_CONFIG.md) and the JSON
files under [`Configs/`](Configs/).
