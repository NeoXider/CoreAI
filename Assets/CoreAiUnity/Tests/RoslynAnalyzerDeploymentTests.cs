using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace CoreAI.Tests
{
    public sealed class RoslynAnalyzerDeploymentTests
    {
        [Test]
        public void CoreAI_UnityAsyncAnalyzers_dll_exists_under_CoreAiUnity()
        {
            string path = Path.GetFullPath(Path.Combine(Application.dataPath, "CoreAiUnity", "RoslynAnalyzers",
                "CoreAI.UnityAsyncAnalyzers.dll"));
            Assert.That(File.Exists(path), Is.True,
                "Run Tools/build-analyzers.ps1 at CoreAI repo root, then commit Assets/CoreAiUnity/RoslynAnalyzers.");
        }
    }
}
