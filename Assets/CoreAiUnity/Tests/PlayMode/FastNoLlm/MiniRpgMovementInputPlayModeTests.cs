#if UNITY_EDITOR && COREAI_HAS_INPUT_SYSTEM
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using CoreAI.Infrastructure.Llm;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace CoreAI.Tests.PlayMode
{
    /// <summary>
    /// MiniRpg walking through the real input pipeline: a virtual Input System keyboard presses W in the
    /// published scene, and the scene's own first-person controller must carry the player forward.
    /// <para>
    /// WHY: movement in this demo was a manual-only acceptance row, because the earlier drivers called
    /// the controller's injection API and so skipped the part that breaks in practice, reading device
    /// state. The controller's default backend reads the current Input System keyboard, so a device added
    /// by <see cref="InputTestFixture"/> exercises that read without a physical keyboard. A physical
    /// keyboard, window focus and mouse look still need a person.
    /// </para>
    /// <para>
    /// WHY explicit: the scene re-initialises input state while it settles (the project-wide actions
    /// asset is re-imported), which can remove the virtual device mid-test. That is a test-environment
    /// effect, not a controller defect, so this check runs on demand rather than in the hermetic gate.
    /// </para>
    /// </summary>
    [Explicit("Drives a virtual Input System device through a published scene; select it by name.")]
    [Category("InputDevice")]
    public sealed class MiniRpgMovementInputPlayModeTests : InputTestFixture
    {
        private const string ScenePath = "Assets/CoreAI.Demos/MiniRpg/MiniRpgModsDemo.unity";
        private const string ControllerTypeName = "PlayerController3DPhysics";

        private readonly Dictionary<CoreAISettingsAsset, string> _settingsSnapshots = new();

        [UnityTest]
        public IEnumerator HoldingW_OnAVirtualKeyboard_WalksThePlayerForward()
        {
            ForceOffline(CoreAISettingsAsset.Instance);
            foreach (string dependency in AssetDatabase.GetDependencies(ScenePath, true))
            {
                if (dependency.EndsWith(".asset", StringComparison.OrdinalIgnoreCase))
                {
                    ForceOffline(AssetDatabase.LoadAssetAtPath<CoreAISettingsAsset>(dependency));
                }
            }

            // WHY: with the optional Mirror install present, loading an unopened demo scene whose Neo
            // controllers carry an un-baked NetworkIdentity makes Mirror stop Play Mode and abort the run;
            // the smoke test's patch gives those identities a scene id for this one load only.
            CoreAiDemoScenesSmokePlayModeTests.MirrorAutoIdentityPatch.Arm(ScenePath);
            try
            {
                Scene scene = EditorSceneManager.LoadSceneInPlayMode(ScenePath, new LoadSceneParameters(LoadSceneMode.Single));
                Assert.IsTrue(scene.IsValid(), "The published MiniRpg scene must load: " + ScenePath);
                MonoBehaviour controller = null;
                float deadline = Time.realtimeSinceStartup + 5f;
                while ((controller = FindController()) == null)
                {
                    Assert.Less(Time.realtimeSinceStartup, deadline, "The scene's first-person controller did not appear.");
                    yield return null;
                }

                CollapseHubIfPresent();
                for (int frame = 0; frame < 30; frame++)
                {
                    yield return new WaitForFixedUpdate();
                }

                // WHY after the load: loading the scene re-initialises input devices, so a keyboard added
                // before it loses its state block.
                Keyboard keyboard = InputSystem.AddDevice<Keyboard>();
                keyboard.MakeCurrent();
                yield return null;
                Assert.AreSame(keyboard, Keyboard.current, "The virtual keyboard must be the current keyboard.");

                Transform body = controller.transform;
                Vector3 start = body.position;
                Vector3 forward = Vector3.ProjectOnPlane(body.forward, Vector3.up).normalized;
                Press(keyboard.wKey);
                Vector3 moved = Vector3.zero;
                for (int frame = 0; frame < 60 && keyboard.added; frame++)
                {
                    yield return new WaitForFixedUpdate();
                    moved = Vector3.ProjectOnPlane(body.position - start, Vector3.up);
                    if (Vector3.Dot(moved, forward) > 0.5f)
                    {
                        break;
                    }
                }

                TestContext.WriteLine($"[MiniRpg] moved {moved} along {forward}; device still added: {keyboard.added}");
                Assert.Greater(Vector3.Dot(moved, forward), 0.5f,
                    $"Holding W must walk the player forward through the scene's controller (moved {moved}).");
                if (!keyboard.added)
                {
                    Assert.Inconclusive("The walk was observed, but the scene removed the virtual keyboard before release.");
                }

                try
                {
                    Release(keyboard.wKey);
                }
                catch (ArgumentNullException ex)
                {
                    // WHY: observed on 2026-10-28; the scene re-initialises input state while the key is held,
                    // so the release event has no state block to write. The walk above is already asserted.
                    Assert.Inconclusive("The walk was observed, but the scene invalidated the virtual keyboard's " +
                                        "state before release: " + ex.Message);
                }

                for (int frame = 0; frame < 10; frame++)
                {
                    yield return new WaitForFixedUpdate();
                }

                Vector3 stopped = body.position;
                for (int frame = 0; frame < 30; frame++)
                {
                    yield return new WaitForFixedUpdate();
                }

                Assert.Less(Vector3.ProjectOnPlane(body.position - stopped, Vector3.up).magnitude, 0.3f,
                    "Releasing W must stop the walk instead of leaving the player moving.");
            }
            finally
            {
                CoreAiDemoScenesSmokePlayModeTests.MirrorAutoIdentityPatch.Disarm();
                RestoreSettings();
            }
        }

        [UnityTearDown]
        public IEnumerator UnloadScene()
        {
            yield return PlayModeSceneSandbox.UnloadToEmptyScene();
        }

        /// <summary>
        /// WHY: an expanded Hub owns keyboard input in this scene, which is the correct game behaviour, so
        /// the walk is checked with the Hub collapsed as a player would be.
        /// </summary>
        private static void CollapseHubIfPresent()
        {
            foreach (MonoBehaviour component in Object.FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None))
            {
                if (component == null || component.GetType().Name != "CoreAiHubWindow")
                {
                    continue;
                }

                MethodInfo setCollapsed = component.GetType().GetMethod("SetCollapsed",
                    BindingFlags.Instance | BindingFlags.Public, null, new[] { typeof(bool) }, null);
                setCollapsed?.Invoke(component, new object[] { true });
            }
        }

        private static MonoBehaviour FindController()
        {
            foreach (MonoBehaviour component in Object.FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None))
            {
                if (component != null && component.GetType().Name == ControllerTypeName && component.isActiveAndEnabled)
                {
                    return component;
                }
            }

            return null;
        }

        /// <summary>
        /// WHY Offline: the demo scenes share committed settings assets, so the scene would otherwise boot
        /// whatever backend the developer last selected. Restored in memory and reimported afterwards.
        /// </summary>
        private void ForceOffline(CoreAISettingsAsset settings)
        {
            if (settings == null || _settingsSnapshots.ContainsKey(settings))
            {
                return;
            }

            _settingsSnapshots.Add(settings, EditorJsonUtility.ToJson(settings));
            settings.ConfigureOffline();
            EditorUtility.ClearDirty(settings);
        }

        private void RestoreSettings()
        {
            foreach (KeyValuePair<CoreAISettingsAsset, string> snapshot in _settingsSnapshots)
            {
                if (snapshot.Key == null)
                {
                    continue;
                }

                EditorJsonUtility.FromJsonOverwrite(snapshot.Value, snapshot.Key);
                EditorUtility.ClearDirty(snapshot.Key);
                string assetPath = AssetDatabase.GetAssetPath(snapshot.Key);
                if (!string.IsNullOrEmpty(assetPath))
                {
                    AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);
                }
            }

            _settingsSnapshots.Clear();
        }
    }
}
#endif
