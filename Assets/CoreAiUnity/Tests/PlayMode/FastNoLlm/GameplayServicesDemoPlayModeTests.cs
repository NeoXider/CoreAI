#if UNITY_EDITOR
using System.Collections;
using CoreAI.Mods.Rbx.Binding;
using CoreAI.Mods.Rbx.Instances;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UI;

namespace CoreAI.Tests.PlayMode
{
    /// <summary>
    /// The MVP8 demo actually runs: the scene loads, the tour builds a world, and the panel says so.
    /// </summary>
    /// <remarks>
    /// WHY a demo needs its own PlayMode gate: "the scene opens" is what the shared smoke proves, and
    /// that is compatible with a demo whose buttons do nothing. This one presses the button a visitor
    /// would press and then looks at the world the Lua tour was supposed to build. A demo nobody
    /// exercises is a screenshot with a play icon on it.
    /// </remarks>
    public sealed class GameplayServicesDemoPlayModeTests
    {
        private const string ScenePath =
            "Assets/CoreAI.Demos/GameplayServices/GameplayServicesDemo.unity";

        [UnityTest]
        public IEnumerator RunningTheTour_BuildsTheWorldTheDemoPromises()
        {
            LogAssert.ignoreFailingMessages = true;
            EditorSceneManager.LoadSceneInPlayMode(ScenePath,
                new LoadSceneParameters(LoadSceneMode.Single));
            yield return null;
            yield return null;

            RbxWorldHost host = Object.FindFirstObjectByType<RbxWorldHost>();
            Assert.IsNotNull(host, "the demo scene must contain a world host");
            Button run = FindButton("Run tour");
            Assert.IsNotNull(run, "the demo must offer the button a visitor is told to press");

            run.onClick.Invoke();
            // The tour connects signals and plays a tween; a few frames let the scheduler run them.
            for (int frame = 0; frame < 8; frame++)
            {
                yield return null;
            }

            RbxInstance workspace = host.Game?.FindFirstChildOfClass("Workspace");
            Assert.IsNotNull(workspace);
            Assert.IsNotNull(workspace.FindFirstChild("Door"),
                "the tweened door is the first thing the tour builds");
            Assert.IsNotNull(workspace.FindFirstChild("KillBrick"),
                "the tagged kill brick must exist for the Touched demonstration");
            Assert.IsNotNull(workspace.FindFirstChild("Humanoid"),
                "the Humanoid is what the kill brick acts on");

            object status = workspace.GetAttribute("DemoStatus");
            Assert.IsInstanceOf<string>(status,
                "the tour reports through a workspace attribute, as a Roblox script would");
            Assert.IsNotEmpty((string)status);
        }

        [UnityTest]
        public IEnumerator Negative_TheDemoUsesNoImmediateModeGui()
        {
            // The owner's standing rule, and a practical one: IMGUI draws nothing in a build, so a
            // demo written on OnGUI works only on the machine that wrote it.
            EditorSceneManager.LoadSceneInPlayMode(ScenePath,
                new LoadSceneParameters(LoadSceneMode.Single));
            yield return null;

            Assert.IsNotNull(Object.FindFirstObjectByType<Canvas>(),
                "the demo draws through a real Canvas");
            foreach (MonoBehaviour behaviour in Object.FindObjectsByType<MonoBehaviour>(
                         FindObjectsSortMode.None))
            {
                if (behaviour == null)
                {
                    continue;
                }

                System.Type type = behaviour.GetType();
                if (!type.FullName!.StartsWith("CoreAI.Demos", System.StringComparison.Ordinal))
                {
                    continue;
                }

                Assert.IsNull(
                    type.GetMethod("OnGUI",
                        System.Reflection.BindingFlags.Instance
                        | System.Reflection.BindingFlags.NonPublic
                        | System.Reflection.BindingFlags.Public),
                    type.FullName + " draws with IMGUI, which renders nothing in a build");
            }
        }

        private static Button FindButton(string caption)
        {
            foreach (Button button in Object.FindObjectsByType<Button>(FindObjectsSortMode.None))
            {
                if (button.name.Contains(caption, System.StringComparison.OrdinalIgnoreCase))
                {
                    return button;
                }
            }

            return null;
        }
    }
}
#endif
