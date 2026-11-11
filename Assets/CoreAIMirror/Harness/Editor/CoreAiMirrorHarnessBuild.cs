using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CoreAI.Net.Mirror.Harness.Editor
{
    /// <summary>
    /// Builds the two-process harness player: an empty scene and the
    /// <c>COREAI_MIRROR_HARNESS</c> define, which only this build compiles in, so the harness never
    /// reaches a game's own player. Run from the menu or from the command line:
    /// <c>Unity.exe -batchmode -quit -projectPath &lt;project&gt; -executeMethod
    /// CoreAI.Net.Mirror.Harness.Editor.CoreAiMirrorHarnessBuild.BuildFromCommandLine
    /// [-coreaiHarnessBuildOut &lt;folder&gt;] [-coreaiHarnessBuildTarget Windows|Linux]</c>.
    /// </summary>
    public static class CoreAiMirrorHarnessBuild
    {
        /// <summary>The scripting define the harness assembly is constrained to.</summary>
        public const string HarnessDefine = "COREAI_MIRROR_HARNESS";

        /// <summary>The folder a build goes to when none is given, relative to the project.</summary>
        public const string DefaultOutput = "Builds/CoreAiMirrorHarness";

        private const string TemporaryScenePath = "Assets/CoreAiMirrorHarnessTemporary.unity";

        /// <summary>Builds a Windows 64-bit harness player into <see cref="DefaultOutput"/>.</summary>
        [MenuItem("Tools/CoreAI/Mirror/Build Two-Process Harness (Windows)")]
        public static void BuildWindowsFromMenu()
        {
            Build(BuildTarget.StandaloneWindows64, DefaultOutput);
        }

        /// <summary>The <c>-executeMethod</c> entry point; throws, and so fails the batch run, when the build fails.</summary>
        public static void BuildFromCommandLine()
        {
            string output = Argument("-coreaiHarnessBuildOut") ?? DefaultOutput;
            string target = Argument("-coreaiHarnessBuildTarget") ?? "Windows";
            Build(string.Equals(target, "Linux", StringComparison.OrdinalIgnoreCase)
                ? BuildTarget.StandaloneLinux64
                : BuildTarget.StandaloneWindows64, output);
        }

        private static void Build(BuildTarget target, string output)
        {
            string executable = target == BuildTarget.StandaloneWindows64 ? "CoreAiMirrorHarness.exe" : "CoreAiMirrorHarness";
            // WHY a scene saved for the build and deleted after it: a player needs one scene, and
            // the harness creates everything it needs at load, so the scene stays empty.
            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            try
            {
                if (!EditorSceneManager.SaveScene(scene, TemporaryScenePath))
                {
                    throw new BuildFailedException("the harness scene could not be saved to " + TemporaryScenePath);
                }

                EditorSceneManager.CloseScene(scene, true);
                BuildPlayerOptions options = new()
                {
                    scenes = new[] { TemporaryScenePath },
                    locationPathName = Path.Combine(output, executable),
                    target = target,
                    targetGroup = BuildPipeline.GetBuildTargetGroup(target),
                    options = BuildOptions.None,
                    extraScriptingDefines = new[] { HarnessDefine }
                };
                BuildReport report = BuildPipeline.BuildPlayer(options);
                if (report.summary.result != BuildResult.Succeeded)
                {
                    throw new BuildFailedException("the harness build " + report.summary.result + " with "
                                                   + report.summary.totalErrors + " errors");
                }

                Debug.Log("[CoreAI.Mirror.Harness] built " + options.locationPathName);
            }
            finally
            {
                AssetDatabase.DeleteAsset(TemporaryScenePath);
            }
        }

        private static string Argument(string name)
        {
            string[] args = Environment.GetCommandLineArgs();
            int index = Array.IndexOf(args, name);
            return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
        }
    }
}
