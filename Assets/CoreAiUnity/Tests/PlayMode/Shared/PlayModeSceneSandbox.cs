using System.Collections;
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
                if (scene == empty || !scene.isLoaded)
                {
                    continue;
                }

                yield return SceneManager.UnloadSceneAsync(scene);
            }

            // One extra frame so destroyed scopes finish disposing before the next test's SetUp runs.
            yield return null;
        }
    }
}
