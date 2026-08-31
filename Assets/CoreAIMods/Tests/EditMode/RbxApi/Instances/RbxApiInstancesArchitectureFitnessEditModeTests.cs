using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using CoreAI.Mods.Rbx.Instances.Scheduling;
using NUnit.Framework;
using UnityEngine;

namespace CoreAI.Tests.EditMode.RbxApi.Instances
{
    /// <summary>
    /// Architecture-fitness tripwire (ARCHITECTURE_RULES.md §5, mirrors
    /// ScriptingSeamHonestyEditModeTests): the Instance registry is an engine-free Domain
    /// module — no UnityEngine/UnityEditor imports in its sources and its asmdef must keep
    /// noEngineReferences: true with no assembly references.
    /// </summary>
    [TestFixture]
    public sealed class RbxApiInstancesArchitectureFitnessEditModeTests
    {
        private static readonly Regex EngineUsing = new(
            @"^\s*using\s+(static\s+)?Unity(Engine|Editor)(\s*;|\s*\.)|\bUnity(Engine|Editor)\s*\.",
            RegexOptions.Compiled);

        private static string ModuleRoot => Path.Combine(Application.dataPath,
            "CoreAIMods", "Runtime", "RbxApi", "Instances");

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
                "CoreAI.RbxApi.Instances is an engine-free Domain assembly; move engine code " +
                "into the Unity adapter (world-binding task) instead:\n" + string.Join("\n", offenders));
        }

        [Test]
        public void ObservabilityPort_LivesInEngineFreeDomainAssembly()
        {
            Assert.AreSame(typeof(IRbxScriptThread).Assembly,
                typeof(IRbxRuntimeObservabilitySink).Assembly);
            Assert.AreSame(typeof(IRbxScriptThread).Assembly,
                typeof(NullRbxRuntimeObservabilitySink).Assembly);
        }

        [Test]
        public void NoOpObservabilitySink_IsDisabledAndAllocationFree()
        {
            IRbxRuntimeObservabilitySink sink = NullRbxRuntimeObservabilitySink.Instance;
            Assert.IsFalse(sink.IsEnabled);
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int index = 0; index < 256; index++)
            {
                sink.RecordGuardedInstructionSteps(1);
                sink.RecordThreadResumes(1);
                sink.RecordEventsDelivered(1);
                sink.RecordCompletedOperations(1);
            }

            long after = GC.GetAllocatedBytesForCurrentThread();
            Assert.AreEqual(before, after);
        }

        // WHY: ARCHITECTURE_RULES §1 lets a Domain assembly reference sibling engine-free Domain
        // contracts only; datatype-valued attributes (audit finding 4) need the Datatypes sibling,
        // so the fitness test allows that single reference while still forbidding any engine/VM/DI
        // assembly from creeping in.
        private static readonly HashSet<string> AllowedDomainReferences = new()
        {
            "CoreAI.RbxApi.Datatypes"
        };

        [Test]
        public void Asmdef_DeclaresNoEngineReferencesAndOnlyDomainReferences()
        {
            string asmdefPath = Path.Combine(ModuleRoot, "CoreAI.RbxApi.Instances.asmdef");
            Assert.IsTrue(File.Exists(asmdefPath), $"asmdef not found: {asmdefPath}");

            string json = File.ReadAllText(asmdefPath);
            StringAssert.Contains("\"noEngineReferences\": true", json);

            List<string> offenders = new();
            foreach (Match match in Regex.Matches(json, "\"references\"\\s*:\\s*\\[(?<body>[^\\]]*)\\]"))
            {
                foreach (Match entry in Regex.Matches(match.Groups["body"].Value, "\"(?<name>[^\"]+)\""))
                {
                    string name = entry.Groups["name"].Value;
                    if (!AllowedDomainReferences.Contains(name))
                    {
                        offenders.Add(name);
                    }
                }
            }

            Assert.IsEmpty(offenders,
                "the Domain asmdef may reference sibling engine-free Domain assemblies only " +
                "(inward-only rule); disallowed references: " + string.Join(", ", offenders));
        }
    }
}
