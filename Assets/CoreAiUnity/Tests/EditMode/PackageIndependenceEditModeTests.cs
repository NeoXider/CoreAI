using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// CoreAI — packages and demos alike — depends on no other product of ours.
    /// </summary>
    /// <remarks>
    /// WHY this is a test and not a rule in a document: the reference that breaks it is one line in
    /// an asmdef, added in a minute by someone reaching for a component that already exists in a
    /// sibling repository. It compiles here, where both are checked out, and fails for the person
    /// evaluating CoreAI on its own — which is the only audience whose experience this protects.
    /// A demo scene is the worst place to find out, because a demo is what an evaluator opens first.
    /// </remarks>
    public sealed class PackageIndependenceEditModeTests
    {
        /// <summary>Assembly names owned by other Neoxider products, never by CoreAI.</summary>
        private static readonly string[] ForeignAssemblyPrefixes = { "Neo.", "Neoxider" };

        [Test]
        public void NoCoreAiAssemblyReferencesAnotherProduct()
        {
            List<string> problems = new();

            foreach (string asmdef in CoreAiAssemblyDefinitions())
            {
                string text = File.ReadAllText(asmdef);
                foreach (string reference in ReferencedAssemblies(text))
                {
                    if (ForeignAssemblyPrefixes.Any(prefix =>
                            reference.StartsWith(prefix, StringComparison.Ordinal)))
                    {
                        problems.Add(Relative(asmdef) + " references '" + reference
                                     + "', which CoreAI does not ship");
                    }
                }
            }

            Assert.IsEmpty(problems, string.Join("\n", problems));
        }

        [Test]
        public void NoCoreAiSourceUsesAnotherProductsNamespace()
        {
            List<string> problems = new();

            foreach (string source in CoreAiSourceFiles())
            {
                foreach (string line in File.ReadLines(source))
                {
                    string trimmed = line.TrimStart();
                    if (trimmed.StartsWith("using Neo.", StringComparison.Ordinal)
                        || trimmed.StartsWith("using Neoxider", StringComparison.Ordinal))
                    {
                        problems.Add(Relative(source) + ": " + trimmed);
                        break;
                    }
                }
            }

            Assert.IsEmpty(problems,
                "CoreAI code may not import another product's namespace:\n"
                + string.Join("\n", problems));
        }

        [Test]
        public void NoDemoSceneReferencesAnotherProductsComponent()
        {
            // A scene keeps the component's type name in its YAML, so a stale reference survives
            // long after the code that added it is gone — and shows up as a missing script only when
            // someone opens the scene without the other product installed.
            List<string> problems = new();

            foreach (string scene in Directory.GetFiles(
                         Path.Combine(Application.dataPath, "CoreAI.Demos"), "*.unity",
                         SearchOption.AllDirectories))
            {
                string text = File.ReadAllText(scene);
                foreach (string prefix in ForeignAssemblyPrefixes)
                {
                    if (text.Contains(prefix, StringComparison.Ordinal))
                    {
                        problems.Add(Relative(scene) + " still names '" + prefix + "'");
                    }
                }
            }

            Assert.IsEmpty(problems, string.Join("\n", problems));
        }

        [Test]
        public void TheDemoAssemblyExistsAndIsTheOneBeingChecked()
        {
            // The negative twin: every assertion above passes vacuously if the demos are deleted or
            // moved, and "no violations found" must not be reachable by removing what is constrained.
            string demos = Path.Combine(Application.dataPath, "CoreAI.Demos",
                "CoreAI.Demos.asmdef");

            Assert.IsTrue(File.Exists(demos), "Missing " + Relative(demos));
            Assert.IsNotEmpty(
                Directory.GetFiles(Path.Combine(Application.dataPath, "CoreAI.Demos"), "*.unity",
                    SearchOption.AllDirectories),
                "the demos ship scenes; a check over zero scenes proves nothing");
        }

        private static IEnumerable<string> CoreAiAssemblyDefinitions()
        {
            return CoreAiFolders().SelectMany(folder =>
                Directory.GetFiles(folder, "*.asmdef", SearchOption.AllDirectories));
        }

        private static IEnumerable<string> CoreAiSourceFiles()
        {
            return CoreAiFolders().SelectMany(folder =>
                Directory.GetFiles(folder, "*.cs", SearchOption.AllDirectories));
        }

        private static IEnumerable<string> CoreAiFolders()
        {
            return Directory.GetDirectories(Application.dataPath, "CoreAI*",
                    SearchOption.TopDirectoryOnly)
                .Concat(Directory.GetDirectories(Application.dataPath, "CoreAi*",
                    SearchOption.TopDirectoryOnly))
                .Distinct(StringComparer.OrdinalIgnoreCase);
        }

        private static IEnumerable<string> ReferencedAssemblies(string asmdefText)
        {
            int start = asmdefText.IndexOf("\"references\"", StringComparison.Ordinal);
            if (start < 0)
            {
                yield break;
            }

            int open = asmdefText.IndexOf('[', start);
            int close = asmdefText.IndexOf(']', open + 1);
            if (open < 0 || close < 0)
            {
                yield break;
            }

            foreach (string entry in asmdefText.Substring(open + 1, close - open - 1)
                         .Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                string trimmed = entry.Trim().Trim('"');
                if (trimmed.Length > 0)
                {
                    yield return trimmed;
                }
            }
        }

        private static string Relative(string path)
        {
            string root = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            string full = Path.GetFullPath(path);
            return full.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                ? full.Substring(root.Length).TrimStart('\\', '/')
                : path;
        }
    }
}
