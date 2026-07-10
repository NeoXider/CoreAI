using System.Collections.Generic;
using System.Diagnostics;
using CoreAI.Ai.LuaCs;
using CoreAI.Infrastructure.World;
using NUnit.Framework;
using UnityEngine;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// Covers F-15: the shared world query scene walker must bound main-thread work with a
    /// visited-node budget instead of traversing an entire large/deep hierarchy on a no-match query.
    /// </summary>
    public sealed class WorldQuerySceneWalkerEditModeTests
    {
        private readonly List<GameObject> _created = new();

        [TearDown]
        public void TearDown()
        {
            // Destroy deepest children first and detach them before destruction. Destroying a
            // 5,000-level root recursively can overflow Unity's native destruction stack.
            for (int i = _created.Count - 1; i >= 0; i--)
            {
                if (_created[i] != null)
                {
                    _created[i].transform.SetParent(null, worldPositionStays: false);
                    Object.DestroyImmediate(_created[i]);
                }
            }

            _created.Clear();
        }

        [Test]
        public void CollectByName_WideHierarchyNoMatch_TruncatesAndCompletesFast()
        {
            const int childCount = 15_000;
            GameObject root = CreateObject("WideRoot");
            for (int i = 0; i < childCount; i++)
            {
                GameObject child = CreateObject("Leaf");
                child.transform.SetParent(root.transform, worldPositionStays: false);
            }

            List<object> results = new();
            Stopwatch stopwatch = Stopwatch.StartNew();

            bool truncated = WorldQuerySceneWalker.CollectByName(
                new[] { root },
                "NoSuchObjectNameAnywhere",
                LuaCsWorldQueryBindings.MaxFindResults,
                (name, pattern) => name.IndexOf(pattern, System.StringComparison.OrdinalIgnoreCase) >= 0,
                results);

            stopwatch.Stop();

            Assert.IsTrue(truncated,
                "A no-match query over more nodes than MaxVisitedNodes must report truncation.");
            Assert.AreEqual(0, results.Count);
            Assert.Less(stopwatch.ElapsedMilliseconds, 2000,
                "The visited-node budget should keep a no-match walk fast even over a wide hierarchy.");
        }

        [Test]
        public void CollectByName_DeepHierarchy_DoesNotOverflowStack()
        {
            const int depth = 5_000;
            GameObject root = CreateObject("DeepRoot");
            Transform current = root.transform;
            for (int i = 0; i < depth; i++)
            {
                GameObject child = CreateObject("DeepChild");
                child.transform.SetParent(current, worldPositionStays: false);
                current = child.transform;
            }

            List<object> results = new();

            Assert.DoesNotThrow(() => WorldQuerySceneWalker.CollectByName(
                new[] { root },
                "DeepChild",
                LuaCsWorldQueryBindings.MaxFindResults,
                (name, pattern) => name.IndexOf(pattern, System.StringComparison.OrdinalIgnoreCase) >= 0,
                results));

            Assert.AreEqual(LuaCsWorldQueryBindings.MaxFindResults, results.Count);
        }

        private GameObject CreateObject(string name)
        {
            GameObject go = new(name);
            _created.Add(go);
            return go;
        }
    }
}
