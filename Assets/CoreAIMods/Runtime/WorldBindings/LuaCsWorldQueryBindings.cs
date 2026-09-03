using System;
using System.Collections.Generic;
using CoreAI.Infrastructure.World;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Spatial;
using CoreAI.Sandbox.LuaCs;
using CoreAI.Scripting;
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

        public void Register(IScriptFunctionRegistry registry, LuaCapabilities capabilities)
        {
            if ((capabilities & LuaCapabilities.Read) == 0)
            {
                return;
            }

            RegisterGameplayApis(registry);
        }

        public void RegisterGameplayApis(IScriptFunctionRegistry registry)
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

                // WHY: Lua-facing positions are studs; the transform is metres.
                RbxVector3 studs = RbxSpace.FromUnity(target.transform.position);
                return new Dictionary<string, object>
                {
                    { "x", (double)studs.X },
                    { "y", (double)studs.Y },
                    { "z", (double)studs.Z }
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
                    CoreAI.Logging.Log.Instance.Warn(
                        $"coreai_world_find: visited-node budget ({WorldQuerySceneWalker.MaxVisitedNodes}) " +
                        "reached before the scene walk finished; results may be incomplete.",
                        CoreAI.Logging.LogTag.World);
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

                    // WHY: Lua-facing raycast is studs throughout: origin scales, direction
                    // mirrors without scaling, maxDistance is a studs length.
                    Vector3 origin = RbxSpace.ToUnity(new RbxVector3(
                        ToFiniteFloat(ox), ToFiniteFloat(oy), ToFiniteFloat(oz)));
                    Vector3 direction = RbxSpace.DirectionToUnity(new RbxVector3(
                        ToFiniteFloat(dx), ToFiniteFloat(dy), ToFiniteFloat(dz)));
                    if (direction == Vector3.zero)
                    {
                        throw new ArgumentException("raycast: direction must be non-zero.");
                    }

                    float distance = RbxSpace.LengthToUnity(
                        (float)Math.Min(Math.Max(maxDistance, 0.0001d), 1000d));
                    if (!Physics.Raycast(origin, direction.normalized, out RaycastHit hit, distance))
                    {
                        return null;
                    }

                    RbxVector3 point = RbxSpace.FromUnity(hit.point);
                    return new Dictionary<string, object>
                    {
                        { "name", hit.collider.gameObject.name },
                        { "x", (double)point.X },
                        { "y", (double)point.Y },
                        { "z", (double)point.Z },
                        { "distance", (double)RbxSpace.LengthFromUnity(hit.distance) }
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
