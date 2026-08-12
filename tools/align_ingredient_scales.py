"""Align ingredient skin child scales to Bread × relative real-world proportions."""
import re
import struct
import zlib
from pathlib import Path

ROOT = Path(r"d:\DataAsset\UnityProject\MulPlayerLearningDemo-main\MulPlayerLearningDemo-main\Assets")
ING = ROOT / "Resources/So/Skin/SkinAsset/Ingredients"
BREAD = ING / "Skin_Bread_Visual.prefab"

# Keep in sync with IngredientVisualProportion.cs (Bread = 1).
RELATIVE = {
    "Bread": 1.00,
    "PizzaDough": 1.00,
    "PizzaUnbaked": 1.00,
    "PizzaBaked": 1.00,
    "BeefBurger": 1.00,
    "ChickenBurger": 1.00,
    "CabbageSlices": 0.92,
    "Nori": 0.92,
    "MeatPattyUncooked": 0.82,
    "MeatPattyCooked": 0.82,
    "MeatPattyBurned": 0.82,
    "ChickenRaw": 0.82,
    "ChickenCooked": 0.82,
    "ChickenBurned": 0.82,
    "CheeseSlices": 0.72,
    "TomatoSlices": 0.55,
    "FishSlices": 0.55,
    "ShrimpSlices": 0.42,
    "Tomato": 0.62,
    "Cabbage": 0.70,
    "CheeseBlock": 0.70,
    "Fish": 0.65,
    "Shrimp": 0.38,
    "RiceBall": 0.50,
    "FishSushi": 0.50,
    "ShrimpSushi": 0.50,
    "Cream": 0.32,
    "TomatoSalad": 0.72,
    "CabbageSalad": 0.72,
    "TomatoCabbageSalad": 0.72,
    "TomatoShake": 0.72,
    "CabbageShake": 0.72,
    "TomatoCabbageShake": 0.72,
    "Plate": 1.15,
}


def relative_for_prefab(name: str) -> float:
    stem = name.replace("Skin_", "").replace("_Visual.prefab", "").replace(".prefab", "")
    return RELATIVE.get(stem, 0.75)


def read_fbx_aabb_max(path: str):
    data = open(path, "rb").read()
    if not data.startswith(b"Kaydara FBX Binary"):
        return None
    key = b"Vertices"
    best = None
    start = 0
    while True:
        i = data.find(key, start)
        if i < 0:
            break
        start = i + 1
        for off in range(i + len(key), min(i + len(key) + 80, len(data) - 13)):
            t = data[off : off + 1]
            if t not in (b"d", b"f"):
                continue
            count = struct.unpack_from("<I", data, off + 1)[0]
            encoding = struct.unpack_from("<I", data, off + 5)[0]
            clen = struct.unpack_from("<I", data, off + 9)[0]
            if count < 9 or count > 5_000_000 or count % 3 != 0:
                continue
            if clen <= 0 or off + 13 + clen > len(data):
                continue
            payload = data[off + 13 : off + 13 + clen]
            if encoding == 1:
                try:
                    payload = zlib.decompress(payload)
                except Exception:
                    continue
            elif encoding != 0:
                continue
            fmt = "<" + ("d" if t == b"d" else "f") * count
            need = struct.calcsize(fmt)
            if len(payload) < need:
                continue
            vals = struct.unpack(fmt, payload[:need])
            xs, ys, zs = vals[0::3], vals[1::3], vals[2::3]
            # Horizontal footprint (XZ) — matches plate/hand visual size better than including height.
            m = max(max(xs) - min(xs), max(zs) - min(zs))
            if m > 0 and (best is None or m > best):
                best = m
            break
    return best


def read_unit_scale_factor(path: str) -> float:
    """FBX UnitScaleFactor: 1 ≈ cm, 100 ≈ m (Autodesk)."""
    data = open(path, "rb").read()
    i = data.find(b"UnitScaleFactor")
    if i < 0:
        return 1.0
    for off in range(i, min(i + 90, len(data) - 9)):
        if data[off : off + 1] == b"D":
            val = struct.unpack_from("<d", data, off + 1)[0]
            if 1e-6 < abs(val) < 1e6:
                return float(val)
    return 1.0


def unity_native_size(fbx_path: str) -> float | None:
    """Approximate Unity imported mesh max size in meters (useFileScale on)."""
    raw = read_fbx_aabb_max(fbx_path)
    if raw is None or raw <= 0:
        return None
    unit = read_unit_scale_factor(fbx_path)
    # Unity File Scale ≈ UnitScaleFactor * 0.01 when importing to meters
    return raw * unit * 0.01


def build_guid_map():
    guid_map = {}
    for meta in ROOT.rglob("*.meta"):
        try:
            head = meta.read_text(encoding="utf-8", errors="ignore").splitlines()[:6]
        except Exception:
            continue
        for line in head:
            if line.startswith("guid: "):
                guid_map[line.split()[1].strip()] = meta.with_suffix("")
                break
    return guid_map


def resolve_mesh_path(guid: str, guid_map: dict) -> Path | None:
    p = guid_map.get(guid)
    if p is None:
        return None
    if p.suffix.lower() == ".fbx":
        return p if p.exists() else None
    # Skin_PizzaBaked_Visual.fbx copy lives next to prefab
    return None


def fmt_scale(s: float) -> str:
    s = max(0.05, min(80.0, s))
    text = f"{s:.4f}".rstrip("0").rstrip(".")
    return text if "." in text else text + ".0"


def apply_scale_to_prefab(prefab: Path, new_scale: float):
    text = prefab.read_text(encoding="utf-8", errors="ignore")
    ns = fmt_scale(new_scale)
    text2 = re.sub(
        r"(propertyPath: m_LocalScale\.[xyz]\s*\r?\n\s*value: )([0-9.\-]+)(\r?\n)",
        lambda m: m.group(1) + ns + m.group(3),
        text,
    )

    def repl_direct(m):
        x, y, z = float(m.group(1)), float(m.group(2)), float(m.group(3))
        if abs(x - 1) < 1e-6 and abs(y - 1) < 1e-6 and abs(z - 1) < 1e-6:
            return m.group(0)
        return f"m_LocalScale: {{x: {ns}, y: {ns}, z: {ns}}}"

    text2 = re.sub(
        r"m_LocalScale: \{x: ([0-9.\-]+), y: ([0-9.\-]+), z: ([0-9.\-]+)\}",
        repl_direct,
        text2,
    )
    if text2 != text:
        prefab.write_text(text2, encoding="utf-8")
    return ns


def main():
    guid_map = build_guid_map()
    bread_text = BREAD.read_text(encoding="utf-8", errors="ignore")

    bread_world = []
    for inst in re.finditer(
        r"(--- !u!1001 &\d+[\s\S]*?m_SourcePrefab: \{fileID: 100100000, guid: ([a-f0-9]{32}))",
        bread_text,
    ):
        block, guid = inst.group(1), inst.group(2)
        sm = re.search(r"propertyPath: m_LocalScale\.x\s*\r?\n\s*value: ([0-9.\-]+)", block)
        if not sm:
            continue
        scale = float(sm.group(1))
        src = resolve_mesh_path(guid, guid_map) or guid_map.get(guid)
        if src is None or not src.exists() or src.suffix.lower() != ".fbx":
            print("bread skip", guid)
            continue
        native = unity_native_size(str(src))
        if native is None:
            print("bread aabb fail", src)
            continue
        world = native * scale
        bread_world.append(world)
        print(f"BREAD {src.name}: unityNative={native:.4f} scale={scale} world={world:.4f}")

    if not bread_world:
        raise SystemExit("No bread measurements")
    target = max(bread_world)
    print(f"TARGET world max (meters-ish) = {target:.4f}")

    for prefab in sorted(ING.glob("*.prefab")):
        if prefab.name == "Skin_Bread_Visual.prefab":
            continue
        text = prefab.read_text(encoding="utf-8", errors="ignore")

        candidates = []
        for inst in re.finditer(
            r"(--- !u!1001 &\d+[\s\S]*?m_SourcePrefab: \{fileID: 100100000, guid: ([a-f0-9]{32}))",
            text,
        ):
            block, guid = inst.group(1), inst.group(2)
            sm = re.search(r"propertyPath: m_LocalScale\.x\s*\r?\n\s*value: ([0-9.\-]+)", block)
            old = float(sm.group(1)) if sm else 1.0
            src = guid_map.get(guid)
            if src is None:
                continue
            if src.suffix.lower() != ".fbx":
                # wrapper referencing fbx by same folder name
                maybe = src.with_suffix(".fbx") if src.suffix.lower() == ".prefab" else None
                # For Skin_PizzaBaked: source prefab's m_SourcePrefab points to Skin_PizzaBaked_Visual.fbx guid
                continue
            native = unity_native_size(str(src))
            if native:
                candidates.append((native, old, src.name))

        if not candidates:
            mm = re.search(r"m_Mesh: \{fileID: [^,]+, guid: ([a-f0-9]{32})", text)
            if mm:
                src = guid_map.get(mm.group(1))
                if src and src.exists() and src.suffix.lower() == ".fbx":
                    native = unity_native_size(str(src))
                    old = 1.0
                    for sm2 in re.finditer(
                        r"m_LocalScale: \{x: ([0-9.\-]+), y: ([0-9.\-]+), z: ([0-9.\-]+)\}",
                        text,
                    ):
                        x = float(sm2.group(1))
                        if abs(x - 1.0) > 1e-4:
                            old = x
                    if native:
                        candidates.append((native, old, src.name))

        # PizzaBaked: nested Skin_PizzaBaked_Visual.fbx via wrapper's PrefabInstance source guid
        if not candidates:
            for g in re.findall(r"guid: ([a-f0-9]{32})", text):
                src = guid_map.get(g)
                if src and src.suffix.lower() == ".fbx" and src.exists():
                    native = unity_native_size(str(src))
                    if native:
                        old = 1.0
                        sm = re.search(
                            r"propertyPath: m_LocalScale\.x\s*\r?\n\s*value: ([0-9.\-]+)",
                            text,
                        )
                        if sm:
                            old = float(sm.group(1))
                        candidates.append((native, old, src.name))
                        break

        if not candidates:
            print("SKIP", prefab.name)
            continue

        # Use largest native part (main visual)
        native, old, src_name = max(candidates, key=lambda x: x[0])
        if native < 1e-8:
            print("SKIP tiny", prefab.name)
            continue
        rel = relative_for_prefab(prefab.name)
        new_scale = (target * rel) / native
        ns = apply_scale_to_prefab(prefab, new_scale)
        print(
            f"SET {prefab.name}: rel={rel:.2f} {src_name} native={native:.4f} "
            f"{old} -> {ns} (world~{native * float(ns):.3f})"
        )


if __name__ == "__main__":
    main()
