using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using CoreAI.Mods.Folders;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.ModFolders
{
    /// <summary>
    /// Architecture fitness of the mod-folder Domain: its own assembly, referencing nothing but the base
    /// class library (no engine, no VM, no DI or async framework, no JSON library, no other CoreAI assembly),
    /// with an asmdef that says so.
    /// </summary>
    [TestFixture]
    public sealed class ModFolderArchitectureFitnessEditModeTests
    {
        private const string AsmdefPath = "Assets/CoreAIMods/Runtime/ModFolders/Domain/CoreAI.Mods.Folders.asmdef";

        private static readonly string[] AllowedReferences =
        {
            "netstandard", "mscorlib", "System", "System.Core", "System.Runtime", "System.Collections",
            "System.Runtime.Extensions", "System.Globalization"
        };

        [Test]
        public void DomainAssembly_ReferencesOnlyTheBaseClassLibrary()
        {
            Assembly assembly = typeof(ModFolderPath).Assembly;
            Assert.AreEqual("CoreAI.Mods.Folders", assembly.GetName().Name);

            List<string> offenders = new();
            foreach (AssemblyName reference in assembly.GetReferencedAssemblies())
            {
                if (Array.IndexOf(AllowedReferences, reference.Name) < 0)
                {
                    offenders.Add(reference.Name);
                }
            }

            Assert.IsEmpty(offenders, "CoreAI.Mods.Folders must stay a pure Domain assembly; it references: " +
                                      string.Join(", ", offenders));
        }

        [Test]
        public void DomainAsmdef_DeclaresNoEngineAndNoReferences()
        {
            if (!File.Exists(AsmdefPath))
            {
                Assert.Ignore("The asmdef is read relative to the repository root; the current directory is " +
                              Environment.CurrentDirectory);
            }

            string text = File.ReadAllText(AsmdefPath).Replace(" ", "").Replace("\r", "").Replace("\n", "");

            StringAssert.Contains("\"noEngineReferences\":true", text);
            StringAssert.Contains("\"references\":[]", text);
            StringAssert.Contains("\"overrideReferences\":true", text);
            StringAssert.Contains("\"precompiledReferences\":[]", text);
        }
    }
}
