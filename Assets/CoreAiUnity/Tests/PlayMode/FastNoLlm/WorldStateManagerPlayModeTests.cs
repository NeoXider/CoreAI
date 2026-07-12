using System.Collections;
using System.IO;
using CoreAI.Infrastructure;
using CoreAI.Infrastructure.Logging;
using CoreAI.Infrastructure.World;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace CoreAI.Tests.PlayMode
{
    /// <summary>
    /// Exercises WorldStateManager save/load/reset, including the optional-colour semantics:
    /// only objects with an explicit MaterialPropertyBlock colour persist it; prefabs and primitives
    /// without one reload with their original material.
    /// </summary>
    public sealed class WorldStateManagerPlayModeTests
    {
        private string _saveFilePath;
        private string _backupPath;

        private sealed class StubPrefabRegistry : ICoreAiPrefabRegistry
        {
            private readonly GameObject _prefab;
            private readonly string _key;

            public StubPrefabRegistry(GameObject prefab, string key)
            {
                _prefab = prefab;
                _key = key;
            }

            public bool TryResolve(string keyOrName, out GameObject prefab)
            {
                if (string.Equals(keyOrName, _key, System.StringComparison.Ordinal))
                {
                    prefab = _prefab;
                    return true;
                }

                prefab = null;
                return false;
            }
        }

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            _saveFilePath = Path.Combine(
                Application.persistentDataPath,
                CoreAiPersistentPaths.RootFolderName,
                CoreAiPersistentPaths.WorldState,
                "world_state.json");
            _backupPath = _saveFilePath + ".test_backup";

            // Back up any real save so the test never clobbers a live world.
            if (File.Exists(_saveFilePath))
            {
                File.Copy(_saveFilePath, _backupPath, true);
                File.Delete(_saveFilePath);
            }

            DestroyAllWorldObjects();
            yield return null;
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            DestroyAllWorldObjects();

            if (File.Exists(_saveFilePath))
            {
                File.Delete(_saveFilePath);
            }

            if (File.Exists(_backupPath))
            {
                File.Copy(_backupPath, _saveFilePath, true);
                File.Delete(_backupPath);
            }

            yield return null;
        }

        private static void DestroyAllWorldObjects()
        {
            WorldObjectComponent[] tags =
                Object.FindObjectsByType<WorldObjectComponent>(
                    FindObjectsInactive.Include, FindObjectsSortMode.None);
            foreach (WorldObjectComponent t in tags)
            {
                if (t != null && t.gameObject != null)
                {
                    Object.DestroyImmediate(t.gameObject);
                }
            }
        }

        private static WorldObjectComponent Tag(GameObject go, string key)
        {
            WorldObjectComponent tag = go.AddComponent<WorldObjectComponent>();
            tag.persistentId = System.Guid.NewGuid().ToString();
            tag.prefabKey = key;
            return tag;
        }

        private static void SetColor(GameObject go, Color color)
        {
            MaterialPropertyBlock mpb = new();
            Renderer r = go.GetComponent<Renderer>();
            r.GetPropertyBlock(mpb);
            mpb.SetColor("_Color", color);
            r.SetPropertyBlock(mpb);
        }

        // GameObject.Find() skips inactive objects, so tests covering F-04A need to search
        // via the tracked component instead.
        private static GameObject FindInactiveByName(string name)
        {
            WorldObjectComponent[] tags = Object.FindObjectsByType<WorldObjectComponent>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);
            foreach (WorldObjectComponent t in tags)
            {
                if (t != null && t.gameObject != null && t.gameObject.name == name)
                {
                    return t.gameObject;
                }
            }

            return null;
        }

        [UnityTest]
        public IEnumerator Save_ThenLoad_RestoresTransformsAndExplicitColors()
        {
            // Coloured primitive.
            GameObject cube = CoreAiPrimitiveFactory.Create("cube");
            cube.name = "ColorCube";
            cube.transform.position = new Vector3(1, 2, 3);
            cube.transform.localScale = new Vector3(2, 2, 2);
            Tag(cube, "cube");
            SetColor(cube, Color.red);

            // Plain primitive — no explicit colour, must keep default material on reload.
            GameObject sphere = CoreAiPrimitiveFactory.Create("sphere");
            sphere.name = "PlainSphere";
            sphere.transform.position = new Vector3(-1, 0.5f, 4);
            Tag(sphere, "sphere");

            WorldStateManager manager = new(GameLoggerUnscopedFallback.Instance);
            manager.Save();
            Assert.IsTrue(File.Exists(_saveFilePath), "Save should write the world_state.json file.");

            Object.DestroyImmediate(cube);
            Object.DestroyImmediate(sphere);
            yield return null;

            Assert.IsTrue(manager.TryLoad(), "TryLoad should succeed.");
            yield return null;

            GameObject loadedCube = GameObject.Find("ColorCube");
            GameObject loadedSphere = GameObject.Find("PlainSphere");
            Assert.IsNotNull(loadedCube);
            Assert.IsNotNull(loadedSphere);

            Assert.AreEqual(new Vector3(1, 2, 3), loadedCube.transform.position);
            Assert.AreEqual(new Vector3(2, 2, 2), loadedCube.transform.localScale);

            MaterialPropertyBlock mpb = new();
            loadedCube.GetComponent<Renderer>().GetPropertyBlock(mpb);
            Assert.IsTrue(mpb.HasColor("_Color"), "Explicit colour should be restored via MPB.");
            Assert.AreEqual(Color.red, mpb.GetColor("_Color"));

            MaterialPropertyBlock sphereMpb = new();
            loadedSphere.GetComponent<Renderer>().GetPropertyBlock(sphereMpb);
            Assert.IsFalse(sphereMpb.HasColor("_Color"),
                "Plain primitive should NOT get an MPB colour override on reload.");
        }

        [UnityTest]
        public IEnumerator Save_ThenLoad_PrefabKeepsOriginalMaterialWithoutColorOverride()
        {
            GameObject prefab = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(
                "Assets/CoreAI.Demos/Shared/EnemyBasic.prefab");
            Assert.IsNotNull(prefab, "Demo enemy.basic prefab should exist.");

            GameObject enemy = Object.Instantiate(prefab, new Vector3(5, 0.5f, 0), Quaternion.identity);
            enemy.name = "TestEnemy";
            Tag(enemy, "enemy.basic");
            // Intentionally no SetColor — prefab material must be preserved.

            WorldStateManager manager = new(
                GameLoggerUnscopedFallback.Instance,
                new StubPrefabRegistry(prefab, "enemy.basic"));
            manager.Save();
            Object.DestroyImmediate(enemy);
            yield return null;

            Assert.IsTrue(manager.TryLoad(), "Prefab should reload from the registry.");
            yield return null;

            GameObject loaded = GameObject.Find("TestEnemy");
            Assert.IsNotNull(loaded, "Prefab instance should be re-created.");
            Assert.AreEqual("enemy.basic", loaded.GetComponent<WorldObjectComponent>().prefabKey);

            MaterialPropertyBlock mpb = new();
            loaded.GetComponent<Renderer>().GetPropertyBlock(mpb);
            Assert.IsFalse(mpb.HasColor("_Color"),
                "Prefab without explicit colour must not receive an MPB override.");
        }

        [UnityTest]
        public IEnumerator Save_ThenLoad_RestoresParentByIdWhenNamesAreAmbiguous()
        {
            // Untracked decoy sharing the real parent's name — GameObject.Find() could resolve to
            // either of the two, so parenting must never rely on name when the parent is tracked.
            GameObject decoy = new("SharedParentName");

            try
            {
                GameObject parentGo = CoreAiPrimitiveFactory.Create("cube");
                parentGo.name = "SharedParentName";
                Tag(parentGo, "cube");

                GameObject childGo = CoreAiPrimitiveFactory.Create("sphere");
                childGo.name = "ChildSphere";
                Tag(childGo, "sphere");
                childGo.transform.SetParent(parentGo.transform, true);

                WorldStateManager manager = new(GameLoggerUnscopedFallback.Instance);
                manager.Save();

                Object.DestroyImmediate(childGo);
                Object.DestroyImmediate(parentGo);
                yield return null;

                Assert.IsTrue(manager.TryLoad(), "TryLoad should succeed.");
                yield return null;

                GameObject loadedChild = GameObject.Find("ChildSphere");
                Assert.IsNotNull(loadedChild, "Child should be re-created.");
                Assert.IsNotNull(loadedChild.transform.parent, "Child should have a parent after load.");
                Assert.IsNotNull(loadedChild.transform.parent.GetComponent<WorldObjectComponent>(),
                    "Child must be re-parented to the tracked world object resolved by persistentId, " +
                    "not the untracked decoy sharing its name.");
                Assert.AreNotEqual(decoy.transform, loadedChild.transform.parent,
                    "Parent resolution must not fall back to the untracked decoy with the same name.");
            }
            finally
            {
                Object.DestroyImmediate(decoy);
            }
        }

        [UnityTest]
        public IEnumerator Save_EmptyWorld_RoundTrip_LoadsZeroObjects()
        {
            GameObject cube = CoreAiPrimitiveFactory.Create("cube");
            cube.name = "TempCube";
            Tag(cube, "cube");

            WorldStateManager manager = new(GameLoggerUnscopedFallback.Instance);
            manager.Save();
            Assert.IsTrue(File.Exists(_saveFilePath));

            Object.DestroyImmediate(cube);
            yield return null;

            // Save again with zero tracked objects — the stale snapshot (still listing "TempCube")
            // must be overwritten with a valid empty snapshot, not left untouched.
            manager.Save();
            yield return null;

            Assert.IsTrue(manager.TryLoad(), "TryLoad of an empty snapshot should still succeed (clean slate).");
            yield return null;

            Assert.IsNull(GameObject.Find("TempCube"),
                "Deleted object must not reappear after loading the empty snapshot.");
            int count = Object.FindObjectsByType<WorldObjectComponent>(FindObjectsSortMode.None).Length;
            Assert.AreEqual(0, count, "World should contain zero tracked objects after loading an empty snapshot.");
        }

        [UnityTest]
        public IEnumerator Save_MissingPrefab_RetainsObjectUntilPrefabReturns()
        {
            GameObject prefab = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(
                "Assets/CoreAI.Demos/Shared/EnemyBasic.prefab");
            Assert.IsNotNull(prefab, "Demo enemy.basic prefab should exist.");

            GameObject enemy = Object.Instantiate(prefab, new Vector3(2, 0, 0), Quaternion.identity);
            enemy.name = "RetainedEnemy";
            Tag(enemy, "enemy.basic");

            StubPrefabRegistry withPrefab = new(prefab, "enemy.basic");
            WorldStateManager manager = new(GameLoggerUnscopedFallback.Instance, withPrefab);
            manager.Save();
            Object.DestroyImmediate(enemy);
            yield return null;

            // Simulate the prefab becoming unavailable (registry never resolves it) — load must
            // fail to spawn it but retain it in memory instead of dropping it.
            WorldStateManager managerNoPrefab = new(
                GameLoggerUnscopedFallback.Instance,
                new StubPrefabRegistry(null, "no-such-key"));
            Assert.IsTrue(managerNoPrefab.TryLoad(), "TryLoad should still succeed even if a prefab is unresolved.");
            yield return null;

            Assert.IsNull(GameObject.Find("RetainedEnemy"), "Object should not spawn while its prefab is unresolved.");

            // Save while the prefab is still missing — the retained entry must survive in the file.
            managerNoPrefab.Save();
            yield return null;

            // Prefab becomes available again; loading should now restore the retained object.
            WorldStateManager managerPrefabRestored = new(GameLoggerUnscopedFallback.Instance, withPrefab);
            Assert.IsTrue(managerPrefabRestored.TryLoad(),
                "TryLoad should succeed once the prefab is available again.");
            yield return null;

            GameObject restored = GameObject.Find("RetainedEnemy");
            Assert.IsNotNull(restored,
                "Object retained through the missing-prefab window must be restored once the prefab returns.");
        }

        [UnityTest]
        public IEnumerator Save_ChildOfUnresolvedParent_KeepsParentLinkUntilPrefabReturns()
        {
            GameObject prefab = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(
                "Assets/CoreAI.Demos/Shared/EnemyBasic.prefab");
            Assert.IsNotNull(prefab, "Demo enemy.basic prefab should exist.");

            GameObject parentGo = Object.Instantiate(prefab, new Vector3(1, 0, 0), Quaternion.identity);
            parentGo.name = "LinkParent";
            WorldObjectComponent parentTag = Tag(parentGo, "enemy.basic");
            string parentId = parentTag.persistentId;

            GameObject childGo = CoreAiPrimitiveFactory.Create("cube");
            childGo.name = "LinkChild";
            Tag(childGo, "cube");
            childGo.transform.SetParent(parentGo.transform, true);

            StubPrefabRegistry withPrefab = new(prefab, "enemy.basic");
            WorldStateManager manager = new(GameLoggerUnscopedFallback.Instance, withPrefab);
            manager.Save();
            Object.DestroyImmediate(childGo);
            Object.DestroyImmediate(parentGo);
            yield return null;

            // WHY: With the parent's prefab unresolvable the child (a primitive) still spawns, but at
            // the scene root — exactly the state a periodic auto-save would then snapshot.
            WorldStateManager managerNoPrefab = new(
                GameLoggerUnscopedFallback.Instance,
                new StubPrefabRegistry(null, "no-such-key"));
            Assert.IsTrue(managerNoPrefab.TryLoad(), "TryLoad should succeed with the parent unresolved.");
            yield return null;

            GameObject orphan = GameObject.Find("LinkChild");
            Assert.IsNotNull(orphan, "Child should spawn even while its parent is unresolved.");
            Assert.IsNull(orphan.transform.parent, "Child sits at the scene root while the parent is unresolved.");

            // WHY: This Save() is the regression trigger — before the fix it wrote parent="" from the
            // live root transform and orphaned the child forever.
            managerNoPrefab.Save();
            yield return null;

            string json = File.ReadAllText(_saveFilePath);
            StringAssert.Contains($"\"parent\": \"{parentId}\"", json,
                "A save taken while the parent is unresolved must keep the child's intended parent id.");

            WorldStateManager managerRestored = new(GameLoggerUnscopedFallback.Instance, withPrefab);
            Assert.IsTrue(managerRestored.TryLoad(), "TryLoad should succeed once the parent prefab returns.");
            yield return null;

            GameObject restoredChild = GameObject.Find("LinkChild");
            Assert.IsNotNull(restoredChild, "Child must be restored after the parent prefab returns.");
            Assert.IsNotNull(restoredChild.transform.parent,
                "Child must be reattached once the parent's prefab resolves again.");
            WorldObjectComponent restoredParentTag =
                restoredChild.transform.parent.GetComponent<WorldObjectComponent>();
            Assert.IsNotNull(restoredParentTag, "Restored parent must be the tracked world object.");
            Assert.AreEqual(parentId, restoredParentTag.persistentId,
                "Child must be reattached to its original parent, not orphaned by the interim save.");
        }

        [UnityTest]
        public IEnumerator Reset_DeletesSaveFileAndDestroysObjects()
        {
            GameObject cube = CoreAiPrimitiveFactory.Create("cube");
            cube.name = "ResetCube";
            Tag(cube, "cube");

            WorldStateManager manager = new(GameLoggerUnscopedFallback.Instance);
            manager.Save();
            Assert.IsTrue(File.Exists(_saveFilePath));

            manager.Reset();
            yield return null;
            Assert.IsFalse(File.Exists(_saveFilePath), "Reset must delete the save file.");
            Assert.IsNull(GameObject.Find("ResetCube"), "Reset must destroy tracked objects.");
            Assert.IsFalse(manager.HasSavedState);

            yield return null;
        }

        [UnityTest]
        public IEnumerator Save_ThenLoad_PreservesInactiveObjectAcrossMultipleRoundTrips()
        {
            GameObject cube = CoreAiPrimitiveFactory.Create("cube");
            cube.name = "InactiveCube";
            Tag(cube, "cube");
            cube.SetActive(false);

            WorldStateManager manager = new(GameLoggerUnscopedFallback.Instance);
            manager.Save();
            Object.DestroyImmediate(cube);
            yield return null;

            Assert.IsTrue(manager.TryLoad(), "TryLoad should succeed.");
            yield return null;

            GameObject loaded = FindInactiveByName("InactiveCube");
            Assert.IsNotNull(loaded, "Inactive object must be restored by load.");
            Assert.IsFalse(loaded.activeSelf, "Restored object must keep its saved inactive state.");

            // F-04A: an inactive object must not be dropped by a subsequent Save().
            manager.Save();
            yield return null;

            Assert.IsTrue(manager.TryLoad(), "Second TryLoad should succeed.");
            yield return null;

            GameObject loadedAgain = FindInactiveByName("InactiveCube");
            Assert.IsNotNull(loadedAgain, "Inactive object must still be present after a second save/load round-trip.");
            Assert.IsFalse(loadedAgain.activeSelf, "Object must remain inactive after the second round-trip.");
        }

        [UnityTest]
        public IEnumerator Load_CleanSlate_DestroysPreExistingInactiveTrackedObject()
        {
            GameObject cube = CoreAiPrimitiveFactory.Create("cube");
            cube.name = "SnapshotCube";
            WorldObjectComponent cubeTag = Tag(cube, "cube");
            string sharedId = cubeTag.persistentId;

            WorldStateManager manager = new(GameLoggerUnscopedFallback.Instance);
            manager.Save();
            Object.DestroyImmediate(cube);
            yield return null;

            // Simulate a stale INACTIVE tracked object left behind in the scene, sharing the
            // snapshot's persistentId (e.g. a survivor of a previously buggy load). Clean-slate
            // must destroy it too, not just active objects, or the reloaded scene ends up with a
            // duplicate persistentId.
            GameObject stale = CoreAiPrimitiveFactory.Create("cube");
            stale.name = "StaleLeftover";
            WorldObjectComponent staleTag = stale.AddComponent<WorldObjectComponent>();
            staleTag.persistentId = sharedId;
            staleTag.prefabKey = "cube";
            stale.SetActive(false);
            yield return null;

            Assert.IsTrue(manager.TryLoad(), "TryLoad should succeed.");
            yield return null;

            WorldObjectComponent[] afterLoad = Object.FindObjectsByType<WorldObjectComponent>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);
            int matching = 0;
            foreach (WorldObjectComponent t in afterLoad)
            {
                if (t != null && t.persistentId == sharedId)
                {
                    matching++;
                }
            }

            Assert.AreEqual(1, matching,
                "Clean-slate load must destroy the stale inactive object, leaving exactly one " +
                "object with the shared persistentId.");
        }

        [UnityTest]
        public IEnumerator Reset_ClearsUnresolvedObjects_SoTheyDoNotReturnAfterReSave()
        {
            GameObject prefab = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(
                "Assets/CoreAI.Demos/Shared/EnemyBasic.prefab");
            Assert.IsNotNull(prefab, "Demo enemy.basic prefab should exist.");

            GameObject enemy = Object.Instantiate(prefab, new Vector3(3, 0, 0), Quaternion.identity);
            enemy.name = "UnresolvedThenReset";
            Tag(enemy, "enemy.basic");

            StubPrefabRegistry withPrefab = new(prefab, "enemy.basic");
            WorldStateManager manager = new(GameLoggerUnscopedFallback.Instance, withPrefab);
            manager.Save();
            Object.DestroyImmediate(enemy);
            yield return null;

            // Load while the prefab is unavailable — the object is retained in memory as unresolved.
            WorldStateManager managerNoPrefab = new(
                GameLoggerUnscopedFallback.Instance,
                new StubPrefabRegistry(null, "no-such-key"));
            Assert.IsTrue(managerNoPrefab.TryLoad(), "TryLoad should succeed even with an unresolved prefab.");
            yield return null;
            Assert.IsNull(GameObject.Find("UnresolvedThenReset"),
                "Object should not spawn while its prefab is unresolved.");

            // F-04B: Reset must be truly final — it must discard the retained-unresolved list, not
            // just delete the save file.
            managerNoPrefab.Reset();
            yield return null;

            managerNoPrefab.Save();
            yield return null;

            // Prefab becomes available again — the object must NOT resurrect since Reset discarded it.
            WorldStateManager managerPrefabRestored = new(GameLoggerUnscopedFallback.Instance, withPrefab);
            Assert.IsTrue(managerPrefabRestored.TryLoad(), "TryLoad should succeed after reset+re-save.");
            yield return null;

            Assert.IsNull(GameObject.Find("UnresolvedThenReset"),
                "Object discarded by Reset must not come back after the prefab becomes available again.");
        }
    }
}
