using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.Npc
{
    /// <summary>
    /// Dependency direction of the NPC block: the director, arbiter, budgets and sinks stay engine-free and
    /// thread-free, and the Unity layer keeps editor APIs, IMGUI, threads, blocking waits and per-frame tree queries
    /// off its runtime path. File based, so it runs in Unity EditMode (working directory = project root) and in the
    /// portable suite (working directory = repository root).
    /// </summary>
    [TestFixture]
    public sealed class NpcArchitectureFitnessEditModeTests
    {
        private static string PortableRoot => Path.Combine(Environment.CurrentDirectory, "Assets", "CoreAI", "Runtime",
            "Core", "Features", "Npc");

        private static string UnityRoot => Path.Combine(Environment.CurrentDirectory, "Assets", "CoreAiUnity",
            "Runtime", "Source", "Features", "Npc");

        [Test]
        public void PortableBlock_ReferencesNoEngineFrameworkThreadOrBlockingWait()
        {
            List<string> hits = Scan(PortableRoot, "using UnityEngine", "using UnityEditor", "using VContainer",
                "using Cysharp", "using MessagePipe", "UnityEngine.", "UniTask", "new Thread", "Thread.Sleep",
                "Task.Run(", ".Wait(", ".Result;", ".Result)", "ConfigureAwait", "CancelAfter(");

            CollectionAssert.IsEmpty(hits, "The NPC block must stay engine-free and WebGL-safe:\n" +
                                           string.Join("\n", hits));
        }

        [Test]
        public void UnityLayer_KeepsEditorImguiThreadsAndBlockingOffTheRuntimePath()
        {
            List<string> hits = Scan(UnityRoot, "UnityEditor", "OnGUI", "GUILayout", "IMGUIContainer", "Thread.",
                "new Thread", ".Wait(", ".Result;", ".Result)", "void Update(", "void LateUpdate(", "Q<", ".Query<",
                "FindObjectOfType", "FindAnyObjectByType", "FindObjectsByType", "ConfigureAwait");

            CollectionAssert.IsEmpty(hits, "The NPC sinks run in players and on WebGL:\n" + string.Join("\n", hits));
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
                    string trimmed = lines[i].TrimStart();
                    if (trimmed.StartsWith("//", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    foreach (string needle in needles)
                    {
                        if (lines[i].Contains(needle))
                        {
                            hits.Add(Path.GetFileName(file) + ":" + (i + 1) + " " + needle);
                        }
                    }
                }
            }

            return hits;
        }
    }
}
