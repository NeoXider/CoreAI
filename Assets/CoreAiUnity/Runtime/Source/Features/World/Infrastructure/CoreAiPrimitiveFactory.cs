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

            return isEmpty ? new GameObject() : GameObject.CreatePrimitive(type);
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
