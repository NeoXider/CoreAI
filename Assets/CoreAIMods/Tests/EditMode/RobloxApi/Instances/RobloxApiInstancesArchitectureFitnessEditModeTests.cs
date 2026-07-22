using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;

namespace CoreAI.Tests.EditMode.RobloxApi.Instances
{
    /// <summary>
    /// Architecture-fitness tripwire (ARCHITECTURE_RULES.md §5, mirrors
    /// ScriptingSeamHonestyEditModeTests): the Instance registry is an engine-free Domain
    /// module — no UnityEngine/UnityEditor imports in its sources and its asmdef must keep
    /// noEngineReferences: true with no assembly references.
    /// </summary>
    [TestFixture]
    public sealed class RobloxApiInstancesArchitectureFitnessEditModeTests
    {
        private static readonly Regex EngineUsing = new(
            @"^\s*using\s+(static\s+)?Unity(Engine|Editor)(\s*;|\s*\.)|\bUnity(Engine|Editor)\s*\.",
            RegexOptions.Compiled);

        private static string ModuleRoot => Path.Combine(Application.dataPath,
            "CoreAIMods", "Runtime", "RobloxApi", "Instances");

        [Test]
        public void DomainSources_HaveNoEngineReferences()
        {
            Assert.IsTrue(Directory.Exists(ModuleRoot), $"Module folder not found: {ModuleRoot}");

            List<string> offenders = new();
            foreach (string file in Directory.GetFiles(ModuleRoot, "*.cs", SearchOption.AllDirectories))
            {
                foreach (string line in File.ReadLines(file))
                {
                    if (EngineUsing.IsMatch(line))
                    {
                        offenders.Add($"{file}: {line.Trim()}");
                        break;
                    }
                }
            }

            Assert.IsEmpty(offenders,
                "CoreAI.RobloxApi.Instances is an engine-free Domain assembly; move engine code " +
                "into the Unity adapter (world-binding task) instead:\n" + string.Join("\n", offenders));
        }

        [Test]
        public void Asmdef_DeclaresNoEngineReferencesAndNoAssemblyReferences()
        {
            string asmdefPath = Path.Combine(ModuleRoot, "CoreAI.RobloxApi.Instances.asmdef");
            Assert.IsTrue(File.Exists(asmdefPath), $"asmdef not found: {asmdefPath}");

            string json = File.ReadAllText(asmdefPath);
            StringAssert.Contains("\"noEngineReferences\": true", json);
            StringAssert.Contains("\"references\": []", json,
                "the Domain asmdef must reference no other assemblies (inward-only rule)");
        }
    }
}
