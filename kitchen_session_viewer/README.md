# Kitchen Session Viewer

离线回放 `KitchenSessionRecorder` 采集的多视角训练数据：全局画面、各 AI 第一人称、WASD/E 输入、任务/子状态与世界快照。

## 环境

- Python 3.10+
- Windows / macOS / Linux（桌面 GUI，依赖 Tkinter + Pillow）

```bash
cd kitchen_session_viewer
python -m pip install -r requirements.txt
```

## 启动

```bash
# 在工程根目录：自动打开 KitchenTrainingRecordings 下最新 session
python -m kitchen_session_viewer

# 或进入本目录后指定 session
cd kitchen_session_viewer
python app.py "D:/path/to/KitchenTrainingRecordings/session_yyyyMMdd_HHmmss"
```

也可在界面里点 **Open Session…** / **Open Recordings Folder…**。

## 操作

| 键/控件 | 作用 |
|--------|------|
| ▶ Play / ⏸ Pause / Space | 按采集帧率播放或暂停 |
| 进度条 | 拖动到任意帧 |
| Prev / Next、← / → | 逐帧 |
| 左栏 Global + World State | 全局图与订单/任务/设施/物品 |
| 右栏各 Chef 卡片（固定 2 列，通常 2×2） | FP 图、WASD/E、本地 move、mouse XY、substate、task、持物、位姿 |

### 输入语义（与录制端一致）

- `moveX` / `moveZ`：相对 AI 视角 yaw 的本地平移（右/前），对应 A/D、W/S
- `mouseX` / `mouseY`：该帧视角旋转增量（度），yaw / pitch
- 旧 session 若无 `mouseX/Y`，播放器显示为 0

## 数据路径

Unity 录制输出（相对工程根目录）：

```
KitchenTrainingRecordings/session_*/
  manifest.json
  frames.jsonl
  global/frame_XXXXXX.png
  agent_{id}/fp_XXXXXX.png
```

## 无真实数据时的样例

```bash
python make_sample_session.py
python app.py ../KitchenTrainingRecordings/session_sample_preview
```
