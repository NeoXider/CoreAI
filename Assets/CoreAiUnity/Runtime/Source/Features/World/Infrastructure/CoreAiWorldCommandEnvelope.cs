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
        public string targetName = "";
        public int boolValue;
        public float floatValue;
        public string stringValue = "";

        // Spawn
        public string prefabKeyOrName = "";

        // Unified XYZ
        public float x;
        public float y;
        public float z;

        // Force
        public float fx;
        public float fy;
        public float fz;

        // Scene
        public string sceneName = "";

        public static CoreAiWorldCommandEnvelope Spawn(string prefabKeyOrName, string targetName, Vector3 pos)
        {
            return new CoreAiWorldCommandEnvelope
            {
                action = "spawn",
                prefabKeyOrName = prefabKeyOrName ?? "",
                targetName = targetName ?? "",
                x = pos.x,
                y = pos.y,
                z = pos.z
            };
        }

        public static CoreAiWorldCommandEnvelope Move(string targetName, Vector3 pos)
        {
            return new CoreAiWorldCommandEnvelope
            {
                action = "move",
                targetName = targetName ?? "",
                x = pos.x,
                y = pos.y,
                z = pos.z
            };
        }

        public static CoreAiWorldCommandEnvelope Destroy(string targetName)
        {
            return new CoreAiWorldCommandEnvelope
            {
                action = "destroy",
                targetName = targetName ?? ""
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

        public static CoreAiWorldCommandEnvelope ReloadScene()
        {
            return new CoreAiWorldCommandEnvelope
            {
                action = "reload_scene"
            };
        }

        public static CoreAiWorldCommandEnvelope SetActive(string targetName, bool active)
        {
            return new CoreAiWorldCommandEnvelope
            {
                action = "set_active",
                targetName = targetName ?? "",
                boolValue = active ? 1 : 0
            };
        }

        public static CoreAiWorldCommandEnvelope PlayAnimation(string targetName, string animationName)
        {
            return new CoreAiWorldCommandEnvelope
            {
                action = "play_animation",
                targetName = targetName ?? "",
                stringValue = animationName ?? ""
            };
        }

        public static CoreAiWorldCommandEnvelope PlaySound(string targetName, string clipName, float volume)
        {
            return new CoreAiWorldCommandEnvelope
            {
                action = "play_sound",
                targetName = targetName ?? "",
                stringValue = clipName ?? "",
                floatValue = volume
            };
        }

        public static CoreAiWorldCommandEnvelope ShowText(string targetName, string text)
        {
            return new CoreAiWorldCommandEnvelope
            {
                action = "show_text",
                targetName = targetName ?? "",
                stringValue = text ?? ""
            };
        }

        public static CoreAiWorldCommandEnvelope ApplyForce(string targetName, Vector3 force)
        {
            return new CoreAiWorldCommandEnvelope
            {
                action = "apply_force",
                targetName = targetName ?? "",
                fx = force.x,
                fy = force.y,
                fz = force.z
            };
        }


        public static CoreAiWorldCommandEnvelope SpawnParticles(string targetName, string effectName)
        {
            return new CoreAiWorldCommandEnvelope
            {
                action = "spawn_particles",
                targetName = targetName ?? "",
                stringValue = effectName ?? ""
            };
        }

        public static CoreAiWorldCommandEnvelope ListObjects(string searchPattern = "")
        {
            return new CoreAiWorldCommandEnvelope
            {
                action = "list_objects",
                stringValue = searchPattern ?? ""
            };
        }

        public static CoreAiWorldCommandEnvelope ListAnimations(string targetName = "")
        {
            return new CoreAiWorldCommandEnvelope
            {
                action = "list_animations",
                targetName = targetName ?? ""
            };
        }
    }
}