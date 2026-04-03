using System;
using UnityEngine;

namespace CoreAI.Infrastructure.World
{
    /// <summary>JSON envelope для <c>AiGameCommandTypeIds.WorldCommand</c>.</summary>
    [Serializable]
    public sealed class CoreAiWorldCommandEnvelope
    {
        public string action = "";

        // Общие поля
        public string instanceId = "";

        // Spawn
        public string prefabKeyOrName = "";
        public float px;
        public float py;
        public float pz;

        // Move
        public float mx;
        public float my;
        public float mz;

        // Scene
        public string sceneName = "";

        public static CoreAiWorldCommandEnvelope Spawn(string prefabKeyOrName, string instanceId, Vector3 pos)
        {
            return new CoreAiWorldCommandEnvelope
            {
                action = "spawn",
                prefabKeyOrName = prefabKeyOrName ?? "",
                instanceId = instanceId ?? "",
                px = pos.x,
                py = pos.y,
                pz = pos.z
            };
        }

        public static CoreAiWorldCommandEnvelope Move(string instanceId, Vector3 pos)
        {
            return new CoreAiWorldCommandEnvelope
            {
                action = "move",
                instanceId = instanceId ?? "",
                mx = pos.x,
                my = pos.y,
                mz = pos.z
            };
        }

        public static CoreAiWorldCommandEnvelope Destroy(string instanceId)
        {
            return new CoreAiWorldCommandEnvelope
            {
                action = "destroy",
                instanceId = instanceId ?? ""
            };
        }

        public static CoreAiWorldCommandEnvelope LoadScene(string sceneName)
        {
            return new CoreAiWorldCommandEnvelope
            {
                action = "load_scene",
                sceneName = sceneName ?? ""
            };
        }
    }
}

