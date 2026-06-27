using System.Collections.Generic;
using UnityEngine;

namespace Kitchen
{
    /// <summary>
    /// Centralized data table manager. Loads all configuration from ScriptableObjects in Resources.
    /// Replaces the old CSV/JSON/hardcoded approach with fully configurable SO assets.
    /// </summary>
    public class DataTableManager : MonoBehaviour
    {
        public static DataTableManager Sigleton { get; private set; }

        private Dictionary<KitchenObjEnum, KitchenObjSo> _kitchenDict;
        private Dictionary<(KitchenObjEnum, FacilityEnum), KitchenProcessSo> _processDict;

        private const string _kitchenDataSoDir = "So/KitchenObj/";
        private const string _processDataSoDir = "So/Processes/";

        #region KitchenObj

        public KitchenObjSo GetKitchenObjSo(KitchenObjEnum kitchenObjEnum)
        {
            return _kitchenDict[kitchenObjEnum];
        }

        #endregion

        #region Process

        /// <summary>
        /// Returns the process definition for a given input ingredient and facility,
        /// or null if no process is defined.
        /// </summary>
        public KitchenProcessSo GetProcess(KitchenObjEnum input, FacilityEnum facility)
        {
            _processDict.TryGetValue((input, facility), out var process);
            return process;
        }

        /// <summary>
        /// Returns true if a process exists for the given input on the given facility.
        /// </summary>
        public bool CanProcess(KitchenObjEnum input, FacilityEnum facility)
        {
            return _processDict.ContainsKey((input, facility));
        }

        #endregion

        #region Init

        private void _Init()
        {
            _InitKitchenDict();
            _InitProcessDict();
        }

        private void _InitKitchenDict()
        {
            _kitchenDict = new Dictionary<KitchenObjEnum, KitchenObjSo>();
            var kitchenObjSoList = Resources.LoadAll<KitchenObjSo>(_kitchenDataSoDir);
            foreach (var kitchenObjSo in kitchenObjSoList)
            {
                _kitchenDict.TryAdd(kitchenObjSo.kitchenObjEnum, kitchenObjSo);
            }
        }

        private void _InitProcessDict()
        {
            _processDict = new Dictionary<(KitchenObjEnum, FacilityEnum), KitchenProcessSo>();
            var processList = Resources.LoadAll<KitchenProcessSo>(_processDataSoDir);
            foreach (var process in processList)
            {
                var key = (process.inputEnum, process.requiredFacility);
                _processDict.TryAdd(key, process);
            }
        }

        #endregion

        private void Awake()
        {
            if (Sigleton != null)
            {
                Destroy(gameObject);
                return;
            }

            Sigleton = this;
            _Init();
        }
    }
}
