using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// The packages compile without URP: URP types appear only in the optional <c>CoreAI.Look.Urp</c>
    /// assembly, its own tests, and the demos (which declare URP themselves), and that assembly compiles
    /// only when URP is installed.
    /// </summary>
    public sealed class LookUrpBoundaryEditModeTests
    {
        // WHY: assembled at runtime so this file does not match its own scan.
        private static readonly string UrpNamespace = "UnityEngine.Rendering." + "Universal";

        private static readonly string[] AllowedFolders =
        {
            "CoreAiUnity/Runtime/LookUrp/",
            "CoreAiUnity/Tests/EditMode/LookUrp/",
            "CoreAiUnity/Tests/PlayMode/LookUrp/",
            "CoreAI.Demos/"
        };

        [Test]
        public void UrpTypesStayInTheOptionalAssembly()
        {
            List<string> problems = new();
            int scanned = 0;
            foreach (string source in PackageSources())
            {
                scanned++;
                string relative = Relative(source);
                if (AllowedFolders.Any(folder => relative.StartsWith(folder, StringComparison.Ordinal)))
                {
                    continue;
                }

                if (File.ReadAllText(source).Contains(UrpNamespace, StringComparison.Ordinal))
                {
                    problems.Add(relative);
                }
            }

            Assert.Greater(scanned, 100, "a scan over no sources proves nothing");
            Assert.IsEmpty(problems, "URP types outside CoreAI.Look.Urp break a project without URP:\n" +
                                     string.Join("\n", problems));
        }

        [Test]
        public void OptionalAssembly_CompilesOnlyWithUrp()
        {
            string asmdef = File.ReadAllText(Path.Combine(Application.dataPath,
                "CoreAiUnity", "Runtime", "LookUrp", "CoreAI.Look.Urp.asmdef"));

            StringAssert.Contains("\"defineConstraints\"", asmdef);
            StringAssert.Contains("\"COREAI_HAS_URP\"", asmdef);
            StringAssert.Contains("\"com.unity.render-pipelines.universal\"", asmdef);
        }

        private static IEnumerable<string> PackageSources()
        {
            return Directory.GetDirectories(Application.dataPath, "CoreAI*", SearchOption.TopDirectoryOnly)
                .Concat(Directory.GetDirectories(Application.dataPath, "CoreAi*", SearchOption.TopDirectoryOnly))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .SelectMany(folder => Directory.GetFiles(folder, "*.cs", SearchOption.AllDirectories));
        }

        private static string Relative(string path)
        {
            return Path.GetRelativePath(Application.dataPath, path).Replace('\\', '/');
        }
    }
}
