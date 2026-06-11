#if COREAI_HAS_MOONSHARP && !COREAI_NO_LUA
using System;
using System.Collections.Generic;
using CoreAI.Ai;
using CoreAI.Sandbox;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CoreAI.Infrastructure.World
{
    /// <summary>
    /// Registers read-only world query Lua APIs. These APIs must run on the Unity main thread,
    /// using the same assumption as LuaTimeBindings. Results reflect applied state; commands
    /// published earlier in the same script may not be applied yet.
    /// </summary>
    public sealed class CoreAiWorldQueryLuaBindings : IGameLuaRuntimeBindings
    {
        /// <summary>Maximum object names returned by <c>coreai_world_find</c>.</summary>
        public const int MaxFindResults = 100;

        private readonly ICoreAiPrefabRegistry _prefabRegistry;

        /// <param name="prefabRegistry">Optional prefab registry used for read-only prefab key listing.</param>
        public CoreAiWorldQueryLuaBindings(ICoreAiPrefabRegistry prefabRegistry = null)
        {
            _prefabRegistry = prefabRegistry;
        }

        /// <inheritdoc />
        public void RegisterGameplayApis(LuaApiRegistry registry)
        {
            registry.Register("coreai_world_exists", new Func<string, bool>(name =>
            {
                string targetName = (name ?? "").Trim();
                return !string.IsNullOrEmpty(targetName) && GameObject.Find(targetName) != null;
            }));

            registry.Register("coreai_world_pos", new Func<string, object>(name =>
            {
                string targetName = (name ?? "").Trim();
                if (string.IsNullOrEmpty(targetName))
                {
                    return null;
                }

                GameObject target = GameObject.Find(targetName);
                if (target == null)
                {
                    return null;
                }

                Vector3 position = target.transform.position;
                return new Dictionary<string, object>
                {
                    { "x", (double)position.x },
                    { "y", (double)position.y },
                    { "z", (double)position.z }
                };
            }));

            registry.Register("coreai_world_find", new Func<string, List<object>>(pattern =>
            {
                string searchPattern = (pattern ?? "").Trim();
                GameObject[] rootObjects = SceneManager.GetActiveScene().GetRootGameObjects();
                List<object> results = new();

                for (int i = 0; i < rootObjects.Length; i++)
                {
                    CollectObjectsRecursive(rootObjects[i], searchPattern, results);
                    if (results.Count >= MaxFindResults)
                    {
                        break;
                    }
                }

                return results;
            }));

            registry.Register("coreai_world_list_prefabs", new Func<List<object>>(() =>
            {
                List<object> results = new();
                if (_prefabRegistry is not ICoreAiPrefabCatalog catalog)
                {
                    return results;
                }

                IReadOnlyList<string> keys = catalog.ListPrefabKeys();
                for (int i = 0; i < keys.Count; i++)
                {
                    results.Add(keys[i]);
                }

                return results;
            }));

            registry.Register("coreai_world_raycast",
                new Func<double, double, double, double, double, double, double, object>(
                    (ox, oy, oz, dx, dy, dz, maxDistance) =>
                    {
                        if (!IsFinite(ox) || !IsFinite(oy) || !IsFinite(oz) ||
                            !IsFinite(dx) || !IsFinite(dy) || !IsFinite(dz) ||
                            !IsFinite(maxDistance))
                        {
                            throw new ArgumentException("raycast: arguments must be finite.");
                        }

                        Vector3 direction = new(ToFiniteFloat(dx), ToFiniteFloat(dy), ToFiniteFloat(dz));
                        if (direction == Vector3.zero)
                        {
                            throw new ArgumentException("raycast: direction must be non-zero.");
                        }

                        Vector3 origin = new(ToFiniteFloat(ox), ToFiniteFloat(oy), ToFiniteFloat(oz));
                        float distance = (float)Math.Min(Math.Max(maxDistance, 0.0001d), 1000d);
                        if (!Physics.Raycast(origin, direction.normalized, out RaycastHit hit, distance))
                        {
                            return null;
                        }

                        Vector3 point = hit.point;
                        return new Dictionary<string, object>
                        {
                            { "name", hit.collider.gameObject.name },
                            { "x", (double)point.x },
                            { "y", (double)point.y },
                            { "z", (double)point.z },
                            { "distance", (double)hit.distance }
                        };
                    }));
        }

        private static void CollectObjectsRecursive(GameObject parent, string searchPattern, List<object> results)
        {
            if (parent == null || results.Count >= MaxFindResults)
            {
                return;
            }

            if (string.IsNullOrEmpty(searchPattern) ||
                parent.name.IndexOf(searchPattern, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                results.Add(parent.name);
            }

            if (results.Count >= MaxFindResults)
            {
                return;
            }

            for (int i = 0; i < parent.transform.childCount; i++)
            {
                CollectObjectsRecursive(parent.transform.GetChild(i).gameObject, searchPattern, results);
                if (results.Count >= MaxFindResults)
                {
                    return;
                }
            }
        }

        private static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }

        private static float ToFiniteFloat(double value)
        {
            float converted = (float)value;
            if (float.IsNaN(converted) || float.IsInfinity(converted))
            {
                throw new ArgumentException("raycast: arguments must be finite.");
            }

            return converted;
        }
    }
}
#endif
