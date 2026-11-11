using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.ChatOverlay
{
    /// <summary>
    /// Dependency direction of the in-world chat: the command registry, feed, HUD and controller stay
    /// engine-free, and the Unity layer keeps editor APIs, IMGUI, threads, blocking waits and per-frame tree
    /// queries off its runtime path. File based, so it runs in Unity EditMode (working directory = project root)
    /// and in the portable suite (working directory = repository root).
    /// </summary>
    [TestFixture]
    public sealed class ChatOverlayArchitectureFitnessEditModeTests
    {
        private static string PortableRoot => Path.Combine(Environment.CurrentDirectory, "Assets", "CoreAI", "Runtime",
            "Core", "Features", "ChatOverlay");

        private static string UnityRoot => Path.Combine(Environment.CurrentDirectory, "Assets", "CoreAiUnity",
            "Runtime", "Source", "Features", "ChatOverlay");

        [Test]
        public void PortableCore_ReferencesNoEngineOrFrameworkType()
        {
            List<string> hits = Scan(PortableRoot, "using UnityEngine", "using UnityEditor", "using VContainer",
                "using Cysharp", "using MessagePipe", "UnityEngine.", "UniTask");

            CollectionAssert.IsEmpty(hits, "The chat overlay core must stay engine-free:\n" + string.Join("\n", hits));
        }

        [Test]
        public void UnityLayer_KeepsEditorImguiThreadsAndBlockingOffTheRuntimePath()
        {
            List<string> hits = Scan(UnityRoot, "UnityEditor", "OnGUI", "GUILayout", "IMGUIContainer", "Thread.",
                "new Thread", ".Wait(", ".Result;", ".Result)", "void Update(", "void LateUpdate(", ".Every(",
                "Q<", ".Query<", "FindObjectOfType", "FindAnyObjectByType", "FindObjectsByType", "ConfigureAwait");

            CollectionAssert.IsEmpty(hits, "The overlay runs in players and on WebGL, event-driven:\n" +
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
                    if (trimmed.StartsWith("//", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    foreach (string needle in needles)
                    {
                        if (line.Contains(needle))
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
