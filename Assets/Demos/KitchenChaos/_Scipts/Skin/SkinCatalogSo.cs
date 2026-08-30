using System;
using System.Collections.Generic;
using UnityEngine;

namespace Kitchen.Skin
{
    [Serializable]
    public class SkinMeshMaterial
    {
        public Mesh mesh;
        public Material material;

        public bool HasAny => mesh != null || material != null;
        public bool IsComplete => mesh != null && material != null;
    }

    [Serializable]
    public class CounterPartVisual
    {
        public CounterPartSlot slot;
        public Mesh mesh;
        public Material material;

        public bool HasAny => mesh != null || material != null;
    }

    [Serializable]
    public class IngredientSkinEntry
    {
        public KitchenObjEnum kind;
        public int skinId = 0;
        public SkinMeshMaterial meshMaterial = new();
        public GameObject visualPrefab;
        public SkinPreferMode preferMode = SkinPreferMode.PreferMeshMaterial;
    }

    [Serializable]
    public class CounterSkinEntry
    {
        public SkinCounterKind kind;
        public int skinId = 0;
        public List<CounterPartVisual> parts = new();
        public GameObject visualPrefab;
        public SkinPreferMode preferMode = SkinPreferMode.PreferMeshMaterial;
    }

    [Serializable]
    public class CharacterSkinEntry
    {
        public SkinCharacterKind kind;
        public int skinId = 0;
        public GameObject visualPrefab;
    }

    [Serializable]
    public class IngredientSkinOverride
    {
        public KitchenObjEnum kind;
        public int skinId;
    }

    [Serializable]
    public class CounterSkinOverride
    {
        public SkinCounterKind kind;
        public int skinId;
    }

    [Serializable]
    public class CharacterSkinOverride
    {
        public SkinCharacterKind kind;
        public int skinId;
    }

    /// <summary>
    /// 一套皮肤数据包（食材 / 柜子）。角色表现不在此 SO —— 由 CharacterSimple 上的
    /// CharacterSkinVisual 自包含；与 SkinManager 仅在需要时用事件订阅通讯。
    /// </summary>
    [CreateAssetMenu(menuName = "Kitchen/Skin/Skin Catalog", fileName = "SkinCatalog")]
    public class SkinCatalogSo : ScriptableObject
    {
        [Tooltip("全局默认 skinId")]
        public int defaultSkinId;

        [Header("按种类覆盖（可选）")]
        public List<IngredientSkinOverride> ingredientOverrides = new();
        public List<CounterSkinOverride> counterOverrides = new();
        [Tooltip("已弃用：角色不走 Catalog。保留字段仅兼容旧资产。")]
        public List<CharacterSkinOverride> characterOverrides = new();

        [Header("皮肤资源库")]
        public List<IngredientSkinEntry> ingredients = new();
        public List<CounterSkinEntry> counters = new();
        [Tooltip("已弃用：角色不走 Catalog。请保持为空。")]
        public List<CharacterSkinEntry> characters = new();

        public int ResolveIngredientSkinId(KitchenObjEnum kind)
        {
            for (int i = 0; i < ingredientOverrides.Count; i++)
            {
                if (ingredientOverrides[i].kind == kind)
                    return ingredientOverrides[i].skinId;
            }
            return defaultSkinId;
        }

        public int ResolveCounterSkinId(SkinCounterKind kind)
        {
            for (int i = 0; i < counterOverrides.Count; i++)
            {
                if (counterOverrides[i].kind == kind)
                    return counterOverrides[i].skinId;
            }
            return defaultSkinId;
        }

        public int ResolveCharacterSkinId(SkinCharacterKind kind)
        {
            for (int i = 0; i < characterOverrides.Count; i++)
            {
                if (characterOverrides[i].kind == kind)
                    return characterOverrides[i].skinId;
            }
            return defaultSkinId;
        }

        public IngredientSkinEntry FindIngredient(KitchenObjEnum kind, int skinId)
        {
            IngredientSkinEntry fallback = null;
            for (int i = 0; i < ingredients.Count; i++)
            {
                var e = ingredients[i];
                if (e == null || e.kind != kind) continue;
                if (e.skinId == skinId) return e;
                if (e.skinId == defaultSkinId) fallback = e;
            }
            return fallback;
        }

        public CounterSkinEntry FindCounter(SkinCounterKind kind, int skinId)
        {
            CounterSkinEntry fallback = null;
            for (int i = 0; i < counters.Count; i++)
            {
                var e = counters[i];
                if (e == null || e.kind != kind) continue;
                if (e.skinId == skinId) return e;
                if (e.skinId == defaultSkinId) fallback = e;
            }
            return fallback;
        }

        public CharacterSkinEntry FindCharacter(SkinCharacterKind kind, int skinId)
        {
            CharacterSkinEntry fallback = null;
            for (int i = 0; i < characters.Count; i++)
            {
                var e = characters[i];
                if (e == null || e.kind != kind) continue;
                if (e.skinId == skinId) return e;
                if (e.skinId == defaultSkinId) fallback = e;
            }
            return fallback;
        }

        public static SkinResolvedMode ResolveMode(bool hasMeshMaterial, bool hasPrefab, SkinPreferMode prefer)
        {
            if (hasMeshMaterial && hasPrefab)
                return prefer == SkinPreferMode.PreferPrefab
                    ? SkinResolvedMode.Prefab
                    : SkinResolvedMode.MeshMaterial;
            if (hasMeshMaterial) return SkinResolvedMode.MeshMaterial;
            if (hasPrefab) return SkinResolvedMode.Prefab;
            return SkinResolvedMode.None;
        }
    }
}
