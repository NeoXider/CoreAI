using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// MVP11 gate N11.7, the half that runs everywhere: Mirror stays inside its own optional package.
    /// </summary>
    /// <remarks>
    /// WHY this is worth a repository-walking test: the failure it guards against is invisible until
    /// someone installs CoreAI WITHOUT Mirror. A single <c>using Mirror</c> that drifts into
    /// <c>CoreAI.Mods</c>, or an assembly that references Mirror without carrying the define
    /// constraint, breaks the solo build for every user who never asked for networking — and it breaks
    /// it at THEIR compile time, not ours, because this repository has Mirror installed. The rule is
    /// therefore checked against the files rather than against what compiles here.
    /// <para>
    /// The check reads the repository, not the loaded assemblies, deliberately: an assembly that fails
    /// to compile is absent from the domain, so a domain-based version of this test would go quiet
    /// exactly when it should shout.
    /// </para>
    /// </remarks>
    public sealed class MirrorPackagingBoundaryEditModeTests
    {
        /// <summary>The one folder allowed to know Mirror exists.</summary>
        private const string MirrorPackageFolder = "CoreAIMirror";

        [Test]
        public void OnlyTheMirrorPackageReferencesMirrorAssemblies()
        {
            List<string> problems = new();

            foreach (string asmdef in CoreAiAssemblyDefinitions())
            {
                string text = File.ReadAllText(asmdef);
                bool referencesMirror = ReferencedAssemblies(text)
                    .Any(reference => reference.Equals("Mirror", StringComparison.Ordinal)
                                      || reference.StartsWith("Mirror.", StringComparison.Ordinal));
                if (!referencesMirror)
                {
                    continue;
                }

                if (!IsInsideMirrorPackage(asmdef))
                {
                    problems.Add(Relative(asmdef)
                        + " references a Mirror assembly but lives outside Assets/" + MirrorPackageFolder
                        + "; a solo install would fail to compile it");
                    continue;
                }

                if (!text.Contains("\"MIRROR\"", StringComparison.Ordinal))
                {
                    problems.Add(Relative(asmdef)
                        + " references Mirror without defineConstraints MIRROR; Unity would try to "
                        + "compile it in a project that has no Mirror");
                }
            }

            Assert.IsEmpty(problems, string.Join("\n", problems));
        }

        [Test]
        public void NoSourceOutsideTheMirrorPackageUsesTheMirrorNamespace()
        {
            List<string> problems = new();

            foreach (string source in CoreAiSourceFiles())
            {
                if (IsInsideMirrorPackage(source))
                {
                    continue;
                }

                foreach (string line in File.ReadLines(source))
                {
                    string trimmed = line.TrimStart();
                    if (trimmed.StartsWith("using Mirror;", StringComparison.Ordinal)
                        || trimmed.StartsWith("using Mirror.", StringComparison.Ordinal))
                    {
                        problems.Add(Relative(source) + ": " + trimmed);
                        break;
                    }
                }
            }

            Assert.IsEmpty(problems,
                "Mirror may only be named inside Assets/" + MirrorPackageFolder + ":\n"
                + string.Join("\n", problems));
        }

        [Test]
        public void TheMirrorPackageExistsAndDeclaresItsPlaceInTheGraph()
        {
            // The negative twin of the two rules above: they would both pass vacuously if the package
            // were deleted or renamed, and "no violations found" must not be reachable by removing the
            // thing being constrained.
            string package = Path.Combine(PackagesRoot(), MirrorPackageFolder, "package.json");
            Assert.IsTrue(File.Exists(package), "Missing " + Relative(package));

            string manifest = File.ReadAllText(package);
            StringAssert.Contains("com.neoxider.coreaimirror", manifest);
            StringAssert.Contains("com.neoxider.coreai", manifest,
                "the transport package depends on the core it transports for");

            string[] assemblies = Directory
                .GetFiles(Path.Combine(PackagesRoot(), MirrorPackageFolder), "*.asmdef",
                    SearchOption.AllDirectories);
            Assert.IsNotEmpty(assemblies, "the Mirror package ships no assembly definition");
            foreach (string assembly in assemblies)
            {
                StringAssert.Contains("\"MIRROR\"", File.ReadAllText(assembly),
                    Relative(assembly) + " must be constrained to MIRROR");
            }
        }

        private static IEnumerable<string> CoreAiAssemblyDefinitions()
        {
            return CoreAiPackageFolders().SelectMany(folder =>
                Directory.GetFiles(folder, "*.asmdef", SearchOption.AllDirectories));
        }

        private static IEnumerable<string> CoreAiSourceFiles()
        {
            return CoreAiPackageFolders().SelectMany(folder =>
                Directory.GetFiles(folder, "*.cs", SearchOption.AllDirectories));
        }

        private static IEnumerable<string> CoreAiPackageFolders()
        {
            return Directory.GetDirectories(PackagesRoot(), "CoreAI*", SearchOption.TopDirectoryOnly)
                .Concat(Directory.GetDirectories(PackagesRoot(), "CoreAi*", SearchOption.TopDirectoryOnly))
                .Distinct(StringComparer.OrdinalIgnoreCase);
        }

        private static IEnumerable<string> ReferencedAssemblies(string asmdefText)
        {
            // A hand-rolled read of the "references" array: JsonUtility cannot deserialize a bare
            // string array field on Unity's own asmdef shape without a wrapper type, and pulling in a
            // JSON dependency for one array would be the heavier answer.
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

        private static bool IsInsideMirrorPackage(string path)
        {
            string relative = Relative(path).Replace('\\', '/');
            return relative.StartsWith("Assets/" + MirrorPackageFolder + "/", StringComparison.Ordinal);
        }

        private static string PackagesRoot()
        {
            return Application.dataPath;
        }

        private static string Relative(string path)
        {
            string root = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            return Path.GetFullPath(path).StartsWith(root, StringComparison.OrdinalIgnoreCase)
                ? Path.GetFullPath(path).Substring(root.Length).TrimStart('\\', '/')
                : path;
        }
    }
}
