"""Generate KitchenObj / Process / Assembly / Recipe ScriptableObject YAML assets."""
from __future__ import annotations

import uuid
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
SO = ROOT / "Assets" / "Resources" / "So"

KIT_SCRIPT = "790a2a2d6379a964aa1c70f10f9e5a12"
PROC_SCRIPT = "b2c3d4e5f6a7b8c9d0e1f2a3b4c5d6e7"
RECIPE_SCRIPT = "c3d4e5f6a7b8c9d0e1f2a3b4c5d6e7f8"
ASM_SCRIPT = "3480d0825edc4ce0b7b42dfc136e64e9"

# Reuse Tomato logic prefab + icon as placeholder for new items
TOMATO_PREFAB = ("7347640811211430993", "9f157b8fe44e4604fadbec419d45007f")
TOMATO_SPRITE = ("21300000", "91e8fe3de9249fc44b7bdd8050062896")
BREAD_PREFAB = ("7347640811211430993", "35011eeaf51c5684ea32ffe898ce442d")  # from Bread.asset if needed

# Enum values
E = {
    "Tomato": 0,
    "TomatoSlices": 1,
    "CheeseBlock": 2,
    "Bread": 3,
    "Cabbage": 4,
    "MeatPattyUncooked": 5,
    "CabbageSlices": 6,
    "MeatPattyCooked": 7,
    "CheeseSlices": 8,
    "MeatPattyBurned": 9,
    "Plate": 10,
    "ChickenRaw": 11,
    "ChickenCooked": 12,
    "ChickenBurned": 13,
    "Fish": 14,
    "FishSlices": 15,
    "Shrimp": 16,
    "ShrimpSlices": 17,
    "RiceBall": 18,
    "Nori": 19,
    "Cream": 20,
    "PizzaDough": 21,
    "PizzaUnbaked": 22,
    "PizzaBaked": 23,
    "ChickenBurger": 24,
    "BeefBurger": 25,
    "FishSushi": 26,
    "ShrimpSushi": 27,
    "TomatoSalad": 28,
    "CabbageSalad": 29,
    "TomatoCabbageSalad": 30,
    "TomatoShake": 31,
    "CabbageShake": 32,
    "TomatoCabbageShake": 33,
}

FAC = {"CuttingCounter": 0, "StoveCounter": 1, "OvenCounter": 2, "BlenderCounter": 3}


def meta_for(path: Path) -> None:
    m = path.with_suffix(path.suffix + ".meta")
    if m.exists():
        return
    m.write_text(
        f"""fileFormatVersion: 2
guid: {uuid.uuid4().hex}
NativeFormatImporter:
  externalObjects: {{}}
  mainObjectFileID: 11400000
  userData: 
  assetBundleName: 
  assetBundleVariant: 
""",
        encoding="utf-8",
    )


def write_kitchen_obj(name: str, enum_val: int) -> None:
    d = SO / "KitchenObj"
    d.mkdir(parents=True, exist_ok=True)
    path = d / f"{name}.asset"
    # Keep existing assets' prefab refs if file exists and is known legacy
    prefab_id, prefab_guid = TOMATO_PREFAB
    sprite_id, sprite_guid = TOMATO_SPRITE
    text = f"""%YAML 1.1
%TAG !u! tag:unity3d.com,2011:
--- !u!114 &11400000
MonoBehaviour:
  m_ObjectHideFlags: 0
  m_CorrespondingSourceObject: {{fileID: 0}}
  m_PrefabInstance: {{fileID: 0}}
  m_PrefabAsset: {{fileID: 0}}
  m_GameObject: {{fileID: 0}}
  m_Enabled: 1
  m_EditorHideFlags: 0
  m_Script: {{fileID: 11500000, guid: {KIT_SCRIPT}, type: 3}}
  m_Name: {name}
  m_EditorClassIdentifier: 
  prefab: {{fileID: {prefab_id}, guid: {prefab_guid}, type: 3}}
  sprite: {{fileID: {sprite_id}, guid: {sprite_guid}, type: 3}}
  kitchenObjEnum: {enum_val}
"""
    # Only create if missing — don't overwrite existing Tomato etc prefab wiring
    if path.exists() and name in {
        "Tomato", "TomatoSlices", "CheeseBlock", "Bread", "Cabbage",
        "MetaPattyUncooked", "CabbageSlices", "MetaPattyCooked", "CheeseSlices",
        "MetaPattyBurned", "Plate",
    }:
        # Patch kitchenObjEnum into existing if needed
        raw = path.read_text(encoding="utf-8")
        if "kitchenObjEnum:" not in raw:
            if "objName:" in raw:
                raw = raw.replace("objName: Tomato", f"kitchenObjEnum: {enum_val}")
            else:
                raw = raw.rstrip() + f"\n  kitchenObjEnum: {enum_val}\n"
            path.write_text(raw, encoding="utf-8")
        return
    path.write_text(text, encoding="utf-8")
    meta_for(path)


def write_process(name: str, inp: str, out: str, facility: str, value: float) -> None:
    d = SO / "Processes"
    d.mkdir(parents=True, exist_ok=True)
    path = d / f"{name}.asset"
    text = f"""%YAML 1.1
%TAG !u! tag:unity3d.com,2011:
--- !u!114 &11400000
MonoBehaviour:
  m_ObjectHideFlags: 0
  m_CorrespondingSourceObject: {{fileID: 0}}
  m_PrefabInstance: {{fileID: 0}}
  m_PrefabAsset: {{fileID: 0}}
  m_GameObject: {{fileID: 0}}
  m_Enabled: 1
  m_EditorHideFlags: 0
  m_Script: {{fileID: 11500000, guid: {PROC_SCRIPT}, type: 3}}
  m_Name: {name}
  m_EditorClassIdentifier: 
  inputEnum: {E[inp]}
  outputEnum: {E[out]}
  requiredFacility: {FAC[facility]}
  processValue: {value}
"""
    path.write_text(text, encoding="utf-8")
    meta_for(path)


def write_assembly(aid: str, inputs: list[str], output: str) -> None:
    d = SO / "Assemblies"
    d.mkdir(parents=True, exist_ok=True)
    path = d / f"{aid}.asset"
    lines = "\n".join(f"  - {E[i]}" for i in inputs)
    text = f"""%YAML 1.1
%TAG !u! tag:unity3d.com,2011:
--- !u!114 &11400000
MonoBehaviour:
  m_ObjectHideFlags: 0
  m_CorrespondingSourceObject: {{fileID: 0}}
  m_PrefabInstance: {{fileID: 0}}
  m_PrefabAsset: {{fileID: 0}}
  m_GameObject: {{fileID: 0}}
  m_Enabled: 1
  m_EditorHideFlags: 0
  m_Script: {{fileID: 11500000, guid: {ASM_SCRIPT}, type: 3}}
  m_Name: {aid}
  m_EditorClassIdentifier: 
  assemblyId: {aid}
  inputs:
{lines}
  output: {E[output]}
"""
    path.write_text(text, encoding="utf-8")
    meta_for(path)


def write_recipe(name: str, display: str, required: str) -> None:
    d = SO / "Recipes"
    d.mkdir(parents=True, exist_ok=True)
    path = d / f"{name}.asset"
    text = f"""%YAML 1.1
%TAG !u! tag:unity3d.com,2011:
--- !u!114 &11400000
MonoBehaviour:
  m_ObjectHideFlags: 0
  m_CorrespondingSourceObject: {{fileID: 0}}
  m_PrefabInstance: {{fileID: 0}}
  m_PrefabAsset: {{fileID: 0}}
  m_GameObject: {{fileID: 0}}
  m_Enabled: 1
  m_EditorHideFlags: 0
  m_Script: {{fileID: 11500000, guid: {RECIPE_SCRIPT}, type: 3}}
  m_Name: {name}
  m_EditorClassIdentifier: 
  recipeName: {display}
  requiredItem: {E[required]}
"""
    path.write_text(text, encoding="utf-8")
    meta_for(path)


def folder_meta(path: Path) -> None:
    m = path.with_suffix(path.suffix + ".meta") if path.suffix else Path(str(path) + ".meta")
    # for directories: path.meta
    m = Path(str(path) + ".meta")
    if m.exists():
        return
    m.write_text(
        f"""fileFormatVersion: 2
guid: {uuid.uuid4().hex}
folderAsset: yes
DefaultImporter:
  externalObjects: {{}}
  userData: 
  assetBundleName: 
  assetBundleVariant: 
""",
        encoding="utf-8",
    )


def main() -> None:
    # Kitchen objects (new + ensure enum on old)
    for name, val in E.items():
        file_name = {
            "MeatPattyUncooked": "MetaPattyUncooked",
            "MeatPattyCooked": "MetaPattyCooked",
            "MeatPattyBurned": "MetaPattyBurned",
        }.get(name, name)
        write_kitchen_obj(file_name, val)

    # Processes
    processes = [
        ("Tomato_Cut", "Tomato", "TomatoSlices", "CuttingCounter", 4),
        ("Cabbage_Cut", "Cabbage", "CabbageSlices", "CuttingCounter", 4),
        ("CheeseBlock_Cut", "CheeseBlock", "CheeseSlices", "CuttingCounter", 4),
        ("Fish_Cut", "Fish", "FishSlices", "CuttingCounter", 4),
        ("Shrimp_Cut", "Shrimp", "ShrimpSlices", "CuttingCounter", 4),
        ("MeatPattyUncooked_Cook", "MeatPattyUncooked", "MeatPattyCooked", "StoveCounter", 3),
        ("MeatPattyCooked_Cook", "MeatPattyCooked", "MeatPattyBurned", "StoveCounter", 3),
        ("ChickenRaw_Cook", "ChickenRaw", "ChickenCooked", "StoveCounter", 3),
        ("ChickenCooked_Burn", "ChickenCooked", "ChickenBurned", "StoveCounter", 3),
        ("Pizza_Bake", "PizzaUnbaked", "PizzaBaked", "OvenCounter", 4),
        ("TomatoSalad_Blend", "TomatoSalad", "TomatoShake", "BlenderCounter", 3),
        ("CabbageSalad_Blend", "CabbageSalad", "CabbageShake", "BlenderCounter", 3),
        ("TomatoCabbageSalad_Blend", "TomatoCabbageSalad", "TomatoCabbageShake", "BlenderCounter", 3),
    ]
    for row in processes:
        write_process(*row)

    # Assemblies
    assemblies = [
        ("T1_ChickenBurger", ["TomatoSlices", "CabbageSlices", "CheeseSlices", "ChickenCooked", "Bread"], "ChickenBurger"),
        ("T2_BeefBurger", ["TomatoSlices", "CabbageSlices", "CheeseSlices", "MeatPattyCooked", "Bread"], "BeefBurger"),
        ("T3_FishSushi", ["FishSlices", "Nori", "RiceBall"], "FishSushi"),
        ("T4_ShrimpSushi", ["ShrimpSlices", "Nori", "RiceBall"], "ShrimpSushi"),
        ("T5_PizzaUnbaked", ["ChickenCooked", "TomatoSlices", "CheeseSlices", "PizzaDough"], "PizzaUnbaked"),
        ("T9_TomatoSalad", ["TomatoSlices", "Cream"], "TomatoSalad"),
        ("T10_TomatoCabbageSalad", ["TomatoSlices", "CabbageSlices", "Cream"], "TomatoCabbageSalad"),
        ("T11_CabbageSalad", ["CabbageSlices", "Cream"], "CabbageSalad"),
    ]
    folder_meta(SO / "Assemblies")
    for row in assemblies:
        write_assembly(*row)

    # Remove obsolete recipes then write new
    recipes_dir = SO / "Recipes"
    for old in ["Salad", "CheeseBurger", "MeatCheeseBurger", "Tomato"]:
        p = recipes_dir / f"{old}.asset"
        if p.exists():
            p.unlink()
        m = Path(str(p) + ".meta")
        if m.exists():
            m.unlink()

    recipes = [
        ("ChickenBurger", "鸡肉汉堡", "ChickenBurger"),
        ("BeefBurger", "牛肉汉堡", "BeefBurger"),
        ("FishSushi", "鱼肉寿司", "FishSushi"),
        ("ShrimpSushi", "鲜虾寿司", "ShrimpSushi"),
        ("PizzaBaked", "番茄鸡腿披萨", "PizzaBaked"),
        ("TomatoSalad", "番茄沙拉", "TomatoSalad"),
        ("CabbageSalad", "包菜沙拉", "CabbageSalad"),
        ("TomatoCabbageSalad", "番茄包菜沙拉", "TomatoCabbageSalad"),
        ("FishSlices", "切碎鱼肉", "FishSlices"),
        ("ShrimpSlices", "切碎虾肉", "ShrimpSlices"),
        ("ChickenSteak", "鸡排", "ChickenCooked"),
        ("BeefSteak", "牛排", "MeatPattyCooked"),
        ("TomatoShake", "番茄奶昔", "TomatoShake"),
        ("CabbageShake", "包菜奶昔", "CabbageShake"),
        ("TomatoCabbageShake", "番茄包菜奶昔", "TomatoCabbageShake"),
        ("CabbageOrder", "包菜", "Cabbage"),
        ("TomatoOrder", "番茄", "Tomato"),
    ]
    for row in recipes:
        write_recipe(*row)

    print("Generated kitchen data under", SO)


if __name__ == "__main__":
    main()
