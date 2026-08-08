using UnityEngine;

namespace Kitchen
{
    /// <summary>
    /// Plate assembly rule: when plate contents exactly match <see cref="inputs"/>,
    /// replace them with <see cref="output"/>.
    /// </summary>
    [CreateAssetMenu(fileName = "PlateAssembly", menuName = "ScriptableObjects/PlateAssembly", order = 3)]
    public class PlateAssemblySo : ScriptableObject
    {
        public string assemblyId;
        public KitchenObjEnum[] inputs;
        public KitchenObjEnum output;
    }
}
