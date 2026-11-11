using System;
using System.IO;
using System.Reflection;
using CoreAI.Editor;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor.Compilation;
using UnityEngine;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// Newcomer audit B1 and B2. The "fix my install" menus (<see cref="CoreAIDependencyInstaller"/>,
    /// <see cref="CoreAINuGetBootstrapper"/>) live in <c>CoreAI.Editor.Setup</c>, an assembly with no references at
    /// all, so they exist before VContainer, MEAI, LLMUnity or Newtonsoft are installed. Newtonsoft is declared by
    /// the core package and listed in INSTALL.md.
    /// </summary>
    [TestFixture]
    public sealed class CoreAISetupAssemblyEditModeTests
    {
        private const string SetupAssemblyName = "CoreAI.Editor.Setup";

        private static readonly string[] ForbiddenReferencePrefixes =
        {
            "CoreAI.", "VContainer", "MessagePipe", "UniTask", "undream", "Newtonsoft", "Microsoft.Extensions"
        };

        [Test]
        public void SetupMenus_LiveInTheSetupAssembly()
        {
            Assert.AreEqual(SetupAssemblyName, typeof(CoreAIDependencyInstaller).Assembly.GetName().Name);
            Assert.AreEqual(SetupAssemblyName, typeof(CoreAINuGetBootstrapper).Assembly.GetName().Name);
            Assert.AreEqual("CoreAI.Editor", typeof(CoreAIDependencyInstaller).Namespace,
                "The namespace is unchanged, so source callers keep compiling.");
        }

        [Test]
        public void SetupAssembly_ReferencesNothingThatABrokenInstallCanLack()
        {
            foreach (AssemblyName reference in typeof(CoreAIDependencyInstaller).Assembly.GetReferencedAssemblies())
            {
                foreach (string forbidden in ForbiddenReferencePrefixes)
                {
                    Assert.IsFalse(reference.Name.StartsWith(forbidden, StringComparison.Ordinal),
                        $"{SetupAssemblyName} references {reference.Name}; the setup menus must compile without it.");
                }
            }
        }

        [Test]
        public void ReferenceProbe_SeesTheReferencesOfAnOrdinaryEditorAssembly()
        {
            bool referencesCore = false;
            foreach (AssemblyName reference in typeof(CoreAILlmModuleBuildGuard).Assembly.GetReferencedAssemblies())
            {
                referencesCore |= reference.Name == "CoreAI.Source" || reference.Name == "CoreAI.Core";
            }

            Assert.IsTrue(referencesCore, "Negative twin: CoreAI.Editor does reference the CoreAI runtime.");
        }

        [Test]
        public void SetupAsmdef_DeclaresNoReferences()
        {
            string path = CompilationPipeline.GetAssemblyDefinitionFilePathFromAssemblyName(SetupAssemblyName);
            Assert.IsFalse(string.IsNullOrEmpty(path), "The setup asmdef is not part of the compilation.");

            JObject asmdef = JObject.Parse(File.ReadAllText(path));
            Assert.IsEmpty((JArray)asmdef["references"]);
            Assert.IsEmpty((JArray)asmdef["precompiledReferences"]);
            Assert.IsTrue((bool)asmdef["overrideReferences"],
                "Without overrideReferences every precompiled DLL (Newtonsoft, MEAI) is referenced implicitly.");
            CollectionAssert.AreEqual(new[] { "Editor" }, ((JArray)asmdef["includePlatforms"]).ToObject<string[]>());
        }

        [TestCase("{}", true)]
        [TestCase("{\"dependencies\":{\"a\":\"1\",\"b\":\"2\"},\"testables\":[\"a\"]}", true)]
        [TestCase("{ \"n\": -1.5e3, \"t\": true, \"f\": false, \"z\": null, \"s\": \"\\u00e9\\n\" }", true)]
        [TestCase("{\"dependencies\":{\"a\":\"1\",}}", false)]
        [TestCase("{\"testables\":[\"a\",]}", false)]
        [TestCase("{\"a\":1} trailing", false)]
        [TestCase("// comment\n{}", false)]
        [TestCase("[]", false)]
        [TestCase("{\"a\":01}", false)]
        [TestCase("{\"a\":\"\\x\"}", false)]
        [TestCase("{\"a\":\"unterminated}", false)]
        [TestCase("", false)]
        public void ManifestJsonSyntax_AcceptsJsonObjects_AndRejectsWhatUnityRefusesToOpen(string text, bool valid)
        {
            Assert.AreEqual(valid, ManifestJsonSyntax.IsObject(text));
        }

        [Test]
        public void TryAddDependency_RefusesAManifestThatIsAlreadyBroken()
        {
            const string broken = "{\n  \"dependencies\": {\n    \"com.unity.ugui\": \"2.0.0\"\n  },\n" +
                                  "  \"testables\": [\"x\",]\n}\n";

            Assert.IsFalse(CoreAIDependencyInstaller.TryAddDependency(broken, "jp.hadashikick.vcontainer",
                "https://example.invalid/vcontainer.git", out string updated));
            Assert.AreEqual(broken, updated, "Nothing is written over a manifest the edit cannot keep valid.");
        }

        [Test]
        public void CorePackage_DeclaresNewtonsoft_AndInstallListsIt()
        {
            string packageJson = Path.Combine(Application.dataPath, "CoreAI", "package.json");
            string install = Path.Combine(Path.GetDirectoryName(Application.dataPath) ?? "", "INSTALL.md");
            if (!File.Exists(packageJson) || !File.Exists(install))
            {
                Assert.Ignore("Runs in the CoreAI repository, where the packages are folders under Assets.");
            }

            JObject manifest = JObject.Parse(File.ReadAllText(packageJson));
            Assert.IsNotNull(manifest["dependencies"]?["com.unity.nuget.newtonsoft-json"],
                "CoreAI.Core, CoreAI.Source and CoreAI.Editor reference Newtonsoft.Json.dll; without the declared " +
                "dependency a project that removes LLMUnity fails with CS0246.");
            StringAssert.Contains("com.unity.nuget.newtonsoft-json", File.ReadAllText(install));
        }
    }
}
