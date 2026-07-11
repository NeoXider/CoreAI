using System;
using System.Collections.Generic;
using CoreAI.Infrastructure.World;
using CoreAI.Sandbox.LuaCs;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CoreAI.Ai.LuaCs
{
    /// <summary>
    /// Lua-CSharp counterpart of <see cref="CoreAI.Infrastructure.World.CoreAiWorldQueryLuaBindings"/>.
    /// </summary>
    public sealed class LuaCsWorldQueryBindings
    {
        public const int MaxFindResults = 100;

        private readonly ICoreAiPrefabRegistry _prefabRegistry;

        public LuaCsWorldQueryBindings(ICoreAiPrefabRegistry prefabRegistry = null)
        {
            _prefabRegistry = prefabRegistry;
        }

        public void Register(LuaCsApiRegistry registry, LuaCapabilities capabilities)
        {
            if ((capabilities & LuaCapabilities.Read) == 0)
            {
                return;
            }

            RegisterGameplayApis(registry);
        }

        public void RegisterGameplayApis(LuaCsApiRegistry registry)
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

                bool truncated = WorldQuerySceneWalker.CollectByName(
                    rootObjects, searchPattern, MaxFindResults, MatchesPattern, results);
                if (truncated)
                {
                    Debug.LogWarning(
                        $"coreai_world_find: visited-node budget ({WorldQuerySceneWalker.MaxVisitedNodes}) " +
                        "reached before the scene walk finished; results may be incomplete.");
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
                new Func<double, double, double, double, double, double, double, object>((ox, oy, oz, dx, dy, dz,
                    maxDistance) =>
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

        private static bool MatchesPattern(string objectName, string searchPattern)
        {
            return objectName.IndexOf(searchPattern, StringComparison.OrdinalIgnoreCase) >= 0;
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