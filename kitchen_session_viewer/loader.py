"""Load KitchenSessionRecorder offline sessions (manifest + frames.jsonl + PNGs)."""

from __future__ import annotations

import json
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any


@dataclass
class SessionMeta:
    session_id: str
    game_name: str
    frame_width: int
    frame_height: int
    capture_fps: float
    player_count: int
    total_frames: int
    task_description: str
    path: Path
    raw_manifest: dict[str, Any] = field(default_factory=dict)


@dataclass
class SessionData:
    meta: SessionMeta
    frames: list[dict[str, Any]] = field(default_factory=list)

    @property
    def frame_count(self) -> int:
        return len(self.frames)

    def frame_at(self, index: int) -> dict[str, Any]:
        if not self.frames:
            raise IndexError("Session has no frames")
        index = max(0, min(index, len(self.frames) - 1))
        return self.frames[index]

    def resolve_image(self, relative: str | None) -> Path | None:
        if not relative:
            return None
        path = (self.meta.path / relative).resolve()
        return path if path.is_file() else None


def frame_players(frame: dict[str, Any]) -> list[dict[str, Any]]:
    return list(frame.get("players") or [])


def player_id(player: dict[str, Any]) -> Any:
    return player.get("playerId", "?")


def player_name(player: dict[str, Any]) -> str:
    name = player.get("playerName")
    if name:
        return str(name)
    return f"player_{player_id(player)}"


def discover_sessions(root: Path) -> list[Path]:
    """Return session directories under root that contain frames.jsonl."""
    if not root.is_dir():
        return []
    sessions: list[Path] = []
    for child in sorted(root.iterdir()):
        if child.is_dir() and (child / "frames.jsonl").is_file():
            sessions.append(child)
    if (root / "frames.jsonl").is_file() and root not in sessions:
        sessions.insert(0, root)
    return sessions


def load_session(session_dir: Path) -> SessionData:
    session_dir = session_dir.resolve()
    frames_path = session_dir / "frames.jsonl"
    if not frames_path.is_file():
        raise FileNotFoundError(f"Missing frames.jsonl in {session_dir}")

    manifest_path = session_dir / "manifest.json"
    raw_manifest: dict[str, Any] = {}
    if manifest_path.is_file():
        raw_manifest = json.loads(manifest_path.read_text(encoding="utf-8"))

    frames: list[dict[str, Any]] = []
    with frames_path.open("r", encoding="utf-8") as fh:
        for line_no, line in enumerate(fh, start=1):
            line = line.strip()
            if not line:
                continue
            try:
                frames.append(json.loads(line))
            except json.JSONDecodeError as exc:
                raise ValueError(f"Invalid JSON on line {line_no} of frames.jsonl") from exc

    player_count = int(raw_manifest.get("playerCount", 0) or 0)
    if player_count <= 0 and frames:
        player_count = len(frame_players(frames[0]))

    total_frames = int(raw_manifest.get("totalFrames", 0) or 0)
    if total_frames <= 0:
        total_frames = len(frames)

    fps = float(raw_manifest.get("captureFps", 0) or 0)
    if fps <= 0 and len(frames) >= 2:
        t0 = float(frames[0].get("time", 0) or 0)
        t1 = float(frames[1].get("time", 0) or 0)
        dt = t1 - t0
        fps = (1.0 / dt) if dt > 1e-6 else 60.0
    if fps <= 0:
        fps = 60.0

    meta = SessionMeta(
        session_id=str(raw_manifest.get("sessionId") or session_dir.name),
        game_name=str(raw_manifest.get("gameName") or "Kitchen Chaos"),
        frame_width=int(raw_manifest.get("frameWidth", 256) or 256),
        frame_height=int(raw_manifest.get("frameHeight", 256) or 256),
        capture_fps=fps,
        player_count=player_count,
        total_frames=total_frames,
        task_description=str(raw_manifest.get("task_description") or ""),
        path=session_dir,
        raw_manifest=raw_manifest,
    )
    return SessionData(meta=meta, frames=frames)


def default_recordings_root() -> Path:
    """Unity writes recordings next to the project root (Application.dataPath/..)."""
    return Path(__file__).resolve().parent.parent / "KitchenTrainingRecordings"
