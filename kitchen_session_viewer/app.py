"""Offline multi-view previewer for KitchenSessionRecorder data."""

from __future__ import annotations

import argparse
import tkinter as tk
from pathlib import Path
from tkinter import filedialog, messagebox, ttk
from typing import Any

from PIL import Image, ImageDraw, ImageFont, ImageTk

try:
    from .loader import SessionData, default_recordings_root, discover_sessions, load_session
except ImportError:  # running as `python app.py` from this folder
    from loader import SessionData, default_recordings_root, discover_sessions, load_session


# Dark theme colors (readable, not purple-glow AI default)
BG = "#1e1f22"
PANEL = "#2b2d31"
CARD = "#313338"
TEXT = "#e3e5e8"
MUTED = "#949ba4"
ACCENT = "#3ba55d"
KEY_ON = "#faa61a"
KEY_OFF = "#4e5058"
BORDER = "#1a1b1e"
CHEF_GRID_COLUMNS = 2


class KeyboardWidget(ttk.Frame):
    """Compact WASD + E indicator for one chef."""

    def __init__(self, master: tk.Misc, **kwargs: Any) -> None:
        super().__init__(master, **kwargs)
        self.configure(style="Card.TFrame")
        self._labels: dict[str, tk.Label] = {}
        grid = ttk.Frame(self, style="Card.TFrame")
        grid.pack(padx=4, pady=2)

        positions = {
            "W": (0, 1),
            "A": (1, 0),
            "S": (1, 1),
            "D": (1, 2),
            "E": (0, 3),
        }
        for key, (row, col) in positions.items():
            lbl = tk.Label(
                grid,
                text=key,
                width=3,
                height=1,
                font=("Segoe UI", 10, "bold"),
                bg=KEY_OFF,
                fg=TEXT,
                relief="flat",
                padx=2,
                pady=1,
            )
            lbl.grid(row=row, column=col, padx=2, pady=2)
            self._labels[key] = lbl

        # spacer so E sits to the right of WASD
        ttk.Label(grid, text="", style="Card.TLabel", width=1).grid(row=0, column=2)

    def set_keys(self, chef: dict[str, Any]) -> None:
        mapping = {
            "W": bool(chef.get("keyW")),
            "A": bool(chef.get("keyA")),
            "S": bool(chef.get("keyS")),
            "D": bool(chef.get("keyD")),
            "E": bool(chef.get("keyE")),
        }
        for key, on in mapping.items():
            lbl = self._labels[key]
            lbl.configure(bg=KEY_ON if on else KEY_OFF, fg="#1a1a1a" if on else TEXT)


class MousePadWidget(ttk.Frame):
    """Crosshair pad showing per-frame mouse look delta (yaw → X, pitch → Y)."""

    def __init__(self, master: tk.Misc, size: int = 72, max_degrees: float = 20.0, **kwargs: Any) -> None:
        super().__init__(master, **kwargs)
        self.configure(style="Card.TFrame")
        self._size = size
        self._max_degrees = max(1.0, max_degrees)
        self._cx = size // 2
        self._cy = size // 2

        ttk.Label(self, text="Mouse", style="Muted.TLabel").pack(anchor="w")
        self.canvas = tk.Canvas(
            self,
            width=size,
            height=size,
            bg=BORDER,
            highlightthickness=1,
            highlightbackground=KEY_OFF,
        )
        self.canvas.pack(padx=2, pady=2)
        self._draw_base()

    def _draw_base(self) -> None:
        c = self.canvas
        c.delete("all")
        s = self._size
        cx, cy = self._cx, self._cy
        # outer ring + crosshair
        pad = 4
        c.create_oval(pad, pad, s - pad, s - pad, outline=KEY_OFF, width=1)
        c.create_line(cx, pad + 2, cx, s - pad - 2, fill=KEY_OFF, width=1)
        c.create_line(pad + 2, cy, s - pad - 2, cy, fill=KEY_OFF, width=1)
        c.create_oval(cx - 2, cy - 2, cx + 2, cy + 2, fill=MUTED, outline="")
        # axis hints
        c.create_text(s - 8, cy - 8, text="X", fill=MUTED, font=("Segoe UI", 7))
        c.create_text(cx + 8, 10, text="Y", fill=MUTED, font=("Segoe UI", 7))

    def set_mouse(self, mouse_x: float, mouse_y: float) -> None:
        self._draw_base()
        c = self.canvas
        cx, cy = self._cx, self._cy

        # +mouseX = look right, +mouseY = look up → screen up is -y
        scale = (self._size * 0.5 - 10) / self._max_degrees
        dx = max(-self._max_degrees, min(self._max_degrees, mouse_x)) * scale
        dy = max(-self._max_degrees, min(self._max_degrees, mouse_y)) * scale
        tx = cx + dx
        ty = cy - dy

        active = abs(mouse_x) > 0.05 or abs(mouse_y) > 0.05
        color = KEY_ON if active else MUTED
        c.create_line(cx, cy, tx, ty, fill=color, width=2, arrow=tk.LAST, arrowshape=(7, 9, 3))
        r = 4 if active else 2
        c.create_oval(tx - r, ty - r, tx + r, ty + r, fill=color, outline="")

        # faint trail ring scaled by magnitude
        mag = (mouse_x * mouse_x + mouse_y * mouse_y) ** 0.5
        if mag > 0.05:
            rr = min(self._size * 0.42, mag * scale)
            c.create_oval(cx - rr, cy - rr, cx + rr, cy + rr, outline=ACCENT, width=1)


class ChefPanel(ttk.Frame):
    def __init__(self, master: tk.Misc, title: str, img_size: int = 200, **kwargs: Any) -> None:
        super().__init__(master, style="Card.TFrame", padding=6, **kwargs)
        self._img_size = img_size
        self._photo: ImageTk.PhotoImage | None = None

        self.title_var = tk.StringVar(value=title)
        ttk.Label(self, textvariable=self.title_var, style="Title.TLabel").pack(anchor="w")

        self.canvas = tk.Label(self, bg=BORDER, width=img_size, height=img_size)
        self.canvas.pack(pady=(4, 4))

        inputs = ttk.Frame(self, style="Card.TFrame")
        inputs.pack(anchor="w", fill="x")
        self.keys = KeyboardWidget(inputs)
        self.keys.pack(side="left", anchor="n")
        self.mouse_pad = MousePadWidget(inputs, size=72, max_degrees=20.0)
        self.mouse_pad.pack(side="left", anchor="n", padx=(10, 0))

        self.move_var = tk.StringVar(value="move(local): 0.00, 0.00")
        self.mouse_var = tk.StringVar(value="mouse: 0.0, 0.0")
        self.state_var = tk.StringVar(value="idle")
        self.task_var = tk.StringVar(value="task: —")
        self.held_var = tk.StringVar(value="held: —")
        self.pose_var = tk.StringVar(value="pos: —")

        for var in (
            self.move_var,
            self.mouse_var,
            self.state_var,
            self.task_var,
            self.held_var,
            self.pose_var,
        ):
            ttk.Label(self, textvariable=var, style="Muted.TLabel").pack(anchor="w")

    def update_chef(self, chef: dict[str, Any], image_path: Path | None) -> None:
        name = chef.get("chefName") or f"agent_{chef.get('agentId', '?')}"
        self.title_var.set(f"{name}  (id={chef.get('agentId', '?')})")
        self.keys.set_keys(chef)
        mx = float(chef.get("moveX", 0) or 0)
        mz = float(chef.get("moveZ", 0) or 0)
        self.move_var.set(f"move(local XZ): {mx:+.2f}, {mz:+.2f}")
        mouse_x = float(chef.get("mouseX", 0) or 0)
        mouse_y = float(chef.get("mouseY", 0) or 0)
        self.mouse_var.set(f"mouse XY°: {mouse_x:+.1f}, {mouse_y:+.1f}")
        self.mouse_pad.set_mouse(mouse_x, mouse_y)
        self.state_var.set(f"substate: {chef.get('substate') or '—'}")
        task_type = chef.get("taskType") or ""
        task_label = chef.get("taskLabel") or ""
        self.task_var.set(f"task: {task_type} {task_label}".strip() or "task: —")
        self.held_var.set(f"held: {chef.get('heldItem') or '—'}")
        self.pose_var.set(
            f"pos: ({float(chef.get('posX', 0) or 0):.1f}, "
            f"{float(chef.get('posY', 0) or 0):.1f}, "
            f"{float(chef.get('posZ', 0) or 0):.1f})  "
            f"yaw={float(chef.get('rotY', 0) or 0):.0f}° "
            f"pitch={float(chef.get('rotX', 0) or 0):.0f}°"
        )
        self._set_image(image_path)

    def _set_image(self, path: Path | None) -> None:
        img = _load_preview(path, self._img_size, placeholder="no fp")
        self._photo = ImageTk.PhotoImage(img)
        self.canvas.configure(image=self._photo, width=self._img_size, height=self._img_size)


class SessionViewerApp(tk.Tk):
    def __init__(self, session_path: Path | None = None) -> None:
        super().__init__()
        self.title("Kitchen Session Viewer")
        self.configure(bg=BG)
        self.geometry("1440x900")
        self.minsize(1100, 720)

        self._session: SessionData | None = None
        self._frame_index = 0
        self._playing = False
        self._play_job: str | None = None
        self._global_photo: ImageTk.PhotoImage | None = None
        self._chef_panels: list[ChefPanel] = []
        self._recordings_root = default_recordings_root()

        self._setup_styles()
        self._build_ui()

        if session_path is not None:
            self._open_session(session_path)
        else:
            sessions = discover_sessions(self._recordings_root)
            if sessions:
                self._open_session(sessions[-1])

    def _setup_styles(self) -> None:
        style = ttk.Style(self)
        try:
            style.theme_use("clam")
        except tk.TclError:
            pass
        style.configure(".", background=BG, foreground=TEXT, fieldbackground=PANEL)
        style.configure("TFrame", background=BG)
        style.configure("Card.TFrame", background=CARD)
        style.configure("Panel.TFrame", background=PANEL)
        style.configure("TLabel", background=BG, foreground=TEXT, font=("Segoe UI", 10))
        style.configure("Card.TLabel", background=CARD, foreground=TEXT, font=("Segoe UI", 10))
        style.configure("Title.TLabel", background=CARD, foreground=TEXT, font=("Segoe UI", 11, "bold"))
        style.configure("Muted.TLabel", background=CARD, foreground=MUTED, font=("Segoe UI", 9))
        style.configure("Header.TLabel", background=BG, foreground=TEXT, font=("Segoe UI", 12, "bold"))
        style.configure("TButton", font=("Segoe UI", 10))
        style.configure("Horizontal.TScale", background=BG)

    def _build_ui(self) -> None:
        top = ttk.Frame(self, style="TFrame", padding=(10, 8))
        top.pack(fill="x")

        ttk.Button(top, text="Open Session…", command=self._browse_session).pack(side="left")
        ttk.Button(top, text="Open Recordings Folder…", command=self._browse_root).pack(
            side="left", padx=(8, 0)
        )
        self.session_label = ttk.Label(top, text="No session loaded", style="Header.TLabel")
        self.session_label.pack(side="left", padx=(16, 0))

        body = ttk.Frame(self, style="TFrame", padding=(10, 0))
        body.pack(fill="both", expand=True)

        # Left: global view + world state
        left = ttk.Frame(body, style="Panel.TFrame", padding=8)
        left.pack(side="left", fill="both", expand=False)

        ttk.Label(left, text="Global View", style="Header.TLabel").pack(anchor="w")
        self.global_canvas = tk.Label(left, bg=BORDER, width=420, height=420)
        self.global_canvas.pack(pady=(6, 8))

        ttk.Label(left, text="World State", style="Header.TLabel").pack(anchor="w")
        self.world_text = tk.Text(
            left,
            width=52,
            height=18,
            bg=CARD,
            fg=TEXT,
            insertbackground=TEXT,
            relief="flat",
            font=("Consolas", 9),
            wrap="word",
        )
        self.world_text.pack(fill="both", expand=True, pady=(6, 0))
        self.world_text.configure(state="disabled")

        # Right: 2-column chef grid (typically 2x2 for 4 AIs), vertical scroll if more
        right_wrap = ttk.Frame(body, style="TFrame")
        right_wrap.pack(side="left", fill="both", expand=True, padx=(10, 0))

        ttk.Label(right_wrap, text="AI Chefs / Inputs (2 columns)", style="Header.TLabel").pack(
            anchor="w"
        )

        self.chef_canvas = tk.Canvas(right_wrap, bg=BG, highlightthickness=0)
        self.chef_scroll_y = ttk.Scrollbar(
            right_wrap, orient="vertical", command=self.chef_canvas.yview
        )
        self.chef_canvas.configure(yscrollcommand=self.chef_scroll_y.set)
        self.chef_scroll_y.pack(side="right", fill="y")
        self.chef_canvas.pack(side="left", fill="both", expand=True)

        self.chef_inner = ttk.Frame(self.chef_canvas, style="TFrame")
        self._chef_window = self.chef_canvas.create_window((0, 0), window=self.chef_inner, anchor="nw")
        self.chef_inner.bind("<Configure>", self._on_chef_inner_configure)
        self.chef_canvas.bind("<Configure>", self._on_chef_canvas_configure)
        self.chef_canvas.bind_all("<MouseWheel>", self._on_chef_mousewheel)

        # Bottom transport
        bottom = ttk.Frame(self, style="Panel.TFrame", padding=(10, 10))
        bottom.pack(fill="x", side="bottom")

        self.play_btn = ttk.Button(bottom, text="▶ Play", width=10, command=self._toggle_play)
        self.play_btn.pack(side="left")
        ttk.Button(bottom, text="⏮ Prev", width=8, command=self._prev_frame).pack(side="left", padx=4)
        ttk.Button(bottom, text="Next ⏭", width=8, command=self._next_frame).pack(side="left")

        self.frame_var = tk.IntVar(value=0)
        self.scale = ttk.Scale(
            bottom,
            from_=0,
            to=0,
            orient="horizontal",
            variable=self.frame_var,
            command=self._on_scrub,
        )
        self.scale.pack(side="left", fill="x", expand=True, padx=12)

        self.time_label = ttk.Label(bottom, text="frame 0 / 0   t=0.000s")
        self.time_label.pack(side="right")

        self.bind("<space>", lambda _e: self._toggle_play())
        self.bind("<Left>", lambda _e: self._prev_frame())
        self.bind("<Right>", lambda _e: self._next_frame())

    def _browse_root(self) -> None:
        path = filedialog.askdirectory(
            title="Select KitchenTrainingRecordings folder",
            initialdir=str(self._recordings_root if self._recordings_root.exists() else Path.cwd()),
        )
        if not path:
            return
        self._recordings_root = Path(path)
        sessions = discover_sessions(self._recordings_root)
        if not sessions:
            messagebox.showwarning("No sessions", f"No frames.jsonl found under:\n{path}")
            return
        self._open_session(sessions[-1])

    def _browse_session(self) -> None:
        initial = self._recordings_root if self._recordings_root.exists() else Path.cwd()
        path = filedialog.askdirectory(title="Select a session_* folder", initialdir=str(initial))
        if path:
            self._open_session(Path(path))

    def _open_session(self, path: Path) -> None:
        try:
            session = load_session(path)
        except Exception as exc:  # noqa: BLE001 - show to user
            messagebox.showerror("Load failed", str(exc))
            return

        self._stop_play()
        self._session = session
        self._frame_index = 0
        self.frame_var.set(0)
        max_idx = max(0, session.frame_count - 1)
        self.scale.configure(to=max_idx)
        self.session_label.configure(
            text=f"{session.meta.session_id}  |  {session.meta.scene_name}  |  "
            f"{session.frame_count} frames @ {session.meta.capture_fps:.0f}fps"
        )
        self._rebuild_chef_panels(session.meta.chef_count or 1)
        self._render_frame(0)

    def _on_chef_inner_configure(self, _event: tk.Event | None = None) -> None:
        self.chef_canvas.configure(scrollregion=self.chef_canvas.bbox("all"))

    def _on_chef_canvas_configure(self, event: tk.Event) -> None:
        self.chef_canvas.itemconfigure(self._chef_window, width=event.width)

    def _on_chef_mousewheel(self, event: tk.Event) -> None:
        if self.chef_canvas.winfo_containing(event.x_root, event.y_root) is None:
            return
        self.chef_canvas.yview_scroll(int(-event.delta / 120), "units")

    def _rebuild_chef_panels(self, count: int) -> None:
        for panel in self._chef_panels:
            panel.destroy()
        self._chef_panels.clear()
        for child in self.chef_inner.winfo_children():
            child.destroy()

        count = max(1, count)
        for col in range(CHEF_GRID_COLUMNS):
            self.chef_inner.columnconfigure(col, weight=1, uniform="chef")

        for i in range(count):
            row, col = divmod(i, CHEF_GRID_COLUMNS)
            panel = ChefPanel(self.chef_inner, title=f"Chef {i}", img_size=180)
            panel.grid(row=row, column=col, sticky="nsew", padx=4, pady=4)
            self._chef_panels.append(panel)

    def _on_scrub(self, _value: str | None = None) -> None:
        if self._session is None:
            return
        idx = int(float(self.frame_var.get()))
        if idx != self._frame_index:
            self._frame_index = idx
            self._render_frame(idx)

    def _toggle_play(self) -> None:
        if self._session is None or self._session.frame_count == 0:
            return
        if self._playing:
            self._stop_play()
        else:
            self._playing = True
            self.play_btn.configure(text="⏸ Pause")
            self._schedule_next()

    def _stop_play(self) -> None:
        self._playing = False
        self.play_btn.configure(text="▶ Play")
        if self._play_job is not None:
            self.after_cancel(self._play_job)
            self._play_job = None

    def _schedule_next(self) -> None:
        if not self._playing or self._session is None:
            return
        fps = max(1.0, self._session.meta.capture_fps)
        delay_ms = max(1, int(1000.0 / fps))
        self._play_job = self.after(delay_ms, self._play_tick)

    def _play_tick(self) -> None:
        if not self._playing or self._session is None:
            return
        nxt = self._frame_index + 1
        if nxt >= self._session.frame_count:
            self._stop_play()
            return
        self._frame_index = nxt
        self.frame_var.set(nxt)
        self._render_frame(nxt)
        self._schedule_next()

    def _prev_frame(self) -> None:
        if self._session is None:
            return
        self._stop_play()
        self._frame_index = max(0, self._frame_index - 1)
        self.frame_var.set(self._frame_index)
        self._render_frame(self._frame_index)

    def _next_frame(self) -> None:
        if self._session is None:
            return
        self._stop_play()
        self._frame_index = min(self._session.frame_count - 1, self._frame_index + 1)
        self.frame_var.set(self._frame_index)
        self._render_frame(self._frame_index)

    def _render_frame(self, index: int) -> None:
        session = self._session
        if session is None or session.frame_count == 0:
            return

        frame = session.frame_at(index)
        t = float(frame.get("time", 0) or 0)
        self.time_label.configure(
            text=f"frame {index} / {session.frame_count - 1}   t={t:.3f}s"
        )

        global_rel = frame.get("globalImage")
        global_path = session.resolve_image(global_rel)
        gimg = _load_preview(global_path, 420, placeholder="no global")
        self._global_photo = ImageTk.PhotoImage(gimg)
        self.global_canvas.configure(image=self._global_photo, width=420, height=420)

        chefs = list(frame.get("chefs") or [])
        if len(chefs) > len(self._chef_panels):
            self._rebuild_chef_panels(len(chefs))

        for i, panel in enumerate(self._chef_panels):
            if i >= len(chefs):
                panel.update_chef(
                    {
                        "chefName": f"(empty {i})",
                        "agentId": "-",
                        "keyW": False,
                        "keyA": False,
                        "keyS": False,
                        "keyD": False,
                        "keyE": False,
                        "moveX": 0,
                        "moveZ": 0,
                        "mouseX": 0,
                        "mouseY": 0,
                        "substate": "—",
                        "taskType": "",
                        "taskLabel": "",
                        "heldItem": "",
                        "posX": 0,
                        "posY": 0,
                        "posZ": 0,
                        "rotX": 0,
                        "rotY": 0,
                    },
                    None,
                )
                continue
            chef = chefs[i]
            img_path = session.resolve_image(chef.get("fpImage"))
            panel.update_chef(chef, img_path)

        self._fill_world_text(frame.get("world") or {})

    def _fill_world_text(self, world: dict[str, Any]) -> None:
        lines: list[str] = []

        orders = world.get("orders") or []
        lines.append(f"=== Orders ({len(orders)}) ===")
        for o in orders:
            ings = ", ".join(o.get("ingredients") or [])
            lines.append(f"  #{o.get('orderId')}: {o.get('recipeName')} [{ings}]")

        tasks = world.get("tasks") or []
        lines.append("")
        lines.append(f"=== Tasks ({len(tasks)}) ===")
        for t in tasks:
            lines.append(
                f"  [{t.get('status')}] {t.get('taskType')} {t.get('label')} "
                f"agent={t.get('assignedAgentId')} order={t.get('orderId')}"
            )

        facilities = world.get("facilities") or []
        lines.append("")
        lines.append(f"=== Facilities ({len(facilities)}) ===")
        for f in facilities:
            item = f.get("counterItem") or "-"
            lines.append(
                f"  {f.get('name')} ({f.get('facilityType')}): {f.get('state')} "
                f"item={item} res={f.get('reservedByAgent')} occ={f.get('occupiedByAgent')}"
            )

        items = world.get("items") or []
        lines.append("")
        lines.append(f"=== Items ({len(items)}) ===")
        for it in items[:40]:
            lines.append(
                f"  id={it.get('id')} {it.get('itemType')}/{it.get('stage')} "
                f"carry={it.get('carriedByAgent')} order={it.get('orderId')}"
            )
        if len(items) > 40:
            lines.append(f"  … {len(items) - 40} more")

        text = "\n".join(lines) if lines else "(empty world snapshot)"
        self.world_text.configure(state="normal")
        self.world_text.delete("1.0", "end")
        self.world_text.insert("1.0", text)
        self.world_text.configure(state="disabled")


def _load_preview(path: Path | None, size: int, placeholder: str = "missing") -> Image.Image:
    if path is not None and path.is_file():
        try:
            img = Image.open(path).convert("RGB")
            img.thumbnail((size, size), Image.Resampling.LANCZOS)
            canvas = Image.new("RGB", (size, size), (30, 31, 34))
            ox = (size - img.width) // 2
            oy = (size - img.height) // 2
            canvas.paste(img, (ox, oy))
            return canvas
        except OSError:
            pass
    return _placeholder(size, placeholder)


def _placeholder(size: int, text: str) -> Image.Image:
    img = Image.new("RGB", (size, size), (40, 42, 46))
    draw = ImageDraw.Draw(img)
    try:
        font = ImageFont.truetype("segoeui.ttf", 14)
    except OSError:
        font = ImageFont.load_default()
    draw.text((12, size // 2 - 8), text, fill=(150, 155, 160), font=font)
    return img


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Offline Kitchen AI session viewer")
    parser.add_argument(
        "session",
        nargs="?",
        type=Path,
        help="Path to a session_* folder (contains frames.jsonl)",
    )
    parser.add_argument(
        "--recordings-root",
        type=Path,
        default=None,
        help="Override KitchenTrainingRecordings root",
    )
    return parser.parse_args()


def main() -> None:
    args = parse_args()
    session_path = args.session
    if args.recordings_root is not None:
        # Patch default by loading from that root if no session given
        if session_path is None:
            sessions = discover_sessions(args.recordings_root)
            session_path = sessions[-1] if sessions else None

    app = SessionViewerApp(session_path=session_path)
    if args.recordings_root is not None:
        app._recordings_root = args.recordings_root.resolve()
    app.mainloop()


if __name__ == "__main__":
    main()
