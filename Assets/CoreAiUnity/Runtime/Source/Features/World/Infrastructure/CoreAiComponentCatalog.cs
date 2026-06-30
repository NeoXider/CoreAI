using System;
using System.Collections.Generic;
using UnityEngine;

namespace CoreAI.Infrastructure.World
{
    /// <summary>Curated component catalog for component commands. This catalog intentionally avoids reflection.</summary>
    public static class CoreAiComponentCatalog
    {
        public const string SupportedTypes =
            "rigidbody, rigidbody2d, boxcollider, spherecollider, capsulecollider, meshcollider, light, " +
            "audiosource, camera, linerenderer, trailrenderer, textmesh, meshrenderer, particlesystem";

        private static readonly Dictionary<string, Entry> Entries = BuildEntries();

        public sealed class Entry
        {
            public Func<GameObject, Component> Get;
            public Func<GameObject, Component> Add;
            public Dictionary<string, Action<Component, CoreAiComponentCommandEnvelope>> Setters;
        }

        public static bool TryGet(string typeName, out Entry entry)
        {
            string key = Normalize(typeName);
            return Entries.TryGetValue(key, out entry);
        }

        private static Dictionary<string, Entry> BuildEntries()
        {
            Dictionary<string, Entry> entries = new(StringComparer.Ordinal);

            Register<Rigidbody>(entries, "rigidbody", RigidbodySetters(), "rb");
            Register<Rigidbody2D>(entries, "rigidbody2d", Rigidbody2DSetters(), "rb2d");
            Register<BoxCollider>(entries, "boxcollider", BoxColliderSetters(), "box", "box collider");
            Register<SphereCollider>(entries, "spherecollider", SphereColliderSetters(), "sphere", "sphere collider");
            Register<CapsuleCollider>(entries, "capsulecollider", CapsuleColliderSetters(), "capsule",
                "capsule collider");
            Register<MeshCollider>(entries, "meshcollider", MeshColliderSetters(), "mesh collider");
            Register<Light>(entries, "light", LightSetters());
            Register<AudioSource>(entries, "audiosource", AudioSourceSetters(), "audio", "audio source");
            Register<Camera>(entries, "camera", CameraSetters(), "cam");
            Register<LineRenderer>(entries, "linerenderer", LineRendererSetters(), "line", "line renderer");
            Register<TrailRenderer>(entries, "trailrenderer", TrailRendererSetters(), "trail", "trail renderer");
            Register<TextMesh>(entries, "textmesh", TextMeshSetters(), "text", "text mesh");
            Register<MeshRenderer>(entries, "meshrenderer",
                new Dictionary<string, Action<Component, CoreAiComponentCommandEnvelope>>(StringComparer.Ordinal),
                "renderer", "mesh renderer");
            Register<ParticleSystem>(entries, "particlesystem",
                new Dictionary<string, Action<Component, CoreAiComponentCommandEnvelope>>(StringComparer.Ordinal),
                "particles", "particle system");

            return entries;
        }

        private static void Register<T>(
            Dictionary<string, Entry> entries,
            string key,
            Dictionary<string, Action<Component, CoreAiComponentCommandEnvelope>> setters,
            params string[] aliases)
            where T : Component
        {
            Entry entry = new()
            {
                Get = go => go.GetComponent<T>(),
                Add = go => go.AddComponent<T>(),
                Setters = setters
            };

            entries[Normalize(key)] = entry;
            foreach (string alias in aliases)
            {
                entries[Normalize(alias)] = entry;
            }
        }

        private static Dictionary<string, Action<Component, CoreAiComponentCommandEnvelope>> RigidbodySetters()
        {
            Dictionary<string, Action<Component, CoreAiComponentCommandEnvelope>> setters = NewSetters();
            setters["mass"] = (component, env) => ((Rigidbody)component).mass = env.floatValue;
            setters["usegravity"] = (component, env) => ((Rigidbody)component).useGravity = env.boolValue != 0;
            setters["iskinematic"] = (component, env) => ((Rigidbody)component).isKinematic = env.boolValue != 0;
            setters["drag"] = (component, env) => ((Rigidbody)component).linearDamping = env.floatValue;
            setters["lineardamping"] = (component, env) => ((Rigidbody)component).linearDamping = env.floatValue;
            setters["angulardrag"] = (component, env) => ((Rigidbody)component).angularDamping = env.floatValue;
            setters["angulardamping"] = (component, env) => ((Rigidbody)component).angularDamping = env.floatValue;
            return setters;
        }

        private static Dictionary<string, Action<Component, CoreAiComponentCommandEnvelope>> Rigidbody2DSetters()
        {
            Dictionary<string, Action<Component, CoreAiComponentCommandEnvelope>> setters = NewSetters();
            setters["mass"] = (component, env) => ((Rigidbody2D)component).mass = env.floatValue;
            setters["gravityscale"] = (component, env) => ((Rigidbody2D)component).gravityScale = env.floatValue;
            return setters;
        }

        private static Dictionary<string, Action<Component, CoreAiComponentCommandEnvelope>> BoxColliderSetters()
        {
            Dictionary<string, Action<Component, CoreAiComponentCommandEnvelope>> setters = NewSetters();
            setters["istrigger"] = (component, env) => ((BoxCollider)component).isTrigger = env.boolValue != 0;
            setters["size"] = (component, env) => ((BoxCollider)component).size = new Vector3(env.x, env.y, env.z);
            setters["center"] = (component, env) => ((BoxCollider)component).center = new Vector3(env.x, env.y, env.z);
            return setters;
        }

        private static Dictionary<string, Action<Component, CoreAiComponentCommandEnvelope>> SphereColliderSetters()
        {
            Dictionary<string, Action<Component, CoreAiComponentCommandEnvelope>> setters = NewSetters();
            setters["istrigger"] = (component, env) => ((SphereCollider)component).isTrigger = env.boolValue != 0;
            setters["radius"] = (component, env) => ((SphereCollider)component).radius = env.floatValue;
            return setters;
        }

        private static Dictionary<string, Action<Component, CoreAiComponentCommandEnvelope>> CapsuleColliderSetters()
        {
            Dictionary<string, Action<Component, CoreAiComponentCommandEnvelope>> setters = NewSetters();
            setters["istrigger"] = (component, env) => ((CapsuleCollider)component).isTrigger = env.boolValue != 0;
            setters["radius"] = (component, env) => ((CapsuleCollider)component).radius = env.floatValue;
            setters["height"] = (component, env) => ((CapsuleCollider)component).height = env.floatValue;
            return setters;
        }

        private static Dictionary<string, Action<Component, CoreAiComponentCommandEnvelope>> MeshColliderSetters()
        {
            Dictionary<string, Action<Component, CoreAiComponentCommandEnvelope>> setters = NewSetters();
            setters["convex"] = (component, env) => ((MeshCollider)component).convex = env.boolValue != 0;
            setters["istrigger"] = (component, env) => ((MeshCollider)component).isTrigger = env.boolValue != 0;
            return setters;
        }

        private static Dictionary<string, Action<Component, CoreAiComponentCommandEnvelope>> LightSetters()
        {
            Dictionary<string, Action<Component, CoreAiComponentCommandEnvelope>> setters = NewSetters();
            setters["type"] = (component, env) =>
            {
                if (Enum.TryParse(env.stringValue, true, out LightType type))
                {
                    ((Light)component).type = type;
                }
            };
            setters["intensity"] = (component, env) => ((Light)component).intensity = env.floatValue;
            setters["range"] = (component, env) => ((Light)component).range = env.floatValue;
            setters["color"] = (component, env) =>
            {
                if (ColorUtility.TryParseHtmlString(env.stringValue, out Color color))
                {
                    ((Light)component).color = color;
                }
            };
            setters["spotangle"] = (component, env) => ((Light)component).spotAngle = env.floatValue;
            return setters;
        }

        private static Dictionary<string, Action<Component, CoreAiComponentCommandEnvelope>> AudioSourceSetters()
        {
            Dictionary<string, Action<Component, CoreAiComponentCommandEnvelope>> setters = NewSetters();
            setters["volume"] = (component, env) => ((AudioSource)component).volume = Mathf.Clamp01(env.floatValue);
            setters["pitch"] = (component, env) => ((AudioSource)component).pitch = env.floatValue;
            setters["loop"] = (component, env) => ((AudioSource)component).loop = env.boolValue != 0;
            setters["playonawake"] = (component, env) => ((AudioSource)component).playOnAwake = env.boolValue != 0;
            setters["spatialblend"] =
                (component, env) => ((AudioSource)component).spatialBlend = Mathf.Clamp01(env.floatValue);
            return setters;
        }

        private static Dictionary<string, Action<Component, CoreAiComponentCommandEnvelope>> CameraSetters()
        {
            Dictionary<string, Action<Component, CoreAiComponentCommandEnvelope>> setters = NewSetters();
            setters["fieldofview"] = (component, env) => ((Camera)component).fieldOfView = env.floatValue;
            setters["orthographic"] = (component, env) => ((Camera)component).orthographic = env.boolValue != 0;
            setters["orthographicsize"] = (component, env) => ((Camera)component).orthographicSize = env.floatValue;
            return setters;
        }

        private static Dictionary<string, Action<Component, CoreAiComponentCommandEnvelope>> LineRendererSetters()
        {
            Dictionary<string, Action<Component, CoreAiComponentCommandEnvelope>> setters = NewSetters();
            setters["startwidth"] = (component, env) => ((LineRenderer)component).startWidth = env.floatValue;
            setters["endwidth"] = (component, env) => ((LineRenderer)component).endWidth = env.floatValue;
            setters["positioncount"] = (component, env) => ((LineRenderer)component).positionCount = (int)env.floatValue;
            setters["loop"] = (component, env) => ((LineRenderer)component).loop = env.boolValue != 0;
            return setters;
        }

        private static Dictionary<string, Action<Component, CoreAiComponentCommandEnvelope>> TrailRendererSetters()
        {
            Dictionary<string, Action<Component, CoreAiComponentCommandEnvelope>> setters = NewSetters();
            setters["time"] = (component, env) => ((TrailRenderer)component).time = env.floatValue;
            setters["startwidth"] = (component, env) => ((TrailRenderer)component).startWidth = env.floatValue;
            setters["endwidth"] = (component, env) => ((TrailRenderer)component).endWidth = env.floatValue;
            return setters;
        }

        private static Dictionary<string, Action<Component, CoreAiComponentCommandEnvelope>> TextMeshSetters()
        {
            Dictionary<string, Action<Component, CoreAiComponentCommandEnvelope>> setters = NewSetters();
            setters["text"] = (component, env) => ((TextMesh)component).text = env.stringValue;
            setters["fontsize"] = (component, env) => ((TextMesh)component).fontSize = (int)env.floatValue;
            setters["charactersize"] = (component, env) => ((TextMesh)component).characterSize = env.floatValue;
            return setters;
        }

        private static Dictionary<string, Action<Component, CoreAiComponentCommandEnvelope>> NewSetters()
        {
            return new Dictionary<string, Action<Component, CoreAiComponentCommandEnvelope>>(StringComparer.Ordinal);
        }

        private static string Normalize(string value)
        {
            return (value ?? "").Trim().ToLowerInvariant();
        }
    }
}
