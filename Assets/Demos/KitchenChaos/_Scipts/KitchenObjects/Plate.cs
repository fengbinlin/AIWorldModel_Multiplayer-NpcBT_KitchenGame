using System;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

namespace Kitchen
{
    public class Plate : KitchenObj
    {
        private static readonly HashSet<KitchenObjEnum> _cannotPlace = new()
        {
            KitchenObjEnum.Plate,
            KitchenObjEnum.MeatPattyBurned,
            KitchenObjEnum.ChickenBurned,
        };

        public EventHandler<KitchenObjEnum> onIngredientAdded;
        public event Action onContentsChanged;
        private readonly HashSet<KitchenObjEnum> _ingredients = new();
        // Insertion order for stacking visuals / icons.
        private readonly List<KitchenObjEnum> _ingredientOrder = new();
        // Dedicated servers do not execute ClientRpc bodies. Keep an
        // authoritative server-side set so duplicate ADD_TO_PLATE RPCs cannot
        // be accepted before the clients receive the visual update.
        private readonly HashSet<KitchenObjEnum> _serverIngredients = new();

        [Header("Debug (Play Mode)")]
        [Tooltip("Read-only mirror of plate contents for the Inspector.")]
        [SerializeField] private List<KitchenObjEnum> inspectorContents = new();

        public bool TryAddIngredient(KitchenObj obj)
        {
            return TryAddIngredient(obj, 0);
        }

        public bool TryAddIngredient(KitchenObj obj, int orderId)
        {
            if (obj == null) return false;
            if (BoundOrderId != orderId)
                return false;
            if (_cannotPlace.Contains(obj.objEnum) || PlateAssemblyMatcher.IsBurnedWaste(obj.objEnum))
                return false;

            if (_ingredients.Contains(obj.objEnum))
                return false;

            AddIngredientServerRpc(obj.objEnum, orderId);
            return true;
        }

        [ServerRpc(RequireOwnership = false)]
        private void AddIngredientServerRpc(KitchenObjEnum objEnum, int orderId)
        {
            if (BoundOrderId != orderId)
                return;
            // The caller-side check is not sufficient when two agents reach the
            // same plate in the same network update. Keep the plate authoritative.
            if (_serverIngredients.Contains(objEnum))
                return;
            if (_cannotPlace.Contains(objEnum) || PlateAssemblyMatcher.IsBurnedWaste(objEnum))
                return;
            if (!_serverIngredients.Add(objEnum))
                return;

            if (PlateAssemblyMatcher.TryMatch(
                    _serverIngredients, out var assembledOutput, out _))
            {
                _serverIngredients.Clear();
                _serverIngredients.Add(assembledOutput);
            }
            AddIngredientClientRpc(objEnum);
        }

        [ClientRpc]
        private void AddIngredientClientRpc(KitchenObjEnum objEnum)
        {
            AddLocal(objEnum);
            onIngredientAdded?.Invoke(this, objEnum);

            if (PlateAssemblyMatcher.TryMatch(_ingredients, out var output, out var rule))
            {
                Debug.Log($"[Plate] Assembly {rule.assemblyId}: → {output}");
                ReplaceLocalWith(output);
                onIngredientAdded?.Invoke(this, output);
            }

            SyncInspectorContents();
            onContentsChanged?.Invoke();
        }

        public HashSet<KitchenObjEnum> GetIngredients()
        {
            return _ingredients;
        }

        /// <summary>Ingredients in add order (bottom → top for stacking).</summary>
        public IReadOnlyList<KitchenObjEnum> GetIngredientsOrdered()
        {
            return _ingredientOrder;
        }

        /// <summary>Single deliverable item if the plate holds exactly one ingredient.</summary>
        public bool TryGetDeliverableItem(out KitchenObjEnum item)
        {
            item = default;
            var source = IsServer && _serverIngredients.Count > 0
                ? _serverIngredients
                : _ingredients;
            if (source.Count != 1) return false;
            foreach (var i in source)
            {
                item = i;
                return true;
            }
            return false;
        }

        public bool CanProcessOn(FacilityEnum facility)
        {
            return TryGetDeliverableItem(out var item)
                   && DataTableManager.Sigleton.CanProcess(item, facility);
        }

        /// <summary>
        /// Removes the single assembled item from this plate so a timed
        /// facility can process the item itself while the now-empty plate is
        /// returned to staging. Server-only; clients receive the empty-plate
        /// state through the RPC below.
        /// </summary>
        public bool TryExtractIngredientForProcessingServer(
            FacilityEnum facility,
            int orderId,
            out KitchenObjEnum input)
        {
            input = default;
            if (!IsServer || orderId == 0 || BoundOrderId != orderId)
                return false;
            if (!TryGetDeliverableItem(out input))
                return false;
            if (DataTableManager.Sigleton.GetProcess(input, facility) == null)
                return false;

            ClearLocal();
            _serverIngredients.Clear();
            ClearIngredientsClientRpc(input);
            return true;
        }

        /// <summary>Server-only: transform the single plate ingredient via a facility process.</summary>
        public bool ApplyProcessServer(FacilityEnum facility)
        {
            if (!IsServer) return false;
            if (!TryGetDeliverableItem(out var input)) return false;
            var process = DataTableManager.Sigleton.GetProcess(input, facility);
            if (process == null) return false;
            _serverIngredients.Clear();
            _serverIngredients.Add(process.outputEnum);
            ReplaceIngredientClientRpc(input, process.outputEnum);
            return true;
        }

        [ClientRpc]
        private void ReplaceIngredientClientRpc(KitchenObjEnum from, KitchenObjEnum to)
        {
            if (!_ingredients.Contains(from)) return;
            _ingredients.Remove(from);
            _ingredientOrder.Remove(from);
            AddLocal(to);
            onIngredientAdded?.Invoke(this, to);
            SyncInspectorContents();
            onContentsChanged?.Invoke();
        }

        [ClientRpc]
        private void ClearIngredientsClientRpc(KitchenObjEnum removed)
        {
            ClearLocal();
            SyncInspectorContents();
            onContentsChanged?.Invoke();
        }

        private void AddLocal(KitchenObjEnum objEnum)
        {
            if (!_ingredients.Add(objEnum))
                return;
            _ingredientOrder.Add(objEnum);
        }

        private void ClearLocal()
        {
            _ingredients.Clear();
            _ingredientOrder.Clear();
        }

        private void ReplaceLocalWith(KitchenObjEnum single)
        {
            ClearLocal();
            AddLocal(single);
        }

        private void SyncInspectorContents()
        {
            inspectorContents.Clear();
            inspectorContents.AddRange(_ingredientOrder);
        }

#if UNITY_EDITOR
        private void OnValidate()
        {
            // Keep list in sync if inspecting while not playing after domain reload mid-session.
            if (!Application.isPlaying) return;
            SyncInspectorContents();
        }
#endif
    }
}
