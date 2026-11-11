using CoreAI.Mods.Rbx.Instances;
using UnityEngine;

namespace CoreAI.Mods.Rbx.Binding
{
    /// <summary>
    /// What water a character's body is in, as the physics port last sensed it from the Terrain's cells: kept on the
    /// body so its motor reads it without knowing the Terrain. Added by the port to character bodies only.
    /// </summary>
    public sealed class RbxCharacterWater : MonoBehaviour
    {
        /// <summary>The contact sensed in the last fixed step.</summary>
        public RbxWaterContact Contact { get; set; } = RbxWaterContact.Dry;
    }
}
