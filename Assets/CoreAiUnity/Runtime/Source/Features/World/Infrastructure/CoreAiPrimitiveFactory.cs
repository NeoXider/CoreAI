using UnityEngine;

namespace CoreAI.Infrastructure.World
{
    /// <summary>
    /// Creates built-in Unity primitive GameObjects (and empties) directly from a string key, with no
    /// prefab or registry required. Shared by the native <c>world_command</c> spawn path and the
    /// benchmark harness so "spawn a cube/sphere/..." works out of the box. Engine-side only; no reflection.
    /// </summary>
    public static class CoreAiPrimitiveFactory
    {
        /// <summary>Human-readable list of accepted primitive keys, for tool descriptions and docs.</summary>
        public const string SupportedKeys = "cube, sphere, cylinder, capsule, plane, empty";

        /// <summary>True when <paramref name="key"/> names a built-in primitive or an empty object.</summary>
        public static bool IsPrimitiveKey(string key)
        {
            return TryNormalize(key, out _, out _);
        }

        /// <summary>
        /// Maps a primitive key to its <see cref="PrimitiveType"/>. Returns <c>false</c> for the "empty" key
        /// (which has no mesh) or any unrecognized key. Lets callers pick a shape without instantiating.
        /// </summary>
        public static bool TryGetPrimitiveType(string key, out PrimitiveType type)
        {
            if (TryNormalize(key, out bool isEmpty, out type) && !isEmpty)
            {
                return true;
            }

            type = PrimitiveType.Cube;
            return false;
        }

        /// <summary>
        /// Creates a primitive (or empty) GameObject for <paramref name="key"/>, or returns <c>null</c> when
        /// the key is not a recognized primitive. The caller names and positions the returned object.
        /// </summary>
        public static GameObject Create(string key)
        {
            if (!TryNormalize(key, out bool isEmpty, out PrimitiveType type))
            {
                return null;
            }

            if (isEmpty)
            {
                return new GameObject();
            }

            GameObject go = GameObject.CreatePrimitive(type);
            EnsureRenderPipelineCompatibleMaterial(go);
            return go;
        }

        private static Material _runtimeDefaultMaterial;

        /// <summary>
        /// Replaces the built-in Default-Material on a freshly created primitive when a scriptable
        /// render pipeline is active. <see cref="GameObject.CreatePrimitive(PrimitiveType)"/> assigns
        /// the built-in Standard material, which URP/HDRP do not support - in a player build the
        /// object renders solid magenta (Hidden/InternalErrorShader). Safe to call on any object;
        /// no-op without a renderer, without an SRP, or when the current shader is supported.
        /// </summary>
        public static void EnsureRenderPipelineCompatibleMaterial(GameObject go)
        {
            if (go == null || UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline == null)
            {
                return;
            }

            Renderer renderer = go.GetComponent<Renderer>();
            if (renderer == null)
            {
                return;
            }

            Material current = renderer.sharedMaterial;
            if (current != null && current.shader != null && current.shader.isSupported &&
                current.shader.name != "Standard")
            {
                return;
            }

            Material replacement = GetOrCreateRuntimeDefaultMaterial();
            if (replacement != null)
            {
                renderer.sharedMaterial = replacement;
            }
        }

        private static Material GetOrCreateRuntimeDefaultMaterial()
        {
            if (_runtimeDefaultMaterial != null)
            {
                return _runtimeDefaultMaterial;
            }

            // Shader.Find only resolves shaders included in the build; URP Lit is referenced by any
            // URP scene material, Simple Lit / HDRP Lit are fallbacks for stripped-down setups.
            Shader shader = Shader.Find("Universal Render Pipeline/Lit")
                            ?? Shader.Find("Universal Render Pipeline/Simple Lit")
                            ?? Shader.Find("HDRP/Lit");
            if (shader == null)
            {
                return null;
            }

            _runtimeDefaultMaterial = new Material(shader);
            return _runtimeDefaultMaterial;
        }

        private static bool TryNormalize(string key, out bool isEmpty, out PrimitiveType type)
        {
            isEmpty = false;
            type = PrimitiveType.Cube;
            if (string.IsNullOrWhiteSpace(key))
            {
                return false;
            }

            switch (key.Trim().ToLowerInvariant())
            {
                case "cube":
                    type = PrimitiveType.Cube;
                    return true;
                case "sphere":
                    type = PrimitiveType.Sphere;
                    return true;
                case "cylinder":
                    type = PrimitiveType.Cylinder;
                    return true;
                case "capsule":
                    type = PrimitiveType.Capsule;
                    return true;
                case "plane":
                    type = PrimitiveType.Plane;
                    return true;
                case "empty":
                    isEmpty = true;
                    return true;
                default:
                    return false;
            }
        }
    }
}