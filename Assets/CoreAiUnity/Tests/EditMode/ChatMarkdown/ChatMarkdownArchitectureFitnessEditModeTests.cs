using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.ChatMarkdown
{
    /// <summary>
    /// Keeps the optional Markdown renderer optional: only the gated assembly (and its gated tests) may name the
    /// package's types or assemblies, that assembly compiles only when the package is installed, no package depends
    /// on it, the engine-free text rules stay engine-free, and nothing in the seam opens a URL by itself. File based,
    /// so it runs in Unity EditMode (working directory = project root) and in the portable suite (repository root).
    /// </summary>
    [TestFixture]
    public sealed class ChatMarkdownArchitectureFitnessEditModeTests
    {
        private const string PackageName = "com.neoxider.markdownrenderer";
        private const string Define = "COREAI_HAS_MARKDOWN_RENDERER";

        private static readonly string[] PackageRoots =
        {
            "Assets/CoreAI", "Assets/CoreAiUnity", "Assets/CoreAIHub", "Assets/CoreAIMods", "Assets/CoreAIMcp",
            "Assets/CoreAIBenchmark", "Assets/CoreAIMirror"
        };

        private static readonly string[] GatedFolders =
        {
            "Assets/CoreAiUnity/Runtime/Source/Features/ChatMarkdown/Renderer",
            "Assets/CoreAiUnity/Tests/EditMode/ChatMarkdown/Renderer"
        };

        private static string Root => Environment.CurrentDirectory;

        private static string SeamFolder => Path.Combine(Root, "Assets", "CoreAiUnity", "Runtime", "Source", "Features",
            "ChatMarkdown");

        [Test]
        public void EngineFreeRules_ReferenceNoEngineOrFrameworkType()
        {
            string folder = Path.Combine(Root, "Assets", "CoreAI", "Runtime", "Core", "Features", "ChatOverlay");
            string[] files = Directory.GetFiles(folder, "ChatMarkdown*.cs");
            Assert.GreaterOrEqual(files.Length, 3, "The engine-free Markdown rules moved; update this test.");
            List<string> hits = Scan(files, "UnityEngine", "UnityEditor", "VContainer", "Cysharp", "UniTask", "Markdig");
            CollectionAssert.IsEmpty(hits, string.Join("\n", hits));
        }

        [Test]
        public void PackageTypes_AreNamedOnlyInsideTheGatedFolders()
        {
            List<string> files = new();
            foreach (string package in PackageRoots)
            {
                string root = Path.Combine(Root, package);
                if (Directory.Exists(root))
                {
                    files.AddRange(Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories)
                        .Where(file => !IsGated(file) && !IsBuildOutput(file) &&
                                       Path.GetFileName(file) != nameof(ChatMarkdownArchitectureFitnessEditModeTests) + ".cs"));
                }
            }

            List<string> hits = Scan(files, "UIMarkdownRenderer", "using Markdig", "Markdig.");
            CollectionAssert.IsEmpty(hits,
                "Only the gated assembly may name the optional package; anything else breaks every project without it:\n" +
                string.Join("\n", hits));
        }

        [Test]
        public void OnlyTheGatedAssemblies_ReferenceThePackageAssemblies()
        {
            List<string> referencing = new();
            foreach (string package in PackageRoots)
            {
                string root = Path.Combine(Root, package);
                if (!Directory.Exists(root))
                {
                    continue;
                }

                foreach (string asmdef in Directory.GetFiles(root, "*.asmdef", SearchOption.AllDirectories))
                {
                    JArray references = JObject.Parse(File.ReadAllText(asmdef))["references"] as JArray;
                    bool usesPackage = references != null && references.Any(reference =>
                        reference.ToString() == "Rtl.MarkdownRenderer.Runtime" || reference.ToString() == "Markdig");
                    if (usesPackage)
                    {
                        referencing.Add(asmdef);
                        Assert.IsTrue(IsGated(asmdef), asmdef + " references the optional package outside the gated folders.");
                        AssertGatedOnThePackage(asmdef);
                    }
                }
            }

            Assert.AreEqual(2, referencing.Count, "Expected the renderer assembly and its tests:\n" + string.Join("\n", referencing));
        }

        [Test]
        public void NoPackage_DeclaresTheRendererAsADependency()
        {
            foreach (string package in PackageRoots)
            {
                string manifest = Path.Combine(Root, package, "package.json");
                if (File.Exists(manifest))
                {
                    JObject dependencies = JObject.Parse(File.ReadAllText(manifest))["dependencies"] as JObject;
                    Assert.IsNull(dependencies?[PackageName],
                        manifest + " must not depend on the optional, separately licensed renderer.");
                }
            }
        }

        [Test]
        public void Seam_OpensNoUrlAndFetchesNothingByItself()
        {
            string[] files = Directory.GetFiles(SeamFolder, "*.cs", SearchOption.AllDirectories)
                .Where(file => !Path.GetFileName(file).Equals("ICoreAiMarkdownLinkHandler.cs", StringComparison.Ordinal))
                .ToArray();
            List<string> hits = Scan(files, "Application.OpenURL", "UnityWebRequest", "OpenURL(");
            CollectionAssert.IsEmpty(hits, "Only the opt-in link handler may open a URL:\n" + string.Join("\n", hits));
        }

        [Test]
        public void SetupMenu_OffersThePackagePinnedToARelease()
        {
            string manager = File.ReadAllText(Path.Combine(Root, "Assets", "CoreAiUnity", "Editor", "CoreAIModuleManager.cs"));
            StringAssert.Contains("\"" + PackageName + "\"", manager);
            StringAssert.Contains("https://github.com/NeoXider/NeoxiderMarkdownRenderer.git#v", manager,
                "The optional module installs a pinned tag, never a moving branch.");
        }

        private static void AssertGatedOnThePackage(string asmdef)
        {
            JObject json = JObject.Parse(File.ReadAllText(asmdef));
            JArray constraints = json["defineConstraints"] as JArray;
            Assert.IsNotNull(constraints, asmdef);
            CollectionAssert.Contains(constraints.Select(token => token.ToString()).ToList(), Define,
                asmdef + " must compile only when the package is installed.");
            JArray versionDefines = json["versionDefines"] as JArray;
            Assert.IsNotNull(versionDefines, asmdef);
            Assert.IsTrue(versionDefines.Any(entry => (string)entry["name"] == PackageName && (string)entry["define"] == Define),
                asmdef + " must derive " + Define + " from the installed package.");
        }

        private static bool IsGated(string path)
        {
            string normalized = path.Replace('\\', '/');
            return GatedFolders.Any(folder => normalized.Contains("/" + folder + "/") ||
                                              normalized.EndsWith("/" + folder, StringComparison.Ordinal) ||
                                              normalized.StartsWith(folder + "/", StringComparison.Ordinal));
        }

        private static bool IsBuildOutput(string path)
        {
            string normalized = path.Replace('\\', '/');
            return normalized.Contains("/obj/") || normalized.Contains("/bin/");
        }

        private static List<string> Scan(IEnumerable<string> files, params string[] needles)
        {
            List<string> hits = new();
            foreach (string file in files)
            {
                string[] lines = File.ReadAllLines(file);
                for (int index = 0; index < lines.Length; index++)
                {
                    string trimmed = lines[index].TrimStart();
                    if (trimmed.StartsWith("//", StringComparison.Ordinal) || trimmed.StartsWith("*", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    foreach (string needle in needles)
                    {
                        if (lines[index].Contains(needle))
                        {
                            hits.Add(Path.GetFileName(file) + ":" + (index + 1) + " " + needle);
                        }
                    }
                }
            }

            return hits;
        }
    }
}
