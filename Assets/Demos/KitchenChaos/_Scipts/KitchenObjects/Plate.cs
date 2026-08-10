using System;
using System.Collections.Generic;
using System.Linq;
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
        private readonly HashSet<KitchenObjEnum> _ingredients = new();
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
            if (_ingredients.Contains(objEnum))
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
            _ingredients.Add(objEnum);
            onIngredientAdded?.Invoke(this, objEnum);

            if (PlateAssemblyMatcher.TryMatch(_ingredients, out var output, out var rule))
            {
                Debug.Log($"[Plate] Assembly {rule.assemblyId}: → {output}");
                _ingredients.Clear();
                _ingredients.Add(output);
                onIngredientAdded?.Invoke(this, output);
            }

            SyncInspectorContents();
        }

        public HashSet<KitchenObjEnum> GetIngredients()
        {
            return _ingredients;
        }

        /// <summary>Single deliverable item if the plate holds exactly one ingredient.</summary>
        public bool TryGetDeliverableItem(out KitchenObjEnum item)
        {
            item = default;
            if (_ingredients.Count != 1) return false;
            foreach (var i in _ingredients)
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

        /// <summary>Server-only: transform the single plate ingredient via a facility process.</summary>
        public bool ApplyProcessServer(FacilityEnum facility)
        {
            if (!IsServer) return false;
            if (!TryGetDeliverableItem(out var input)) return false;
            var process = DataTableManager.Sigleton.GetProcess(input, facility);
            if (process == null) return false;
            ReplaceIngredientClientRpc(input, process.outputEnum);
            return true;
        }

        [ClientRpc]
        private void ReplaceIngredientClientRpc(KitchenObjEnum from, KitchenObjEnum to)
        {
            if (!_ingredients.Contains(from)) return;
            _ingredients.Remove(from);
            _ingredients.Add(to);
            onIngredientAdded?.Invoke(this, to);
            SyncInspectorContents();
        }

        private void SyncInspectorContents()
        {
            inspectorContents.Clear();
            inspectorContents.AddRange(_ingredients.OrderBy(x => (int)x));
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
