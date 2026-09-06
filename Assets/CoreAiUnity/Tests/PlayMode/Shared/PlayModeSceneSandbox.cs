using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CoreAI.Tests.PlayMode
{
    /// <summary>
    /// Returns the play session to a neutral empty scene after a test loaded real scenes Single-mode.
    /// A demo scene left loaded keeps its CoreAILifetimeScope alive for every later test in the run:
    /// <c>CoreAiBackend.Status</c> (and anything else that discovers the scope via
    /// <c>FindAnyObjectByType</c>) then reads backend state from an arbitrary leftover scope instead of
    /// the scope the current test built, and the leftover scene's controllers keep running.
    /// </summary>
    public static class PlayModeSceneSandbox
    {
        // UnityEngine.TestTools.TestRunner.PlaymodeTestsController.kPlaymodeTestControllerName; the
        // constant is internal to the Test Framework, so the name is mirrored here.
        private const string TestRunnerObjectName = "Code-based tests runner";

        private static int _counter;

        /// <summary>
        /// Creates a fresh empty active scene and unloads every other loaded scene. Call from
        /// <c>[UnityTearDown]</c> in any test that loads scenes with <c>LoadSceneMode.Single</c>.
        /// </summary>
        public static IEnumerator UnloadToEmptyScene()
        {
            Scene empty = SceneManager.CreateScene($"CoreAI_TestSandbox_{_counter++}");
            SceneManager.SetActiveScene(empty);

            for (int i = SceneManager.sceneCount - 1; i >= 0; i--)
            {
                Scene scene = SceneManager.GetSceneAt(i);
                if (scene == empty || !scene.isLoaded || HostsTestRunner(scene))
                {
                    continue;
                }

                yield return SceneManager.UnloadSceneAsync(scene);
            }

            // One extra frame so destroyed scopes finish disposing before the next test's SetUp runs.
            yield return null;
        }

        /// <summary>
        /// True when the scene holds the Test Framework's own runner object.
        /// </summary>
        /// <remarks>
        /// WHY this guard exists: the runner lives in the bootstrap scene the Test Framework creates, and
        /// it carries <c>HideFlags.DontSave</c>, so a Single-mode scene load moves it out of harm's way but
        /// <c>UnloadSceneAsync</c> on its own scene destroys it. A test that loads scenes Single-mode never
        /// hits that; a test that FAILS before its first load does, and the whole run then hangs with no
        /// results file — a failing assertion turned into a dead batchmode editor. Skipping the runner's
        /// scene costs nothing (it holds no demo content) and keeps a failure a failure.
        /// </remarks>
        private static bool HostsTestRunner(Scene scene)
        {
            GameObject[] roots = scene.GetRootGameObjects();
            for (int i = 0; i < roots.Length; i++)
            {
                if (roots[i] != null && roots[i].name == TestRunnerObjectName)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
