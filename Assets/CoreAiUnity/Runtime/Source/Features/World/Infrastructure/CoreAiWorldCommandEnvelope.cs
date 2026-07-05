using System;
using UnityEngine;

namespace CoreAI.Infrastructure.World
{
    /// <summary>Serializable envelope for world commands emitted by AI tools.</summary>
    [Serializable]
    public sealed class CoreAiWorldCommandEnvelope
    {
        public string action = "";

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

        // Optional non-uniform scale. 0 means "use floatValue/default" for that axis.
        public float scaleX;
        public float scaleY;
        public float scaleZ;

        // Optional transform block flags for change/set_transform commands.
        public bool hasPosition;
        public bool hasRotation;
        public bool hasScale;
        public bool hasX;
        public bool hasY;
        public bool hasZ;
        public bool hasFx;
        public bool hasFy;
        public bool hasFz;

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
                z = pos.z,
                hasPosition = true
            };
        }

        /// <summary>
        /// Spawn carrying an optional initial rotation (Euler degrees in fx/fy/fz) and uniform scale
        /// (floatValue). A scale &lt;= 0 means "leave at the prefab/primitive default" (1). The executor
        /// applies rotation and scale during instantiation so a model can place a fully-oriented, sized
        /// object in a single tool call.
        /// </summary>
        public static CoreAiWorldCommandEnvelope Spawn(
            string prefabKeyOrName, string targetName, Vector3 pos, Vector3 eulerAngles, float uniformScale)
        {
            return Spawn(prefabKeyOrName, targetName, pos, eulerAngles, uniformScale, Vector3.zero);
        }

        public static CoreAiWorldCommandEnvelope Spawn(
            string prefabKeyOrName, string targetName, Vector3 pos, Vector3 eulerAngles, float uniformScale,
            Vector3 nonUniformScale)
        {
            return new CoreAiWorldCommandEnvelope
            {
                action = "spawn",
                prefabKeyOrName = prefabKeyOrName ?? "",
                targetName = targetName ?? "",
                x = pos.x,
                y = pos.y,
                z = pos.z,
                fx = eulerAngles.x,
                fy = eulerAngles.y,
                fz = eulerAngles.z,
                floatValue = uniformScale,
                scaleX = nonUniformScale.x,
                scaleY = nonUniformScale.y,
                scaleZ = nonUniformScale.z,
                hasPosition = true,
                hasRotation = eulerAngles != Vector3.zero,
                hasScale = uniformScale > 0f || nonUniformScale != Vector3.zero
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

        public static CoreAiWorldCommandEnvelope Rotate(string targetName, Vector3 eulerAngles)
        {
            return new CoreAiWorldCommandEnvelope
            {
                action = "rotate",
                targetName = targetName ?? "",
                fx = eulerAngles.x,
                fy = eulerAngles.y,
                fz = eulerAngles.z
            };
        }

        public static CoreAiWorldCommandEnvelope SetTransform(
            string targetName,
            Vector3 pos,
            Vector3 eulerAngles,
            float uniformScale)
        {
            return SetTransform(targetName, pos, eulerAngles, uniformScale, Vector3.zero);
        }

        public static CoreAiWorldCommandEnvelope SetTransform(
            string targetName,
            Vector3 pos,
            Vector3 eulerAngles,
            float uniformScale,
            Vector3 nonUniformScale)
        {
            CoreAiWorldCommandEnvelope env = Change(
                targetName,
                pos,
                true,
                eulerAngles,
                true,
                uniformScale,
                nonUniformScale,
                true,
                null);
            env.action = "set_transform";
            return env;
        }

        public static CoreAiWorldCommandEnvelope Change(
            string targetName,
            Vector3 pos,
            bool hasPosition,
            Vector3 eulerAngles,
            bool hasRotation,
            float uniformScale,
            Vector3 nonUniformScale,
            bool hasScale,
            string parentName)
        {
            return Change(
                targetName,
                pos,
                hasPosition,
                hasPosition,
                hasPosition,
                hasPosition,
                eulerAngles,
                hasRotation,
                hasRotation,
                hasRotation,
                hasRotation,
                uniformScale,
                nonUniformScale,
                hasScale,
                parentName);
        }

        public static CoreAiWorldCommandEnvelope Change(
            string targetName,
            Vector3 pos,
            bool hasPosition,
            bool hasX,
            bool hasY,
            bool hasZ,
            Vector3 eulerAngles,
            bool hasRotation,
            bool hasFx,
            bool hasFy,
            bool hasFz,
            float uniformScale,
            Vector3 nonUniformScale,
            bool hasScale,
            string parentName)
        {
            return new CoreAiWorldCommandEnvelope
            {
                action = "change",
                targetName = targetName ?? "",
                x = pos.x,
                y = pos.y,
                z = pos.z,
                fx = eulerAngles.x,
                fy = eulerAngles.y,
                fz = eulerAngles.z,
                floatValue = uniformScale,
                scaleX = nonUniformScale.x,
                scaleY = nonUniformScale.y,
                scaleZ = nonUniformScale.z,
                hasPosition = hasPosition,
                hasRotation = hasRotation,
                hasScale = hasScale,
                hasX = hasX,
                hasY = hasY,
                hasZ = hasZ,
                hasFx = hasFx,
                hasFy = hasFy,
                hasFz = hasFz,
                stringValue = parentName ?? ""
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

        public static CoreAiWorldCommandEnvelope Parent(string childName, string parentName)
        {
            return new CoreAiWorldCommandEnvelope
            {
                action = "parent",
                targetName = childName ?? "",
                stringValue = parentName ?? ""
            };
        }

        public static CoreAiWorldCommandEnvelope SetScale(string targetName, float uniformScale)
        {
            return SetScale(targetName, uniformScale, Vector3.zero);
        }

        public static CoreAiWorldCommandEnvelope SetScale(string targetName, float uniformScale,
            Vector3 nonUniformScale)
        {
            return new CoreAiWorldCommandEnvelope
            {
                action = "set_scale",
                targetName = targetName ?? "",
                floatValue = uniformScale,
                scaleX = nonUniformScale.x,
                scaleY = nonUniformScale.y,
                scaleZ = nonUniformScale.z
            };
        }

        public static CoreAiWorldCommandEnvelope SetColor(string targetName, string htmlColor)
        {
            return new CoreAiWorldCommandEnvelope
            {
                action = "set_color",
                targetName = targetName ?? "",
                stringValue = htmlColor ?? ""
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

        public static CoreAiWorldCommandEnvelope StopAnimation(string targetName)
        {
            return new CoreAiWorldCommandEnvelope
            {
                action = "stop_animation",
                targetName = targetName ?? ""
            };
        }

        public static CoreAiWorldCommandEnvelope SetVolume(string targetName, float volume)
        {
            return new CoreAiWorldCommandEnvelope
            {
                action = "set_volume",
                targetName = targetName ?? "",
                floatValue = volume
            };
        }

        public static CoreAiWorldCommandEnvelope HidePanel(string targetName)
        {
            return new CoreAiWorldCommandEnvelope
            {
                action = "hide_panel",
                targetName = targetName ?? ""
            };
        }

        public static CoreAiWorldCommandEnvelope SetVelocity(string targetName, Vector3 velocity)
        {
            return new CoreAiWorldCommandEnvelope
            {
                action = "set_velocity",
                targetName = targetName ?? "",
                fx = velocity.x,
                fy = velocity.y,
                fz = velocity.z
            };
        }
    }
}