#!/usr/bin/env python3
"""Offline batch: convert SkinAsset materials to TCP2 Hybrid (Robot Hybrid Crisp style)."""
from __future__ import annotations

import hashlib
import os
import re
import shutil
import uuid
from pathlib import Path

ROOT = Path(r"d:\DataAsset\UnityProject\MulPlayerLearningDemo-main\MulPlayerLearningDemo-main")
ASSETS = ROOT / "Assets"
SKIN = ASSETS / "Resources/So/Skin/SkinAsset"
TOON_DIR = SKIN / "ToonMaterials"
TEMPLATE_A = SKIN / "Counters/Robot Hybrid Crisp.mat"
TEMPLATE_B = SKIN / "Counters/Robot Hybrid Crisp 1.mat"
TCP2_SHADER_GUID = "df5bb027d94a6c44bb32b3c31ec1303f"

SKIP_NAME_TOKENS = ("particle", "sizzling", "trail", "font")
SKIP_MAT_GUIDS = {
    # keep VFX as-is
    "c78259c28adc42140b5e2e8419c882be",  # StoveSizzlingParticles
    "776cc3ef592a836479bea3edeefd0106",  # Trail
}

GUID_RE = re.compile(r"guid: ([a-f0-9]{32})")
MAT_REF_RE = re.compile(r"\{fileID: 2100000, guid: ([a-f0-9]{32}), type: 2\}")
SOURCE_PREFAB_RE = re.compile(
    r"m_SourcePrefab:\s*\{fileID: 100100000, guid: ([a-f0-9]{32}), type: 3\}"
)


def new_guid() -> str:
    return uuid.uuid4().hex


def read_text(path: Path) -> str:
    return path.read_text(encoding="utf-8", errors="replace")


def write_text(path: Path, text: str) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(text, encoding="utf-8", newline="\n")


def build_guid_map() -> dict[str, Path]:
    mapping: dict[str, Path] = {}
    for dirpath, dirnames, filenames in os.walk(ASSETS):
        # skip huge/irrelevant
        low = dirpath.lower().replace("\\", "/")
        if "/library/" in low or "/temp/" in low:
            continue
        for fn in filenames:
            if not fn.endswith(".meta"):
                continue
            meta = Path(dirpath) / fn
            try:
                with meta.open("r", encoding="utf-8", errors="replace") as f:
                    # guid is almost always line 2
                    f.readline()
                    line = f.readline()
                m = re.match(r"guid: ([a-f0-9]{32})", line.strip())
                if not m:
                    continue
                asset = Path(str(meta)[:-5])  # strip .meta
                mapping[m.group(1)] = asset
            except OSError:
                continue
    return mapping


def extract_prop_block(mat_text: str, prop: str) -> str | None:
    # Match YAML texture block or color line under m_SavedProperties
    # Texture:
    tex = re.search(
        rf"- {re.escape(prop)}:\n(?:        .*\n)*?        m_Texture: \{{fileID: [^}}]+\}}",
        mat_text,
    )
    if tex:
        return tex.group(0)
    # Color single line
    col = re.search(rf"- {re.escape(prop)}: \{{r: [^}}]+\}}", mat_text)
    if col:
        return col.group(0)
    return None


def get_texture_guid(mat_text: str, prop: str) -> str | None:
    m = re.search(
        rf"- {re.escape(prop)}:\n(?:        .*\n)*?        m_Texture: \{{fileID: \d+, guid: ([a-f0-9]{{32}}), type: 3\}}",
        mat_text,
    )
    return m.group(1) if m else None


def get_color(mat_text: str, prop: str) -> str | None:
    m = re.search(rf"- {re.escape(prop)}: (\{{r: [^}}]+\}})", mat_text)
    return m.group(1) if m else None


def set_texture(mat_text: str, prop: str, tex_guid: str | None) -> str:
    """Replace texture guid for prop; if missing guid, set fileID 0."""
    if tex_guid:
        repl = (
            f"- {prop}:\n"
            f"        m_Texture: {{fileID: 2800000, guid: {tex_guid}, type: 3}}\n"
            f"        m_Scale: {{x: 1, y: 1}}\n"
            f"        m_Offset: {{x: 0, y: 0}}"
        )
    else:
        repl = (
            f"- {prop}:\n"
            f"        m_Texture: {{fileID: 0}}\n"
            f"        m_Scale: {{x: 1, y: 1}}\n"
            f"        m_Offset: {{x: 0, y: 0}}"
        )

    pattern = re.compile(
        rf"- {re.escape(prop)}:\n(?:        .*\n)*?        m_Offset: \{{x: [^}}]+\}}",
        re.M,
    )
    if pattern.search(mat_text):
        return pattern.sub(repl, mat_text, count=1)
    return mat_text


def set_color(mat_text: str, prop: str, color_val: str) -> str:
    pattern = re.compile(rf"- {re.escape(prop)}: \{{r: [^}}]+\}}")
    if pattern.search(mat_text):
        return pattern.sub(f"- {prop}: {color_val}", mat_text, count=1)
    return mat_text


def is_tcp2_mat(mat_text: str) -> bool:
    return TCP2_SHADER_GUID in mat_text or "TCP2_RAMP_CRISP" in mat_text


def should_skip_mat(name: str, guid: str) -> bool:
    if guid in SKIP_MAT_GUIDS:
        return True
    low = name.lower()
    return any(tok in low for tok in SKIP_NAME_TOKENS)


def sanitize(name: str) -> str:
    bad = '<>:"/\\|?*'
    for c in bad:
        name = name.replace(c, "_")
    return name.replace(" ", "_").strip("_") or "Mat"


def short_hash(s: str) -> str:
    return hashlib.md5(s.encode("utf-8")).hexdigest()[:8]


def create_toon_mat(
    template_text: str,
    src_path: Path,
    src_guid: str,
    dest_path: Path,
    dest_guid: str,
) -> None:
    src_text = read_text(src_path)
    name = dest_path.stem
    text = template_text
    text = re.sub(r"  m_Name: .*", f"  m_Name: {name}", text, count=1)

    base = get_texture_guid(src_text, "_BaseMap") or get_texture_guid(src_text, "_MainTex")
    bump = get_texture_guid(src_text, "_BumpMap")
    emis = get_texture_guid(src_text, "_EmissionMap")
    base_col = get_color(src_text, "_BaseColor") or get_color(src_text, "_Color")
    emis_col = get_color(src_text, "_EmissionColor")

    text = set_texture(text, "_BaseMap", base)
    text = set_texture(text, "_MainTex", base)
    text = set_texture(text, "_BumpMap", bump)
    # Keep template emission map unless source has one
    if emis:
        text = set_texture(text, "_EmissionMap", emis)
    if base_col:
        text = set_color(text, "_BaseColor", base_col)
        text = set_color(text, "_Color", base_col)
    if emis_col:
        text = set_color(text, "_EmissionColor", emis_col)

    # Normal map toggle
    if bump and "m_Floats:" in text:
        text = re.sub(r"- _UseNormalMap: .*", "- _UseNormalMap: 1", text)
        if "TCP2_BUMP" not in text and "m_ValidKeywords:" in text:
            text = text.replace(
                "m_ValidKeywords:\n",
                "m_ValidKeywords:\n  - TCP2_BUMP\n",
                1,
            )

    write_text(dest_path, text)
    write_text(
        dest_path.with_suffix(".mat.meta"),
        "fileFormatVersion: 2\n"
        f"guid: {dest_guid}\n"
        "NativeFormatImporter:\n"
        "  externalObjects: {}\n"
        "  mainObjectFileID: 2100000\n"
        "  userData: \n"
        "  assetBundleName: \n"
        "  assetBundleVariant: \n",
    )


def find_renderer_mats_in_fbx(fbx_path: Path) -> list[tuple[str, list[str]]]:
    """Return list of (renderer_fileID, [mat_guid...]) from an FBX/prefab YAML."""
    if not fbx_path.exists():
        # Unity stores fbx as binary sometimes; companion .prefab may not exist.
        # Try reading .meta only — can't get materials from binary FBX offline.
        return []
    # FBX imported assets often have a YAML representation only inside Library.
    # If the file is binary, skip.
    try:
        head = fbx_path.read_bytes()[:64]
    except OSError:
        return []
    if not head.lstrip().startswith(b"%YAML"):
        return []

    text = read_text(fbx_path)
    results: list[tuple[str, list[str]]] = []
    # Split by MeshRenderer / SkinnedMeshRenderer blocks
    for block in re.split(r"\n--- !u!", text):
        if "MeshRenderer:" not in block and "SkinnedMeshRenderer:" not in block:
            continue
        # fileID of the renderer component is in the preceding GameObject... actually
        # the --- !u!23 &FILEID is MeshRenderer
        # When split we lose the header. Re-parse differently.
        pass

    # Better: find all &fileID MeshRenderer sections
    for m in re.finditer(
        r"--- !u!(23|137) &(-?\d+)\n(?:MeshRenderer|SkinnedMeshRenderer):\n(.*?)(?=\n--- !u!|\Z)",
        text,
        re.S,
    ):
        file_id = m.group(2)
        body = m.group(3)
        mats = MAT_REF_RE.findall(body)
        if mats:
            results.append((file_id, mats))
    return results


def collect_skin_prefabs() -> list[Path]:
    out = []
    for sub in ("Counters", "Ingredients", "Characters"):
        d = SKIN / sub
        if d.is_dir():
            out.extend(sorted(d.glob("*.prefab")))
    return out


def replace_mat_guid_in_text(text: str, old: str, new: str) -> tuple[str, int]:
    if old == new:
        return text, 0
    count = text.count(old)
    if count:
        text = text.replace(old, new)
    return text, count


def ensure_material_override(
    prefab_text: str, source_guid: str, renderer_file_id: str, slot: int, toon_guid: str
) -> str:
    """Insert or replace PrefabInstance material override for a nested FBX renderer."""
    prop = f"m_Materials.Array.data[{slot}]"
    # Find existing override for this target+prop
    pattern = re.compile(
        rf"(    - target: \{{fileID: {re.escape(renderer_file_id)}, guid: {source_guid},\n"
        rf"        type: 3\}}\n"
        rf"      propertyPath: {re.escape(prop)}\n"
        rf"      value: \n"
        rf"      objectReference: \{{fileID: 2100000, guid: )([a-f0-9]{{32}})(, type: 2\}})",
        re.M,
    )
    if pattern.search(prefab_text):
        return pattern.sub(rf"\g<1>{toon_guid}\g<3>", prefab_text)

    # Insert before m_RemovedComponents of the PrefabInstance that uses this source
    override = (
        f"    - target: {{fileID: {renderer_file_id}, guid: {source_guid},\n"
        f"        type: 3}}\n"
        f"      propertyPath: {prop}\n"
        f"      value: \n"
        f"      objectReference: {{fileID: 2100000, guid: {toon_guid}, type: 2}}\n"
    )

    # Prefer insert right before m_RemovedComponents in the matching PrefabInstance block
    # Find the PrefabInstance whose m_SourcePrefab uses source_guid
    src_marker = f"guid: {source_guid}, type: 3}}"
    idx = 0
    while True:
        sp = prefab_text.find("m_SourcePrefab:", idx)
        if sp < 0:
            break
        line_end = prefab_text.find("\n", sp)
        line = prefab_text[sp:line_end]
        if source_guid in line:
            # walk back to PrefabInstance start, find m_RemovedComponents within this instance
            inst_start = prefab_text.rfind("PrefabInstance:", 0, sp)
            removed = prefab_text.find("m_RemovedComponents:", inst_start)
            if removed > 0 and removed < sp + 2000:
                prefab_text = prefab_text[:removed] + override + prefab_text[removed:]
                return prefab_text
        idx = sp + 1

    return prefab_text


def main() -> None:
    print("Building GUID map...")
    guid_map = build_guid_map()
    print(f"  {len(guid_map)} assets")

    TOON_DIR.mkdir(parents=True, exist_ok=True)

    # Template
    if not TEMPLATE_A.exists() and not (TOON_DIR / "Robot Hybrid Crisp.mat").exists():
        raise SystemExit("Missing template Robot Hybrid Crisp.mat")

    template_path = TEMPLATE_A if TEMPLATE_A.exists() else (TOON_DIR / "Robot Hybrid Crisp.mat")
    template_text = read_text(template_path)
    template_guid = None
    meta = template_path.with_suffix(".mat.meta")
    if meta.exists():
        m = re.search(r"guid: ([a-f0-9]{32})", read_text(meta))
        template_guid = m.group(1) if m else None

    # Move demo mats into ToonDir (keep same guids via move)
    for src in (TEMPLATE_A, TEMPLATE_B):
        if not src.exists():
            continue
        dest = TOON_DIR / src.name
        if dest.exists():
            # update map if we know guid
            meta_dest = dest.with_suffix(".mat.meta")
            if meta_dest.exists():
                gm = re.search(r"guid: ([a-f0-9]{32})", read_text(meta_dest))
                if gm:
                    guid_map[gm.group(1)] = dest
            continue
        print(f"MOVE {src.name} → ToonMaterials/")
        meta_src = src.with_suffix(".mat.meta")
        src_guid = None
        if meta_src.exists():
            gm = re.search(r"guid: ([a-f0-9]{32})", read_text(meta_src))
            src_guid = gm.group(1) if gm else None
        shutil.move(str(src), str(dest))
        if meta_src.exists():
            shutil.move(str(meta_src), str(dest.with_suffix(".mat.meta")))
        if src_guid:
            guid_map[src_guid] = dest
        if src.name == "Robot Hybrid Crisp.mat":
            template_path = dest
            template_text = read_text(template_path)

    # Collect materials to convert: all mat guids referenced by skin prefabs + nested sources
    needed: dict[str, Path] = {}  # src_guid -> path

    prefabs = collect_skin_prefabs()
    nested_jobs: list[tuple[Path, str]] = []  # (skin_prefab, source_fbx_guid)

    for prefab in prefabs:
        text = read_text(prefab)
        for g in MAT_REF_RE.findall(text):
            if g in SKIP_MAT_GUIDS:
                continue
            p = guid_map.get(g)
            if p and p.suffix.lower() == ".mat" and p.exists():
                needed[g] = p
        for sg in SOURCE_PREFAB_RE.findall(text):
            nested_jobs.append((prefab, sg))
            sp = guid_map.get(sg)
            if sp is None:
                continue
            # Try YAML sibling or the asset itself
            for candidate in (sp, Path(str(sp) + ".prefab")):
                if candidate.exists() and candidate.suffix.lower() in {".prefab", ".fbx"}:
                    for _rid, mats in find_renderer_mats_in_fbx(candidate):
                        for mg in mats:
                            if mg in SKIP_MAT_GUIDS:
                                continue
                            mp = guid_map.get(mg)
                            if mp and mp.suffix.lower() == ".mat" and mp.exists():
                                needed[mg] = mp

    # Also pull materials from Character_1 and counters even if already TCP2 (no recreate)
    print(f"Source materials found: {len(needed)}")

    # Create toon materials
    src_to_toon: dict[str, str] = {}  # src_guid -> toon_guid
    created = 0

    # Already TCP2 sources map to themselves (and should live in ToonDir)
    for g, p in list(needed.items()):
        if not p.exists():
            print(f"SKIP missing {p}")
            continue
        text = read_text(p)
        name = p.stem
        if should_skip_mat(name, g):
            continue
        if is_tcp2_mat(text):
            src_to_toon[g] = g
            # ensure in ToonDir
            if p.parent != TOON_DIR:
                dest = TOON_DIR / p.name
                if not dest.exists():
                    print(f"MOVE TCP2 {p.name}")
                    meta_p = p.with_suffix(".mat.meta")
                    shutil.move(str(p), str(dest))
                    if meta_p.exists():
                        shutil.move(str(meta_p), str(dest.with_suffix(".mat.meta")))
                    guid_map[g] = dest
            continue

        dest_name = f"Toon_{sanitize(name)}_{short_hash(g)}.mat"
        dest = TOON_DIR / dest_name
        # reuse if exists
        if dest.exists():
            dm = dest.with_suffix(".mat.meta")
            tg = re.search(r"guid: ([a-f0-9]{32})", read_text(dm)).group(1)
            src_to_toon[g] = tg
            continue

        tg = new_guid()
        create_toon_mat(template_text, p, g, dest, tg)
        src_to_toon[g] = tg
        guid_map[tg] = dest
        created += 1
        print(f"CREATE {dest_name} ← {name}")

    print(f"Created {created} toon materials; map size {len(src_to_toon)}")

    # Replace direct material refs in all skin prefabs
    prefab_updates = 0
    slot_updates = 0
    for prefab in prefabs:
        text = read_text(prefab)
        orig = text
        for old, new in src_to_toon.items():
            if old == new:
                continue
            text, n = replace_mat_guid_in_text(text, old, new)
            slot_updates += n

        # Nested FBX material overrides (only when we can parse YAML source)
        for skin_prefab, source_guid in nested_jobs:
            if skin_prefab != prefab:
                continue
            sp = guid_map.get(source_guid)
            if sp is None:
                continue
            candidates = [sp]
            if sp.suffix.lower() == ".fbx":
                # sometimes materials only in Library; try reading fbx as yaml unlikely
                pass
            for cand in candidates:
                for renderer_id, mats in find_renderer_mats_in_fbx(cand):
                    for slot, mg in enumerate(mats):
                        if mg not in src_to_toon:
                            continue
                        tg = src_to_toon[mg]
                        if tg == mg:
                            continue
                        text = ensure_material_override(
                            text, source_guid, renderer_id, slot, tg
                        )

        if text != orig:
            write_text(prefab, text)
            prefab_updates += 1
            print(f"UPDATE {prefab.relative_to(ASSETS)}")

    # Additionally: for nested FBX that are binary, convert widely-used shared mats
    # by replacing at material asset usage only via overrides we already did for direct refs.
    # Scan ingredient nesteds: if no YAML, create overrides using common Unity FBX renderer fileIDs
    # is unreliable. Rely on Editor script for remaining.

    report = TOON_DIR / "_convert_report.txt"
    lines = [
        f"created={created}",
        f"mapped={len(src_to_toon)}",
        f"prefabs_updated={prefab_updates}",
        f"guid_replacements={slot_updates}",
        f"toon_dir={TOON_DIR}",
        "",
        "mapping:",
    ]
    for old, new in sorted(src_to_toon.items(), key=lambda x: x[0]):
        op = guid_map.get(old)
        np = guid_map.get(new)
        lines.append(f"  {old} -> {new}  |  {op.name if op else '?'} -> {np.name if np else '?'}")
    write_text(report, "\n".join(lines) + "\n")
    print(f"Report: {report}")
    print("Done. Run Unity menu Kitchen/Skin/Convert SkinAssets To Toon Materials (TCP2) for nested FBX leftovers.")


if __name__ == "__main__":
    main()
