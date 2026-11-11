using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace CoreAI.Tests.EditMode.RuntimeUi
{
    /// <summary>
    /// Dependency direction of the runtime UI feature: the parsers and the cascade stay engine-free, and
    /// the Unity host keeps editor APIs, IMGUI, threads, blocking waits and per-frame hooks off its
    /// runtime path (it must work the same in a built player and on WebGL).
    /// </summary>
    [TestFixture]
    public sealed class RuntimeUiArchitectureFitnessEditModeTests
    {
        private static readonly string PortableRoot =
            Path.Combine(Application.dataPath, "CoreAI", "Runtime", "Core", "Features", "RuntimeUi");

        private static readonly string UnityRoot =
            Path.Combine(Application.dataPath, "CoreAiUnity", "Runtime", "Source", "Features", "RuntimeUi");

        [Test]
        public void PortableCore_ReferencesNoEngineOrEditorType()
        {
            List<string> hits = Scan(PortableRoot, "using UnityEngine", "using UnityEditor", "using VContainer",
                "UnityEngine.Debug", "UnityEngine.Object", "UnityEngine.Mathf", "UnityEditor.AssetDatabase");

            CollectionAssert.IsEmpty(hits, "The UXML/USS parsers and the cascade must stay engine-free:\n" +
                                           string.Join("\n", hits));
        }

        [Test]
        public void UnityHost_KeepsEditorImguiThreadsAndPerFrameWorkOffTheRuntimePath()
        {
            List<string> hits = Scan(UnityRoot, "UnityEditor", "OnGUI", "GUILayout", "IMGUIContainer", "Thread",
                ".Wait(", ".Result", "void Update(", "void LateUpdate(", ".Every(", "Q<", ".Query<");

            CollectionAssert.IsEmpty(hits, "The runtime UI host runs in players and on WebGL, event-driven:\n" +
                                           string.Join("\n", hits));
        }

        private static List<string> Scan(string root, params string[] needles)
        {
            Assert.IsTrue(Directory.Exists(root), root + " is missing.");
            List<string> hits = new();
            foreach (string file in Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories))
            {
                string[] lines = File.ReadAllLines(file);
                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i];
                    string trimmed = line.TrimStart();
                    if (trimmed.StartsWith("///") || trimmed.StartsWith("//"))
                    {
                        continue;
                    }

                    for (int n = 0; n < needles.Length; n++)
                    {
                        if (line.Contains(needles[n]))
                        {
                            hits.Add(Path.GetFileName(file) + ":" + (i + 1) + " " + needles[n]);
                        }
                    }
                }
            }

            return hits;
        }
    }
}
