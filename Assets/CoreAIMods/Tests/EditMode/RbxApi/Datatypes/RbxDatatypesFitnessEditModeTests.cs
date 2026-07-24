using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;

namespace CoreAI.Tests.EditMode.RbxApi.Datatypes
{
    /// <summary>
    /// Architecture-fitness tests for the Roblox datatypes Domain slice
    /// (ARCHITECTURE_RULES.md §5, mirroring ScriptingSeamHonestyEditModeTests):
    /// the Datatypes assembly stays engine-free, and RbxSpace remains THE single
    /// Roblox-to-Unity conversion boundary (ROBLOX_API_ROADMAP.md D2 lint rule).
    /// </summary>
    [TestFixture]
    public sealed class RbxDatatypesFitnessEditModeTests
    {
        private static readonly Regex UnityUsing = new(
            @"^\s*using\s+(static\s+)?UnityEngine(\s*;|\s*\.)|(?<![\w.])UnityEngine\s*\.",
            RegexOptions.Compiled);

        private static string RbxApiRoot =>
            Path.Combine(Application.dataPath, "CoreAIMods", "Runtime", "RbxApi");

        [Test]
        public void DatatypesDomain_HasNoUnityEngineReferences()
        {
            string datatypesRoot = Path.Combine(RbxApiRoot, "Datatypes");
            Assert.IsTrue(Directory.Exists(datatypesRoot), $"Datatypes folder not found: {datatypesRoot}");

            List<string> offenders = ScanForUnityUsage(datatypesRoot, excluded: System.Array.Empty<string>());
            Assert.IsEmpty(offenders,
                "The Roblox datatypes Domain must stay engine-free (ARCHITECTURE_RULES.md §1); " +
                "move engine-touching code into the RbxApi/Unity adapter:\n" +
                string.Join("\n", offenders));
        }

        [Test]
        public void DatatypesAsmdef_DeclaresEngineFreeDomain()
        {
            string asmdefPath = Path.Combine(
                RbxApiRoot, "Datatypes", "CoreAI.RbxApi.Datatypes.asmdef");
            Assert.IsTrue(File.Exists(asmdefPath), $"asmdef not found: {asmdefPath}");

            string json = File.ReadAllText(asmdefPath);
            StringAssert.Contains("\"noEngineReferences\": true", json,
                "Domain asmdef must set noEngineReferences (ARCHITECTURE_RULES.md §1)");
            StringAssert.Contains("\"references\": []", json.Replace("\r", string.Empty),
                "Domain asmdef must reference nothing (inward-only dependency rule)");
        }

        [Test]
        public void DatatypesAssembly_ReferencesNoUnityAssemblies()
        {
            var assembly = typeof(CoreAI.Mods.Rbx.Datatypes.RbxVector3).Assembly;
            string[] unityRefs = assembly.GetReferencedAssemblies()
                .Where(a => a.Name.StartsWith("UnityEngine") || a.Name.StartsWith("UnityEditor"))
                .Select(a => a.Name)
                .ToArray();
            Assert.IsEmpty(unityRefs,
                "CoreAI.RbxApi.Datatypes must not link any Unity assembly: " +
                string.Join(", ", unityRefs));
        }

        /// <summary>
        /// The D2 lint rule (RbxSpaceUsageLintTests in the roadmap): nothing in the RbxApi
        /// layer outside the Unity adapter folder may touch UnityEngine types, so no second
        /// Z-flip or scale factor can sneak in (the design's primary failure mode).
        /// </summary>
        [Test]
        public void D2_RobloxSpaceIsTheOnlyConversionBoundary()
        {
            Assert.IsTrue(Directory.Exists(RbxApiRoot), $"RbxApi folder not found: {RbxApiRoot}");

            // WHY: the roadmap allows exactly two engine-touching homes — the RbxSpace
            // adapter (Unity/) and the GameObject binder's single call sites (Binding/, MVP1
            // task 7); everything else in the RbxApi layer stays engine-free.
            var allowed = new[]
            {
                NormalizeDir(Path.Combine(RbxApiRoot, "Unity")),
                NormalizeDir(Path.Combine(RbxApiRoot, "Binding"))
            };
            List<string> offenders = ScanForUnityUsage(RbxApiRoot, allowed);
            Assert.IsEmpty(offenders,
                "Only the RbxApi/Unity adapter (RbxSpace + future binder) may use " +
                "UnityEngine types — all spatial conversion goes through RbxSpace (D2):\n" +
                string.Join("\n", offenders));
        }

        private static List<string> ScanForUnityUsage(string root, string[] excluded)
        {
            var offenders = new List<string>();
            foreach (string file in Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories))
            {
                string normalized = Path.GetFullPath(file).Replace('\\', '/');
                if (excluded.Any(dir =>
                        normalized.StartsWith(dir, System.StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                foreach (string line in File.ReadLines(file))
                {
                    if (UnityUsing.IsMatch(line))
                    {
                        offenders.Add($"{normalized}: {line.Trim()}");
                        break;
                    }
                }
            }

            return offenders;
        }

        private static string NormalizeDir(string path) =>
            Path.GetFullPath(path).Replace('\\', '/').TrimEnd('/') + "/";
    }
}
