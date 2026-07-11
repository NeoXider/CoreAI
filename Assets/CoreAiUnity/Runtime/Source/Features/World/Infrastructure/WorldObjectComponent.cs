using UnityEngine;

namespace CoreAI.Infrastructure.World
{
    [AddComponentMenu("")]
    public sealed class WorldObjectComponent : MonoBehaviour
    {
        public string persistentId;
        public string prefabKey;
    }
}